using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FaRajni.App.Services;
using FaRajni.App.ViewModels;
using FaRajni.Core.Library;
using FaRajni.Core.Models;
using FaRajni.Core.Scanning;

namespace FaRajni.App.Views;

/// <summary>The player screen. Created once and reused for every video (with the same VLC
/// MediaPlayer), so starting playback is just "open media".</summary>
public partial class PlayerView : UserControl
{
    private static readonly float[] Speeds = { 1f, 1.25f, 1.5f, 2f, 0.5f };

    private readonly MainWindow _host;
    private readonly LibraryViewModel _lib;
    private readonly Player _player;
    private readonly DispatcherTimer _tick, _cursorPoll;

    private List<long> _playlist = new();
    private int _index;
    private Video? _video;
    private bool _durationSaved;
    private float _speed = 1f;
    private bool _seeking;
    private long _pendingSeek = -1;
    private DateTime _seekDeadline;
    private bool _chromeVisible = true;
    private POINT _lastCursor;
    private DateTime _lastMove = DateTime.Now;
    private float _zoom = 1f;
    private object? _editor;   // FaRajni.Editor.EditPanel, typed loosely so this class never loads the editor assembly
    private readonly HashSet<Window> _hookedWindows = new();
    private readonly Dictionary<long, Video> _loose = new();   // files outside the library (negative ids)

    public PlayerView(MainWindow host, LibraryViewModel lib, Player player)
    {
        InitializeComponent();
        _host = host;
        _lib = lib;
        _player = player;

        Loaded += (_, _) => VideoView.MediaPlayer = _player.MediaPlayer;
        // The controls live in VideoView's own overlay window; once one is clicked, that window has
        // keyboard focus, so forward its keys to the same shortcut handler as the main window.
        Overlay.Loaded += (_, _) =>
        {
            if (Window.GetWindow(Overlay) is { } w && w != _host && !_hookedWindows.Contains(w))
            {
                _hookedWindows.Add(w);
                w.PreviewKeyDown += (_, e) => { if (IsOpen && HandleKey(e)) e.Handled = true; };
            }
        };
        _player.MediaPlayer.EndReached += (_, _) => Dispatcher.BeginInvoke(OnEnded);
        _player.MediaPlayer.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => _host.ShowToast("This video can't be played.", "\uE7BA"));

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _tick.Tick += (_, _) => UpdateProgress();
        _cursorPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _cursorPoll.Tick += (_, _) => PollCursor();
    }

    public bool IsOpen { get; private set; }
    public long? CurrentId => _video?.Id;
    private bool Editing => _editor is not null;

    // ---------------- open / switch / close ----------------

    public void Play(long id, List<long> playlist)
    {
        _playlist = playlist.Count > 0 ? playlist : new List<long> { id };
        _index = Math.Max(0, _playlist.IndexOf(id));
        IsOpen = true;
        _tick.Start();
        _cursorPoll.Start();
        GetCursorPos(out _lastCursor);
        ShowChrome();
        OpenCurrent(resume: true);
    }

    /// <summary>Play a file that isn't in the library (opened from Explorer). The other videos in
    /// its folder become the playlist, in Explorer's name order.</summary>
    public void PlayFile(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        List<string> files;
        try { files = Directory.EnumerateFiles(dir).Where(VideoExtensions.IsVideo).ToList(); }
        catch { files = new List<string>(); }
        if (!files.Contains(path, StringComparer.OrdinalIgnoreCase)) files.Add(path);

        _loose.Clear();
        long id = 0, start = -1;
        foreach (var f in files.OrderBy(Path.GetFileName, NaturalOrder.Instance))
        {
            var v = new Video { Id = --id, FilePath = f, Title = Path.GetFileNameWithoutExtension(f) };
            try { var fi = new FileInfo(f); v.FileSize = fi.Length; v.FileModified = fi.LastWriteTime; } catch { }
            _loose[v.Id] = v;
            if (string.Equals(f, path, StringComparison.OrdinalIgnoreCase)) start = v.Id;
        }
        Play(start, _loose.Keys.ToList());
    }

    private Video? Find(long id) => id < 0 ? _loose.GetValueOrDefault(id) : _lib.FindVideo(id);

    private void OpenCurrent(bool resume)
    {
        var v = Find(_playlist[_index]);
        if (v is null) { Close(); return; }
        if (!File.Exists(v.FilePath))
        {
            _host.ShowToast($"“{v.Title}” is missing from disk.", "\uE7BA");
            if (!Go(+1, save: false)) Close();
            return;
        }
        Start(v, resume ? v.ResumeMs : 0);
    }

    private void Start(Video v, long fromMs)
    {
        CloseEditor();
        _video = v;
        _durationSaved = false;
        _pendingSeek = -1;
        _zoom = 1f;
        _player.MediaPlayer.Scale = 0;
        _player.Volume = _host.Settings.Volume;
        _player.Open(v.FilePath, fromMs);
        if (_speed != 1f) _player.SetRate(_speed);

        TitleText.Text = v.Title;
        var folder = _lib.FolderOf(v);
        var where = folder?.Name ?? Folder.NameFromPath(Path.GetDirectoryName(v.FilePath) ?? "");
        SubText.Text = _playlist.Count > 1 ? $"{_index + 1} of {_playlist.Count} · {where}" : where;
        SubText.ToolTip = Path.GetDirectoryName(v.FilePath);
        // Library videos follow their folder's delete mode; loose files always ask first.
        DeleteBtn.Visibility = v.Id < 0 || folder is not null && folder.DeleteMode != DeleteMode.Off ? Visibility.Visible : Visibility.Collapsed;
        DeleteBtn.ToolTip = folder?.DeleteMode == DeleteMode.Direct ? "Move to Recycle Bin (Del)" : "Delete… (Del)";
        UpdateNav();
        SetProgress(fromMs, v.DurationMs);
    }

    private bool Go(int dir, bool save = true)
    {
        var i = _index + dir;
        if (i < 0 || i >= _playlist.Count) return false;
        if (save) SaveResume();
        _index = i;
        OpenCurrent(resume: false);
        return true;
    }

    private void OnEnded()
    {
        if (!IsOpen || Editing) return;
        // Stay on the finished video (no auto-advance); the side buttons move on.
        if (_video is { Id: > 0 } v) _lib.SaveResume(v.Id, 0, _player.LengthMs);
        PlayIcon.Text = "\uE768";
        PausedBadge.Visibility = Visibility.Visible;
        ShowChrome();
    }

    public void SaveResume()
    {
        if (_video is not { Id: > 0 } v) return;
        var len = _player.LengthMs;
        _lib.SaveResume(v.Id, LibraryQuery.ResumePointFor(_player.TimeMs, len), len);
    }

    public void Close()
    {
        if (!IsOpen) return;
        SaveResume();
        CloseEditor();
        IsOpen = false;
        _tick.Stop();
        _cursorPoll.Stop();
        _video = null;
        var mp = _player.MediaPlayer;
        ThreadPool.QueueUserWorkItem(_ => { try { mp.Stop(); } catch { } });   // Stop() can block for a moment
        _host.ClosePlayer();
    }

    /// <summary>Called after videos were deleted (staged). Drops them from the playlist and moves
    /// on if the current one went.</summary>
    public void OnVideosDeleted(IReadOnlyCollection<long> ids)
    {
        if (!IsOpen) return;
        var currentGone = _video is not null && ids.Contains(_video.Id);
        var before = _index;
        _playlist = _playlist.Where((id, i) =>
        {
            if (!ids.Contains(id)) return true;
            if (i < before) _index--;
            return false;
        }).ToList();
        if (!currentGone) { UpdateNav(); return; }
        if (_playlist.Count == 0) { _video = null; Close(); return; }
        _index = Math.Clamp(_index, 0, _playlist.Count - 1);
        OpenCurrent(resume: true);
    }

    // ---------------- progress ----------------

    private void UpdateProgress()
    {
        if (!IsOpen) return;
        var len = _player.LengthMs;
        var pos = _player.TimeMs;
        if (_video is { Id: > 0 } v && !_durationSaved && len > 0)
        {
            _durationSaved = true;
            if (Math.Abs(v.DurationMs - len) > 1000) _lib.SaveResume(v.Id, v.ResumeMs, len);
        }
        if (_seeking) return;
        if (_pendingSeek >= 0)
        {
            // VLC's time lags right after a seek; hold the knob where the user put it until it catches up.
            if (Math.Abs(pos - _pendingSeek) < 700 || DateTime.Now > _seekDeadline) _pendingSeek = -1;
            else pos = _pendingSeek;
        }
        SetProgress(pos, len);
        var playing = _player.IsPlaying;
        PlayIcon.Text = playing ? "\uE769" : "\uE768";
        PausedBadge.Visibility = !playing && len > 0 && !Editing ? Visibility.Visible : Visibility.Collapsed;
        VolIcon.Text = _player.Muted || _player.Volume == 0 ? "\uE74F" : "\uE767";
    }

    private void SetProgress(long pos, long len)
    {
        var frac = len > 0 ? Math.Clamp((double)pos / len, 0, 1) : 0;
        var w = SeekTrack.ActualWidth;
        SeekFill.Width = w * frac;
        KnobX.X = w * frac - 7;
        TimeText.Text = $"{Fmt.Duration(pos)} / {Fmt.Duration(len > 0 ? len : _video?.DurationMs ?? 0)}";
    }

    /// <summary>Show the side arrows only when there is a video in that direction.</summary>
    private void UpdateNav()
    {
        var prev = _index > 0 ? Find(_playlist[_index - 1]) : null;
        var next = _index < _playlist.Count - 1 ? Find(_playlist[_index + 1]) : null;
        PrevBtn.Visibility = prev is null ? Visibility.Collapsed : Visibility.Visible;
        NextBtn.Visibility = next is null ? Visibility.Collapsed : Visibility.Visible;
        PrevBtn.ToolTip = prev is null ? null : $"Previous · {prev.Title} (P)";
        NextBtn.ToolTip = next is null ? null : $"Next · {next.Title} (N)";
    }

    // ---------------- seeking ----------------

    private long PosFromMouse(MouseEventArgs e)
    {
        var len = _player.LengthMs;
        var w = SeekTrack.ActualWidth;
        if (len <= 0 || w <= 0) return -1;
        var frac = Math.Clamp(e.GetPosition(SeekTrack).X / w, 0, 1);
        var ms = (long)(frac * len);
        SetProgress(ms, len);
        return ms;
    }

    private void Seek_Down(object s, MouseButtonEventArgs e)
    {
        _seeking = true;
        SeekTrack.CaptureMouse();
        PosFromMouse(e);
        e.Handled = true;
    }

    private void Seek_Move(object s, MouseEventArgs e)
    {
        if (_seeking) PosFromMouse(e);   // move the knob only; seek once on release (Game-DVR files re-buffer hard)
    }

    private void Seek_Up(object s, MouseButtonEventArgs e)
    {
        if (!_seeking) return;
        var ms = PosFromMouse(e);
        _seeking = false;
        SeekTrack.ReleaseMouseCapture();
        if (ms >= 0) SeekTo(ms);
    }

    private void Seek_Lost(object s, MouseEventArgs e) => _seeking = false;

    public void SeekTo(long ms, bool resume = true)
    {
        _player.Seek(ms, resume);
        _pendingSeek = ms;
        _seekDeadline = DateTime.Now.AddSeconds(1);
        SetProgress(ms, _player.LengthMs);
    }

    private void SeekBy(long delta)
    {
        var len = _player.LengthMs;
        if (len <= 0) return;
        SeekTo(Math.Clamp((_pendingSeek >= 0 ? _pendingSeek : _player.TimeMs) + delta, 0, len - 500));
        Poke();
    }

    // ---------------- buttons ----------------

    private void Back_Click(object s, RoutedEventArgs e) => Close();
    private void Prev_Click(object s, RoutedEventArgs e) => Go(-1);
    private void Next_Click(object s, RoutedEventArgs e) => Go(+1);
    private void PlayPause_Click(object s, RoutedEventArgs e) => TogglePlay();
    private void Fullscreen_Click(object s, RoutedEventArgs e) => _host.ToggleFullscreen();
    private void Delete_Click(object s, RoutedEventArgs e) => DeleteCurrent();
    private void Edit_Click(object s, RoutedEventArgs e) => ToggleEditor();

    private void TogglePlay()
    {
        if (_player.IsFinished) { SeekTo(0); return; }
        _player.TogglePause();
        Poke();
    }

    private void Speed_Click(object s, RoutedEventArgs e)
    {
        _speed = Speeds[(Array.IndexOf(Speeds, _speed) + 1) % Speeds.Length];
        _player.SetRate(_speed);
        SpeedText.Text = $"{_speed:0.##}×";
    }

    private void Mute_Click(object s, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        _player.Muted = !_player.Muted;
        Say(_player.Muted ? "Muted" : $"Volume {_player.Volume}%");
    }

    private void ChangeVolume(int delta)
    {
        _player.SetVolume(_player.Volume + delta);
        _host.Settings.Volume = _player.Volume;
        Say($"Volume {_player.Volume}%");
    }

    private void DeleteCurrent()
    {
        if (_video is not { } v) return;
        if (v.Id > 0) _host.RequestDelete(new[] { v });
        else _host.DeleteLooseFile(v);
    }

    public void SetFullscreenIcon(bool fullscreen) => FsIcon.Text = fullscreen ? "\uE73F" : "\uE740";

    private void Overlay_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != Overlay) return;   // only clicks on the bare video
        if (e.ClickCount == 2) _host.ToggleFullscreen();
        else TogglePlay();
    }

    private void Overlay_MouseWheel(object s, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            _zoom = Math.Clamp(_zoom + (e.Delta > 0 ? 0.1f : -0.1f), 1f, 4f);
            _player.MediaPlayer.Scale = _zoom <= 1.001f ? 0 : _zoom;
        }
        else ChangeVolume(e.Delta > 0 ? 5 : -5);
    }

    // ---------------- keyboard (forwarded by the main window) ----------------

    public bool HandleKey(KeyEventArgs e)
    {
        if (Editing && _editor is not null && EditorKey(e)) return true;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Space: case Key.K: TogglePlay(); return true;
            case Key.N: case Key.PageDown: case Key.MediaNextTrack: case Key.Right when shift: Go(+1); Poke(); return true;
            case Key.P: case Key.PageUp: case Key.MediaPreviousTrack: case Key.Left when shift: Go(-1); Poke(); return true;
            case Key.MediaPlayPause: TogglePlay(); return true;
            case Key.Right: case Key.L: SeekBy(+10_000); return true;
            case Key.Left: case Key.J: SeekBy(-10_000); return true;
            case Key.Up: ChangeVolume(+5); return true;
            case Key.Down: ChangeVolume(-5); return true;
            case Key.M: ToggleMute(); return true;
            case Key.F: _host.ToggleFullscreen(); return true;
            case Key.E: ToggleEditor(); return true;
            case Key.Delete: DeleteCurrent(); return true;
            case Key.Escape:
                if (Editing) CloseEditor();
                else if (_host.IsFullscreen) _host.ToggleFullscreen();
                else Close();
                return true;
        }
        return false;
    }

    // ---------------- chrome auto-hide ----------------

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private void PollCursor()
    {
        GetCursorPos(out var c);
        if (c.X != _lastCursor.X || c.Y != _lastCursor.Y) { _lastCursor = c; Poke(); return; }
        if (_chromeVisible && !Editing && _player.IsPlaying && !_seeking && (DateTime.Now - _lastMove).TotalSeconds >= 2.5)
            HideChrome();
    }

    private void Poke()
    {
        _lastMove = DateTime.Now;
        ShowChrome();
    }

    private void ShowChrome()
    {
        if (_chromeVisible && TopBar.Opacity > 0.99) return;
        _chromeVisible = true;
        Fade(TopBar, 1); Fade(BottomArea, 1); Fade(PrevBtn, 1); Fade(NextBtn, 1);
        Overlay.Cursor = null;
    }

    private void HideChrome()
    {
        _chromeVisible = false;
        Fade(TopBar, 0); Fade(BottomArea, 0); Fade(PrevBtn, 0); Fade(NextBtn, 0);
        Overlay.Cursor = Cursors.None;
    }

    private static void Fade(UIElement el, double to)
    {
        el.IsHitTestVisible = to > 0;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(200)));
    }

    private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromSeconds(1.1) };

    private void Say(string text)
    {
        FlashText.Text = text;
        Flash.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));
        _flashTimer.Stop();
        _flashTimer.Tick -= FlashDone;
        _flashTimer.Tick += FlashDone;
        _flashTimer.Start();
        Poke();
    }

    private void FlashDone(object? s, EventArgs e)
    {
        _flashTimer.Stop();
        Flash.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
    }

    // ---------------- editor (separate assembly, loaded on first use) ----------------

    private void ToggleEditor()
    {
        if (Editing) CloseEditor();
        else _ = OpenEditorAsync();
    }

    private async Task OpenEditorAsync()
    {
        if (_video is not { } v) return;
        if (_player.LengthMs <= 0) { Say("Wait for the video to load…"); return; }
        var panel = await EditorBridge.CreatePanelAsync(this, _host, _lib, _player, v);
        if (panel is null || _video != v) return;
        _editor = panel;
        EditorHost.Content = panel;
        EditorHost.Visibility = Visibility.Visible;
        ControlBar.Visibility = Visibility.Collapsed;
        EditBtn.Background = (System.Windows.Media.Brush)FindResource("AccentBrush");
        Poke();
    }

    public void CloseEditor()
    {
        if (_editor is null) return;
        EditorBridge.Dispose(_editor);
        _editor = null;
        EditorHost.Content = null;
        EditorHost.Visibility = Visibility.Collapsed;
        ControlBar.Visibility = Visibility.Visible;
        EditBtn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool EditorKey(KeyEventArgs e) => EditorBridge.HandleKey(_editor!, e);
}
