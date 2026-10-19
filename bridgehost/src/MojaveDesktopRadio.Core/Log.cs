using System.Text;

namespace MojaveDesktopRadio;

/// <summary>
/// Minimal thread-safe file logger. Only events are logged (never per audio block),
/// the file is rotated at 1 MB so it cannot grow without bound.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static bool _echo;

    public static string? CurrentFile { get; private set; }
    public static bool Verbose { get; set; }

    public static void Init(string fileName, bool echoToConsole)
    {
        lock (Gate)
        {
            _echo = echoToConsole;
            try
            {
                AppPaths.EnsureCreated();
                var path = Path.Combine(AppPaths.LogDir, fileName);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".old", overwrite: true);
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
                CurrentFile = path;
            }
            catch
            {
                _writer = null; // logging must never take the app down
            }
        }
    }

    public static void Debug(string message)
    {
        if (Verbose) Write("DBG", message);
    }

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (Gate)
        {
            try
            {
                _writer?.WriteLine(line);
                if (_writer is not null && _writer.BaseStream.Length > MaxBytes * 2)
                    _writer.BaseStream.SetLength(0);
            }
            catch
            {
                // ignore
            }

            if (_echo)
                Console.WriteLine(line);
        }
    }
}
