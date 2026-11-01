using System.Diagnostics;
using System.Globalization;

namespace FaRajni.Core.Editing;

/// <summary>Finds ffmpeg. FaRajni doesn't ship it: it uses a copy downloaded into its own tools
/// folder, or one already installed on the PC (PATH / WinGet).</summary>
public static class FfmpegLocator
{
    public static string ToolsPath(string toolsDir) => Path.Combine(toolsDir, "ffmpeg.exe");

    public static string? Find(string toolsDir)
    {
        var local = ToolsPath(toolsDir);
        return File.Exists(local) ? local : VideoCutter.ResolveFfmpeg();
    }

    /// <summary>Grab one frame at <paramref name="atMs"/>, scaled to <paramref name="width"/> px, as JPEG.</summary>
    public static async Task<bool> ExtractFrameAsync(string ffmpeg, string src, long atMs, int width, string dest,
                                                     CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var a in new[]
                 {
                     "-y", "-loglevel", "error",
                     "-ss", (atMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture),
                     "-i", src, "-frames:v", "1", "-vf", $"scale={width}:-2", "-q:v", "5", dest,
                 })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        _ = proc.StandardError.ReadToEndAsync(ct);
        _ = proc.StandardOutput.ReadToEndAsync(ct);
        try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { proc.Kill(); } catch { } throw; }
        return proc.ExitCode == 0 && File.Exists(dest) && new FileInfo(dest).Length > 0;
    }
}
