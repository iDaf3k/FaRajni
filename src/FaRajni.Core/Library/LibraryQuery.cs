using FaRajni.Core.Models;

namespace FaRajni.Core.Library;

public sealed record LibraryFilter(
    long? FolderId = null,
    string? Search = null,
    bool HideDuplicates = false,
    VideoSort Sort = VideoSort.RecordedNewest);

/// <summary>Pure in-memory filtering/sorting of the catalog (fast enough for tens of thousands
/// of rows, and keeps the UI free of SQL).</summary>
public static class LibraryQuery
{
    public static List<Video> Apply(IEnumerable<Video> videos, IReadOnlyDictionary<long, Folder> folders, LibraryFilter f)
    {
        IEnumerable<Video> q = videos.Where(v => folders.ContainsKey(v.FolderId));

        // A specific folder shows everything in it (even when hidden); "All videos" skips hidden folders.
        q = f.FolderId is { } id
            ? q.Where(v => v.FolderId == id)
            : q.Where(v => !folders[v.FolderId].Hidden);

        var search = f.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
            q = q.Where(v => v.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase));

        q = f.Sort switch
        {
            VideoSort.Name => q.OrderBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase),
            VideoSort.RecentlyAdded => q.OrderByDescending(v => v.DateAdded).ThenByDescending(v => v.Id),
            VideoSort.RecordedOldest => q.OrderBy(v => v.FileModified),
            VideoSort.Longest => q.OrderByDescending(v => v.DurationMs),
            _ => q.OrderByDescending(v => v.FileModified),
        };

        var list = q.ToList();
        return f.HideDuplicates ? WithoutDuplicates(list) : list;
    }

    /// <summary>Keep the first of each group of identical files. Identical = same byte size, and
    /// the same duration when both durations are known (size alone is a strong signal already).</summary>
    public static List<Video> WithoutDuplicates(List<Video> ordered)
    {
        var seen = new Dictionary<long, List<Video>>();
        var result = new List<Video>(ordered.Count);
        foreach (var v in ordered)
        {
            if (v.FileSize <= 0) { result.Add(v); continue; }
            if (!seen.TryGetValue(v.FileSize, out var group)) seen[v.FileSize] = group = new List<Video>();
            var dupe = group.Any(g => g.DurationMs == 0 || v.DurationMs == 0 || g.DurationMs == v.DurationMs);
            group.Add(v);
            if (!dupe) result.Add(v);
        }
        return result;
    }

    /// <summary>Where a video should resume: only from a meaningful point that isn't the very end.</summary>
    public static long ResumePointFor(long positionMs, long lengthMs)
        => positionMs > 5_000 && (lengthMs <= 0 || positionMs < lengthMs - 5_000) ? positionMs : 0;
}
