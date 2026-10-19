namespace MojaveDesktopRadio;

/// <summary>Well-known locations. Everything lives under %LOCALAPPDATA%\MojaveDesktopRadio.</summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MojaveDesktopRadio");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>Small recovery markers (routing / session volume) so a crash never leaves Windows audio altered.</summary>
    public static string StateDir => Path.Combine(DataDir, "state");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(StateDir);
    }
}
