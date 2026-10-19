using System.Text.Json;
using System.Text.Json.Serialization;
using MojaveDesktopRadio.Fx;

namespace MojaveDesktopRadio.Config;

/// <summary>
/// BridgeHost settings (settings.json). The game pushes its own FX settings over IPC; the values
/// here are only the desktop defaults used when no game is connected.
/// </summary>
public sealed class BridgeSettings
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Named pipe name (without \\.\pipe\). Must match the NVSE plugin INI.</summary>
    public string PipeName { get; set; } = Ipc.Protocol.DefaultPipeName;

    /// <summary>A GSMTC session is accepted only if its SourceAppUserModelId contains one of these (case-insensitive).</summary>
    public List<string> SessionMatch { get; set; } = ["Spotify"];

    /// <summary>Process names (without .exe) whose audio is routed for FX.</summary>
    public List<string> AudioProcessNames { get; set; } = ["Spotify"];

    /// <summary>Explicit route endpoint id (MMDevice id). Empty = pick the first match of RouteDeviceKeywords.</summary>
    public string RouteDeviceId { get; set; } = "";

    /// <summary>Inaudible render endpoints that may be used as the FX route, in priority order.</summary>
    public List<string> RouteDeviceKeywords { get; set; } = ["CABLE Input", "VB-Audio Virtual", "Steam Streaming Speakers"];

    /// <summary>Explicit output endpoint id. Empty = follow the Windows default multimedia device.</summary>
    public string OutputDeviceId { get; set; } = "";

    /// <summary>Automatically route Spotify to the route device while FX is active and restore it afterwards.</summary>
    public bool AutoRouteSpotify { get; set; } = true;

    /// <summary>WASAPI output latency in milliseconds.</summary>
    public int OutputLatencyMs { get; set; } = 60;

    /// <summary>When launched by the game (--game-pid), exit shortly after the game process ends.</summary>
    public bool ExitWithGame { get; set; } = true;

    /// <summary>Pause Spotify when the game disconnects (crash/exit) while the station was on.</summary>
    public bool PauseOnGameDisconnect { get; set; } = true;

    public bool VerboseLog { get; set; }

    /// <summary>Runtime only (--no-audio): when false the engine never touches devices, sessions or routing.</summary>
    [JsonIgnore]
    public bool AudioEnabled { get; set; } = true;

    /// <summary>FX used for "Desktop FX" from the tray when no game is connected.</summary>
    public FxSettings DesktopFx { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static BridgeSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var s = JsonSerializer.Deserialize<BridgeSettings>(json, JsonOptions);
                if (s is not null)
                {
                    s.Normalize();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"settings.json could not be read, defaults are used ({ex.Message})");
            return new BridgeSettings();
        }

        // First run: write the defaults once so the user can edit them.
        var defaults = new BridgeSettings();
        defaults.Save();
        return defaults;
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"settings.json could not be written ({ex.Message})");
        }
    }

    private void Normalize()
    {
        if (string.IsNullOrWhiteSpace(PipeName)) PipeName = Ipc.Protocol.DefaultPipeName;
        if (SessionMatch is null || SessionMatch.Count == 0) SessionMatch = ["Spotify"];
        if (AudioProcessNames is null || AudioProcessNames.Count == 0) AudioProcessNames = ["Spotify"];
        RouteDeviceKeywords ??= [];
        OutputLatencyMs = Math.Clamp(OutputLatencyMs, 20, 300);
        DesktopFx = (DesktopFx ?? new FxSettings()).Sanitized();
    }
}
