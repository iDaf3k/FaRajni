using LibVLCSharp.Shared;

namespace FaRajni.App.Services;

/// <summary>The one process-wide LibVLC. Created on a background thread right after the window
/// appears; opening a video awaits <see cref="Ready"/> (normally finished long before the first click).</summary>
public static class VlcEngine
{
    private static readonly Lazy<Task<LibVLC>> _ready = new(() => Task.Run(Create));

    public static Task<LibVLC> Ready => _ready.Value;

    public static void Start() => _ = _ready.Value;

    private static LibVLC Create()
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC("--no-video-title-show", "--no-osd", "--no-snapshot-preview", "--no-stats");
    }

    /// <summary>Writes plugins.dat next to VLC's plugins so later starts read one index file instead
    /// of loading all ~270 plugin DLLs. Run by the installer (the folder needs admin rights).</summary>
    public static void BuildPluginCache()
    {
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            new LibVLC("--reset-plugins-cache").Dispose();
        }
        catch (Exception ex) { AppPaths.LogError("PluginCache", ex); }
    }
}
