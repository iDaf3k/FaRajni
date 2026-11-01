using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FaRajni.App.ViewModels;
using FaRajni.Core.Models;

namespace FaRajni.App.Views;

public partial class LibraryView : UserControl
{
    private readonly MainWindow _host;

    public LibraryViewModel Vm { get; }

    public LibraryView(LibraryViewModel vm, MainWindow host)
    {
        InitializeComponent();
        Vm = vm;
        _host = host;
        DataContext = vm;
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void SetFfmpegStatus(bool found)
    {
        FfmpegDot.Fill = found ? (Brush)FindResource("OkBrush") : (Brush)FindResource("FaintBrush");
        FfmpegText.Text = found ? "Editing ready · ffmpeg found" : "Editing · ffmpeg downloads on first use";
    }

    // Every tile carries a 16 px right-hand gap; leave room for the scrollbar too.
    private void Grid_SizeChanged(object sender, SizeChangedEventArgs e) => Vm.SetAvailableWidth(TileGrid.ActualWidth - 14);

    // ---------------- sidebar ----------------

    private void AllVideos_Click(object s, RoutedEventArgs e) => Vm.SelectFolder(null);

    private void FolderRow_Click(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) is not null) return;   // eye / settings buttons
        if (((FrameworkElement)s).DataContext is FolderItem f) Vm.SelectFolder(f.Id);
    }

    private void FolderEye_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is FolderItem f) _host.ToggleFolderHidden(f.Folder);
    }

    private void FolderSettings_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is FolderItem f) _host.ShowFolderSettings(f.Folder);
    }

    private void AddFolder_Click(object s, RoutedEventArgs e) => _host.ShowAddFolder();
    private void Unhide_Click(object s, RoutedEventArgs e)
    {
        if (Vm.CurrentFolder is { } f) _host.ToggleFolderHidden(f);
    }

    private void More_Click(object s, RoutedEventArgs e) => OpenMenu(MoreBtn);
    private async void RescanAll_Click(object s, RoutedEventArgs e) => await Vm.RescanAllAsync();
    private void ImportLegacy_Click(object s, RoutedEventArgs e) => _host.ImportLegacy(manual: true);
    private void DefaultPlayer_Click(object s, RoutedEventArgs e) => _host.RegisterDefaultPlayer();

    // ---------------- header ----------------

    private void Sort_Click(object s, RoutedEventArgs e)
    {
        foreach (MenuItem mi in SortMenu.Items)
            mi.IsChecked = Enum.TryParse<VideoSort>((string)mi.Tag, out var v) && v == Vm.Sort;
        OpenMenu(SortBtn);
    }

    private void SortItem_Click(object s, RoutedEventArgs e)
    {
        if (Enum.TryParse<VideoSort>((string)((MenuItem)s).Tag, out var v)) Vm.Sort = v;
    }

    private void Dupes_Click(object s, RoutedEventArgs e) => Vm.HideDuplicates = !Vm.HideDuplicates;

    private static void OpenMenu(Button b)
    {
        if (b.ContextMenu is null) return;
        b.ContextMenu.PlacementTarget = b;
        b.ContextMenu.IsOpen = true;
    }

    // ---------------- tiles ----------------

    private static VideoTile? TileOf(object sender) => (sender as FrameworkElement)?.DataContext as VideoTile;

    private void Tile_MouseEnter(object s, MouseEventArgs e)
    {
        if (TileOf(s) is { } t) { t.IsHovered = true; Vm.Hovered = t; }
    }

    private void Tile_MouseLeave(object s, MouseEventArgs e)
    {
        if (TileOf(s) is { } t) { t.IsHovered = false; if (Vm.Hovered == t) Vm.Hovered = null; }
    }

    private void Tile_Click(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) is not null) return;
        if (TileOf(s) is not { } t) return;
        if (Vm.HasSelection) Vm.ToggleSelect(t);
        else _host.OpenPlayer(t.Id, Vm.CurrentOrder());
    }

    private void TileCheck_Click(object s, RoutedEventArgs e)
    {
        if (TileOf(s) is { } t) Vm.ToggleSelect(t);
    }

    private void TileTrash_Click(object s, RoutedEventArgs e)
    {
        if (TileOf(s) is { } t) _host.RequestDelete(new[] { t.Video });
    }

    // ---------------- selection bar ----------------

    private void Merge_Click(object s, RoutedEventArgs e) => _host.ShowMerge(Vm.Selected.Select(t => t.Video).ToList());
    private void DeleteSelected_Click(object s, RoutedEventArgs e) => _host.RequestDelete(Vm.Selected.Select(t => t.Video).ToList());
    private void ClearSelection_Click(object s, RoutedEventArgs e) => Vm.ClearSelection();

    /// <summary>Del in the library: the selection if there is one, else the hovered tile.</summary>
    public void DeleteKey()
    {
        if (Vm.HasSelection) _host.RequestDelete(Vm.Selected.Select(t => t.Video).ToList());
        else if (Vm.Hovered is { } h) _host.RequestDelete(new[] { h.Video });
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur is not null; cur = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }
}
