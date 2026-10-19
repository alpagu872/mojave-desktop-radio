using System.Text.Json;
using System.Text.Json.Serialization;
using MojaveDesktopRadio.Fx;
using MojaveDesktopRadio.Media;

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

    /// <summary>
    /// Desktop player: "spotify" (default), "foobar2000", "musicbee", "aimp", "wacup" or "custom".
    /// While the game is connected its MCM option "Desktop Player" decides (and is saved here).
    /// Empty = "spotify", or "custom" for settings files from before 0.2.2 whose lists below were edited.
    /// </summary>
    public string Player { get; set; } = "";

    /// <summary>Name shown for the "custom" player (empty = first process name).</summary>
    public string CustomPlayerName { get; set; } = "";

    /// <summary>"custom" player only: a GSMTC session is accepted only if its SourceAppUserModelId contains one of these (case-insensitive).</summary>
    public List<string> SessionMatch { get; set; } = ["Spotify"];

    /// <summary>"custom" player only: process names (without .exe) whose audio is faded / routed for FX.</summary>
    public List<string> AudioProcessNames { get; set; } = ["Spotify"];

    /// <summary>Explicit route endpoint id (MMDevice id). Empty = pick the first match of RouteDeviceKeywords.</summary>
    public string RouteDeviceId { get; set; } = "";

    /// <summary>Inaudible render endpoints that may be used as the FX route, in priority order.</summary>
    public List<string> RouteDeviceKeywords { get; set; } = ["CABLE Input", "VB-Audio Virtual", "Steam Streaming Speakers"];

    /// <summary>Explicit output endpoint id. Empty = follow the Windows default multimedia device.</summary>
    public string OutputDeviceId { get; set; } = "";

    /// <summary>Automatically route the player to the route device while FX is active and restore it afterwards (name kept for old settings files).</summary>
    public bool AutoRouteSpotify { get; set; } = true;

    /// <summary>WASAPI output latency in milliseconds.</summary>
    public int OutputLatencyMs { get; set; } = 60;

    /// <summary>When launched by the game (--game-pid), exit shortly after the game process ends.</summary>
    public bool ExitWithGame { get; set; } = true;

    /// <summary>Pause the player when the game disconnects (crash/exit) while the station was on.</summary>
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
        defaults.Normalize();
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

    /// <summary>The player selected by <paramref name="key"/> (default: <see cref="Player"/>). Unknown keys fall back to Spotify.</summary>
    public PlayerProfile ResolvePlayer(string? key = null)
    {
        key = (key ?? Player)?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key)) key = LegacyPlayerKey();
        if (key == PlayerProfiles.CustomKey)
            return PlayerProfiles.Custom(Clean(SessionMatch), Clean(AudioProcessNames), CustomPlayerName);
        return PlayerProfiles.FindBuiltIn(key) ?? PlayerProfiles.Spotify;
    }

    // Before 0.2.2 other players were configured only through the two lists.
    private string LegacyPlayerKey() =>
        IsSpotifyList(SessionMatch) && IsSpotifyList(AudioProcessNames) ? PlayerProfiles.SpotifyKey : PlayerProfiles.CustomKey;

    private static bool IsSpotifyList(List<string>? l) =>
        l is null || l.Count == 0 || l.All(x => string.IsNullOrWhiteSpace(x) || x.Trim().Equals("Spotify", StringComparison.OrdinalIgnoreCase));

    private static List<string> Clean(List<string>? l)
    {
        var r = (l ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
        return r.Count > 0 ? r : ["Spotify"];
    }

    private void Normalize()
    {
        if (string.IsNullOrWhiteSpace(PipeName)) PipeName = Ipc.Protocol.DefaultPipeName;
        if (string.IsNullOrWhiteSpace(Player)) Player = LegacyPlayerKey();
        Player = Player.Trim().ToLowerInvariant();
        if (!PlayerProfiles.IsKnownKey(Player))
        {
            Log.Warn($"settings.json: unknown player '{Player}', using Spotify");
            Player = PlayerProfiles.SpotifyKey;
        }

        CustomPlayerName ??= "";
        SessionMatch = Clean(SessionMatch);
        AudioProcessNames = Clean(AudioProcessNames);
        RouteDeviceKeywords ??= [];
        OutputLatencyMs = Math.Clamp(OutputLatencyMs, 20, 300);
        DesktopFx = (DesktopFx ?? new FxSettings()).Sanitized();
    }
}
