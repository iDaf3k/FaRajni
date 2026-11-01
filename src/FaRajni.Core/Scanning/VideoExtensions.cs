using System;
using System.Collections.Generic;
using System.IO;

namespace FaRajni.Core.Scanning;

public static class VideoExtensions
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v",
        ".flv", ".webm", ".mpg", ".mpeg", ".ts", ".3gp"
    };

    public static bool IsVideo(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && Known.Contains(ext);
    }
}
