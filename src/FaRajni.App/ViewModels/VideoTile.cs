using System.Windows;
using System.Windows.Media;
using FaRajni.Core.Models;

namespace FaRajni.App.ViewModels;

/// <summary>One video in the grid / continue-watching row. Created once per video and reused
/// across refreshes, so its thumbnail survives sorting and searching.</summary>
public sealed class VideoTile : Observable
{
    private readonly LibraryViewModel _owner;
    private ImageSource? _thumb;
    private bool _thumbRequested;
    private bool _isSelected, _isHovered;

    public VideoTile(Video video, LibraryViewModel owner)
    {
        Video = video;
        _owner = owner;
        Placeholder = Placeholders.For(video.Id);
    }

    public Video Video { get; private set; }
    public long Id => Video.Id;
    public Folder? Folder => _owner.FolderOf(Video);

    public string Title => Video.Title;
    public string DurationText => Fmt.Duration(Video.DurationMs);
    public bool HasDuration => Video.DurationMs > 0;
    public string Meta
    {
        get
        {
            var parts = new List<string>(3);
            if (_owner.ShowFolderInMeta && Folder is { } f) parts.Add(f.Name);
            var d = Fmt.Date(_owner.ShowDateAddedInMeta ? Video.DateAdded : Video.FileModified); if (d.Length > 0) parts.Add(d);
            var s = Fmt.Size(Video.FileSize); if (s.Length > 0) parts.Add(s);
            return string.Join(" · ", parts);
        }
    }

    public bool HasResume => Video.ResumeMs > 0 && Video.DurationMs > 0;
    /// <summary>Resume point as a 0..1 fraction (drives the thin progress bar).</summary>
    public double ResumeFraction => Video.DurationMs > 0 ? Math.Clamp((double)Video.ResumeMs / Video.DurationMs, 0, 1) : 0;
    public GridLength ResumeDone => new(ResumeFraction, GridUnitType.Star);
    public GridLength ResumeRest => new(1 - ResumeFraction, GridUnitType.Star);
    public string LeftText => $"{Fmt.Duration(Math.Max(0, Video.DurationMs - Video.ResumeMs))} left";

    public Brush Placeholder { get; }

    /// <summary>Requested lazily: only tiles WPF actually realizes on screen ever bind this.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumb is null && !_thumbRequested)
            {
                _thumbRequested = true;
                _owner.RequestThumbnail(this);
            }
            else if (_thumb is not null) _owner.TouchThumbnail(this);
            return _thumb;
        }
    }

    internal void SetThumbnail(ImageSource? img, long durationMs)
    {
        if (durationMs > 0 && durationMs != Video.DurationMs)
        {
            Video.DurationMs = durationMs;
            Raise(nameof(DurationText)); Raise(nameof(HasDuration));
            RaiseResume();
        }
        _thumb = img;
        Raise(nameof(Thumbnail));
    }

    /// <summary>Drop the decoded image to save memory; it reloads if the tile is shown again.</summary>
    internal void EvictThumbnail()
    {
        _thumb = null;
        _thumbRequested = false;
    }

    internal void Update(Video fresh)
    {
        Video = fresh;
        Raise(nameof(Title)); Raise(nameof(DurationText)); Raise(nameof(HasDuration)); Raise(nameof(Meta));
        RaiseResume();
    }

    internal void RaiseMeta() => Raise(nameof(Meta));

    private void RaiseResume()
    {
        Raise(nameof(HasResume)); Raise(nameof(ResumeFraction)); Raise(nameof(ResumeDone)); Raise(nameof(ResumeRest)); Raise(nameof(LeftText));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) Raise(nameof(ShowCheck)); }
    }

    public bool IsHovered
    {
        get => _isHovered;
        set { if (Set(ref _isHovered, value)) { Raise(nameof(ShowCheck)); Raise(nameof(ShowTrash)); } }
    }

    public bool ShowCheck => _isHovered || _isSelected || _owner.HasSelection;
    public bool ShowTrash => _isHovered && !_owner.HasSelection && Folder?.DeleteMode != DeleteMode.Off;
    public string TrashTip => Folder?.DeleteMode == DeleteMode.Direct ? "Move to Recycle Bin (Del)" : "Delete… (Del)";

    internal void RaiseSelectionChrome()
    {
        Raise(nameof(ShowCheck)); Raise(nameof(ShowTrash)); Raise(nameof(TrashTip));
    }
}

/// <summary>Dark striped stand-ins shown until a thumbnail arrives (frozen, shared).</summary>
internal static class Placeholders
{
    private static readonly Brush[] Cache = Enumerable.Range(0, 12).Select(Make).ToArray();

    public static Brush For(long id) => Cache[(int)(Math.Abs(id * 7 + 3) % Cache.Length)];

    private static Brush Make(int i)
    {
        var hue = (i * 53 + 210) % 360;
        var a = Hsl(hue, 0.32, 0.17);
        var b = Hsl(hue, 0.32, 0.145);
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(13, 13),
            SpreadMethod = GradientSpreadMethod.Repeat,
            GradientStops = { new GradientStop(a, 0), new GradientStop(a, 0.5), new GradientStop(b, 0.5), new GradientStop(b, 1) },
        };
        brush.Freeze();
        return brush;
    }

    private static Color Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
