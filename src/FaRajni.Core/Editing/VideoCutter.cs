using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FaRajni.Core.Editing;

/// <summary>Outcome of a cut. Never throws to the caller — failures come back as
/// <see cref="Ok"/> = false with a human-readable <see cref="Error"/>.</summary>
public sealed record CutResult(bool Ok, string? OutputPath, string? Error);

/// <summary>Trims a video to a single [start, end] segment using ffmpeg stream-copy
/// (fast, lossless; the start snaps to the nearest keyframe). Standalone, no UI deps.</summary>
public sealed class VideoCutter
{
    private readonly string? _ffmpegPath;

    /// <param name="ffmpegPath">Explicit ffmpeg path; when null it is resolved from PATH
    /// and the WinGet Links shim.</param>
    public VideoCutter(string? ffmpegPath = null)
        => _ffmpegPath = ffmpegPath ?? ResolveFfmpeg();

    /// <summary>True when an ffmpeg executable was found and cutting is possible.</summary>
    public bool IsAvailable => _ffmpegPath is not null;

    /// <summary>Cut <paramref name="src"/> to the segment [startMs, endMs) into
    /// <paramref name="dest"/> (defaults to "&lt;name&gt; - cut&lt;ext&gt;" beside the source).</summary>
    public async Task<CutResult> CutAsync(string src, long startMs, long endMs,
                                          string? dest = null, CancellationToken ct = default)
    {
        if (_ffmpegPath is null)
            return new CutResult(false, null, "ffmpeg was not found. Install it and try again.");
        if (!File.Exists(src))
            return new CutResult(false, null, "The source file no longer exists.");
        if (endMs <= startMs)
            return new CutResult(false, null, "The end point must be after the start point.");

        dest ??= BuildDestPath(src);
        var startSec = (startMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        var durSec = ((endMs - startMs) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

        // -ss before -i = fast input seek; -t (duration) avoids the -to-relative-to-seek
        // ambiguity. -c copy = no re-encode; make_zero rebases timestamps so the clip starts at 0.
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(startSec);
        psi.ArgumentList.Add("-i");  psi.ArgumentList.Add(src);
        psi.ArgumentList.Add("-t");  psi.ArgumentList.Add(durSec);
        psi.ArgumentList.Add("-c");  psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-avoid_negative_ts"); psi.ArgumentList.Add("make_zero");
        psi.ArgumentList.Add(dest);

        try
        {
            using var proc = new Process { StartInfo = psi };
            var stderr = new StringBuilder();
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (proc.ExitCode == 0 && File.Exists(dest) && new FileInfo(dest).Length > 0)
                return new CutResult(true, dest, null);

            TryDelete(dest);   // don't leave a 0-byte/partial file behind
            return new CutResult(false, null, "ffmpeg could not cut this file.\n" + Tail(stderr.ToString()));
        }
        catch (OperationCanceledException)
        {
            TryDelete(dest);
            throw;
        }
        catch (Exception ex)
        {
            return new CutResult(false, null, ex.Message);
        }
    }

    /// <summary>"&lt;name&gt; - cut&lt;ext&gt;" beside the source, bumping to "(2)", "(3)"… if taken.</summary>
    public static string BuildDestPath(string src)
    {
        var dir = Path.GetDirectoryName(src) ?? "";
        var name = Path.GetFileNameWithoutExtension(src);
        var ext = Path.GetExtension(src);

        var candidate = Path.Combine(dir, $"{name} - cut{ext}");
        for (var n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(dir, $"{name} - cut ({n}){ext}");
        return candidate;
    }

    /// <summary>Find ffmpeg on PATH, then fall back to the per-user WinGet Links shim.</summary>
    public static string? ResolveFfmpeg()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; }
            catch { /* malformed PATH entry — skip */ }
        }

        var shim = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", exe);
        return File.Exists(shim) ? shim : null;
    }

    private static string Tail(string s, int lines = 4)
    {
        var all = s.TrimEnd().Split('\n');
        return string.Join('\n', all[Math.Max(0, all.Length - lines)..]).Trim();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
