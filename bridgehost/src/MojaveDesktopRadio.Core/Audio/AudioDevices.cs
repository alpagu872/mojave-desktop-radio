using System.Diagnostics;
using MojaveDesktopRadio.Config;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MojaveDesktopRadio.Audio;

public sealed record RenderDeviceInfo(string Id, string Name, bool IsDefault, int Channels, int SampleRate);

public sealed record AppAudioSession(string DeviceId, string DeviceName, int ProcessId, float Volume, bool Muted, AudioSessionState State);

/// <summary>
/// Thin wrapper over the Windows MMDevice API. All calls are expected on an MTA thread
/// (the engine thread or a CLI thread) — never on the WinForms UI thread.
/// </summary>
public sealed class AudioDevices : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    // Reading FriendlyName opens each endpoint's property store, which costs tens of milliseconds per
    // device (1.5 s for 9 endpoints measured). Names are cached until Windows reports a device change.
    private List<(string Id, string Name)>? _activeRender;

    public MMDeviceEnumerator Enumerator => _enumerator;

    /// <summary>Call when Windows reports an endpoint change.</summary>
    public void InvalidateCache() => _activeRender = null;

    private List<(string Id, string Name)> ActiveRender()
    {
        if (_activeRender is not null) return _activeRender;
        var list = new List<(string, string)>();
        foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try { list.Add((d.ID, d.FriendlyName)); }
            catch { /* endpoint vanished while enumerating */ }
        }

        _activeRender = list;
        return list;
    }

    public IReadOnlyList<RenderDeviceInfo> ListRenderDevices()
    {
        var defaultId = TryGetDefaultRender()?.ID;
        var list = new List<RenderDeviceInfo>();
        foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            int channels = 0, rate = 0;
            try
            {
                var mix = d.AudioClient.MixFormat;
                channels = mix.Channels;
                rate = mix.SampleRate;
            }
            catch
            {
                // some virtual devices refuse AudioClient activation while idle
            }

            list.Add(new RenderDeviceInfo(d.ID, d.FriendlyName, d.ID == defaultId, channels, rate));
        }

        return list;
    }

    public MMDevice? TryGetDefaultRender()
    {
        try
        {
            return _enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public MMDevice? TryGetDevice(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            var d = _enumerator.GetDevice(id);
            return d.State == DeviceState.Active ? d : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The inaudible endpoint Spotify is routed to while FX runs. Never returns the output device.
    /// </summary>
    public MMDevice? FindRouteDevice(BridgeSettings settings, string? outputDeviceId)
    {
        var explicitDev = TryGetDevice(settings.RouteDeviceId);
        if (explicitDev is not null && explicitDev.ID != outputDeviceId)
            return explicitDev;

        foreach (var keyword in settings.RouteDeviceKeywords)
        {
            foreach (var (id, name) in ActiveRender())
            {
                if (id != outputDeviceId && name.Contains(keyword, StringComparison.OrdinalIgnoreCase) && TryGetDevice(id) is { } dev)
                    return dev;
            }
        }

        return null;
    }

    /// <summary>The device the user actually hears: explicit setting or the Windows default, never the route device.</summary>
    public MMDevice? FindOutputDevice(BridgeSettings settings, string? excludeId)
    {
        var explicitDev = TryGetDevice(settings.OutputDeviceId);
        if (explicitDev is not null && explicitDev.ID != excludeId)
            return explicitDev;

        var def = TryGetDefaultRender();
        if (def is not null && def.ID != excludeId)
            return def;

        // Default device IS the route device (user error) — pick any other real device.
        foreach (var (id, name) in ActiveRender())
        {
            if (id != excludeId && !IsKnownVirtual(name, settings) && TryGetDevice(id) is { } dev)
                return dev;
        }

        return null;
    }

    public static bool IsKnownVirtual(string name, BridgeSettings settings) =>
        settings.RouteDeviceKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>All audio sessions (on all active render devices) that belong to the given process names.</summary>
    public IReadOnlyList<AppAudioSession> FindSessions(IReadOnlyCollection<string> processNames)
    {
        var pids = GetProcessIds(processNames);
        var result = new List<AppAudioSession>();
        if (pids.Count == 0) return result;
        var names = ActiveRender().ToDictionary(d => d.Id, d => d.Name);

        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var mgr = device.AudioSessionManager;
                mgr.RefreshSessions();
                var sessions = mgr.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var s = sessions[i];
                    var pid = (int)s.GetProcessID;
                    if (!pids.Contains(pid) || s.State == AudioSessionState.AudioSessionStateExpired) continue;
                    result.Add(new AppAudioSession(device.ID, names.GetValueOrDefault(device.ID, device.ID), pid,
                        s.SimpleAudioVolume.Volume, s.SimpleAudioVolume.Mute, s.State));
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"session enumeration failed on '{device.FriendlyName}': {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>Runs an action for every session of the given processes (used for direct-mode fades).</summary>
    public int ForEachSession(IReadOnlyCollection<string> processNames, Action<AudioSessionControl> action)
    {
        var pids = GetProcessIds(processNames);
        if (pids.Count == 0) return 0;
        var count = 0;
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var mgr = device.AudioSessionManager;
                mgr.RefreshSessions();
                var sessions = mgr.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var s = sessions[i];
                    if (pids.Contains((int)s.GetProcessID) && s.State != AudioSessionState.AudioSessionStateExpired)
                    {
                        action(s);
                        count++;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"session access failed on '{device.FriendlyName}': {ex.Message}");
            }
        }

        return count;
    }

    /// <summary>Live session controls of the given processes. The caller must dispose them.</summary>
    public List<AudioSessionControl> CollectSessions(IReadOnlyCollection<string> processNames)
    {
        var pids = GetProcessIds(processNames);
        var list = new List<AudioSessionControl>();
        if (pids.Count == 0) return list;
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var mgr = device.AudioSessionManager;
                mgr.RefreshSessions();
                var sessions = mgr.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    if (pids.Contains((int)s.GetProcessID) && s.State != AudioSessionState.AudioSessionStateExpired)
                        list.Add(s);
                    else
                        s.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"session access failed on '{device.FriendlyName}': {ex.Message}");
            }
        }

        return list;
    }

    public static HashSet<int> GetProcessIds(IReadOnlyCollection<string> processNames)
    {
        var set = new HashSet<int>();
        foreach (var name in processNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                set.Add(p.Id);
                p.Dispose();
            }
        }

        return set;
    }

    public void Dispose() => _enumerator.Dispose();
}
