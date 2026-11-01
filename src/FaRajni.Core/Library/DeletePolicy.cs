using FaRajni.Core.Models;

namespace FaRajni.Core.Library;

public enum DeleteAction
{
    /// <summary>Nothing can be deleted: every video is in a folder with deleting turned off.</summary>
    Blocked,
    /// <summary>Show a confirmation first (at least one video's folder asks).</summary>
    Confirm,
    /// <summary>Delete right away (with Undo).</summary>
    Direct,
}

/// <param name="Deletable">Videos that may be deleted.</param>
/// <param name="Skipped">Videos left alone because their folder has deleting turned off.</param>
/// <param name="BlockingFolder">When <see cref="DeleteAction.Blocked"/>, the folder to point the user at.</param>
public sealed record DeleteDecision(
    DeleteAction Action,
    IReadOnlyList<Video> Deletable,
    int Skipped,
    Folder? BlockingFolder);

public static class DeletePolicy
{
    public static DeleteDecision Decide(IReadOnlyList<Video> videos, IReadOnlyDictionary<long, Folder> folders)
    {
        DeleteMode ModeOf(Video v) => folders.TryGetValue(v.FolderId, out var f) ? f.DeleteMode : DeleteMode.Off;

        var ok = videos.Where(v => ModeOf(v) != DeleteMode.Off).ToList();
        var skipped = videos.Count - ok.Count;

        if (ok.Count == 0)
        {
            var first = videos.Select(v => folders.GetValueOrDefault(v.FolderId)).FirstOrDefault(f => f is not null);
            return new DeleteDecision(DeleteAction.Blocked, ok, skipped, first);
        }

        var action = ok.Any(v => ModeOf(v) == DeleteMode.Confirm) ? DeleteAction.Confirm : DeleteAction.Direct;
        return new DeleteDecision(action, ok, skipped, null);
    }
}
