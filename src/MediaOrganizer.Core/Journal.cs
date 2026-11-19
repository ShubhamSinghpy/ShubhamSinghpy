using Microsoft.Data.Sqlite;

namespace MediaOrganizer.Core;

public sealed record CopyRecord(string SourcePath, long Size, long MtimeTicks, string DestPath, string Sha256);

/// <summary>
/// Crash-safe record of every file that has been copied AND verified. It lives inside the destination
/// folder (.media_organizer\journal.db). After a crash, cancel or restart, files found here are not
/// copied again, so a resumed job never duplicates work.
/// </summary>
public sealed class Journal : IDisposable
{
    private readonly SqliteConnection _c;
    private readonly object _gate = new();

    public Journal(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
        Directory.CreateDirectory(dir);
        try { File.SetAttributes(dir, File.GetAttributes(dir) | FileAttributes.Hidden); } catch { /* cosmetic */ }

        _c = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        _c.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("CREATE TABLE IF NOT EXISTS copied(" +
             "source_path TEXT NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL, " +
             "dest_path TEXT NOT NULL, sha256 TEXT NOT NULL, verified_utc TEXT NOT NULL, " +
             "PRIMARY KEY(source_path, size, mtime));");
    }

    public CopyRecord? Find(string sourcePath, long size, long mtimeTicks)
    {
        lock (_gate)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "SELECT dest_path, sha256 FROM copied WHERE source_path=$p AND size=$s AND mtime=$m";
            cmd.Parameters.AddWithValue("$p", sourcePath);
            cmd.Parameters.AddWithValue("$s", size);
            cmd.Parameters.AddWithValue("$m", mtimeTicks);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new CopyRecord(sourcePath, size, mtimeTicks, r.GetString(0), r.GetString(1)) : null;
        }
    }

    public void Record(CopyRecord rec)
    {
        lock (_gate)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText =
                "INSERT OR REPLACE INTO copied(source_path, size, mtime, dest_path, sha256, verified_utc) " +
                "VALUES($p, $s, $m, $d, $h, $t)";
            cmd.Parameters.AddWithValue("$p", rec.SourcePath);
            cmd.Parameters.AddWithValue("$s", rec.Size);
            cmd.Parameters.AddWithValue("$m", rec.MtimeTicks);
            cmd.Parameters.AddWithValue("$d", rec.DestPath);
            cmd.Parameters.AddWithValue("$h", rec.Sha256);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
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
