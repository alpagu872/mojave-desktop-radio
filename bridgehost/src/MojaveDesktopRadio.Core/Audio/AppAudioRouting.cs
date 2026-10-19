using System.Runtime.InteropServices;
using System.Text.Json;

namespace MojaveDesktopRadio.Audio;

/// <summary>
/// Per-application output device ("App volume and device preferences" in Windows Settings).
///
/// Windows has no public API for this. We use the same internal WinRT factory as EarTrumpet
/// (Windows.Media.Internal.AudioPolicyConfig, IID for Windows 10 21H2+/Windows 11). If anything
/// about it fails we report it and the engine stays in Direct mode — nothing is left half-changed.
///
/// Every change is recorded in a marker file first, and undone on: game disconnect, BridgeHost
/// exit, and the next BridgeHost start after a crash.
/// </summary>
public sealed class AppAudioRouting
{
    private const int EInvalidArg = unchecked((int)0x80070057);
    private const string MmdevapiToken = @"\\?\SWD#MMDEVAPI#";
    private const string DevInterfaceAudioRender = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private static readonly string MarkerFile = Path.Combine(AppPaths.StateDir, "routing.json");

    private IAudioPolicyConfigFactory? _factory;
    private bool _initFailed;

    public bool IsSupported => EnsureFactory() is not null;

    public string? LastError { get; private set; }

    /// <summary>True while we have an un-restored routing change recorded on disk.</summary>
    public bool HasPendingRestore => Enabled && File.Exists(MarkerFile);

    /// <summary>False in --no-audio mode: nothing is routed, restored or recovered.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Routes every running process of <paramref name="processNames"/> to the given render device.</summary>
    public bool Route(IReadOnlyCollection<string> processNames, string deviceId)
    {
        if (!Enabled) return false;
        var factory = EnsureFactory();
        if (factory is null) return false;
        LastError = null;

        var pids = AudioDevices.GetProcessIds(processNames);
        if (pids.Count == 0)
        {
            LastError = "process not running";
            return false;
        }

        WriteMarker(processNames, deviceId);
        var ok = false;
        var hstr = IntPtr.Zero;
        try
        {
            var full = MmdevapiToken + deviceId + DevInterfaceAudioRender;
            Check(WindowsCreateString(full, full.Length, out hstr), "WindowsCreateString");
            // Chromium-based apps (Spotify) run many helper processes; sandboxed ones answer
            // E_INVALIDARG. The setting is per application, so one accepted process is enough.
            foreach (var pid in pids)
            {
                var a = factory.SetPersistedDefaultAudioEndpoint((uint)pid, EDataFlow.eRender, ERole.eMultimedia, hstr);
                var b = factory.SetPersistedDefaultAudioEndpoint((uint)pid, EDataFlow.eRender, ERole.eConsole, hstr);
                if (a >= 0 && b >= 0) ok = true;
                else if (a != EInvalidArg) Succeeded(a, pid);
            }

            if (!ok && LastError is null) LastError = "no process accepted the device preference";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ok = false;
        }
        finally
        {
            if (hstr != IntPtr.Zero) WindowsDeleteString(hstr);
        }

        if (!ok)
        {
            Log.Warn($"per-app routing failed: {LastError}; restoring");
            Restore(processNames);
        }

        return ok;
    }

    /// <summary>
    /// Puts the processes back on "Default". Returns false if the process is not running — the marker
    /// is kept and the restore is retried the next time the process is seen.
    /// </summary>
    public bool Restore(IReadOnlyCollection<string> processNames)
    {
        if (!Enabled) return true;
        var factory = EnsureFactory();
        if (factory is null) return false;

        var names = processNames.Count > 0 ? processNames : ReadMarkerProcesses();
        var pids = AudioDevices.GetProcessIds(names);
        if (pids.Count == 0) return false;

        var ok = false;
        try
        {
            foreach (var pid in pids)
            {
                var a = factory.SetPersistedDefaultAudioEndpoint((uint)pid, EDataFlow.eRender, ERole.eMultimedia, IntPtr.Zero);
                var b = factory.SetPersistedDefaultAudioEndpoint((uint)pid, EDataFlow.eRender, ERole.eConsole, IntPtr.Zero);
                if (a >= 0 && b >= 0) ok = true;
                else if (a != EInvalidArg) Succeeded(a, pid);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ok = false;
        }

        if (ok)
        {
            TryDelete(MarkerFile);
            Log.Info($"per-app routing restored to Default for {string.Join(", ", names)}");
        }

        return ok;
    }

    /// <summary>Crash recovery: if a marker exists, restore now (or as soon as the process appears).</summary>
    public void RecoverIfNeeded()
    {
        if (!HasPendingRestore) return; // also false when !Enabled
        var names = ReadMarkerProcesses();
        if (Restore(names))
            Log.Info("recovered a routing change left over from a previous session");
    }

    /// <summary>Diagnostics: tries Set + immediate restore for every process and reports the HRESULTs.</summary>
    public IReadOnlyList<(int Pid, string Name, int SetHr, int RestoreHr)> Probe(IReadOnlyCollection<string> processNames, string deviceId)
    {
        var result = new List<(int, string, int, int)>();
        var factory = EnsureFactory();
        if (factory is null) return result;
        var full = MmdevapiToken + deviceId + DevInterfaceAudioRender;
        Check(WindowsCreateString(full, full.Length, out var hstr), "WindowsCreateString");
        try
        {
            foreach (var name in processNames)
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                {
                    using (p)
                    {
                        var set = factory.SetPersistedDefaultAudioEndpoint((uint)p.Id, EDataFlow.eRender, ERole.eMultimedia, hstr);
                        var restore = factory.SetPersistedDefaultAudioEndpoint((uint)p.Id, EDataFlow.eRender, ERole.eMultimedia, IntPtr.Zero);
                        string title;
                        try { title = p.MainWindowTitle; } catch { title = ""; }
                        result.Add((p.Id, $"{p.ProcessName} '{title}'", set, restore));
                    }
                }
            }
        }
        finally
        {
            WindowsDeleteString(hstr);
        }

        return result;
    }

    /// <summary>Returns the device id Windows has persisted for the process (null = Default).</summary>
    public string? GetRoutedDevice(int pid)
    {
        var factory = EnsureFactory();
        if (factory is null) return null;
        try
        {
            if (factory.GetPersistedDefaultAudioEndpoint((uint)pid, EDataFlow.eRender, ERole.eMultimedia | ERole.eConsole, out var h) != 0 || h == IntPtr.Zero)
                return null;
            try
            {
                var raw = Marshal.PtrToStringUni(WindowsGetStringRawBuffer(h, out _));
                if (string.IsNullOrEmpty(raw)) return null;
                if (raw.StartsWith(MmdevapiToken, StringComparison.Ordinal)) raw = raw[MmdevapiToken.Length..];
                if (raw.EndsWith(DevInterfaceAudioRender, StringComparison.OrdinalIgnoreCase)) raw = raw[..^DevInterfaceAudioRender.Length];
                return raw;
            }
            finally
            {
                WindowsDeleteString(h);
            }
        }
        catch
        {
            return null;
        }
    }

    private IAudioPolicyConfigFactory? EnsureFactory()
    {
        if (_factory is not null || _initFailed) return _factory;
        if (Environment.OSVersion.Version.Build < 21390)
        {
            // The pre-21H2 IID exists too, but this project targets Windows 11; stay safe.
            _initFailed = true;
            LastError = "Windows build older than 21H2 is not supported for automatic routing";
            return null;
        }

        var hName = IntPtr.Zero;
        try
        {
            const string className = "Windows.Media.Internal.AudioPolicyConfig";
            Check(WindowsCreateString(className, className.Length, out hName), "WindowsCreateString");
            var iid = typeof(IAudioPolicyConfigFactory).GUID;
            Check(RoGetActivationFactory(hName, ref iid, out var ptr), "RoGetActivationFactory");
            try
            {
                _factory = (IAudioPolicyConfigFactory)Marshal.GetObjectForIUnknown(ptr);
            }
            finally
            {
                Marshal.Release(ptr);
            }
        }
        catch (Exception ex)
        {
            _initFailed = true;
            LastError = ex.Message;
            Log.Warn($"automatic per-app routing unavailable: {ex.Message}");
        }
        finally
        {
            if (hName != IntPtr.Zero) WindowsDeleteString(hName);
        }

        return _factory;
    }

    private bool Succeeded(int hr, int pid)
    {
        if (hr >= 0) return true;
        LastError = $"HRESULT 0x{hr:X8} for pid {pid}";
        return false;
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed", hr);
    }

    private static void WriteMarker(IReadOnlyCollection<string> processNames, string deviceId)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(MarkerFile, JsonSerializer.Serialize(new RoutingMarker([.. processNames], deviceId, DateTime.Now)));
        }
        catch (Exception ex)
        {
            Log.Warn($"could not write routing marker: {ex.Message}");
        }
    }

    private static string[] ReadMarkerProcesses()
    {
        try
        {
            if (File.Exists(MarkerFile))
                return JsonSerializer.Deserialize<RoutingMarker>(File.ReadAllText(MarkerFile))?.Processes ?? ["Spotify"];
        }
        catch
        {
            // fall through
        }

        return ["Spotify"];
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private sealed record RoutingMarker(string[] Processes, string DeviceId, DateTime RoutedAt);

    // ---- interop -------------------------------------------------------------------------------

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }

    [Flags]
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    /// <summary>
    /// IAudioPolicyConfigFactory (Windows 10 21H2+ / Windows 11). It derives from IInspectable;
    /// the three IInspectable slots and 19 unused methods are declared only to keep the vtable layout.
    /// </summary>
    [ComImport]
    [Guid("ab3d4648-e242-459f-b02f-541c70306324")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfigFactory
    {
        void IInspectable_GetIids();
        void IInspectable_GetRuntimeClassName();
        void IInspectable_GetTrustLevel();

        void Unused_add_CtxVolumeChange();
        void Unused_remove_CtxVolumeChanged();
        void Unused_add_RingerVibrateStateChanged();
        void Unused_remove_RingerVibrateStateChange();
        void Unused_SetVolumeGroupGainForId();
        void Unused_GetVolumeGroupGainForId();
        void Unused_GetActiveVolumeGroupForEndpointId();
        void Unused_GetVolumeGroupsForEndpoint();
        void Unused_GetCurrentVolumeContext();
        void Unused_SetVolumeGroupMuteForId();
        void Unused_GetVolumeGroupMuteForId();
        void Unused_SetRingerVibrateState();
        void Unused_GetRingerVibrateState();
        void Unused_SetPreferredChatApplication();
        void Unused_ResetPreferredChatApplication();
        void Unused_GetPreferredChatApplication();
        void Unused_GetCurrentChatApplications();
        void Unused_add_ChatContextChanged();
        void Unused_remove_ChatContextChanged();

        [PreserveSig]
        int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);

        [PreserveSig]
        int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);

        [PreserveSig]
        int ClearAllPersistedApplicationDefaultEndpoints();
    }

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
}
