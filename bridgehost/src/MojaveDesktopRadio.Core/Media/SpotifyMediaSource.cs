using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Media.Control;

namespace MojaveDesktopRadio.Media;

public enum PlayerStatus
{
    /// <summary>Spotify.exe is not running.</summary>
    NotRunning,

    /// <summary>Spotify runs but has not registered a media session yet (nothing played since launch).</summary>
    NoSession,
    Stopped,
    Paused,
    Playing,
    Changing,
}

public sealed record MediaSnapshot(
    PlayerStatus Status,
    string Artist,
    string Title,
    string Album,
    string? AppId,
    bool CanNext,
    bool CanPrevious)
{
    public static readonly MediaSnapshot NotRunningSnapshot = new(PlayerStatus.NotRunning, "", "", "", null, false, false);

    public bool IsPlaying => Status == PlayerStatus.Playing;
    public bool HasSession => Status is PlayerStatus.Stopped or PlayerStatus.Paused or PlayerStatus.Playing or PlayerStatus.Changing;

    public string StatusKey => Status switch
    {
        PlayerStatus.NotRunning => "not_running",
        PlayerStatus.NoSession => "no_session",
        PlayerStatus.Stopped => "stopped",
        PlayerStatus.Paused => "paused",
        PlayerStatus.Playing => "playing",
        PlayerStatus.Changing => "changing",
        _ => "unknown",
    };
}

public enum MediaCommand { Play, Pause, Toggle, Next, Previous }

public enum CommandOutcome { Ok, NotRunning, NoSession, Rejected, Timeout }

/// <summary>
/// Spotify control through Windows Global System Media Transport Controls.
/// Only sessions whose SourceAppUserModelId contains one of <c>sessionMatch</c> are ever touched;
/// there is deliberately no fallback to the "current" session, so other players are never controlled.
/// All methods are synchronous with short timeouts and must be called from an MTA thread.
/// </summary>
public sealed class SpotifyMediaSource
{
    private const int TimeoutMs = 2000;
    private readonly IReadOnlyList<string> _sessionMatch;
    private readonly IReadOnlyCollection<string> _processNames;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private DateTime _nextManagerAttempt = DateTime.MinValue;

    public SpotifyMediaSource(IReadOnlyList<string> sessionMatch, IReadOnlyCollection<string> processNames)
    {
        _sessionMatch = sessionMatch;
        _processNames = processNames;
    }

    public string? LastError { get; private set; }

    public MediaSnapshot Refresh()
    {
        if (!IsProcessRunning())
            return MediaSnapshot.NotRunningSnapshot;

        var session = FindSession();
        if (session is null)
            return new MediaSnapshot(PlayerStatus.NoSession, "", "", "", null, false, false);

        try
        {
            var info = session.GetPlaybackInfo();
            var status = info.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlayerStatus.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlayerStatus.Paused,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => PlayerStatus.Changing,
                _ => PlayerStatus.Stopped,
            };

            string artist = "", title = "", album = "";
            var props = Await(session.TryGetMediaPropertiesAsync().AsTask());
            if (props is not null)
            {
                artist = props.Artist ?? "";
                title = props.Title ?? "";
                album = props.AlbumTitle ?? "";
            }

            return new MediaSnapshot(status, artist, title, album, session.SourceAppUserModelId,
                info.Controls.IsNextEnabled, info.Controls.IsPreviousEnabled);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _manager = null; // session manager may be stale after Spotify restarts
            return new MediaSnapshot(PlayerStatus.NoSession, "", "", "", null, false, false);
        }
    }

    public CommandOutcome Execute(MediaCommand command)
    {
        if (!IsProcessRunning()) return CommandOutcome.NotRunning;

        var session = FindSession();
        if (session is null)
        {
            // Spotify is open but has not created a media session yet (nothing played since launch).
            // Send the command to Spotify's own window only — never a global media key.
            if (command is MediaCommand.Play or MediaCommand.Toggle && SendAppCommandToSpotifyWindow(AppCommandPlayPause))
                return CommandOutcome.Ok;
            return CommandOutcome.NoSession;
        }

        try
        {
            var op = command switch
            {
                MediaCommand.Play => session.TryPlayAsync(),
                MediaCommand.Pause => session.TryPauseAsync(),
                MediaCommand.Toggle => session.TryTogglePlayPauseAsync(),
                MediaCommand.Next => session.TrySkipNextAsync(),
                MediaCommand.Previous => session.TrySkipPreviousAsync(),
                _ => throw new ArgumentOutOfRangeException(nameof(command)),
            };

            var task = op.AsTask();
            if (!task.Wait(TimeoutMs)) return CommandOutcome.Timeout;
            return task.Result ? CommandOutcome.Ok : CommandOutcome.Rejected;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _manager = null;
            return CommandOutcome.Rejected;
        }
    }

    /// <summary>All GSMTC sessions currently registered (for diagnostics only).</summary>
    public IReadOnlyList<string> ListAllSessionIds()
    {
        var mgr = GetManager();
        if (mgr is null) return [];
        try
        {
            return mgr.GetSessions().Select(s => s.SourceAppUserModelId ?? "?").ToList();
        }
        catch
        {
            return [];
        }
    }

    public bool IsProcessRunning() => _processNames.Any(n =>
    {
        var procs = Process.GetProcessesByName(n);
        var any = procs.Length > 0;
        foreach (var p in procs) p.Dispose();
        return any;
    });

    private GlobalSystemMediaTransportControlsSession? FindSession()
    {
        var mgr = GetManager();
        if (mgr is null) return null;
        try
        {
            foreach (var s in mgr.GetSessions())
            {
                var id = s.SourceAppUserModelId ?? "";
                if (_sessionMatch.Any(m => id.Contains(m, StringComparison.OrdinalIgnoreCase)))
                    return s;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _manager = null;
        }

        return null;
    }

    private GlobalSystemMediaTransportControlsSessionManager? GetManager()
    {
        if (_manager is not null) return _manager;
        if (DateTime.UtcNow < _nextManagerAttempt) return null;
        try
        {
            _manager = Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask());
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _manager = null;
        }

        if (_manager is null) _nextManagerAttempt = DateTime.UtcNow.AddSeconds(3);
        return _manager;
    }

    private static T? Await<T>(Task<T> task) where T : class
    {
        try
        {
            return task.Wait(TimeoutMs) ? task.Result : null;
        }
        catch
        {
            return null;
        }
    }

    // ---- window-targeted fallback (no global media keys) ---------------------------------------

    private const int WmAppCommand = 0x0319;
    private const int AppCommandPlayPause = 14;

    private bool SendAppCommandToSpotifyWindow(int appCommand)
    {
        foreach (var name in _processNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    var hwnd = p.MainWindowHandle;
                    if (hwnd == IntPtr.Zero) continue;
                    SendMessageTimeout(hwnd, WmAppCommand, hwnd, (IntPtr)(appCommand << 16), 0x0002 /*SMTO_ABORTIFHUNG*/, 500, out _);
                    return true;
                }
            }
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, int flags, int timeout, out IntPtr result);
}
