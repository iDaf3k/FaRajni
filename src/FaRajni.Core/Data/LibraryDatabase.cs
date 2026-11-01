using System.Globalization;
using FaRajni.Core.Models;
using Microsoft.Data.Sqlite;

namespace FaRajni.Core.Data;

/// <summary>SQLite catalog of folders and videos. One connection, every call serialized
/// through a lock (scans run on a background thread while the UI reads).</summary>
public sealed class LibraryDatabase : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();
    private SqliteTransaction? _tx;

    public LibraryDatabase(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Execute("PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
    }

    public void Initialize()
    {
        lock (_gate)
        {
            Execute(@"
                CREATE TABLE IF NOT EXISTS folders (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    name TEXT NOT NULL,
                    hidden INTEGER NOT NULL DEFAULT 0,
                    delete_mode INTEGER NOT NULL DEFAULT 1,
                    date_added TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS videos (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
                    file_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    title TEXT NOT NULL,
                    duration_ms INTEGER NOT NULL DEFAULT 0,
                    file_size INTEGER NOT NULL DEFAULT 0,
                    date_added TEXT NOT NULL,
                    file_modified TEXT NOT NULL,
                    thumbnail_path TEXT NULL,
                    resume_ms INTEGER NOT NULL DEFAULT 0,
                    last_played TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_videos_folder ON videos(folder_id);");
        }
    }

    /// <summary>Run <paramref name="work"/> inside one transaction (much faster for scans/imports).</summary>
    public void Batch(Action work)
    {
        lock (_gate)
        {
            if (_tx is not null) { work(); return; }
            _tx = _conn.BeginTransaction();
            try { work(); _tx.Commit(); }
            catch { _tx.Rollback(); throw; }
            finally { _tx.Dispose(); _tx = null; }
        }
    }

    // ---------------- folders ----------------

    public List<Folder> GetFolders()
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT id, path, name, hidden, delete_mode, date_added FROM folders ORDER BY name COLLATE NOCASE");
            using var r = cmd.ExecuteReader();
            var list = new List<Folder>();
            while (r.Read())
                list.Add(new Folder
                {
                    Id = r.GetInt64(0), Path = r.GetString(1), Name = r.GetString(2),
                    Hidden = r.GetInt64(3) != 0, DeleteMode = (DeleteMode)r.GetInt64(4),
                    DateAdded = ParseDate(r.GetString(5)),
                });
            return list;
        }
    }

    /// <summary>Add a folder (or return the existing one's id when the path is already tracked).</summary>
    public long AddFolder(string path, DeleteMode mode = DeleteMode.Confirm, bool hidden = false, string? name = null)
    {
        lock (_gate)
        {
            using (var ins = Cmd(@"INSERT INTO folders(path, name, hidden, delete_mode, date_added)
                                   VALUES($p, $n, $h, $m, $d) ON CONFLICT(path) DO NOTHING"))
            {
                ins.Parameters.AddWithValue("$p", path);
                ins.Parameters.AddWithValue("$n", name ?? Folder.NameFromPath(path));
                ins.Parameters.AddWithValue("$h", hidden ? 1 : 0);
                ins.Parameters.AddWithValue("$m", (int)mode);
                ins.Parameters.AddWithValue("$d", FormatDate(DateTime.UtcNow));
                ins.ExecuteNonQuery();
            }
            using var sel = Cmd("SELECT id FROM folders WHERE path = $p");
            sel.Parameters.AddWithValue("$p", path);
            return (long)sel.ExecuteScalar()!;
        }
    }

    public void UpdateFolder(Folder f)
    {
        lock (_gate)
        {
            using var cmd = Cmd("UPDATE folders SET name = $n, hidden = $h, delete_mode = $m WHERE id = $id");
            cmd.Parameters.AddWithValue("$n", f.Name);
            cmd.Parameters.AddWithValue("$h", f.Hidden ? 1 : 0);
            cmd.Parameters.AddWithValue("$m", (int)f.DeleteMode);
            cmd.Parameters.AddWithValue("$id", f.Id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Stop tracking a folder. Its video rows go with it; files on disk are untouched.</summary>
    public void RemoveFolder(long id)
    {
        lock (_gate)
        {
            using var cmd = Cmd("DELETE FROM folders WHERE id = $id");
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------- videos ----------------

    private const string VideoCols =
        "id, folder_id, file_path, title, duration_ms, file_size, date_added, file_modified, thumbnail_path, resume_ms, last_played";

    public List<Video> GetVideos()
    {
        lock (_gate)
        {
            using var cmd = Cmd($"SELECT {VideoCols} FROM videos");
            return ReadVideos(cmd);
        }
    }

    public Video? GetVideo(long id)
    {
        lock (_gate)
        {
            using var cmd = Cmd($"SELECT {VideoCols} FROM videos WHERE id = $id");
            cmd.Parameters.AddWithValue("$id", id);
            return ReadVideos(cmd).FirstOrDefault();
        }
    }

    /// <summary>Insert a video, or refresh size/dates/folder of an existing row (keeping its
    /// resume point, thumbnail and duration). Returns the row id.</summary>
    public long UpsertVideo(Video v)
    {
        lock (_gate)
        {
            using (var cmd = Cmd(@"
                INSERT INTO videos(folder_id, file_path, title, duration_ms, file_size, date_added, file_modified, thumbnail_path, resume_ms, last_played)
                VALUES($f, $p, $t, $dur, $s, $da, $fm, $th, $r, $lp)
                ON CONFLICT(file_path) DO UPDATE SET
                    folder_id = excluded.folder_id,
                    file_size = excluded.file_size,
                    file_modified = excluded.file_modified,
                    date_added = excluded.date_added,
                    duration_ms = CASE WHEN excluded.file_size <> videos.file_size THEN 0 ELSE videos.duration_ms END"))
            {
                cmd.Parameters.AddWithValue("$f", v.FolderId);
                cmd.Parameters.AddWithValue("$p", v.FilePath);
                cmd.Parameters.AddWithValue("$t", v.Title);
                cmd.Parameters.AddWithValue("$dur", v.DurationMs);
                cmd.Parameters.AddWithValue("$s", v.FileSize);
                cmd.Parameters.AddWithValue("$da", FormatDate(v.DateAdded == default ? DateTime.UtcNow : v.DateAdded));
                cmd.Parameters.AddWithValue("$fm", FormatDate(v.FileModified));
                cmd.Parameters.AddWithValue("$th", (object?)v.ThumbnailPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$r", v.ResumeMs);
                cmd.Parameters.AddWithValue("$lp", v.LastPlayed is { } lp ? FormatDate(lp) : DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            using var sel = Cmd("SELECT id FROM videos WHERE file_path = $p");
            sel.Parameters.AddWithValue("$p", v.FilePath);
            return (long)sel.ExecuteScalar()!;
        }
    }

    /// <summary>Drop rows of <paramref name="folderId"/> whose file was not seen in the latest scan.</summary>
    public int RemoveVideosNotIn(long folderId, IReadOnlyCollection<string> keepPaths)
    {
        lock (_gate)
        {
            var keep = new HashSet<string>(keepPaths, StringComparer.OrdinalIgnoreCase);
            var stale = new List<long>();
            using (var cmd = Cmd("SELECT id, file_path FROM videos WHERE folder_id = $f"))
            {
                cmd.Parameters.AddWithValue("$f", folderId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (!keep.Contains(r.GetString(1))) stale.Add(r.GetInt64(0));
            }
            foreach (var id in stale) DeleteVideo(id);
            return stale.Count;
        }
    }

    public void DeleteVideo(long id)
    {
        lock (_gate)
        {
            using var cmd = Cmd("DELETE FROM videos WHERE id = $id");
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetThumbnail(long id, string? path) => SetColumn(id, "thumbnail_path", (object?)path ?? DBNull.Value);
    public void SetDuration(long id, long ms) => SetColumn(id, "duration_ms", ms);

    public void SaveResume(long id, long resumeMs, DateTime lastPlayedUtc)
    {
        lock (_gate)
        {
            using var cmd = Cmd("UPDATE videos SET resume_ms = $r, last_played = $lp WHERE id = $id");
            cmd.Parameters.AddWithValue("$r", resumeMs);
            cmd.Parameters.AddWithValue("$lp", FormatDate(lastPlayedUtc));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public int CountVideos()
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT COUNT(*) FROM videos");
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void Dispose()
    {
        lock (_gate) _conn.Dispose();
    }

    // ---------------- helpers ----------------

    private void SetColumn(long id, string column, object value)
    {
        lock (_gate)
        {
            using var cmd = Cmd($"UPDATE videos SET {column} = $v WHERE id = $id");
            cmd.Parameters.AddWithValue("$v", value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    private static List<Video> ReadVideos(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<Video>();
        while (r.Read())
            list.Add(new Video
            {
                Id = r.GetInt64(0), FolderId = r.GetInt64(1), FilePath = r.GetString(2), Title = r.GetString(3),
                DurationMs = r.GetInt64(4), FileSize = r.GetInt64(5),
                DateAdded = ParseDate(r.GetString(6)), FileModified = ParseDate(r.GetString(7)),
                ThumbnailPath = r.IsDBNull(8) ? null : r.GetString(8),
                ResumeMs = r.GetInt64(9),
                LastPlayed = r.IsDBNull(10) ? null : ParseDate(r.GetString(10)),
            });
        return list;
    }

    private SqliteCommand Cmd(string sql)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        return cmd;
    }

    private void Execute(string sql)
    {
        using var cmd = Cmd(sql);
        cmd.ExecuteNonQuery();
    }

    internal static string FormatDate(DateTime d) =>
        DateTime.SpecifyKind(d, d.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : d.Kind)
            .ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    internal static DateTime ParseDate(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d : DateTime.MinValue;
}
