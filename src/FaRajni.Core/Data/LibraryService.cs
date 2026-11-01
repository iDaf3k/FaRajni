using FaRajni.Core.Models;
using FaRajni.Core.Scanning;

namespace FaRajni.Core.Data;

/// <summary>Keeps the catalog in step with what is on disk.</summary>
public sealed class LibraryService
{
    private readonly LibraryDatabase _db;
    private readonly FolderScanner _scanner = new();

    public LibraryService(LibraryDatabase db) => _db = db;

    public long AddAndScanFolder(string path, DeleteMode mode = DeleteMode.Confirm)
    {
        var id = _db.AddFolder(path, mode);
        ScanFolder(id, path);
        return id;
    }

    public void RescanAll()
    {
        foreach (var f in _db.GetFolders()) ScanFolder(f.Id, f.Path);
    }

    /// <summary>Index every video under <paramref name="path"/> and drop rows for files that are gone.
    /// A folder that is temporarily unreachable (unplugged drive) is left alone rather than emptied.</summary>
    public void ScanFolder(long folderId, string path)
    {
        if (!Directory.Exists(path)) return;
        var found = new List<string>();
        _db.Batch(() =>
        {
            foreach (var file in _scanner.FindVideos(path))
            {
                FileInfo info;
                try { info = new FileInfo(file); _ = info.Length; }
                catch { continue; }
                found.Add(file);
                _db.UpsertVideo(new Video
                {
                    FolderId = folderId,
                    FilePath = file,
                    Title = Path.GetFileNameWithoutExtension(file),
                    FileSize = info.Length,
                    DateAdded = ArrivedUtc(info),
                    FileModified = info.LastWriteTimeUtc,
                });
            }
            _db.RemoveVideosNotIn(folderId, found);
        });
    }

    /// <summary>When the file landed in its folder. Windows stamps the creation time when a file is
    /// copied or downloaded (the last-write time keeps the original date), so it is the "date added".
    /// Falls back to the last-write time when the creation time is missing or bogus.</summary>
    public static DateTime ArrivedUtc(FileInfo info)
    {
        var created = info.CreationTimeUtc;
        var now = DateTime.UtcNow;
        if (created.Year < 1980) created = info.LastWriteTimeUtc;
        return created > now ? now : created;
    }
}
