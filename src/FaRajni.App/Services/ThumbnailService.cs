using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FaRajni.App.Interop;
using FaRajni.Core.Data;
using FaRajni.Core.Models;

namespace FaRajni.App.Services;

/// <summary>Loads tile thumbnails on demand. Only tiles that are actually on screen ask (the grid
/// is virtualized), newest request first, on one background STA thread. Missing thumbnails come
/// from the Windows Shell and are cached as JPEG; missing durations are filled in at the same time.</summary>
public sealed class ThumbnailService : IDisposable
{
    public const int DecodeWidth = 360;

    private readonly LibraryDatabase _db;
    private readonly Dispatcher _ui;
    private readonly ConcurrentStack<Job> _stack = new();
    private readonly ConcurrentDictionary<long, byte> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread _worker;
    private volatile bool _disposed;

    private sealed record Job(Video Video, Action<BitmapSource?, long> Done);

    public ThumbnailService(LibraryDatabase db, Dispatcher ui)
    {
        _db = db;
        _ui = ui;
        _worker = new Thread(Run) { IsBackground = true, Name = "Thumbnails", Priority = ThreadPriority.BelowNormal };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    /// <summary>Ask for a video's thumbnail. <paramref name="done"/> runs on the UI thread with the
    /// image (or null) and the duration in ms (0 when still unknown).</summary>
    public void Request(Video video, Action<BitmapSource?, long> done)
    {
        if (_disposed || !_pending.TryAdd(video.Id, 0)) return;
        _stack.Push(new Job(video, done));
        _signal.Release();
    }

    /// <summary>Forget a video's cached image (e.g. after the file changed).</summary>
    public static void DeleteCached(long videoId)
    {
        try { File.Delete(Path.Combine(AppPaths.ThumbnailDir, videoId + ".jpg")); } catch { }
    }

    private void Run()
    {
        while (!_disposed)
        {
            _signal.Wait();
            if (_disposed || !_stack.TryPop(out var req)) continue;
            BitmapSource? img = null;
            long duration = req.Video.DurationMs;
            try { img = Produce(req.Video, ref duration); }
            catch (Exception ex) { AppPaths.LogError("Thumbnail", ex); }
            finally { _pending.TryRemove(req.Video.Id, out _); }
            var d = duration;
            _ui.BeginInvoke(DispatcherPriority.Background, () => req.Done(img, d));
        }
    }

    private BitmapSource? Produce(Video v, ref long duration)
    {
        if (duration <= 0 && File.Exists(v.FilePath))
        {
            duration = ShellMedia.GetDurationMs(v.FilePath);
            if (duration > 0) _db.SetDuration(v.Id, duration);
        }

        if (!string.IsNullOrEmpty(v.ThumbnailPath) && File.Exists(v.ThumbnailPath))
            return LoadFile(v.ThumbnailPath);

        if (!File.Exists(v.FilePath)) return null;
        var shell = ShellMedia.GetThumbnail(v.FilePath, DecodeWidth);
        if (shell is null) return null;

        var dest = Path.Combine(AppPaths.ThumbnailDir, v.Id + ".jpg");
        var enc = new JpegBitmapEncoder { QualityLevel = 82 };
        enc.Frames.Add(BitmapFrame.Create(shell));
        using (var fs = File.Create(dest)) enc.Save(fs);
        _db.SetThumbnail(v.Id, dest);
        v.ThumbnailPath = dest;
        return shell;
    }

    private static BitmapSource LoadFile(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.DecodePixelWidth = DecodeWidth;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public void Dispose()
    {
        _disposed = true;
        _signal.Release();
    }
}
