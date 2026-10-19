using System.Text.Json;
using NAudio.CoreAudioApi;

namespace MojaveDesktopRadio.Audio;

/// <summary>
/// Direct mode (no FX route): fades the player's own audio-session volume for the open/close
/// transitions and the "mute instead of pause" option. The original volume is written to a
/// marker before the first change so it is restored even after a crash.
/// Runs synchronously on the engine thread (a fade is at most ~40 short steps).
/// </summary>
public sealed class SessionVolumeFader
{
    private static readonly string MarkerFile = Path.Combine(AppPaths.StateDir, "session-volume.json");
    private readonly AudioDevices _devices;
    private readonly IReadOnlyCollection<string> _processNames;
    private readonly bool _enabled;
    private float? _original;

    /// <param name="enabled">false in --no-audio mode: every method is a no-op.</param>
    public SessionVolumeFader(AudioDevices devices, IReadOnlyCollection<string> processNames, bool enabled = true)
    {
        _devices = devices;
        _processNames = processNames;
        _enabled = enabled;
    }

    public bool IsAltered => _original.HasValue;

    /// <summary>Fades the sessions to <paramref name="factor"/> × original volume.</summary>
    public void FadeTo(float factor, int ms)
    {
        if (!_enabled) return;
        var sessions = _devices.CollectSessions(_processNames);
        try
        {
            if (sessions.Count == 0) return;

            if (!_original.HasValue)
            {
                var max = sessions.Max(s => s.SimpleAudioVolume.Volume);
                _original = max <= 0.001f ? 1f : max;
                WriteMarker(_original.Value, _processNames);
            }

            var target = Math.Clamp(factor, 0f, 1f) * _original.Value;
            var start = sessions[0].SimpleAudioVolume.Volume;
            var steps = Math.Clamp(ms / 15, 1, 40);
            var sleep = Math.Max(1, ms / steps);
            for (var i = 1; i <= steps; i++)
            {
                var v = start + (target - start) * i / steps;
                SetAll(sessions, v);
                if (i < steps) Thread.Sleep(sleep);
            }

            if (Math.Abs(factor - 1f) < 0.001f)
            {
                _original = null;
                TryDelete();
            }
        }
        finally
        {
            foreach (var s in sessions) s.Dispose();
        }
    }

    /// <summary>Puts the original volume back immediately (no-op if nothing was changed).</summary>
    public void Restore()
    {
        if (!_enabled) return;
        if (!_original.HasValue)
        {
            RecoverIfNeeded();
            return;
        }

        var v = _original.Value;
        var sessions = _devices.CollectSessions(_processNames);
        try
        {
            SetAll(sessions, v);
            if (sessions.Count > 0 || AudioDevices.GetProcessIds(_processNames).Count == 0)
            {
                _original = null;
                TryDelete();
            }
        }
        finally
        {
            foreach (var s in sessions) s.Dispose();
        }
    }

    /// <summary>After a crash: restore the volume recorded in the marker file.</summary>
    public void RecoverIfNeeded()
    {
        if (!_enabled || _original.HasValue || !File.Exists(MarkerFile)) return;
        List<AudioSessionControl>? sessions = null;
        try
        {
            var marker = JsonSerializer.Deserialize<VolumeMarker>(File.ReadAllText(MarkerFile));
            var v = marker?.Volume ?? 1f;
            // the marker names the player whose volume was changed (it may differ after a player switch)
            IReadOnlyCollection<string> names = marker?.Processes is { Length: > 0 } p ? p : _processNames;
            sessions = _devices.CollectSessions(names);
            if (sessions.Count > 0)
            {
                SetAll(sessions, v);
                TryDelete();
                Log.Info($"recovered {string.Join("/", names)} session volume ({v:0.00}) left over from an earlier change");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"volume recovery deferred: {ex.Message}");
        }
        finally
        {
            if (sessions is not null)
                foreach (var s in sessions) s.Dispose();
        }
    }

    private static void SetAll(List<AudioSessionControl> sessions, float v)
    {
        foreach (var s in sessions)
        {
            try { s.SimpleAudioVolume.Volume = Math.Clamp(v, 0f, 1f); }
            catch { /* session may have expired mid-fade */ }
        }
    }

    private static void WriteMarker(float volume, IReadOnlyCollection<string> processes)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(MarkerFile, JsonSerializer.Serialize(new VolumeMarker(volume, processes.ToArray())));
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDelete()
    {
        try { File.Delete(MarkerFile); } catch { }
    }

    private sealed record VolumeMarker(float Volume, string[]? Processes = null);
}
