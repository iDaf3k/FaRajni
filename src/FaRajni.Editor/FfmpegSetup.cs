using System.IO.Compression;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FaRajni.Core.Editing;

namespace FaRajni.Editor;

/// <summary>ffmpeg isn't shipped with FaRajni. The first time the user edits, use an installed
/// copy if there is one, otherwise offer a one-time download into FaRajni's tools folder.</summary>
public static class FfmpegSetup
{
    private const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    /// <returns>Path to ffmpeg.exe, or null if the user declined / the download failed.</returns>
    public static async Task<string?> EnsureAsync(Window owner, string toolsDir)
    {
        var found = FfmpegLocator.Find(toolsDir);
        if (found is not null) return found;

        var dlg = new SetupWindow(toolsDir) { Owner = owner };
        dlg.ShowDialog();
        await Task.CompletedTask;
        return dlg.ResultPath;
    }

    private sealed class SetupWindow : Window
    {
        private readonly string _toolsDir;
        private readonly TextBlock _status;
        private readonly ProgressBar _bar;
        private readonly Button _go, _cancel;
        private CancellationTokenSource? _cts;

        public string? ResultPath { get; private set; }

        public SetupWindow(string toolsDir)
        {
            _toolsDir = toolsDir;
            Style = (Style)Application.Current.FindResource("DialogWindow");
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Width = 470;

            var root = new StackPanel();
            root.Children.Add(new TextBlock { Text = "Editing needs ffmpeg", FontSize = 18, FontWeight = FontWeights.SemiBold });
            root.Children.Add(new TextBlock
            {
                Text = "FaRajni uses the free ffmpeg tool to cut clips, make GIFs, extract audio and merge videos. " +
                       "It isn't bundled to keep the app small. Download it once (about 100 MB)?",
                TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Margin = new Thickness(0, 6, 0, 16),
                Foreground = (Brush)Application.Current.FindResource("MutedBrush"),
            });
            _bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8),
                                     Foreground = (Brush)Application.Current.FindResource("AccentBrush"), Background = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), BorderThickness = new Thickness(0) };
            _status = new TextBlock { FontSize = 12.5, Foreground = (Brush)Application.Current.FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap };
            root.Children.Add(_bar);
            root.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            _cancel = new Button { Content = "Not now", Style = (Style)Application.Current.FindResource("SubtleBtn"), Margin = new Thickness(0, 0, 8, 0) };
            _go = new Button { Content = "Download ffmpeg", Style = (Style)Application.Current.FindResource("PrimaryBtn") };
            _cancel.Click += (_, _) => { _cts?.Cancel(); Close(); };
            _go.Click += async (_, _) => await DownloadAsync();
            buttons.Children.Add(_cancel);
            buttons.Children.Add(_go);
            root.Children.Add(buttons);
            Content = root;
        }

        private async Task DownloadAsync()
        {
            _go.IsEnabled = false;
            _bar.Visibility = Visibility.Visible;
            _cts = new CancellationTokenSource();
            var zip = Path.Combine(Path.GetTempPath(), "farajni-ffmpeg.zip");
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
                using (var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, _cts.Token))
                {
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength ?? 0;
                    await using var src = await resp.Content.ReadAsStreamAsync(_cts.Token);
                    await using var dst = File.Create(zip);
                    var buf = new byte[1 << 16];
                    long done = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, _cts.Token)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), _cts.Token);
                        done += n;
                        if (total > 0) { _bar.Value = (double)done / total; _status.Text = $"Downloading… {done / 1_000_000} of {total / 1_000_000} MB"; }
                    }
                }

                _status.Text = "Unpacking…";
                var dest = FfmpegLocator.ToolsPath(_toolsDir);
                await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(zip);
                    var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidDataException("ffmpeg.exe was not found in the download.");
                    Directory.CreateDirectory(_toolsDir);
                    entry.ExtractToFile(dest, overwrite: true);
                });
                ResultPath = dest;
                Close();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _status.Text = "Download failed: " + ex.Message;
                _go.IsEnabled = true;
                _go.Content = "Try again";
            }
            finally
            {
                try { File.Delete(zip); } catch { }
            }
        }
    }
}
