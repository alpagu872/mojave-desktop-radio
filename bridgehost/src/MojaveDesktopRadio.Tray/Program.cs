using MojaveDesktopRadio.Config;
using MojaveDesktopRadio.Engine;

namespace MojaveDesktopRadio.Tray;

internal static class Program
{
    /// <summary>
    /// Arguments:
    ///   --game-pid N   started by the game; exit after that game has closed (and no game is connected)
    ///   --pipe NAME    pipe name (the plugin passes its INI value, so both sides always match)
    ///   --no-audio     developer/test mode: never touch audio devices or Spotify's routing
    ///   --verbose      more log lines
    /// One instance per pipe name.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        int? gamePid = null;
        string? pipe = null;
        var verbose = false;
        var noAudio = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--game-pid" when i + 1 < args.Length && int.TryParse(args[i + 1], out var pid):
                    gamePid = pid;
                    i++;
                    break;
                case "--pipe" when i + 1 < args.Length && IsValidPipeName(args[i + 1]):
                    pipe = args[++i];
                    break;
                case "--no-audio":
                    noAudio = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
            }
        }

        var settings = BridgeSettings.Load();
        if (pipe is not null) settings.PipeName = pipe;
        settings.AudioEnabled = !noAudio;
        var isDefaultPipe = string.Equals(settings.PipeName, Ipc.Protocol.DefaultPipeName, StringComparison.OrdinalIgnoreCase);

        using var mutex = new Mutex(true, $@"Local\MojaveDesktopRadio.BridgeHost.{settings.PipeName}", out var isFirst);
        if (!isFirst)
            return 0; // already running — the game simply connects to the existing instance

        Log.Init(isDefaultPipe ? "bridgehost.log" : $"bridgehost-{settings.PipeName}.log", echoToConsole: false);
        Log.Verbose = verbose || settings.VerboseLog;
        Log.Info($"Mojave Desktop Radio BridgeHost {Ipc.Protocol.ServerVersion} starting" +
                 (gamePid is { } p ? $" (launched by game pid {p})" : "") +
                 (isDefaultPipe ? "" : $" on pipe '{settings.PipeName}'") +
                 (noAudio ? " in --no-audio mode" : ""));

        ApplicationConfiguration.Initialize();

        var engine = new BridgeEngine(settings, gamePid);
        try
        {
            engine.Start();
            using var tray = new TrayContext(engine);
            Application.Run(tray);
        }
        catch (Exception ex)
        {
            Log.Error("fatal", ex);
            return 1;
        }
        finally
        {
            engine.Dispose(); // restores routing / session volume
            Log.Info("BridgeHost exited");
        }

        return 0;
    }

    private static bool IsValidPipeName(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
}
