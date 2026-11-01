using FaRajni.Core.Data;
using FaRajni.Core.Models;
using Microsoft.Data.Sqlite;

namespace FaRajni.Core.Import;

/// <summary>Imports folders, videos, resume points and thumbnails from an old For Za BoyZ /
/// 3l Jaw <c>library.db</c> into FaRajni's catalog. The old database is opened read-only and
/// never modified.</summary>
public static class LegacyImporter
{
    public sealed record Result(int Folders, int Videos);

    /// <summary>The newest legacy library on this PC, if any (For Za BoyZ first, then 3l Jaw).</summary>
    public static string? FindLegacyDatabase(string roamingAppData)
    {
        foreach (var name in new[] { "ForZaBoyZ", "MyMovies" })
        {
            var p = Path.Combine(roamingAppData, name, "library.db");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static Result Import(string legacyDbPath, LibraryDatabase target, string thumbnailDir)
    {
        var folders = new List<(long Id, string Path)>();
        var videos = new List<(long FolderId, Video V, string? Thumb)>();

        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = legacyDbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            conn.Open();
            var cols = Columns(conn, "videos");
            string Col(string name, string fallback) => cols.Contains(name) ? name : fallback;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, path FROM folders";
                using var r = cmd.ExecuteReader();
                while (r.Read()) folders.Add((r.GetInt64(0), r.GetString(1)));
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"SELECT folder_id, file_path, title, duration_ms, {Col("file_size", "0")},
                                            date_added, file_modified, thumbnail_path,
                                            {Col("resume_position_ms", "0")}, {Col("last_played", "NULL")}
                                     FROM videos";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var v = new Video
                    {
                        FilePath = r.GetString(1), Title = r.GetString(2),
                        DurationMs = r.IsDBNull(3) ? 0 : r.GetInt64(3),
                        FileSize = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                        DateAdded = Date(r, 5) ?? DateTime.UtcNow,
                        FileModified = Date(r, 6) ?? DateTime.UtcNow,
                        ResumeMs = r.IsDBNull(8) ? 0 : r.GetInt64(8),
                        LastPlayed = Date(r, 9),
                    };
                    videos.Add((r.GetInt64(0), v, r.IsDBNull(7) ? null : r.GetString(7)));
                }
            }
        }

        Directory.CreateDirectory(thumbnailDir);
        var map = new Dictionary<long, long>();
        var imported = 0;
        target.Batch(() =>
        {
            foreach (var (id, path) in folders) map[id] = target.AddFolder(path);
            foreach (var (oldFolder, v, thumb) in videos)
            {
                if (!map.TryGetValue(oldFolder, out var fid)) continue;
                v.FolderId = fid;
                var newId = target.UpsertVideo(v);
                imported++;
                if (thumb is not null && File.Exists(thumb))
                {
                    var dest = Path.Combine(thumbnailDir, newId + ".jpg");
                    try
                    {
                        if (!File.Exists(dest)) File.Copy(thumb, dest);
                        target.SetThumbnail(newId, dest);
                    }
                    catch { /* a missing thumbnail is regenerated later */ }
                }
            }
        });
        return new Result(map.Count, imported);
    }

    private static HashSet<string> Columns(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) set.Add(r.GetString(1));
        return set;
    }

    private static DateTime? Date(SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null : LibraryDatabase.ParseDate(r.GetString(i)) is var d && d != DateTime.MinValue ? d : null;
}
