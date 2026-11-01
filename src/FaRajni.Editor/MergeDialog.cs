using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FaRajni.Core.Editing;

namespace FaRajni.Editor;

public sealed record MergeItem(string Path, string Title, string Duration, Brush Thumb);

/// <summary>Join several videos into one file, in an order the user can change.</summary>
public sealed class MergeDialog : Window
{
    private readonly List<MergeItem> _items;
    private readonly string _ffmpeg;
    private readonly StackPanel _list = new();
    private readonly TextBlock _sub = new() { FontSize = 13.5, Margin = new Thickness(0, 4, 0, 14), TextWrapping = TextWrapping.Wrap };
    private readonly Button _merge, _cancel;
    private CancellationTokenSource? _cts;

    /// <summary>The merged file, when the merge succeeded.</summary>
    public string? OutputPath { get; private set; }

    public static async Task<string?> ShowAsync(Window owner, IReadOnlyList<MergeItem> items, string toolsDir)
    {
        var ffmpeg = await FfmpegSetup.EnsureAsync(owner, toolsDir);
        if (ffmpeg is null) return null;
        var dlg = new MergeDialog(items, ffmpeg) { Owner = owner };
        dlg.ShowDialog();
        return dlg.OutputPath;
    }

    private MergeDialog(IReadOnlyList<MergeItem> items, string ffmpeg)
    {
        _items = items.ToList();
        _ffmpeg = ffmpeg;
        Style = (Style)Application.Current.FindResource("DialogWindow");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 520;

        var muted = (Brush)Application.Current.FindResource("MutedBrush");
        _sub.Foreground = muted;
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = $"Merge {_items.Count} videos", FontSize = 18, FontWeight = FontWeights.SemiBold });
        root.Children.Add(_sub);
        root.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        _cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("SubtleBtn"), Margin = new Thickness(0, 0, 8, 0) };
        _merge = new Button { Content = "Merge into one file", Style = (Style)Application.Current.FindResource("PrimaryBtn") };
        _cancel.Click += (_, _) => { _cts?.Cancel(); Close(); };
        _merge.Click += async (_, _) => await MergeAsync();
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_merge);
        root.Children.Add(buttons);
        Content = root;
        Rebuild();
    }

    private void Rebuild()
    {
        _sub.Text = $"Joined in this order. Saved as “{System.IO.Path.GetFileNameWithoutExtension(_items[0].Path)} - merged{System.IO.Path.GetExtension(_items[0].Path)}”.";
        _list.Children.Clear();
        var muted = (Brush)Application.Current.FindResource("MutedBrush");
        var mono = (FontFamily)Application.Current.FindResource("MonoFont");
        var icon = (Style)Application.Current.FindResource("Icon");
        for (var i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            var idx = i;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var w in new[] { 26.0, 64.0, double.NaN, 70.0, 32.0, 32.0 })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(w) ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
            var card = new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255)), Padding = new Thickness(6), Child = row };

            Add(row, 0, new TextBlock { Text = (i + 1).ToString(), FontFamily = mono, FontSize = 12, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            Add(row, 1, new Border { Width = 56, Height = 32, CornerRadius = new CornerRadius(7), Background = it.Thumb });
            Add(row, 2, new TextBlock { Text = it.Title, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) });
            Add(row, 3, new TextBlock { Text = it.Duration, FontFamily = mono, FontSize = 11.5, Foreground = muted, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0) });
            var up = new Button { Style = (Style)Application.Current.FindResource("IconBtn"), ToolTip = "Move up", IsEnabled = i > 0, Content = new TextBlock { Style = icon, Text = "\uE74A", FontSize = 13 } };
            var down = new Button { Style = (Style)Application.Current.FindResource("IconBtn"), ToolTip = "Move down", IsEnabled = i < _items.Count - 1, Content = new TextBlock { Style = icon, Text = "\uE74B", FontSize = 13 } };
            up.Click += (_, _) => Move(idx, -1);
            down.Click += (_, _) => Move(idx, +1);
            Add(row, 4, up);
            Add(row, 5, down);
            _list.Children.Add(card);
        }
    }

    private static void Add(Grid g, int col, UIElement el) { Grid.SetColumn(el, col); g.Children.Add(el); }

    private void Move(int i, int d)
    {
        var j = i + d;
        if (j < 0 || j >= _items.Count) return;
        (_items[i], _items[j]) = (_items[j], _items[i]);
        Rebuild();
    }

    private async Task MergeAsync()
    {
        _merge.IsEnabled = false;
        _merge.Content = "Merging…";
        _cts = new CancellationTokenSource();
        var first = _items[0].Path;
        var dest = MediaExporter.BuildSiblingPath(first, " - merged", System.IO.Path.GetExtension(first));
        var r = await new MediaExporter(_ffmpeg).MergeAsync(_items.Select(x => x.Path).ToList(), dest, _cts.Token);
        if (r.Ok) { OutputPath = r.OutputPath; Close(); return; }
        _sub.Text = r.Error ?? "Merge failed.";
        _sub.Foreground = (Brush)Application.Current.FindResource("DangerTextBrush");
        _merge.IsEnabled = true;
        _merge.Content = "Try again";
    }
}
