namespace FaRajni.Core.Models;

/// <summary>What the Delete key / trash buttons do for videos in a folder.</summary>
public enum DeleteMode
{
    /// <summary>Deleting is disabled for this folder.</summary>
    Off = 0,
    /// <summary>Ask for confirmation before moving to the Recycle Bin (default).</summary>
    Confirm = 1,
    /// <summary>Move straight to the Recycle Bin; an Undo toast is offered.</summary>
    Direct = 2,
}

public sealed class Folder
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Hidden folders stay tracked, but their videos are left out of All videos,
    /// Continue watching and search. The folder itself can still be opened.</summary>
    public bool Hidden { get; set; }
    public DeleteMode DeleteMode { get; set; } = DeleteMode.Confirm;
    public DateTime DateAdded { get; set; }

    public static string NameFromPath(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}

public sealed class Video
{
    public long Id { get; set; }
    public long FolderId { get; set; }
    public string FilePath { get; set; } = "";
    public string Title { get; set; } = "";
    public long DurationMs { get; set; }
    public long FileSize { get; set; }
    public DateTime DateAdded { get; set; }
    /// <summary>File last-write time; shown and sorted as the "recorded" date.</summary>
    public DateTime FileModified { get; set; }
    public string? ThumbnailPath { get; set; }
    public long ResumeMs { get; set; }
    public DateTime? LastPlayed { get; set; }
}

public enum VideoSort
{
    Name,
    RecentlyAdded,
    RecordedNewest,
    RecordedOldest,
    Longest,
}
