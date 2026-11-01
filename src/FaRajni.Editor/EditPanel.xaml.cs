using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FaRajni.Core.Editing;

namespace FaRajni.Editor;

/// <summary>Cut / GIF / audio editor shown in place of the player controls.</summary>
public partial class EditPanel : UserControl, IDisposable
{
    private const int FrameCount = 14;
    private const long MinRange = 500;

    private enum Mode { Cut, Gif, Audio }
    private enum Drag { None, Seek, In, Out }

    private readonly IEditHost _host;
    private readonly string _ffmpeg;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _framesDir = Path.Combine(Path.GetTempPath(), "FaRajni", "frames", Guid.NewGuid().ToString("N"));

    private Mode _mode = Mode.Cut;
    private Drag _drag;
    private long _in, _out;
    private int _fps = 15, _width = 480;
    private bool _busy;

    public EditPanel(IEditHost host, string ffmpegPath)
    {
        InitializeComponent();
        _host = host;
        _ffmpeg = ffmpegPath;
        _out = host.LengthMs;

        for (var i = 0; i < FrameCount; i++)
            Frames.Children.Add(new Border
            {
                Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb((byte)(22 + i), (byte)(26 + i), 40)),
                Child = new Image { Stretch = Stretch.UniformToFill },
            });

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        ApplyMode();
        Loaded += (_, _) => { Layout(); _ = LoadFramesAsync(); };
    }

    // ---------------- timeline ----------------

    private long Len => Math.Max(1, _host.LengthMs);

    private void OnTick()
    {
        var pos = _host.PositionMs;
        // Loop the selection while previewing a cut or GIF.
        if (_mode != Mode.Audio && _drag == Drag.None && _host.IsPlaying && (pos >= _out - 60 || pos < _in - 1500))
        {
            _host.Seek(_in);
            pos = _in;
        }
        PlayIcon.Text = _host.IsPlaying ? "\uE769" : "\uE768";
        Canvas.SetLeft(Playhead, X(pos) - 1);
    }

    private double X(long ms) => Track.ActualWidth * Math.Clamp((double)ms / Len, 0, 1);

    private long MsAt(MouseEventArgs e)
        => (long)(Math.Clamp(e.GetPosition(Track).X / Math.Max(1, Track.ActualWidth), 0, 1) * Len);

    private void Layout()
    {
        var w = Track.ActualWidth;
        if (w <= 0) return;
        double x0 = X(_in), x1 = X(_out);
        var showRange = _mode != Mode.Audio;
        foreach (var el in new FrameworkElement[] { DimLeft, DimRight, SelBox, HandleIn, HandleOut, RangeInfo })
            el.Visibility = showRange ? Visibility.Visible : Visibility.Collapsed;

        Canvas.SetLeft(DimLeft, 0); DimLeft.Width = Math.Max(0, x0);
        Canvas.SetLeft(DimRight, x1); DimRight.Width = Math.Max(0, w - x1);
        Canvas.SetLeft(SelBox, x0); SelBox.Width = Math.Max(4, x1 - x0);
        Canvas.SetLeft(HandleIn, x0 - 7);
        Canvas.SetLeft(HandleOut, x1 - 7);

        InText.Text = Precise(_in);
        OutText.Text = Precise(_out);
        LenText.Text = Precise(_out - _in);
    }

    private static string Precise(long ms)
    {
        var s = Math.Max(0, ms) / 1000.0;
        var m = (int)(s / 60);
        return $"{m}:{s - m * 60:00.0}";
    }

    private void Track_SizeChanged(object s, SizeChangedEventArgs e)
    {
        FrameClip.Clip = new RectangleGeometry(new Rect(0, 0, Track.ActualWidth, Track.ActualHeight), 11, 11);
        Layout();
    }

    private void Track_Down(object s, MouseButtonEventArgs e)
    {
        if (_drag != Drag.None) return;
        _drag = Drag.Seek;
        Track.CaptureMouse();
        _host.Seek(MsAt(e));
        e.Handled = true;
    }

    private void HandleIn_Down(object s, MouseButtonEventArgs e) { _drag = Drag.In; Track.CaptureMouse(); e.Handled = true; }
    private void HandleOut_Down(object s, MouseButtonEventArgs e) { _drag = Drag.Out; Track.CaptureMouse(); e.Handled = true; }

    private void Track_Move(object s, MouseEventArgs e)
    {
        if (_drag == Drag.None) return;
        var ms = MsAt(e);
        switch (_drag)
        {
            case Drag.In: _in = Math.Clamp(ms, 0, _out - MinRange); Canvas.SetLeft(Playhead, X(_in) - 1); break;
            case Drag.Out: _out = Math.Clamp(ms, _in + MinRange, Len); Canvas.SetLeft(Playhead, X(_out) - 1); break;
            default: Canvas.SetLeft(Playhead, X(ms) - 1); break;
        }
        Layout();
    }

    private void Track_Up(object s, MouseButtonEventArgs e)
    {
        var drag = _drag;
        _drag = Drag.None;
        Track.ReleaseMouseCapture();
        switch (drag)
        {
            case Drag.In: _host.Seek(_in); break;
            case Drag.Out: _host.Seek(Math.Max(_in, _out - 2000)); break;   // preview the tail of the clip
            case Drag.Seek: _host.Seek(MsAt(e)); break;
        }
    }

    private void Track_Lost(object s, MouseEventArgs e) => _drag = Drag.None;

    private void SetIn_Click(object s, RoutedEventArgs e) => SetIn();
    private void SetOut_Click(object s, RoutedEventArgs e) => SetOut();
    private void SetIn() { _in = Math.Clamp(_host.PositionMs, 0, _out - MinRange); Layout(); }
    private void SetOut() { _out = Math.Clamp(_host.PositionMs, _in + MinRange, Len); Layout(); }

    private void Play_Click(object s, RoutedEventArgs e) => _host.SetPlaying(!_host.IsPlaying);
    private void Close_Click(object s, RoutedEventArgs e) => _host.CloseEditor();

    /// <summary>Editor shortcuts; returns true when handled.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.OemOpenBrackets: case Key.I: SetIn(); return true;
            case Key.OemCloseBrackets: case Key.O: SetOut(); return true;
            case Key.Enter: _ = ExportAsync(); return true;
            default: return false;
        }
    }

    // ---------------- modes ----------------

    private void Tab_Checked(object s, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        _mode = s == TabGif ? Mode.Gif : s == TabAudio ? Mode.Audio : Mode.Cut;
        ApplyMode();
    }

    private void ApplyMode()
    {
        if (Note is null) return;
        Note.Text = _mode switch
        {
            Mode.Gif => "Renders the selected range to an animated GIF.",
            Mode.Audio => "Extracts the full audio track to MP3.",
            _ => "Lossless and fast. The start snaps to the nearest keyframe.",
        };
        GifOptions.Visibility = _mode == Mode.Gif ? Visibility.Visible : Visibility.Collapsed;
        if (!_busy) ExportText.Text = _mode switch { Mode.Gif => "Export GIF", Mode.Audio => "Extract MP3", _ => "Save cut" };
        if (_mode == Mode.Gif && _out - _in > 30_000) { _out = _in + 10_000; }   // GIFs of whole videos are huge; start with 10 s
        Layout();
    }

    private void Fps_Checked(object s, RoutedEventArgs e) => _fps = int.Parse((string)((FrameworkElement)s).Tag);
    private void Width_Checked(object s, RoutedEventArgs e) => _width = int.Parse((string)((FrameworkElement)s).Tag);

    // ---------------- export ----------------

    private void Export_Click(object s, RoutedEventArgs e) => _ = ExportAsync();

    private async Task ExportAsync()
    {
        if (_busy) return;
        _busy = true;
        ExportBtn.IsEnabled = false;
        ExportText.Text = "Working…";
        _host.SetPlaying(false);
        var src = _host.FilePath;
        try
        {
            switch (_mode)
            {
                case Mode.Cut:
                {
                    var r = await new VideoCutter(_ffmpeg).CutAsync(src, _in, _out);
                    Done(r.Ok, r.OutputPath, r.Error, "clip");
                    break;
                }
                case Mode.Gif:
                {
                    var r = await new MediaExporter(_ffmpeg).ExportGifAsync(src, _in, _out, fps: _fps, width: _width);
                    Done(r.Ok, r.OutputPath, r.Error, "GIF");
                    break;
                }
                default:
                {
                    var r = await new MediaExporter(_ffmpeg).ExtractAudioAsync(src);
                    Done(r.Ok, r.OutputPath, r.Error, "MP3");
                    break;
                }
            }
        }
        catch (Exception ex) { _host.Toast("Export failed: " + ex.Message); }
        finally
        {
            _busy = false;
            ExportBtn.IsEnabled = true;
            ApplyMode();
        }
    }

    private void Done(bool ok, string? path, string? error, string what)
    {
        if (ok && path is not null) _host.Exported(path, $"Saved “{Path.GetFileName(path)}” next to the original");
        else _host.Toast($"Couldn't make the {what}: " + (error?.Split('\n')[0] ?? "unknown error"));
    }

    // ---------------- filmstrip ----------------

    private async Task LoadFramesAsync()
    {
        try
        {
            Directory.CreateDirectory(_framesDir);
            var src = _host.FilePath;
            var len = Len;
            using var gate = new SemaphoreSlim(3);
            var jobs = Enumerable.Range(0, FrameCount).Select(async i =>
            {
                await gate.WaitAsync(_cts.Token);
                try
                {
                    var at = (long)(len * (i + 0.5) / FrameCount);
                    var dest = Path.Combine(_framesDir, $"{i}.jpg");
                    if (!await FfmpegLocator.ExtractFrameAsync(_ffmpeg, src, at, 160, dest, _cts.Token)) return;
                    var img = await Task.Run(() =>
                    {
                        var b = new BitmapImage();
                        b.BeginInit();
                        b.CacheOption = BitmapCacheOption.OnLoad;
                        b.UriSource = new Uri(dest);
                        b.EndInit();
                        b.Freeze();
                        return b;
                    });
                    if (!_cts.IsCancellationRequested && Frames.Children[i] is Border { Child: Image im }) im.Source = img;
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(jobs);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* the strip is decoration; editing works without it */ }
    }

    public void Dispose()
    {
        _timer.Stop();
        _cts.Cancel();
        var dir = _framesDir;
        Task.Delay(1500).ContinueWith(_ => { try { Directory.Delete(dir, true); } catch { } });
    }
}
