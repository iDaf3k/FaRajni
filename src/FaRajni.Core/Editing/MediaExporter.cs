using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FaRajni.Core.Editing;

/// <summary>Outcome of an export. Never throws to the caller — failures come back as
/// <see cref="Ok"/> = false with a human-readable <see cref="Error"/>.</summary>
public sealed record MediaResult(bool Ok, string? OutputPath, string? Error);

/// <summary>ffmpeg-backed exports that complement <see cref="VideoCutter"/>: concatenating
/// several clips, extracting the audio track to MP3, and rendering a range to an animated GIF.
/// Standalone, no UI dependencies; failures are reported, never thrown.</summary>
public sealed class MediaExporter
{
    private readonly string? _ffmpegPath;

    public MediaExporter(string? ffmpegPath = null)
        => _ffmpegPath = ffmpegPath ?? VideoCutter.ResolveFfmpeg();

    /// <summary>True when an ffmpeg executable was found and exporting is possible.</summary>
    public bool IsAvailable => _ffmpegPath is not null;

    // ---- Merge / concatenate ----

    /// <summary>Concatenate <paramref name="sources"/> (in the given order) into one file.
    /// Tries a fast lossless stream-copy first; if that fails (mismatched codecs) it re-encodes.</summary>
    public async Task<MediaResult> MergeAsync(IReadOnlyList<string> sources, string? dest = null,
                                              CancellationToken ct = default)
    {
        if (_ffmpegPath is null) return NotFound();
        if (sources is null || sources.Count < 2)
            return new MediaResult(false, null, "Select at least two videos to merge.");
        foreach (var s in sources)
            if (!File.Exists(s)) return new MediaResult(false, null, "One of the videos no longer exists.");

        dest ??= BuildSiblingPath(sources[0], " - merged", Path.GetExtension(sources[0]));

        // ffmpeg concat demuxer reads a list file of `file '<path>'` lines.
        var listPath = Path.Combine(Path.GetTempPath(), "forzaboyz_concat_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var sb = new StringBuilder();
            foreach (var s in sources)
                sb.Append("file '").Append(s.Replace("'", "'\\''")).Append("'\n");
            await File.WriteAllTextAsync(listPath, sb.ToString(), ct).ConfigureAwait(false);

            // Attempt 1: stream-copy (fast, lossless) — works when inputs share codecs/params.
            var copy = await RunAsync(a =>
            {
                a("-y"); a("-f"); a("concat"); a("-safe"); a("0"); a("-i"); a(listPath);
                a("-c"); a("copy"); a(dest);
            }, ct).ConfigureAwait(false);
            if (copy.ok && GoodOutput(dest)) return new MediaResult(true, dest, null);
            TryDelete(dest);

            // Attempt 2: re-encode (handles mismatched inputs).
            var enc = await RunAsync(a =>
            {
                a("-y"); a("-f"); a("concat"); a("-safe"); a("0"); a("-i"); a(listPath);
                a("-c:v"); a("libx264"); a("-preset"); a("veryfast"); a("-crf"); a("20");
                a("-c:a"); a("aac"); a("-b:a"); a("192k"); a(dest);
            }, ct).ConfigureAwait(false);
            if (enc.ok && GoodOutput(dest)) return new MediaResult(true, dest, null);

            TryDelete(dest);
            return new MediaResult(false, null, "ffmpeg could not merge these videos.\n" + Tail(enc.stderr));
        }
        catch (OperationCanceledException) { TryDelete(dest); throw; }
        catch (Exception ex) { return new MediaResult(false, null, ex.Message); }
        finally { TryDelete(listPath); }
    }

    // ---- Extract audio (MP3) ----

    /// <summary>Extract the audio track of <paramref name="src"/> to an MP3 beside it.</summary>
    public async Task<MediaResult> ExtractAudioAsync(string src, string? dest = null, CancellationToken ct = default)
    {
        if (_ffmpegPath is null) return NotFound();
        if (!File.Exists(src)) return new MediaResult(false, null, "The source file no longer exists.");

        dest ??= BuildSiblingPath(src, "", ".mp3");
        try
        {
            var r = await RunAsync(a =>
            {
                a("-y"); a("-i"); a(src);
                a("-vn"); a("-c:a"); a("libmp3lame"); a("-q:a"); a("2"); a(dest);
            }, ct).ConfigureAwait(false);

            if (r.ok && GoodOutput(dest)) return new MediaResult(true, dest, null);
            TryDelete(dest);
            return new MediaResult(false, null, "ffmpeg could not extract the audio.\n" + Tail(r.stderr));
        }
        catch (OperationCanceledException) { TryDelete(dest); throw; }
        catch (Exception ex) { return new MediaResult(false, null, ex.Message); }
    }

    // ---- GIF from a range ----

    /// <summary>Render [startMs, endMs) of <paramref name="src"/> to an animated GIF, using a
    /// generated palette for good colour quality.</summary>
    public async Task<MediaResult> ExportGifAsync(string src, long startMs, long endMs, string? dest = null,
                                                  int fps = 15, int width = 480, CancellationToken ct = default)
    {
        if (_ffmpegPath is null) return NotFound();
        if (!File.Exists(src)) return new MediaResult(false, null, "The source file no longer exists.");
        if (endMs <= startMs) return new MediaResult(false, null, "The end point must be after the start point.");

        dest ??= BuildSiblingPath(src, "", ".gif");
        var startSec = (startMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        var durSec = ((endMs - startMs) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        var filter = $"fps={fps},scale={width}:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse";

        try
        {
            var r = await RunAsync(a =>
            {
                a("-y"); a("-ss"); a(startSec); a("-t"); a(durSec); a("-i"); a(src);
                a("-filter_complex"); a(filter); a("-loop"); a("0"); a(dest);
            }, ct).ConfigureAwait(false);

            if (r.ok && GoodOutput(dest)) return new MediaResult(true, dest, null);
            TryDelete(dest);
            return new MediaResult(false, null, "ffmpeg could not create the GIF.\n" + Tail(r.stderr));
        }
        catch (OperationCanceledException) { TryDelete(dest); throw; }
        catch (Exception ex) { return new MediaResult(false, null, ex.Message); }
    }

    // ---- helpers ----

    /// <summary>"&lt;name&gt;&lt;suffix&gt;&lt;ext&gt;" beside the source, bumping "(2)", "(3)"… if taken.</summary>
    public static string BuildSiblingPath(string src, string suffix, string ext)
    {
        var dir = Path.GetDirectoryName(src) ?? "";
        var name = Path.GetFileNameWithoutExtension(src);
        var candidate = Path.Combine(dir, $"{name}{suffix}{ext}");
        for (var n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(dir, $"{name}{suffix} ({n}){ext}");
        return candidate;
    }

    private async Task<(bool ok, string stderr)> RunAsync(Action<Action<string>> buildArgs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        buildArgs(psi.ArgumentList.Add);

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return (proc.ExitCode == 0, stderr.ToString());
    }

    private static MediaResult NotFound()
        => new(false, null, "ffmpeg was not found. Install it and try again.");

    private static bool GoodOutput(string dest) => File.Exists(dest) && new FileInfo(dest).Length > 0;

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
