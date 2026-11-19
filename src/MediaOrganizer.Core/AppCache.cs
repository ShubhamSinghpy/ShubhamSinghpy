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

    private void Exec(string sql)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _c.Dispose();
}
