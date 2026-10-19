using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using MojaveDesktopRadio.Audio;
using MojaveDesktopRadio.Config;
using MojaveDesktopRadio.Dsp;
using MojaveDesktopRadio.Fx;
using MojaveDesktopRadio.Media;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MojaveDesktopRadio.Cli;

/// <summary>
/// mdr — diagnostics for Mojave Desktop Radio. Runs the stage-2/3 checks without the game.
/// </summary>
internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.Init("mdr-cli.log", echoToConsole: false);
        var settings = BridgeSettings.Load();
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        try
        {
            return cmd switch
            {
                "status" => Status(settings),
                "play" => Media(settings, MediaCommand.Play),
                "pause" => Media(settings, MediaCommand.Pause),
                "toggle" => Media(settings, MediaCommand.Toggle),
                "next" => Media(settings, MediaCommand.Next),
                "prev" or "previous" => Media(settings, MediaCommand.Previous),
                "watch" => Watch(settings, args.Length > 1 && int.TryParse(args[1], out var s) ? s : 60),
                "devices" => Devices(settings),
                "dsp-test" => DspTest(),
                "loopback-test" => LoopbackTest(settings),
                "dsp-render" => DspRender(args),
                "pipe-test" => PipeTest(settings, args.Skip(1).ToArray()),
                "route-test" => RouteTest(settings, args.Contains("--yes")),
                "route-probe" => RouteProbe(settings),
                "timing" => Timing(settings),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            mdr — Mojave Desktop Radio diagnostics

              status                 Spotify detection, media sessions, audio devices, routing support
              play|pause|toggle      Send a command to the Spotify session only
              next|prev
              watch [seconds]        Print every state change (close / reopen Spotify to test recovery)
              devices                List render endpoints and the chosen route/output devices
              dsp-test               Offline check of all presets (filters, levels, NaN/clipping)
              loopback-test          Plays a test tone into the route device and captures it back (inaudible)
              dsp-render <in.wav> <out.wav> [preset]   Process one of YOUR local files offline
              pipe-test [--as-game]  Talk to a running BridgeHost through the named pipe
              route-test --yes       Route Spotify to the route device for 5 s, then restore (Spotify must be playing)
            """);
        return 0;
    }

    // ---- stage 2: media -------------------------------------------------------------------------

    private static SpotifyMediaSource NewMedia(BridgeSettings s) => new(s.SessionMatch, s.AudioProcessNames);

    private static int Status(BridgeSettings settings)
    {
        var media = NewMedia(settings);
        Console.WriteLine($"Spotify process running : {media.IsProcessRunning()}");
        var all = media.ListAllSessionIds();
        Console.WriteLine($"GSMTC sessions ({all.Count}): {(all.Count == 0 ? "-" : string.Join(", ", all))}");
        var snap = media.Refresh();
        Console.WriteLine($"Spotify session         : {snap.Status} ({snap.AppId ?? "none"})");
        if (snap.HasSession)
        {
            Console.WriteLine($"  artist : {snap.Artist}");
            Console.WriteLine($"  title  : {snap.Title}");
            Console.WriteLine($"  album  : {snap.Album}");
            Console.WriteLine($"  next/prev enabled: {snap.CanNext}/{snap.CanPrevious}");
        }

        Console.WriteLine();
        Devices(settings);

        using var devices = new AudioDevices();
        var sessions = devices.FindSessions(settings.AudioProcessNames);
        Console.WriteLine($"Spotify audio sessions ({sessions.Count}):");
        foreach (var s in sessions)
            Console.WriteLine($"  pid {s.ProcessId} on '{s.DeviceName}' vol={s.Volume:0.00} muted={s.Muted} state={s.State}");

        var routing = new AppAudioRouting();
        Console.WriteLine($"Automatic per-app routing supported: {routing.IsSupported}{(routing.LastError is { } e ? $" ({e})" : "")}");
        Console.WriteLine($"Pending routing restore marker     : {routing.HasPendingRestore}");
        foreach (var pid in AudioDevices.GetProcessIds(settings.AudioProcessNames).Take(1))
            Console.WriteLine($"Persisted device for Spotify       : {routing.GetRoutedDevice(pid) ?? "Default"}");
        return 0;
    }

    private static int Media(BridgeSettings settings, MediaCommand command)
    {
        var media = NewMedia(settings);
        var outcome = media.Execute(command);
        Console.WriteLine($"{command}: {outcome}");
        Thread.Sleep(900);
        var snap = media.Refresh();
        Console.WriteLine($"now: {snap.Status} — {snap.Artist} – {snap.Title}");
        return outcome == CommandOutcome.Ok ? 0 : 1;
    }

    private static int Watch(BridgeSettings settings, int seconds)
    {
        var media = NewMedia(settings);
        MediaSnapshot? last = null;
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"watching for {seconds}s (Ctrl+C to stop)…");
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var snap = media.Refresh();
            if (snap != last)
            {
                Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] {snap.Status,-10} {snap.Artist} – {snap.Title}");
                last = snap;
            }

            Thread.Sleep(500);
        }

        return 0;
    }

    private static int Devices(BridgeSettings settings)
    {
        using var devices = new AudioDevices();
        Console.WriteLine("Render endpoints:");
        foreach (var d in devices.ListRenderDevices())
            Console.WriteLine($"  {(d.IsDefault ? "*" : " ")} {d.Name}  [{d.SampleRate} Hz, {d.Channels}ch]");
        var output = devices.FindOutputDevice(settings, null);
        var route = devices.FindRouteDevice(settings, output?.ID);
        Console.WriteLine($"Output (what you hear)   : {output?.FriendlyName ?? "NONE"}");
        Console.WriteLine($"FX route (inaudible)     : {route?.FriendlyName ?? "NONE — install VB-CABLE or set routeDeviceId in settings.json"}");
        return 0;
    }

    // ---- stage 3: DSP -----------------------------------------------------------------------------

    private static int DspTest()
    {
        const int rate = 48000;
        var failed = false;
        foreach (var preset in Enum.GetValues<RadioPreset>())
        {
            var fx = new RadioFxProcessor(rate);
            fx.Update(new FxSettings { Preset = preset });
            fx.TriggerTuning();

            // 3 s: pink noise + 100 Hz + 1 kHz + 8 kHz tones, stereo-different
            var frames = rate * 3;
            var buf = new float[frames * 2];
            var rng = new FastRandom(42);
            var pink = new PinkNoise();
            for (var i = 0; i < frames; i++)
            {
                var t = (float)i / rate;
                var common = 0.2f * MathF.Sin(2 * MathF.PI * 100 * t) + 0.2f * MathF.Sin(2 * MathF.PI * 1000 * t) + 0.2f * MathF.Sin(2 * MathF.PI * 8000 * t);
                var n = pink.Next(ref rng) * 0.3f;
                buf[2 * i] = common + n;
                buf[2 * i + 1] = common - n;
            }

            var input = (float[])buf.Clone();
            var sw = Stopwatch.StartNew();
            for (var off = 0; off < buf.Length; off += 960)
                fx.Process(buf.AsSpan(off, Math.Min(960, buf.Length - off)));
            var cpu = sw.Elapsed.TotalMilliseconds / 3000.0 * 100;

            var nan = buf.Any(v => !float.IsFinite(v));
            var peak = buf.Max(Math.Abs);
            var tail = buf.AsSpan(rate * 2); // skip the tuning sweep
            var (lo, mid, hi) = Bands(tail, rate);
            var (ilo, imid, ihi) = Bands(input.AsSpan(rate * 2), rate);
            var identical = preset == RadioPreset.Clean && buf.AsSpan().SequenceEqual(input);

            Console.WriteLine($"{preset,-10} peak={peak:0.000} nan={nan} cpu={cpu:0.00}%  " +
                              $"100Hz {Db(lo / ilo),6:0.0} dB  1kHz {Db(mid / imid),6:0.0} dB  8kHz {Db(hi / ihi),6:0.0} dB" +
                              (preset == RadioPreset.Clean ? $"  bit-identical={identical}" : ""));

            if (nan || peak > 1.0001f) failed = true;
            if (preset == RadioPreset.Clean && !identical) failed = true;
            if (preset != RadioPreset.Clean && (Db(hi / ihi) > -20 || Db(lo / ilo) > -12)) failed = true;
        }

        Console.WriteLine(failed ? "DSP TEST FAILED" : "DSP TEST OK");
        return failed ? 1 : 0;
    }

    /// <summary>Amplitude of the 100 Hz / 1 kHz / 8 kHz components (Goertzel on the left channel).</summary>
    private static (double lo, double mid, double hi) Bands(ReadOnlySpan<float> stereo, int rate) =>
        (Goertzel(stereo, rate, 100), Goertzel(stereo, rate, 1000), Goertzel(stereo, rate, 8000));

    private static double Goertzel(ReadOnlySpan<float> stereo, int rate, double freq)
    {
        var n = stereo.Length / 2;
        var k = 2 * Math.Cos(2 * Math.PI * freq / rate);
        double s1 = 0, s2 = 0;
        for (var i = 0; i < n; i++)
        {
            var x = 0.5 * (stereo[2 * i] + stereo[2 * i + 1]);
            var s0 = x + k * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - k * s1 * s2)) / n;
    }

    private static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-9));

    private static int LoopbackTest(BridgeSettings settings)
    {
        using var devices = new AudioDevices();
        var output = devices.FindOutputDevice(settings, null);
        var route = devices.FindRouteDevice(settings, output?.ID);
        if (route is null)
        {
            Console.WriteLine("no route device — install VB-CABLE or set routeDeviceId");
            return 1;
        }

        Console.WriteLine($"route device: {route.FriendlyName}");
        using var capture = new WasapiLoopbackCapture(route);
        double sumSq = 0;
        long samples = 0;
        var firstSignalMs = -1L;
        var sw = new Stopwatch();
        capture.DataAvailable += (_, e) =>
        {
            var fmt = capture.WaveFormat;
            if (fmt.BitsPerSample != 32) return;
            var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
            foreach (var v in span)
            {
                sumSq += v * v;
                samples++;
                if (firstSignalMs < 0 && Math.Abs(v) > 0.05f) firstSignalMs = sw.ElapsedMilliseconds;
            }
        };
        capture.StartRecording();

        // 1.5 s, 440 Hz at -12 dBFS into the route device (inaudible by design)
        var tone = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2)
        {
            Frequency = 440, Gain = 0.25, Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin,
        }.Take(TimeSpan.FromSeconds(1.5));
        using var player = new WasapiOut(route, AudioClientShareMode.Shared, true, 50);
        player.Init(tone);
        sw.Start();
        player.Play();
        Thread.Sleep(2000);
        capture.StopRecording();

        var rms = samples > 0 ? Math.Sqrt(sumSq / samples) : 0;
        Console.WriteLine($"captured {samples} samples, RMS {Db(rms):0.0} dBFS, first signal after {firstSignalMs} ms");
        var ok = rms > 0.01;
        Console.WriteLine(ok ? "LOOPBACK OK — the route device can be captured" : "LOOPBACK FAILED — nothing captured");
        return ok ? 0 : 1;
    }

    private static int DspRender(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: mdr dsp-render <in.wav> <out.wav> [clean|mojave|wasteland|damaged]");
            return 1;
        }

        var preset = RadioPreset.Mojave;
        if (args.Length > 3 && !FxSettings.TryParsePreset(args[3], out preset))
        {
            Console.WriteLine($"unknown preset '{args[3]}'");
            return 1;
        }

        using var reader = new WaveFileReader(args[1]);
        var src = reader.ToSampleProvider();
        if (src.WaveFormat.Channels == 1) src = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(src);
        if (src.WaveFormat.Channels != 2)
        {
            Console.WriteLine("only mono/stereo files are supported");
            return 1;
        }

        var fx = new RadioFxProcessor(src.WaveFormat.SampleRate);
        fx.Update(new FxSettings { Preset = preset });
        fx.TriggerTuning();
        using var writer = new WaveFileWriter(args[2], WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2));
        var buf = new float[4096];
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            fx.Process(buf.AsSpan(0, n - n % 2));
            writer.WriteSamples(buf, 0, n);
        }

        Console.WriteLine($"written {args[2]} ({preset})");
        return 0;
    }

    // ---- stage 4 (without the game): IPC ----------------------------------------------------------

    private static int PipeTest(BridgeSettings settings, string[] args)
    {
        var asGame = args.Contains("--as-game");
        using var pipe = new NamedPipeClientStream(".", settings.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(2000);
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"BridgeHost is not running (no \\\\.\\pipe\\{settings.PipeName})");
            return 1;
        }

        var reader = new StreamReader(pipe, new UTF8Encoding(false));
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        Task<string?>? pending = null;

        // single-threaded: send one line, then print whatever arrives during waitMs
        void Exchange(string json, int waitMs)
        {
            Console.WriteLine($">> {json}");
            writer.WriteLine(json);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < waitMs)
            {
                pending ??= reader.ReadLineAsync();
                if (!pending.Wait(100)) continue;
                var line = pending.Result;
                pending = null;
                if (line is null) return;
                if (!line.Contains("\"ping\"") && !line.Contains("\"pong\"")) Console.WriteLine($"<< {line}");
            }
        }

        Exchange($"{{\"v\":1,\"type\":\"hello\",\"client\":\"{(asGame ? "MDR-NVSE" : "MDR-CLI")}\",\"version\":\"test\",\"gamePid\":{Environment.ProcessId}}}", 600);
        Exchange("{\"v\":1,\"type\":\"status\"}", 400);
        Exchange("{\"v\":2,\"type\":\"status\"}", 400);                       // expected: unsupported_version
        Exchange("{\"v\":1,\"type\":\"cmd\",\"id\":7,\"action\":\"nonsense\"}", 400); // expected: unknown_action

        if (asGame)
        {
            Exchange("{\"v\":1,\"type\":\"config\",\"fxEnabled\":true,\"preset\":\"Mojave\",\"volume\":1,\"fadeMs\":250,\"autoPause\":true,\"autoPlay\":true}", 500);
            Exchange("{\"v\":1,\"type\":\"station\",\"active\":true}", 5000);
            Exchange("{\"v\":1,\"type\":\"cmd\",\"id\":8,\"action\":\"next\"}", 3000);
            Exchange("{\"v\":1,\"type\":\"station\",\"active\":false}", 2000);
            Exchange("{\"v\":1,\"type\":\"bye\"}", 300);
        }

        pipe.Dispose();
        return 0;
    }

    /// <summary>How long the periodic engine work takes on this machine (the engine thread must stay responsive).</summary>
    private static int Timing(BridgeSettings settings)
    {
        var media = NewMedia(settings);
        using var devices = new AudioDevices();
        void Measure(string name, Action a)
        {
            a(); // warm up
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 5; i++) a();
            Console.WriteLine($"{name,-28} {sw.Elapsed.TotalMilliseconds / 5,8:0.0} ms");
        }

        Measure("media.Refresh", () => media.Refresh());
        Measure("process check", () => media.IsProcessRunning());
        Measure("FindSessions (all devices)", () => devices.FindSessions(settings.AudioProcessNames));
        Measure("FindOutputDevice", () => devices.FindOutputDevice(settings, null));
        Measure("FindRouteDevice", () => devices.FindRouteDevice(settings, null));
        return 0;
    }

    private static int RouteProbe(BridgeSettings settings)
    {
        using var devices = new AudioDevices();
        var output = devices.FindOutputDevice(settings, null);
        var route = devices.FindRouteDevice(settings, output?.ID);
        if (route is null) { Console.WriteLine("no route device"); return 1; }
        Console.WriteLine($"route device id: {route.ID}");
        foreach (var (pid, name, set, restore) in new AppAudioRouting().Probe(settings.AudioProcessNames, route.ID))
            Console.WriteLine($"  pid {pid,6} {name,-40} set=0x{set:X8} restore=0x{restore:X8}");
        return 0;
    }

    private static int RouteTest(BridgeSettings settings, bool confirmed)
    {
        if (!confirmed)
        {
            Console.WriteLine("route-test changes Spotify's output device for 5 seconds and restores it. Run with --yes.");
            return 1;
        }

        using var devices = new AudioDevices();
        var output = devices.FindOutputDevice(settings, null);
        var route = devices.FindRouteDevice(settings, output?.ID);
        if (route is null) { Console.WriteLine("no route device"); return 1; }
        var routing = new AppAudioRouting();
        Console.WriteLine($"routing Spotify → {route.FriendlyName}");
        if (!routing.Route(settings.AudioProcessNames, route.ID))
        {
            Console.WriteLine($"FAILED: {routing.LastError}");
            return 1;
        }

        for (var i = 0; i < 5; i++)
        {
            Thread.Sleep(1000);
            var where = devices.FindSessions(settings.AudioProcessNames).Select(s => $"{s.DeviceName}({s.State})");
            Console.WriteLine($"  t+{i + 1}s sessions: {string.Join(", ", where)}");
        }

        var restored = routing.Restore(settings.AudioProcessNames);
        Console.WriteLine($"restored: {restored}");
        return restored ? 0 : 1;
    }
}
