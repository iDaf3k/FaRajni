using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using FaRajni.App.ViewModels;
using FaRajni.App.Views;
using FaRajni.Core.Models;
using FaRajni.Editor;

namespace FaRajni.App.Services;

/// <summary>The only place the app touches the editor assembly. Each method is NoInlining so the
/// JIT doesn't pull FaRajni.Editor.dll in until the user actually opens Edit or Merge.</summary>
internal static class EditorBridge
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<object?> CreatePanelAsync(PlayerView view, MainWindow host, LibraryViewModel lib, Player player, Video video)
    {
        var ffmpeg = await FfmpegSetup.EnsureAsync(host, AppPaths.ToolsDir);
        host.RefreshFfmpegStatus();
        if (ffmpeg is null) return null;
        return new EditPanel(new Adapter(view, host, lib, player, video), ffmpeg);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Dispose(object panel) => ((EditPanel)panel).Dispose();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool HandleKey(object panel, KeyEventArgs e) => ((EditPanel)panel).HandleKey(e);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<string?> MergeAsync(Window owner, IReadOnlyList<VideoTile> tiles)
    {
        var items = tiles.Select(t => new MergeItem(t.Video.FilePath, t.Title, t.DurationText,
            t.Thumbnail is { } img ? new System.Windows.Media.ImageBrush(img) { Stretch = System.Windows.Media.Stretch.UniformToFill } : t.Placeholder)).ToList();
        var result = await MergeDialog.ShowAsync(owner, items, AppPaths.ToolsDir);
        (owner as MainWindow)?.RefreshFfmpegStatus();
        return result;
    }

    private sealed class Adapter : IEditHost
    {
        private readonly PlayerView _view;
        private readonly MainWindow _host;
        private readonly LibraryViewModel _lib;
        private readonly Player _player;
        private readonly Video _video;

        public Adapter(PlayerView view, MainWindow host, LibraryViewModel lib, Player player, Video video)
        {
            _view = view; _host = host; _lib = lib; _player = player; _video = video;
        }

        public string FilePath => _video.FilePath;
        public string Title => _video.Title;
        public long LengthMs => _player.LengthMs > 0 ? _player.LengthMs : _video.DurationMs;
        public long PositionMs => _player.TimeMs;
        public bool IsPlaying => _player.IsPlaying;
        public Window Owner => _host;
        public void Seek(long ms) => _view.SeekTo(ms, resume: _player.IsPlaying);
        public void SetPlaying(bool playing) => _player.SetPaused(!playing);
        public void CloseEditor() => _view.CloseEditor();
        public void Toast(string message) => _host.ShowToast(message, "\uE7BA");

        public async void Exported(string outputPath, string message)
        {
            _host.ShowToast(message, "\uE73E", "Show", () => MainWindow.RevealInExplorer(outputPath));
            // A new video file lands in the same folder: pick it up so it appears in the library.
            if (_lib.FolderOf(_video) is { } f && Core.Scanning.VideoExtensions.IsVideo(outputPath))
                await _lib.RescanAsync(f);
        }
    }
}
