namespace MojaveDesktopRadio.Engine;

public enum PauseMenuMode
{
    KeepPlaying = 0,
    Pause = 1,
    LowerVolume = 2,
}

/// <summary>Behaviour options pushed by the game (MCM "Behaviour" page).</summary>
public sealed record GameBehaviour
{
    /// <summary>Pause Spotify when the station is switched off / another station is chosen. Otherwise it is muted.</summary>
    public bool AutoPause { get; init; } = true;

    /// <summary>Start playback when the station is selected and Spotify is paused.</summary>
    public bool AutoPlay { get; init; } = true;

    public PauseMenuMode PauseMenu { get; init; } = PauseMenuMode.KeepPlaying;
}

public enum RouteState
{
    /// <summary>No processing requested (Clean / FX off / station off).</summary>
    Off,

    /// <summary>Routing requested, Spotify's stream has not moved to the route device yet (original still audible, no double audio).</summary>
    Pending,

    /// <summary>Spotify is on the route device and processed audio is playing.</summary>
    Routed,

    /// <summary>The user routed Spotify to the route device themselves; we pass it through.</summary>
    Manual,

    /// <summary>FX requested but impossible (no virtual device, routing API failed, ...). Spotify plays normally.</summary>
    Unavailable,
}

public sealed record EngineStatus(
    Media.MediaSnapshot Media,
    bool GameConnected,
    string? GameClient,
    bool StationActive,
    Fx.FxSettings Fx,
    RouteState Route,
    string RouteDetail,
    string? RouteDevice,
    string? OutputDevice,
    bool DesktopFx)
{
    public string RouteKey => Route.ToString().ToLowerInvariant();
}
