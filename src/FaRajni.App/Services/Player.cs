using System.Globalization;
using LibVLCSharp.Shared;

namespace FaRajni.App.Services;

/// <summary>One reusable VLC media player for the whole session (creating a player per video
/// was part of why the old app was slow to start playback).</summary>
public sealed class Player : IDisposable
{
    private readonly LibVLC _vlc;
    public MediaPlayer MediaPlayer { get; }

    private string? _path;
    private float _rate = 1f;
    private bool _pauseOnStart;

    public Player(LibVLC vlc)
    {
        _vlc = vlc;
        MediaPlayer = new MediaPlayer(vlc) { EnableHardwareDecoding = true };
        // A finished clip that was re-opened from a paused editor should stay paused. Never call
        // back into VLC from its own event thread (deadlocks on Game-DVR files): hop to the UI.
        // Wait for the clock to move (a frame has been shown) so the picture and playhead update first.
        MediaPlayer.TimeChanged += (_, e) =>
        {
            if (!_pauseOnStart || e.Time <= 0) return;
            _pauseOnStart = false;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => MediaPlayer.SetPause(true));
        };
    }

    public int Volume { get; set; } = 100;

    public void Open(string filePath, long resumeMs)
    {
        _pauseOnStart = false;
        Start(filePath, resumeMs);
    }

    private void Start(string filePath, long resumeMs)
    {
        // Re-assert audio before Play(), on the UI thread: reusing a MediaPlayer across clips can
        // otherwise leave the next one muted, and touching it from VLC's own callbacks deadlocks
        // on Xbox Game-DVR files.
        _path = filePath;
        MediaPlayer.Mute = false;
        MediaPlayer.Volume = Volume;

        using var media = new Media(_vlc, new Uri(filePath));
        media.AddOption(":file-caching=300");   // VLC's default 1000 ms delays every start
        if (resumeMs > 0)
            // Start-time option instead of a seek after Playing: a runtime seek hangs VLC on Game-DVR MP4s.
            media.AddOption(string.Format(CultureInfo.InvariantCulture, ":start-time={0:0.###}", resumeMs / 1000.0));
        MediaPlayer.Play(media);
        if (_rate != 1f) MediaPlayer.SetRate(_rate);
    }

    public bool IsPlaying => MediaPlayer.IsPlaying;
    public long TimeMs => MediaPlayer.Time;
    public long LengthMs => MediaPlayer.Length;

    /// <summary>Play/pause. Playing a finished clip starts it again from the beginning.</summary>
    public void TogglePause()
    {
        if (IsFinished) Seek(0);
        else MediaPlayer.Pause();
    }

    public void SetPaused(bool paused)
    {
        if (!paused && IsFinished) Seek(0);
        else MediaPlayer.SetPause(paused);
    }
    public void SetRate(float rate)
    {
        _rate = rate;
        MediaPlayer.SetRate(rate);
    }

    public bool Muted
    {
        get => MediaPlayer.Mute;
        set => MediaPlayer.Mute = value;
    }

    public void SetVolume(int v)
    {
        Volume = Math.Clamp(v, 0, 100);
        MediaPlayer.Volume = Volume;
        if (Volume > 0 && MediaPlayer.Mute) MediaPlayer.Mute = false;
    }

    /// <summary>True once the clip has played to the end (or stopped): VLC has closed it, so a
    /// plain seek no longer does anything.</summary>
    public bool IsFinished => _path is not null && MediaPlayer.State is VLCState.Ended or VLCState.Stopped or VLCState.Error;

    public void Seek(long ms, bool resume = true)
    {
        ms = Math.Max(0, ms);
        if (IsFinished)
        {
            // Re-open at the target instead of Play() + Time: the seek raced the restart and was
            // dropped, leaving the picture frozen on the last frame.
            _pauseOnStart = !resume;
            Start(_path!, ms);
            return;
        }
        MediaPlayer.Time = ms;
        // Seeking while paused doesn't always take on Game-DVR files; resuming makes it land.
        if (resume) MediaPlayer.SetPause(false);
    }

    public void Stop() => MediaPlayer.Stop();

    public void Dispose()
    {
        try { MediaPlayer.Stop(); } catch { }
        MediaPlayer.Dispose();
    }
}
