using Microsoft.Data.Sqlite;

namespace MediaOrganizer.Core;

/// <summary>
/// Local SQLite cache (%LocalAppData%\MediaOrganizer\cache.db): reverse-geocoded place names and
/// file hashes, so re-analysing a large library is fast and place lookups are never repeated.
/// </summary>
public sealed class AppCache : IDisposable
{
    private readonly SqliteConnection _c;
    private readonly object _gate = new();

    public static string DefaultPath => Path.Combine(OrganizerSettings.AppDataFolder, "cache.db");

    public AppCache(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _c = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        _c.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("CREATE TABLE IF NOT EXISTS places(key TEXT PRIMARY KEY, name TEXT NOT NULL);");
        Exec("CREATE TABLE IF NOT EXISTS hashes(path TEXT NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL, sha256 TEXT NOT NULL, PRIMARY KEY(path, size, mtime));");
    }

    /// <summary>Opens the default cache, or returns null if it cannot be opened (the app then simply runs without a cache).</summary>
    public static AppCache? TryOpenDefault()
    {
        try { return new AppCache(DefaultPath); }
        catch (Exception ex)
        {
            AppLog.Warn("Cache database unavailable; continuing without cache.", ex);
            return null;
        }
    }

    public string? GetPlace(string key)
    {
        lock (_gate)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "SELECT name FROM places WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void PutPlace(string key, string name)
    {
        lock (_gate)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO places(key, name) VALUES($k, $n)";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.ExecuteNonQuery();
        }
    }

    public string? GetHash(string path, long size, long mtimeTicks)
    {
        lock (_gate)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "SELECT sha256 FROM hashes WHERE path=$p AND size=$s AND mtime=$m";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$s", size);
            cmd.Parameters.AddWithValue("$m", mtimeTicks);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void PutHash(string path, long size, long mtimeTicks, string sha256)
    {
        lock (_gate)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO hashes(path, size, mtime, sha256) VALUES($p, $s, $m, $h)";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$s", size);
            cmd.Parameters.AddWithValue("$m", mtimeTicks);
            cmd.Parameters.AddWithValue("$h", sha256);
            cmd.ExecuteNonQuery();
        }
    }

    // ---- optional face recognition data (only touched when the user enables the feature) ----------

    private bool _faceTables;
    private void EnsureFaceTables()
    {
        if (_faceTables) return;
        Exec("CREATE TABLE IF NOT EXISTS face_scans(path TEXT NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL, " +
             "opts TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(path, size, mtime, opts));");
        Exec("CREATE TABLE IF NOT EXISTS face_overrides(path TEXT NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL, " +
             "added TEXT NOT NULL, removed TEXT NOT NULL, PRIMARY KEY(path, size, mtime));");
        _faceTables = true;
    }

    public string? GetFaceScan(string path, long size, long mtimeTicks, string opts)
    {
        lock (_gate)
        {
            EnsureFaceTables();
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "SELECT json FROM face_scans WHERE path=$p AND size=$s AND mtime=$m AND opts=$o";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$s", size);
            cmd.Parameters.AddWithValue("$m", mtimeTicks);
            cmd.Parameters.AddWithValue("$o", opts);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void PutFaceScan(string path, long size, long mtimeTicks, string opts, string json)
    {
        lock (_gate)
        {
            EnsureFaceTables();
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO face_scans(path, size, mtime, opts, json) VALUES($p, $s, $m, $o, $j)";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$s", size);
            cmd.Parameters.AddWithValue("$m", mtimeTicks);
            cmd.Parameters.AddWithValue("$o", opts);
            cmd.Parameters.AddWithValue("$j", json);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Manual corrections for one file: people the user added (semicolon separated) and rejected.</summary>
    public (string Added, string Removed)? GetFaceOverride(string path, long size, long mtimeTicks)
    {
        lock (_gate)
        {
            EnsureFaceTables();
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "SELECT added, removed FROM face_overrides WHERE path=$p AND size=$s AND mtime=$m";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$s", size);
            cmd.Parameters.AddWithValue("$m", mtimeTicks);
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetString(0), r.GetString(1)) : null;
        }
    }

    public void PutFaceOverride(string path, long size, long mtimeTicks, string added, string removed)
    {
        lock (_gate)
        {
            EnsureFaceTables();
            using var cmd = _c.CreateCommand();
            if (added.Length == 0 && removed.Length == 0)
            {
                cmd.CommandText = "DELETE FROM face_overrides WHERE path=$p AND size=$s AND mtime=$m";
            }
            else
            {
                cmd.CommandText = "INSERT OR REPLACE INTO face_overrides(path, size, mtime, added, removed) VALUES($p, $s, $m, $a, $r)";
                cmd.Parameters.AddWithValue("$a", added);
                cmd.Parameters.AddWithValue("$r", removed);
            }
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$s", size);
            cmd.Parameters.AddWithValue("$m", mtimeTicks);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Deletes every stored face fingerprint and manual correction.</summary>
    public void ClearFaceData()
    {
        lock (_gate)
        {
            EnsureFaceTables();
            Exec("DELETE FROM face_scans;");
            Exec("DELETE FROM face_overrides;");
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _c.Dispose();
}
