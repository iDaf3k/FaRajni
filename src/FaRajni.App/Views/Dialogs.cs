using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FaRajni.App.ViewModels;
using FaRajni.Core.Models;

namespace FaRajni.App.Views;

/// <summary>Night-glass dialogs. Real owned windows (not in-window overlays) so they also appear
/// above the native video surface while the player is open.</summary>
internal static class Ui
{
    public static T Res<T>(string key) => (T)Application.Current.FindResource(key);
    public static Brush Brush(string key) => Res<Brush>(key);

    public static TextBlock Icon(string glyph, double size = 16, Brush? fg = null)
        => new() { Style = Res<Style>("Icon"), Text = glyph, FontSize = size, Foreground = fg ?? Brush("InkBrush") };

    public static TextBlock Text(string text, double size = 14, Brush? fg = null, FontWeight? weight = null, bool wrap = true)
        => new()
        {
            Text = text, FontSize = size, Foreground = fg ?? Brush("InkBrush"), FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
        };

    public static Button Button(string style, object content, RoutedEventHandler click, Thickness? margin = null)
    {
        var b = new Button { Style = Res<Style>(style), Content = content, Margin = margin ?? new Thickness(0) };
        b.Click += click;
        return b;
    }

    public static Border IconBadge(string glyph, Brush bg, Brush fg)
        => new() { Width = 44, Height = 44, CornerRadius = new CornerRadius(14), Background = bg, Child = Icon(glyph, 20, fg), HorizontalAlignment = HorizontalAlignment.Left };

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static Window Dialog(Window owner, UIElement content, double width = 500)
    {
        var w = new Window { Style = Res<Style>("DialogWindow"), Owner = owner, Width = width, Content = content, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        w.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) w.Close(); };
        return w;
    }
}

/// <summary>"Move to Recycle Bin?" with a preview of what goes, and "don't ask again".</summary>
internal static class ConfirmDeleteDialog
{
    public sealed record Result(bool Confirmed, bool DontAskAgain);

    public static Result Ask(Window owner, IReadOnlyList<VideoTile> items, int skipped, string? dontAskLabel)
    {
        var confirmed = false;
        var root = new StackPanel();
        root.Children.Add(Ui.IconBadge(Glyph.Delete, Ui.Brush("DangerTintBrush"), Ui.Brush("DangerTextBrush")));
        root.Children.Add(Ui.Text(items.Count == 1 ? "Move this video to the Recycle Bin?" : $"Move {items.Count} videos to the Recycle Bin?",
            18, weight: FontWeights.SemiBold).With(t => t.Margin = new Thickness(0, 16, 0, 4)));
        var sub = $"You can restore {(items.Count == 1 ? "it" : "them")} from the Recycle Bin.";
        if (skipped > 0) sub += $" {Fmt.Plural(skipped, "video", "videos")} in folders with deleting turned off will be skipped.";
        root.Children.Add(Ui.Text(sub, 13.5, Ui.Brush("MutedBrush")));

        var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var t in items.Take(4))
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Brush thumb = t.Id > 0 && t.Thumbnail is { } img ? new ImageBrush(img) { Stretch = Stretch.UniformToFill } : t.Placeholder;
            g.Children.Add(new Border { Width = 56, Height = 32, CornerRadius = new CornerRadius(7), Background = thumb, HorizontalAlignment = HorizontalAlignment.Left });
            var title = Ui.Text(t.Title, 13.5, wrap: false); Grid.SetColumn(title, 1); title.VerticalAlignment = VerticalAlignment.Center;
            var size = Ui.Text(Fmt.Size(t.Video.FileSize), 11.5, Ui.Brush("MutedBrush"), wrap: false);
            size.FontFamily = Ui.Res<FontFamily>("MonoFont"); size.VerticalAlignment = VerticalAlignment.Center; size.Margin = new Thickness(8, 0, 6, 0);
            Grid.SetColumn(size, 2);
            g.Children.Add(title); g.Children.Add(size);
            list.Children.Add(new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(6), Background = Ui.Brush("FieldBrush"), Child = g, Margin = new Thickness(0, 0, 0, 6) });
        }
        if (items.Count > 4) list.Children.Add(Ui.Text($"…and {items.Count - 4} more", 12.5, Ui.Brush("MutedBrush")));
        root.Children.Add(list);

        var dontAsk = new CheckBox { Style = Ui.Res<Style>("Check"), Content = dontAskLabel, Margin = new Thickness(0, 10, 0, 0) };
        if (dontAskLabel is not null) root.Children.Add(dontAsk);

        Window? w = null;
        root.Children.Add(Ui.Row(
            Ui.Button("SubtleBtn", "Cancel", (_, _) => w!.Close(), new Thickness(0, 0, 8, 0)),
            Ui.Button("DangerBtn", "Move to Recycle Bin", (_, _) => { confirmed = true; w!.Close(); }).With(b => b.IsDefault = true)));
        w = Ui.Dialog(owner, root, 480);
        w.ShowDialog();
        return new Result(confirmed, dontAsk.IsChecked == true);
    }
}

/// <summary>Add a folder: path (typed or browsed) and its delete mode.</summary>
internal static class AddFolderDialog
{
    public static (string Path, DeleteMode Mode)? Ask(Window owner)
    {
        (string, DeleteMode)? result = null;
        var root = new StackPanel();
        root.Children.Add(Ui.Text("Add a folder", 18, weight: FontWeights.SemiBold));

        var box = new TextBox { Style = Ui.Res<Style>("BareTextBox"), FontFamily = Ui.Res<FontFamily>("MonoFont"), FontSize = 13 };
        var field = new Grid();
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        field.ColumnDefinitions.Add(new ColumnDefinition());
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        field.Children.Add(Ui.Icon(Glyph.FolderOpen, 15, Ui.Brush("MutedBrush")));
        Grid.SetColumn(box, 1); box.Margin = new Thickness(10, 0, 8, 0);
        field.Children.Add(box);
        var browse = Ui.Button("SubtleBtn", "Browse…", (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder with videos" };
            if (dlg.ShowDialog(owner) == true) box.Text = dlg.FolderName;
        });
        browse.Height = 30; browse.FontSize = 12.5;
        Grid.SetColumn(browse, 2);
        field.Children.Add(browse);
        root.Children.Add(new Border
        {
            Height = 44, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 0, 6, 0), Margin = new Thickness(0, 16, 0, 16),
            Background = Ui.Brush("FieldBrush"), BorderBrush = Ui.Brush("GlassEdgeBrush"), BorderThickness = new Thickness(1), Child = field,
        });

        root.Children.Add(Ui.Text("When I delete a video", 13, weight: FontWeights.Medium).With(t => t.Margin = new Thickness(0, 0, 0, 8)));
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        var mode = DeleteMode.Confirm;
        foreach (var (m, label) in new[] { (DeleteMode.Off, "Don't allow"), (DeleteMode.Confirm, "Ask first"), (DeleteMode.Direct, "Direct to Recycle Bin") })
        {
            var rb = new RadioButton { Style = Ui.Res<Style>("SegBtn"), GroupName = "addmode", Content = label, IsChecked = m == mode, Height = 34, MinWidth = 140 };
            rb.Checked += (_, _) => mode = m;
            seg.Children.Add(rb);
        }
        root.Children.Add(new Border { Background = Ui.Brush("SubtleBtnBrush"), CornerRadius = new CornerRadius(12), Padding = new Thickness(3), Child = seg, HorizontalAlignment = HorizontalAlignment.Left });

        var error = Ui.Text("", 12.5, Ui.Brush("DangerTextBrush")).With(t => t.Margin = new Thickness(0, 10, 0, 0));
        root.Children.Add(error);

        Window? w = null;
        root.Children.Add(Ui.Row(
            Ui.Button("SubtleBtn", "Cancel", (_, _) => w!.Close(), new Thickness(0, 0, 8, 0)),
            Ui.Button("PrimaryBtn", "Add & scan", (_, _) =>
            {
                var path = box.Text.Trim().Trim('"');
                if (!Directory.Exists(path)) { error.Text = "That folder doesn't exist."; return; }
                result = (Path.GetFullPath(path), mode);
                w!.Close();
            }).With(b => b.IsDefault = true)));
        w = Ui.Dialog(owner, root, 520);
        w.Loaded += (_, _) => box.Focus();
        w.ShowDialog();
        return result;
    }
}

/// <summary>Per-folder settings: show in library, delete mode, rescan, remove.</summary>
internal static class FolderSettingsDialog
{
    public enum Outcome { Closed, Rescan, Remove }

    public static Outcome Show(Window owner, Folder folder, Action<Folder> save)
    {
        var outcome = Outcome.Closed;
        var root = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.Children.Add(Ui.IconBadge(Glyph.Folder, Ui.Brush("AccentTintBrush"), Ui.Brush("AccentTextBrush")));
        var names = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Ui.Text(folder.Name, 18, weight: FontWeights.SemiBold, wrap: false));
        names.Children.Add(Ui.Text(folder.Path, 11.5, Ui.Brush("MutedBrush"), wrap: false).With(t => t.FontFamily = Ui.Res<FontFamily>("MonoFont")));
        Grid.SetColumn(names, 1);
        head.Children.Add(names);
        root.Children.Add(head);

        // show in library
        var show = new CheckBox { Style = Ui.Res<Style>("Switch"), IsChecked = !folder.Hidden, VerticalAlignment = VerticalAlignment.Center };
        show.Click += (_, _) => { folder.Hidden = show.IsChecked != true; save(folder); };
        var showRow = new Grid();
        showRow.ColumnDefinitions.Add(new ColumnDefinition());
        showRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var showText = new StackPanel();
        showText.Children.Add(Ui.Text("Show in library", 14, weight: FontWeights.Medium));
        showText.Children.Add(Ui.Text("When off, these videos are hidden from All videos and search.", 12.5, Ui.Brush("MutedBrush")));
        showRow.Children.Add(showText);
        Grid.SetColumn(show, 1);
        showRow.Children.Add(show);
        root.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = Ui.Brush("FieldBrush"), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 18, 0, 18), Child = showRow });

        // delete mode
        root.Children.Add(Ui.Text("When I delete a video", 14, weight: FontWeights.Medium));
        root.Children.Add(Ui.Text("Applies to the Delete key and trash buttons in this folder.", 12.5, Ui.Brush("MutedBrush")).With(t => t.Margin = new Thickness(0, 2, 0, 10)));
        foreach (var (m, glyph, label, desc) in new[]
                 {
                     (DeleteMode.Off, Glyph.Blocked, "Don't allow deleting", "Trash buttons and the Delete key do nothing here."),
                     (DeleteMode.Confirm, Glyph.Help, "Ask me first", "Shows a confirmation before moving to the Recycle Bin."),
                     (DeleteMode.Direct, Glyph.Bolt, "Move straight to Recycle Bin", "One press of Delete. Undo is available for a few seconds."),
                 })
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(Ui.Icon(glyph, 17, Ui.Brush(folder.DeleteMode == m ? "AccentTextBrush" : "MutedBrush")));
            var t = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            t.Children.Add(Ui.Text(label, 13.5, weight: FontWeights.Medium));
            t.Children.Add(Ui.Text(desc, 12, Ui.Brush("MutedBrush")));
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            var rb = new RadioButton { Style = Ui.Res<Style>("OptionCard"), GroupName = "mode", Content = g, IsChecked = folder.DeleteMode == m };
            rb.Checked += (_, _) =>
            {
                folder.DeleteMode = m;
                save(folder);
                foreach (var other in root.Children.OfType<RadioButton>())
                    if (other.Content is Grid og && og.Children[0] is TextBlock ic)
                        ic.Foreground = Ui.Brush(other == rb ? "AccentTextBrush" : "MutedBrush");
            };
            root.Children.Add(rb);
        }

        Window? w = null;
        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var remove = Ui.Button("DangerGhostBtn", Labelled(Glyph.FolderOff, "Remove folder"), (_, _) => { outcome = Outcome.Remove; w!.Close(); });
        footer.Children.Add(remove);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Ui.Button("SubtleBtn", Labelled(Glyph.Refresh, "Rescan"), (_, _) => { outcome = Outcome.Rescan; w!.Close(); }, new Thickness(0, 0, 8, 0)));
        right.Children.Add(Ui.Button("PrimaryBtn", "Done", (_, _) => w!.Close()).With(b => b.IsDefault = true));
        Grid.SetColumn(right, 2);
        footer.Children.Add(right);
        root.Children.Add(footer);

        w = Ui.Dialog(owner, root, 500);
        w.ShowDialog();
        return outcome;
    }

    public static StackPanel Labelled(string glyph, string text)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(new TextBlock { Style = Ui.Res<Style>("Icon"), Text = glyph, FontSize = 14 });
        p.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return p;
    }
}

internal static class RemoveFolderDialog
{
    public static bool Ask(Window owner, Folder folder, int videoCount)
    {
        var ok = false;
        var root = new StackPanel();
        root.Children.Add(Ui.IconBadge(Glyph.FolderOff, Ui.Brush("DangerTintBrush"), Ui.Brush("DangerTextBrush")));
        root.Children.Add(Ui.Text($"Remove “{folder.Name}” from your library?", 18, weight: FontWeights.SemiBold).With(t => t.Margin = new Thickness(0, 16, 0, 4)));
        root.Children.Add(Ui.Text($"The {Fmt.Plural(videoCount, "video", "videos")} stay on disk in {folder.Path}. They just won't appear here anymore.", 13.5, Ui.Brush("MutedBrush")));
        Window? w = null;
        root.Children.Add(Ui.Row(
            Ui.Button("SubtleBtn", "Cancel", (_, _) => w!.Close(), new Thickness(0, 0, 8, 0)),
            Ui.Button("DangerBtn", "Remove folder", (_, _) => { ok = true; w!.Close(); })));
        w = Ui.Dialog(owner, root, 470);
        w.ShowDialog();
        return ok;
    }
}

internal static class FluentExtensions
{
    public static T With<T>(this T obj, Action<T> configure) { configure(obj); return obj; }
}
