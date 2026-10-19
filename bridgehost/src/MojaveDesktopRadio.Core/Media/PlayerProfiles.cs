namespace MojaveDesktopRadio.Media;

/// <summary>
/// A desktop music player the station can control.
/// <para><c>SessionMatch</c>: a Windows media session is used only if its SourceAppUserModelId contains one of
/// these (case-insensitive). <c>ProcessNames</c>: executables (without .exe) whose audio is faded / routed for FX.</para>
/// </summary>
public sealed record PlayerProfile(
    string Key,
    string DisplayName,
    IReadOnlyList<string> SessionMatch,
    IReadOnlyList<string> ProcessNames,
    bool Tested);

/// <summary>
/// Built-in players. Spotify is the default and the only one tested by the author; the others work
/// only if the player publishes a Windows media session (it then shows up in the Windows volume /
/// media overlay with title and buttons).
/// </summary>
public static class PlayerProfiles
{
    public const string SpotifyKey = "spotify";
    public const string CustomKey = "custom";

    public static readonly PlayerProfile Spotify = new(SpotifyKey, "Spotify", ["Spotify"], ["Spotify"], Tested: true);

    public static readonly IReadOnlyList<PlayerProfile> BuiltIn =
    [
        Spotify,
        new("foobar2000", "foobar2000", ["foobar2000"], ["foobar2000"], Tested: false),
        new("musicbee", "MusicBee", ["MusicBee"], ["MusicBee"], Tested: false),
        new("aimp", "AIMP", ["AIMP"], ["AIMP"], Tested: false),
        new("wacup", "WACUP / Winamp", ["WACUP", "Winamp"], ["wacup", "winamp"], Tested: false),
    ];

    /// <summary>
    /// Index order of the MCM option "Desktop Player" (mod/MCM/MojaveDesktopRadio.json) and of the
    /// plugin's "player" key — keep all three in the same order.
    /// </summary>
    public static readonly IReadOnlyList<string> McmOrder = ["spotify", "foobar2000", "musicbee", "aimp", "wacup", "custom"];

    public static PlayerProfile? FindBuiltIn(string? key) =>
        BuiltIn.FirstOrDefault(p => string.Equals(p.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsKnownKey(string? key) =>
        FindBuiltIn(key) is not null || string.Equals(key?.Trim(), CustomKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>A player defined by the user in settings.json (sessionMatch / audioProcessNames).</summary>
    public static PlayerProfile Custom(IReadOnlyList<string> sessionMatch, IReadOnlyList<string> processNames, string? name) =>
        new(CustomKey, string.IsNullOrWhiteSpace(name) ? processNames.FirstOrDefault() ?? "Custom player" : name.Trim(),
            sessionMatch, processNames, Tested: false);
}
