using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FaRajni.App.Interop;
using FaRajni.App.Services;
using FaRajni.App.ViewModels;
using FaRajni.App.Views;
using FaRajni.Core.Data;
using FaRajni.Core.Editing;
using FaRajni.Core.Import;
using FaRajni.Core.Library;
using FaRajni.Core.Models;

namespace FaRajni.App;

public partial class MainWindow : Window
{
    private readonly LibraryDatabase _db;
    private readonly ThumbnailService _thumbs;
    private readonly LibraryViewModel _lib;
    private readonly LibraryView _libraryView;
    private readonly string? _launchFile;

    private Player? _player;
    private PlayerView? _playerView;

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private Action? _toastAction;

    private List<long>? _pendingDelete;   // staged, recycled when the Undo toast expires
    private readonly DispatcherTimer _deleteTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private WindowState _restoreState;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResize;

    public AppSettings Settings { get; } = AppSettings.Load();
    public bool IsFullscreen => WindowStyle == WindowStyle.None;

    public MainWindow(string? launchFile)
    {
        InitializeComponent();
        _launchFile = launchFile;

        _db = new LibraryDatabase(AppPaths.DatabasePath);
        _db.Initialize();
        _thumbs = new ThumbnailService(_db, Dispatcher);
        _lib = new LibraryViewModel(_db, new LibraryService(_db), _thumbs, Settings);
        _libraryView = new LibraryView(_lib, this);
        LibraryHost.Content = _libraryView;
        _lib.Reload();

        _toastTimer.Tick += (_, _) => HideToast();
        _deleteTimer.Tick += async (_, _) => await CommitPendingDeleteAsync();
        SourceInitialized += (_, _) => Dwm.ApplyNight(this);
        Loaded += OnLoaded;
        LocationChanged += (_, _) => PlaceToast();
        SizeChanged += (_, _) => PlaceToast();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshFfmpegStatus();
        // The import fixes the dates itself once it is done (rescanning before it would miss its rows).
        if (!Settings.LegacyImportDone && _db.CountVideos() == 0) ImportLegacy(manual: false);
        else if (!Settings.DateAddedFixed) FixDatesAdded();
        if (_launchFile is not null) OpenFile(_launchFile);
    }

    /// <summary>One-time upgrade: dates added used to be the scan time, so a rescan replaces them
    /// with when each file actually arrived in its folder.</summary>
    private async void FixDatesAdded()
    {
        _lib.Sort = VideoSort.RecentlyAdded;
        try { await _lib.RescanAllAsync(); }
        catch (Exception ex) { AppPaths.LogError("FixDatesAdded", ex); return; }
        Settings.DateAddedFixed = true; Settings.Save();
    }

    // ======================= player =======================

    public async void OpenPlayer(long id, List<long> playlist)
    {
        if (!await EnsurePlayerAsync()) return;
        _playerView!.Play(id, playlist);
    }

    /// <summary>A second launch (Explorer "Open", or the app icon) handed over to this instance.</summary>
    public void OpenForwarded(string? path)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;   // make sure it actually comes in front of Explorer
        if (path is not null && File.Exists(path)) OpenFile(path);
    }

    /// <summary>Play a file opened from Explorer, with the rest of its folder as the playlist.</summary>
    private async void OpenFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        var known = _lib.AllVideos.FirstOrDefault(v => string.Equals(v.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (known is not null)
        {
            var siblings = _lib.AllVideos
                .Where(v => !_lib.IsStaged(v.Id) && string.Equals(Path.GetDirectoryName(v.FilePath), dir, StringComparison.OrdinalIgnoreCase))
                .OrderBy(v => Path.GetFileName(v.FilePath), NaturalOrder.Instance)
                .Select(v => v.Id).ToList();
            OpenPlayer(known.Id, siblings);
            return;
        }
        if (!await EnsurePlayerAsync()) return;
        _playerView!.PlayFile(path);
    }

    private async Task<bool> EnsurePlayerAsync()
    {
        try
        {
            if (_player is null)
            {
                var vlc = await VlcEngine.Ready;
                _player ??= new Player(vlc) { Volume = Settings.Volume };
                _playerView ??= new PlayerView(this, _lib, _player);
            }
            if (PlayerHost.Content is null) PlayerHost.Content = _playerView;
            PlayerHost.Visibility = Visibility.Visible;
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("Player", ex);
            ShowToast("The video engine failed to start: " + ex.Message, Glyph.Warning);
            return false;
        }
    }

    /// <summary>Called by the player when it closes.</summary>
    public void ClosePlayer()
    {
        if (IsFullscreen) ToggleFullscreen();
        PlayerHost.Content = null;   // unloads the VideoView (and its native overlay window)
        PlayerHost.Visibility = Visibility.Collapsed;
        Settings.Save();
        _lib.Refresh();               // continue-watching + progress bars
        Activate();
    }

    public void ToggleFullscreen()
    {
        if (IsFullscreen)
        {
            WindowStyle = _restoreStyle;
            ResizeMode = _restoreResize;
            WindowState = _restoreState;
        }
        else
        {
            _restoreState = WindowState; _restoreStyle = WindowStyle; _restoreResize = ResizeMode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;   // re-maximize to cover the taskbar
            WindowState = WindowState.Maximized;
        }
        _playerView?.SetFullscreenIcon(IsFullscreen);
    }

    // ======================= keyboard =======================

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_playerView is { IsOpen: true } pv && PlayerHost.Visibility == Visibility.Visible)
        {
            if (pv.HandleKey(e)) e.Handled = true;
            return;
        }

        var typing = Keyboard.FocusedElement is TextBox;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && (e.Key == Key.F || e.Key == Key.K)) { _libraryView.FocusSearch(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z && !typing && _pendingDelete is not null) { UndoDelete(); e.Handled = true; }
        else if (e.Key == Key.Delete && !typing) { _libraryView.DeleteKey(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            if (_lib.HasSelection) _lib.ClearSelection();
            else if (_lib.Search.Length > 0) _lib.Search = "";
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    // ======================= delete =======================

    public async void RequestDelete(IReadOnlyList<Video> videos)
    {
        if (videos.Count == 0) return;
        var d = DeletePolicy.Decide(videos, _lib.FolderMap);
        switch (d.Action)
        {
            case DeleteAction.Blocked:
                var f = d.BlockingFolder;
                ShowToast(f is null ? "These videos can't be deleted." : $"Deleting is turned off for “{f.Name}”", Glyph.Blocked,
                          f is null ? null : "Folder settings", f is null ? null : () => ShowFolderSettings(f));
                return;

            case DeleteAction.Confirm:
                var tiles = d.Deletable.Select(v => _lib.Items.FirstOrDefault(t => t.Id == v.Id) ?? new VideoTile(v, _lib)).ToList();
                var confirmFolders = d.Deletable.Select(v => _lib.FolderOf(v)).Where(x => x?.DeleteMode == DeleteMode.Confirm)
                                      .Cast<Folder>().DistinctBy(x => x.Id).ToList();
                var label = confirmFolders.Count == 1 ? $"Don't ask again in “{confirmFolders[0].Name}”" : "Don't ask again in these folders";
                var r = WithDim(() => ConfirmDeleteDialog.Ask(this, tiles, d.Skipped, label));
                if (!r.Confirmed) return;
                if (r.DontAskAgain)
                    foreach (var cf in confirmFolders) { cf.DeleteMode = DeleteMode.Direct; _db.UpdateFolder(cf); }
                if (r.DontAskAgain) _lib.Reload();
                break;
        }
        await DeleteNowAsync(d.Deletable, d.Skipped);
    }

    /// <summary>Delete a file that isn't in the library (opened from Explorer): always asks, since
    /// there is no folder setting to follow and no Undo.</summary>
    public async void DeleteLooseFile(Video video)
    {
        var tile = new VideoTile(video, _lib);
        if (!WithDim(() => ConfirmDeleteDialog.Ask(this, new[] { tile }, 0, dontAskLabel: null)).Confirmed) return;
        _playerView?.OnVideosDeleted(new[] { video.Id });   // let go of the file before recycling it
        string? err = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(300);   // VLC releases the file asynchronously
            err = await Task.Run(() => FileRecycler.MoveToRecycleBin(video.FilePath));
            if (err is null) break;
        }
        if (err is null) ShowToast($"“{video.Title}” moved to Recycle Bin", Glyph.Delete);
        else ShowToast($"Couldn't delete “{video.Title}”: {err}", Glyph.Warning);
    }

    private async Task DeleteNowAsync(IReadOnlyList<Video> videos, int skipped)
    {
        await CommitPendingDeleteAsync();   // one Undo at a time
        var ids = videos.Select(v => v.Id).ToList();
        _lib.Stage(ids);
        _playerView?.OnVideosDeleted(ids);
        _pendingDelete = ids;
        _deleteTimer.Stop();
        _deleteTimer.Start();
        var what = videos.Count == 1 ? $"“{videos[0].Title}”" : $"{videos.Count} videos";
        ShowToast($"{what} moved to Recycle Bin" + (skipped > 0 ? $" · {skipped} skipped" : ""), Glyph.Delete, "Undo", UndoDelete);
    }

    private void UndoDelete()
    {
        if (_pendingDelete is not { } ids) return;
        _deleteTimer.Stop();
        _pendingDelete = null;
        _lib.Unstage(ids);
        ShowToast(ids.Count == 1 ? "Video restored" : $"{ids.Count} videos restored", Glyph.Undo);
    }

    private async Task CommitPendingDeleteAsync()
    {
        _deleteTimer.Stop();
        if (_pendingDelete is not { } ids) return;
        _pendingDelete = null;
        var failed = await _lib.CommitDeletes(ids);
        if (failed.Count > 0)
            ShowToast($"Couldn't delete “{failed[0].Video.Title}”: {failed[0].Error}", Glyph.Warning);
    }

    // ======================= folders =======================

    public void ToggleFolderHidden(Folder folder)
    {
        folder.Hidden = !folder.Hidden;
        _lib.SaveFolder(folder);
        ShowToast(folder.Hidden ? $"“{folder.Name}” hidden from the library" : $"“{folder.Name}” is visible in the library",
                  folder.Hidden ? Glyph.Hide : Glyph.View);
    }

    public async void ShowFolderSettings(Folder folder)
    {
        if (_playerView is { IsOpen: true }) _playerView.Close();
        var outcome = WithDim(() => FolderSettingsDialog.Show(this, folder, f => _lib.SaveFolder(f)));
        switch (outcome)
        {
            case FolderSettingsDialog.Outcome.Rescan:
                await _lib.RescanAsync(folder);
                ShowToast($"“{folder.Name}” is up to date", Glyph.Check);
                break;
            case FolderSettingsDialog.Outcome.Remove:
                if (WithDim(() => RemoveFolderDialog.Ask(this, folder, _lib.VideoCountIn(folder.Id))))
                {
                    _lib.RemoveFolder(folder);
                    ShowToast($"Removed “{folder.Name}” from the library", Glyph.FolderOff);
                }
                break;
        }
    }

    public async void ShowAddFolder()
    {
        var r = WithDim(() => AddFolderDialog.Ask(this));
        if (r is not { } add) return;
        var name = Folder.NameFromPath(add.Path);
        try
        {
            var id = await _lib.AddFolderAsync(add.Path, add.Mode);
            ShowToast($"Found {Fmt.Plural(_lib.VideoCountIn(id), "video", "videos")} in “{name}”", Glyph.Check);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("AddFolder", ex);
            ShowToast($"Couldn't add “{name}”: {ex.Message}", Glyph.Warning);
        }
    }

    public async void ShowMerge(List<Video> videos)
    {
        if (videos.Count < 2) return;
        var tiles = videos.Select(v => _lib.Items.First(t => t.Id == v.Id)).ToList();
        string? output;
        try { output = await WithDimAsync(() => EditorBridge.MergeAsync(this, tiles)); }
        catch (Exception ex) { AppPaths.LogError("Merge", ex); ShowToast("Merge failed: " + ex.Message, Glyph.Warning); return; }
        if (output is null) return;
        _lib.ClearSelection();
        ShowToast($"Saved “{Path.GetFileName(output)}”", Glyph.Check, "Show", () => RevealInExplorer(output));
        if (_lib.FolderOf(videos[0]) is { } f) await _lib.RescanAsync(f);
    }

    public void ImportLegacy(bool manual)
    {
        var path = LegacyImporter.FindLegacyDatabase(AppPaths.RoamingAppData);
        if (path is null)
        {
            Settings.LegacyImportDone = true; Settings.Save();
            if (manual) ShowToast("No For Za BoyZ or 3l Jaw library was found on this PC.", Glyph.Info);
            return;
        }
        Task.Run(() => LegacyImporter.Import(path, _db, AppPaths.ThumbnailDir)).ContinueWith(t =>
        {
            Settings.LegacyImportDone = true; Settings.Save();
            if (t.Exception is { } ex)
            {
                AppPaths.LogError("LegacyImport", ex);
                if (manual) ShowToast("Import failed: " + ex.InnerException?.Message, Glyph.Warning);
                return;
            }
            _lib.Reload();
            if (!Settings.DateAddedFixed) FixDatesAdded();
            if (t.Result.Videos > 0)
                ShowToast($"Imported {Fmt.Plural(t.Result.Videos, "video", "videos")} from your old library", Glyph.Check);
            else if (manual) ShowToast("Your old library had no videos to import.", Glyph.Info);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void RegisterDefaultPlayer()
    {
        try { DefaultPlayerRegistration.RegisterAndOpenSettings(); ShowToast("Pick FaRajni as the video player in Settings", Glyph.Info); }
        catch (Exception ex) { ShowToast("Couldn't register: " + ex.Message, Glyph.Warning); }
    }

    public void RefreshFfmpegStatus() => _libraryView.SetFfmpegStatus(FfmpegLocator.Find(AppPaths.ToolsDir) is not null);

    public static void RevealInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* best effort */ }
    }

    private T WithDim<T>(Func<T> show)
    {
        var dim = _playerView is not { IsOpen: true };
        if (dim) Dim.Visibility = Visibility.Visible;
        try { return show(); }
        finally { Dim.Visibility = Visibility.Collapsed; }
    }

    private async Task<T> WithDimAsync<T>(Func<Task<T>> show)
    {
        Dim.Visibility = Visibility.Visible;
        try { return await show(); }
        finally { Dim.Visibility = Visibility.Collapsed; }
    }

    // ======================= toast =======================

    public void ShowToast(string message, string icon, string? actionLabel = null, Action? action = null)
    {
        ToastText.Text = message;
        ToastIcon.Text = icon;
        _toastAction = action;
        ToastAction.Content = actionLabel;
        ToastAction.Visibility = actionLabel is null ? Visibility.Collapsed : Visibility.Visible;
        ToastPopup.IsOpen = true;
        PlaceToast();
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        ToastPopup.IsOpen = false;
    }

    private void PlaceToast()
    {
        if (!ToastPopup.IsOpen || ToastPopup.Child is not FrameworkElement child) return;
        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = child.DesiredSize;
        var x = Math.Max(0, (Root.ActualWidth - size.Width) / 2);
        ToastPopup.HorizontalOffset = x + 1;   // changing the offset is what makes WPF re-place a popup after the window moved
        ToastPopup.HorizontalOffset = x;
        ToastPopup.VerticalOffset = Math.Max(0, Root.ActualHeight - size.Height - 6);
    }

    private void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var a = _toastAction;
        HideToast();
        a?.Invoke();
    }

    private void ToastClose_Click(object sender, RoutedEventArgs e) => HideToast();

    // ======================= shutdown =======================

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        HideToast();
        _playerView?.SaveResume();
        _playerView?.CloseEditor();
        if (_pendingDelete is not null)
        {
            // Finish the pending Recycle Bin move before exiting (the Undo window ends with the app).
            var ids = _pendingDelete;
            _pendingDelete = null;
            foreach (var id in ids)
                if (_lib.FindVideo(id) is { } v && FileRecycler.MoveToRecycleBin(v.FilePath) is null)
                    _db.DeleteVideo(id);
        }
        Settings.Save();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _thumbs.Dispose();
        try { _player?.Dispose(); } catch { }
        _db.Dispose();
        base.OnClosed(e);
    }
}
