using System.Collections.Concurrent;
using System.Diagnostics;
using MojaveDesktopRadio.Audio;
using MojaveDesktopRadio.Config;
using MojaveDesktopRadio.Fx;
using MojaveDesktopRadio.Ipc;
using MojaveDesktopRadio.Media;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MojaveDesktopRadio.Engine;

/// <summary>
/// The BridgeHost core. A single dedicated MTA thread owns all state ("actor"): IPC messages,
/// media polling, device notifications and tray actions are all posted to it, so there are no
/// data races and no COM apartment problems. The game is never blocked by anything here —
/// the NVSE plugin talks to it asynchronously through the pipe.
/// </summary>
public sealed class BridgeEngine : IDisposable
{
    private const int TickMs = 750;
    private const string GameClientName = "MDR-NVSE";

    private readonly BridgeSettings _settings;
    private readonly int? _gamePid;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();

    private AudioDevices _devices = null!;
    private SpotifyMediaSource _media = null!;
    private AppAudioRouting _routing = null!;
    private SessionVolumeFader _fader = null!;
    private DeviceWatcher? _watcher;
    private PipeServer? _pipe;
    private FxPipeline? _pipeline;
    private string? _pipelineRouteId;
    private Timer? _timer;

    private MediaSnapshot _snapshot = MediaSnapshot.NotRunningSnapshot;
    private FxSettings _gameFx = new();
    private GameBehaviour _behaviour = new();
    private bool _stationActive;
    private bool _holdMuted;
    private bool _pausedByMenu;
    private bool _ducked;
    private bool _desktopFx;
    private bool _weRouted;
    private RouteState _route = RouteState.Off;
    private string _routeDetail = "";
    private string? _routeDeviceName;
    private int _tick;
    private long _seq;
    private string? _lastStateJson;
    private int _pipelineFailures;
    private DateTime _nextPipelineAttempt = DateTime.MinValue;
    private DateTime _routeRetryAt = DateTime.MinValue;
    private DateTime _manualDetectAfter = DateTime.MinValue;
    private bool _exitRequested;
    private bool _disposed;
    private int _disposeCalled;

    public BridgeEngine(BridgeSettings settings, int? launchedByGamePid)
    {
        _settings = settings;
        _gamePid = launchedByGamePid;
        _thread = new Thread(Run) { IsBackground = true, Name = "MDR engine" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    /// <summary>Raised on the engine thread whenever the visible status changes.</summary>
    public event Action<EngineStatus>? StatusChanged;

    /// <summary>Raised when the app should exit (the game that launched us has ended).</summary>
    public event Action? ExitRequested;

    public BridgeSettings Settings => _settings;

    public void Start()
    {
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(10));
    }

    public void Post(Action action)
    {
        if (_queue.IsAddingCompleted) return;
        try { _queue.Add(action); } catch (InvalidOperationException) { }
    }

    /// <summary>Runs a function on the engine thread and waits for its result (tray / CLI use).</summary>
    public T Invoke<T>(Func<T> func, int timeoutMs = 8000)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        if (!tcs.Task.Wait(timeoutMs)) throw new TimeoutException("engine did not respond");
        return tcs.Task.Result;
    }

    // ---- public operations (thread-safe: they post to the engine) -------------------------------

    public void SetDesktopFx(bool enabled) => Post(() =>
    {
        _desktopFx = enabled;
        Log.Info($"desktop FX {(enabled ? "on" : "off")}");
        ApplyAudioPath();
        PublishState(force: true);
    });

    public void SetDesktopPreset(RadioPreset preset) => Post(() =>
    {
        _settings.DesktopFx = _settings.DesktopFx with { Preset = preset, Enabled = true };
        _settings.Save();
        ApplyAudioPath();
        PublishState(force: true);
    });

    public void SendMediaCommand(MediaCommand command) => Post(() => RunMediaCommand(command));

    public EngineStatus GetStatus() => Invoke(BuildStatus);

    // ---- engine thread ------------------------------------------------------------------------

    private void Run()
    {
        try
        {
            _devices = new AudioDevices();
            _media = new SpotifyMediaSource(_settings.SessionMatch, _settings.AudioProcessNames);
            _routing = new AppAudioRouting { Enabled = _settings.AudioEnabled };
            _fader = new SessionVolumeFader(_devices, _settings.AudioProcessNames, _settings.AudioEnabled);

            // Crash recovery first: never leave Spotify routed / muted from a previous run.
            _routing.RecoverIfNeeded();
            _fader.RecoverIfNeeded();

            _watcher = new DeviceWatcher(() => Post(OnDevicesChanged));
            _devices.Enumerator.RegisterEndpointNotificationCallback(_watcher);

            _pipe = new PipeServer(_settings.PipeName);
            _pipe.Connected += s => Post(() => OnConnected(s));
            _pipe.LineReceived += (s, line) => Post(() => OnLine(s, line));
            _pipe.Disconnected += s => Post(() => OnDisconnected(s));
            _pipe.Start();

            _timer = new Timer(_ => Post(Tick), null, 0, TickMs);
        }
        catch (Exception ex)
        {
            Log.Error("engine start failed", ex);
        }
        finally
        {
            _ready.Set();
        }

        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error("engine action failed", ex);
            }
        }
    }

    private bool GameConnected => _pipe?.Sessions.Any(s => s.IsGame) == true;

    private FxSettings ActiveFx => GameConnected ? _gameFx : _settings.DesktopFx;

    private void Tick()
    {
        if (_disposed) return;
        _tick++;

        RefreshMedia();

        if (_tick % 3 == 0 && _pipe is not null)
        {
            foreach (var s in _pipe.Sessions)
            {
                if (s.MillisecondsSinceActivity > 10_000)
                {
                    Log.Warn($"client {s.Id} ({s.ClientName}) timed out");
                    s.Dispose();
                }
            }

            _pipe.Broadcast(Protocol.Build("ping"));
        }

        if (_tick % 4 == 0)
        {
            if (!_weRouted && _routing.HasPendingRestore) _routing.RecoverIfNeeded();
            if (!_fader.IsAltered) _fader.RecoverIfNeeded();
            if (_pipeline is not null || _stationActive || _desktopFx || _route != RouteState.Off)
                ApplyAudioPath();
            else
                DetectManualRoute();

            // Exit once the game that started us is gone — but not while another game instance is
            // connected (quick restart: the new game took over this BridgeHost).
            if (_gamePid is { } pid && _settings.ExitWithGame && !_exitRequested && !GameConnected && !IsProcessAlive(pid))
            {
                _exitRequested = true;
                Log.Info("the game process has exited; shutting down");
                ExitRequested?.Invoke();
            }
        }
    }

    private void RefreshMedia()
    {
        var snap = _media.Refresh();
        if (snap != _snapshot)
        {
            var wasRunning = _snapshot.Status != PlayerStatus.NotRunning;
            _snapshot = snap;
            if (wasRunning && snap.Status == PlayerStatus.NotRunning)
                Log.Info("Spotify closed");
            else if (!wasRunning && snap.Status != PlayerStatus.NotRunning)
                Log.Info("Spotify detected");
            PublishState();
        }
    }

    // ---- IPC ---------------------------------------------------------------------------------

    private void OnConnected(PipeSession session)
    {
        Log.Info($"client {session.Id} connected");
        _ = session.SendAsync(Protocol.Build("welcome", w =>
        {
            w.WriteString("server", Protocol.ServerName);
            w.WriteString("serverVersion", Protocol.ServerVersion);
            w.WriteNumber("protocol", Protocol.Version);
        }));
    }

    private void OnLine(PipeSession session, string line)
    {
        if (!InboundMessage.TryParse(line, out var msg, out var error) || msg is null)
        {
            _ = session.SendAsync(Protocol.Error(null, "bad_message", error ?? "unreadable"));
            return;
        }

        if (msg.Version != Protocol.Version)
        {
            _ = session.SendAsync(Protocol.Error(msg.GetLong("id"), "unsupported_version",
                $"protocol v{msg.Version} is not supported, expected v{Protocol.Version}"));
            return;
        }

        switch (msg.Type)
        {
            case "hello":
                session.ClientName = msg.GetString("client") ?? "?";
                session.ClientVersion = msg.GetString("version") ?? "?";
                session.GamePid = (int?)msg.GetLong("gamePid");
                session.IsGame = string.Equals(session.ClientName, GameClientName, StringComparison.OrdinalIgnoreCase);
                Log.Info($"client {session.Id}: {session.ClientName} {session.ClientVersion}{(session.IsGame ? " (game)" : "")}");
                _lastStateJson = null;
                PublishState(force: true);
                break;

            case "ping":
                _ = session.SendAsync(Protocol.Build("pong"));
                break;

            case "pong":
                break;

            case "status":
                _ = session.SendAsync(BuildStateJson());
                break;

            case "cmd":
                HandleCommand(session, msg);
                break;

            case "station":
                if (msg.GetBool("active") is { } active) SetStation(active);
                break;

            case "gamepause":
                if (msg.GetBool("paused") is { } paused) OnMenuPause(paused);
                break;

            case "config":
                ApplyGameConfig(msg);
                break;

            case "bye":
                Log.Info($"client {session.Id} said goodbye");
                if (session.IsGame) { session.IsGame = false; OnGameGone(); }
                break;

            default:
                _ = session.SendAsync(Protocol.Error(msg.GetLong("id"), "unknown_type", $"unknown message type '{msg.Type}'"));
                break;
        }
    }

    private void OnDisconnected(PipeSession session)
    {
        Log.Info($"client {session.Id} ({session.ClientName}) disconnected");
        if (session.IsGame) OnGameGone();
    }

    private void OnGameGone()
    {
        if (GameConnected) return; // another game session is still there

        if (_stationActive && _settings.PauseOnGameDisconnect && _snapshot.IsPlaying)
        {
            FadeOutCurrent(_gameFx.FadeMs);
            Execute(MediaCommand.Pause);
        }

        _stationActive = false;
        _holdMuted = false;
        _pausedByMenu = false;
        _ducked = false;
        _fader.Restore();
        ApplyAudioPath();
        PublishState(force: true);
    }

    private void HandleCommand(PipeSession session, InboundMessage msg)
    {
        var id = msg.GetLong("id") ?? 0;
        var action = (msg.GetString("action") ?? "").Trim().ToLowerInvariant();
        MediaCommand? media = action switch
        {
            "play" => MediaCommand.Play,
            "pause" => MediaCommand.Pause,
            "toggle" or "play_pause" => MediaCommand.Toggle,
            "next" or "next_track" => MediaCommand.Next,
            "prev" or "previous" or "previous_track" => MediaCommand.Previous,
            _ => null,
        };

        if (media is { } m)
        {
            var outcome = RunMediaCommand(m);
            _ = session.SendAsync(outcome == CommandOutcome.Ok
                ? Protocol.Ack(id, "ok")
                : Protocol.Error(id, OutcomeCode(outcome), $"{action}: {outcome}"));
            return;
        }

        switch (action)
        {
            case "refresh":
                _lastStateJson = null;
                RefreshMedia();
                PublishState(force: true);
                _ = session.SendAsync(Protocol.Ack(id, "ok"));
                break;
            case "desktop_fx_on":
            case "desktop_fx_off":
                _desktopFx = action.EndsWith("on");
                ApplyAudioPath();
                PublishState(force: true);
                _ = session.SendAsync(Protocol.Ack(id, "ok"));
                break;
            default:
                _ = session.SendAsync(Protocol.Error(id, "unknown_action", $"unknown action '{action}'"));
                break;
        }
    }

    private CommandOutcome RunMediaCommand(MediaCommand command)
    {
        var outcome = Execute(command);
        // GSMTC reflects the change after a short delay; poll again soon.
        _ = Task.Delay(350).ContinueWith(_ => Post(RefreshMedia));
        _ = Task.Delay(1200).ContinueWith(_ => Post(RefreshMedia));
        return outcome;
    }

    private CommandOutcome Execute(MediaCommand command)
    {
        var outcome = _media.Execute(command);
        Log.Info($"media {command}: {outcome}");
        return outcome;
    }

    private static string OutcomeCode(CommandOutcome o) => o switch
    {
        CommandOutcome.NotRunning => "spotify_not_running",
        CommandOutcome.NoSession => "spotify_no_session",
        CommandOutcome.Timeout => "timeout",
        _ => "rejected",
    };

    private void ApplyGameConfig(InboundMessage msg)
    {
        var fx = _gameFx;
        if (msg.GetBool("fxEnabled") is { } en) fx = fx with { Enabled = en };
        if (FxSettings.TryParsePreset(msg.GetString("preset"), out var p)) fx = fx with { Preset = p };
        if (msg.GetDouble("volume") is { } vol) fx = fx with { Volume = (float)vol };
        if (msg.GetDouble("static") is { } st) fx = fx with { StaticIntensity = (float)st };
        if (msg.GetDouble("saturation") is { } sat) fx = fx with { Saturation = (float)sat };
        if (msg.GetBool("customTone") is { } ct) fx = fx with { CustomTone = ct };
        if (msg.GetDouble("lowPassHz") is { } lp) fx = fx with { LowPassHz = (float)lp };
        if (msg.GetDouble("highPassHz") is { } hp) fx = fx with { HighPassHz = (float)hp };
        if (msg.GetDouble("stereoWidth") is { } sw) fx = fx with { StereoWidth = (float)sw };
        if (msg.GetDouble("fadeMs") is { } fade) fx = fx with { FadeMs = (int)fade };
        if (msg.GetBool("tuningFx") is { } tf) fx = fx with { TuningFx = tf };
        _gameFx = fx.Sanitized();

        var b = _behaviour;
        if (msg.GetBool("autoPause") is { } ap) b = b with { AutoPause = ap };
        if (msg.GetBool("autoPlay") is { } apl) b = b with { AutoPlay = apl };
        if (msg.GetDouble("pauseMenu") is { } pm) b = b with { PauseMenu = (PauseMenuMode)Math.Clamp((int)pm, 0, 2) };
        _behaviour = b;

        Log.Info($"game config: fx={_gameFx.Enabled} preset={_gameFx.Preset} vol={_gameFx.Volume:0.00} static={_gameFx.StaticIntensity:0.00} " +
                 $"sat={_gameFx.Saturation:0.00} custom={_gameFx.CustomTone} fade={_gameFx.FadeMs} autoPause={b.AutoPause} autoPlay={b.AutoPlay} pauseMenu={b.PauseMenu}");

        ApplyAudioPath();
        PublishState(force: true);
    }

    // ---- station logic -----------------------------------------------------------------------

    private void SetStation(bool active)
    {
        if (active == _stationActive) return;
        Log.Info($"station {(active ? "ON" : "OFF")}");
        var fx = ActiveFx;

        if (active)
        {
            _stationActive = true;
            _holdMuted = false;
            _routeRetryAt = DateTime.MinValue; // re-tuning the station retries a failed route immediately
            ApplyAudioPath();
            var viaPipeline = _pipeline is not null;

            if (fx.TuningFx && fx.WantsProcessing)
            {
                if (viaPipeline) _pipeline!.TriggerTuning();
                else if (_settings.AudioEnabled && _devices.FindOutputDevice(_settings, null) is { } outDev) TuningBlip.Play(outDev);
            }

            if (_behaviour.AutoPlay && !_snapshot.IsPlaying && _snapshot.Status != PlayerStatus.NotRunning)
            {
                if (!viaPipeline) _fader.FadeTo(0f, 0);
                Execute(MediaCommand.Play);
                WaitForPlaying(800);
            }

            if (!viaPipeline && _fader.IsAltered)
                _fader.FadeTo(1f, fx.FadeMs);
        }
        else
        {
            _stationActive = false;
            _pausedByMenu = false;
            _ducked = false;

            if (_behaviour.AutoPause)
            {
                if (_snapshot.IsPlaying)
                {
                    FadeOutCurrent(fx.FadeMs);
                    Execute(MediaCommand.Pause);
                    Thread.Sleep(150); // let the pause land before the volume comes back
                }

                _fader.Restore();
                _holdMuted = false;
            }
            else
            {
                // keep Spotify running but silent
                _holdMuted = true;
                if (_pipeline is null) _fader.FadeTo(0f, fx.FadeMs);
            }

            ApplyAudioPath();
        }

        RefreshMedia();
        PublishState(force: true);
    }

    private void OnMenuPause(bool paused)
    {
        if (!_stationActive) return;
        switch (_behaviour.PauseMenu)
        {
            case PauseMenuMode.Pause:
                if (paused && _snapshot.IsPlaying)
                {
                    Execute(MediaCommand.Pause);
                    _pausedByMenu = true;
                }
                else if (!paused && _pausedByMenu)
                {
                    Execute(MediaCommand.Play);
                    _pausedByMenu = false;
                }

                break;

            case PauseMenuMode.LowerVolume:
                _ducked = paused;
                if (_pipeline is not null) ApplyAudioPath();
                else _fader.FadeTo(paused ? 0.3f : 1f, 150);
                break;
        }

        RefreshMedia();
    }

    private void FadeOutCurrent(int fadeMs)
    {
        if (_pipeline is not null)
        {
            _pipeline.FadeTo(0f, fadeMs);
            Thread.Sleep(fadeMs + 30);
        }
        else
        {
            _fader.FadeTo(0f, fadeMs);
        }
    }

    private void WaitForPlaying(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(100);
            _snapshot = _media.Refresh();
            if (_snapshot.IsPlaying) return;
        }
    }

    // ---- audio path ----------------------------------------------------------------------------

    private float DesiredGain(bool manualPassThrough)
    {
        var fx = ActiveFx;
        if (_stationActive) return fx.Volume * (_ducked ? 0.3f : 1f);
        if (_holdMuted) return 0f;
        if (_desktopFx) return _settings.DesktopFx.Volume;
        return manualPassThrough ? 1f : 0f;
    }

    /// <summary>
    /// Brings the audio path in line with the current state. Idempotent; called after every state
    /// change and periodically. The invariant: Spotify is only ever routed away from the user's
    /// device while a pipeline is running that plays it back.
    /// </summary>
    private void ApplyAudioPath()
    {
        if (!_settings.AudioEnabled)
        {
            SetRoute(RouteState.Off, "audio disabled (--no-audio)");
            return;
        }

        var fx = ActiveFx;
        var processing = fx.WantsProcessing && (_stationActive || _desktopFx || (_holdMuted && _weRouted));

        var output = _devices.FindOutputDevice(_settings, null);
        var routeDev = _devices.FindRouteDevice(_settings, output?.ID);
        _routeDeviceName = routeDev?.FriendlyName;
        var sessions = _devices.FindSessions(_settings.AudioProcessNames);
        var onRoute = routeDev is not null && sessions.Any(s => s.DeviceId == routeDev.ID);
        var activeElsewhere = routeDev is not null && sessions.Any(s => s.DeviceId != routeDev.ID && s.State == AudioSessionState.AudioSessionStateActive);
        // "Manual" = the user routed Spotify to the route device themselves. Right after we restore our own
        // routing the old session lingers on the route device for a moment, so it must not count as manual.
        var manual = !_weRouted && DateTime.UtcNow >= _manualDetectAfter && routeDev is not null &&
                     sessions.Any(s => s.DeviceId == routeDev.ID && s.State == AudioSessionState.AudioSessionStateActive);

        if (!processing && !manual)
        {
            StopPipeline();
            RestoreRouting();
            SetRoute(RouteState.Off, "");
            return;
        }

        if (routeDev is null || output is null)
        {
            StopPipeline();
            RestoreRouting();
            SetRoute(processing ? RouteState.Unavailable : RouteState.Off,
                routeDev is null ? "no inaudible output device found (install VB-CABLE or check settings.json)" : "no output device");
            return;
        }

        // We must route Spotify ourselves unless we already did or the user did it by hand.
        var needRouting = processing && !_weRouted && !manual;

        // A routing attempt failed recently: stay Clean on the normal device for a while instead of retrying every tick.
        if (needRouting && DateTime.UtcNow < _routeRetryAt)
        {
            StopPipeline();
            return;
        }

        // Rebuild when the devices changed (e.g. headset unplugged → new default output).
        if (_pipeline is not null && (_pipelineRouteId != routeDev.ID || _pipeline.OutputDeviceId != output.ID))
            StopPipeline();

        if (_pipeline is null)
        {
            if (DateTime.UtcNow < _nextPipelineAttempt) return;
            try
            {
                _pipeline = new FxPipeline(routeDev, output, processing ? fx : fx with { Enabled = false }, _settings.OutputLatencyMs, 0f);
                _pipelineRouteId = routeDev.ID;
                _pipeline.Faulted += reason => Post(() => OnPipelineFault(reason));
                _pipeline.Start();
                _pipelineFailures = 0;
            }
            catch (Exception ex)
            {
                _pipeline?.Dispose();
                _pipeline = null;
                _pipelineFailures++;
                _nextPipelineAttempt = DateTime.UtcNow.AddSeconds(Math.Min(30, 2 * _pipelineFailures));
                Log.Error("FX pipeline could not start", ex);
                RestoreRouting();
                SetRoute(RouteState.Unavailable, $"audio device error: {ex.Message}");
                return;
            }
        }

        _pipeline.ApplySettings(processing ? fx : fx with { Enabled = false });

        if (needRouting)
        {
            if (!_settings.AutoRouteSpotify)
            {
                SetRoute(RouteState.Unavailable, $"route Spotify to '{routeDev.FriendlyName}' in Windows sound settings");
            }
            else if (!_media.IsProcessRunning())
            {
                SetRoute(RouteState.Pending, "waiting for Spotify");
            }
            else if (_routing.Route(_settings.AudioProcessNames, routeDev.ID))
            {
                _weRouted = true;
                Log.Info($"Spotify routed to '{routeDev.FriendlyName}'");
                SetRoute(RouteState.Pending, routeDev.FriendlyName);
            }
            else
            {
                _routeRetryAt = DateTime.UtcNow.AddSeconds(30);
                StopPipeline();
                SetRoute(RouteState.Unavailable, $"automatic routing failed: {_routing.LastError}");
                return;
            }
        }
        else
        {
            var state = manual ? RouteState.Manual
                : onRoute && !activeElsewhere ? RouteState.Routed
                : RouteState.Pending; // routed by us, stream not moved yet (original still audible, never double)
            SetRoute(state, routeDev.FriendlyName);
        }

        var gain = DesiredGain(manual);
        if (Math.Abs(_pipeline.TargetGain - gain) > 0.001f)
            _pipeline.FadeTo(gain, fx.FadeMs);
    }

    /// <summary>When idle, notice that the user routed Spotify to the route device by hand (so we must pass it through).</summary>
    private void DetectManualRoute()
    {
        if (!_settings.AudioEnabled) return;
        var output = _devices.FindOutputDevice(_settings, null);
        var routeDev = _devices.FindRouteDevice(_settings, output?.ID);
        if (routeDev is null || DateTime.UtcNow < _manualDetectAfter) return;
        if (_devices.FindSessions(_settings.AudioProcessNames)
            .Any(s => s.DeviceId == routeDev.ID && s.State == AudioSessionState.AudioSessionStateActive))
            ApplyAudioPath();
    }

    private void RestoreRouting()
    {
        if (!_weRouted) return;
        if (_routing.Restore(_settings.AudioProcessNames) || !_media.IsProcessRunning())
        {
            // when Spotify is not running the marker stays and the restore is retried in Tick
            _weRouted = false;
            _manualDetectAfter = DateTime.UtcNow.AddSeconds(10);
            Log.Info("Spotify routing restored");
        }
    }

    private void StopPipeline()
    {
        if (_pipeline is null) return;
        _pipeline.Dispose();
        _pipeline = null;
        _pipelineRouteId = null;
    }

    private void OnPipelineFault(string reason)
    {
        Log.Warn($"FX pipeline fault: {reason}");
        StopPipeline();
        _pipelineFailures++;
        _nextPipelineAttempt = DateTime.UtcNow.AddSeconds(Math.Min(30, 1 + _pipelineFailures));
        ApplyAudioPath();
        PublishState(force: true);
    }

    private void OnDevicesChanged()
    {
        Log.Info("audio devices changed");
        _devices.InvalidateCache();
        if (_pipeline is not null || _stationActive || _desktopFx)
        {
            StopPipeline();
            _nextPipelineAttempt = DateTime.MinValue;
            ApplyAudioPath();
        }

        PublishState(force: true);
    }

    private void SetRoute(RouteState state, string detail)
    {
        if (state == _route && detail == _routeDetail) return;
        _route = state;
        _routeDetail = detail;
        Log.Info($"route: {state}{(detail.Length > 0 ? $" ({detail})" : "")}");
        PublishState();
    }

    // ---- state publication ---------------------------------------------------------------------

    private EngineStatus BuildStatus() => new(
        _snapshot,
        GameConnected,
        _pipe?.Sessions.FirstOrDefault(s => s.IsGame)?.ClientVersion,
        _stationActive,
        ActiveFx,
        _route,
        _routeDetail,
        _routeDeviceName,
        _pipeline?.OutputDeviceName,
        _desktopFx);

    private string BuildStateJson()
    {
        var fx = ActiveFx;
        var fxActive = _pipeline is not null && fx.WantsProcessing && _route == RouteState.Routed || _route == RouteState.Manual && fx.WantsProcessing;
        return Protocol.Build("state", w =>
        {
            w.WriteNumber("seq", ++_seq);
            w.WriteString("player", _snapshot.StatusKey);
            w.WriteBoolean("playing", _snapshot.IsPlaying);
            w.WriteString("artist", _snapshot.Artist);
            w.WriteString("title", _snapshot.Title);
            w.WriteString("album", _snapshot.Album);
            w.WriteBoolean("canNext", _snapshot.CanNext);
            w.WriteBoolean("canPrevious", _snapshot.CanPrevious);
            w.WriteString("fx", FxSettings.DisplayKey(fx.EffectivePreset));
            w.WriteString("fxRequested", FxSettings.DisplayKey(fx.Preset));
            w.WriteBoolean("fxEnabled", fx.Enabled);
            w.WriteBoolean("fxActive", fxActive);
            w.WriteString("route", _route.ToString().ToLowerInvariant());
            w.WriteString("routeDetail", _routeDetail);
            w.WriteBoolean("stationActive", _stationActive);
        });
    }

    private void PublishState(bool force = false)
    {
        var status = BuildStatus();
        try { StatusChanged?.Invoke(status); } catch (Exception ex) { Log.Debug($"status listener failed: {ex.Message}"); }

        if (_pipe is null) return;
        var json = BuildStateJson();
        // compare without the sequence number
        var key = json[(json.IndexOf(",\"player\"", StringComparison.Ordinal) is var i and > 0 ? i : 0)..];
        if (!force && key == _lastStateJson) return;
        _lastStateJson = key;
        _pipe.Broadcast(json);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    // ---- shutdown -----------------------------------------------------------------------------

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeCalled, 1) != 0) return;
        var done = new ManualResetEventSlim();
        Post(() =>
        {
            _disposed = true;
            try
            {
                _timer?.Dispose();
                StopPipeline();
                RestoreRouting();
                _fader.Restore();
                if (_watcher is not null)
                {
                    try { _devices.Enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
                }

                _pipe?.DisposeAsync().AsTask().Wait(2000);
                _devices.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("shutdown cleanup failed", ex);
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait(6000);
        _queue.CompleteAdding();
        _thread.Join(2000);
        Log.Info("engine stopped");
    }

    /// <summary>Forwards Windows endpoint notifications (called on a COM thread) to the engine.</summary>
    private sealed class DeviceWatcher(Action onChange) : IMMNotificationClient
    {
        private long _lastTicks;

        private void Changed()
        {
            // debounce bursts (one unplug fires several notifications)
            var now = Environment.TickCount64;
            if (now - Interlocked.Exchange(ref _lastTicks, now) < 300) return;
            _ = Task.Delay(400).ContinueWith(_ => onChange());
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Changed();
        public void OnDeviceAdded(string pwstrDeviceId) => Changed();
        public void OnDeviceRemoved(string deviceId) => Changed();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) Changed();
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
