namespace FaRajni.App;

/// <summary>Where FaRajni keeps its data: %LocalAppData%\FaRajni (separate from For Za BoyZ).</summary>
public static class AppPaths
{
    public static readonly string DataDir = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FaRajni"));

    public static string DatabasePath => Path.Combine(DataDir, "library.db");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string CrashLog => Path.Combine(DataDir, "crash.log");
    public static readonly string ThumbnailDir = Ensure(Path.Combine(DataDir, "thumbnails"));
    public static string ToolsDir => Ensure(Path.Combine(DataDir, "tools"));

    public static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void LogError(string source, Exception? ex)
    {
        try { File.AppendAllText(CrashLog, $"[{DateTime.Now:o}] ({source}) {ex}{Environment.NewLine}{Environment.NewLine}"); }
        catch { /* logging must never throw */ }
    }
}
