using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>The long steps opening a library may take, in this order.</summary>
public enum CatalogOpenPhase
{
    /// <summary>Copy of the catalog before its format changes.</summary>
    SafetyCopy,
    /// <summary>Picture bytes leave the catalog for files beside it (schema 15).</summary>
    Pictures,
    /// <summary>Entries are written anew, smaller; the tables for sorting and filters fill again (schema 16 and older).</summary>
    Entries,
    /// <summary>Picture files are encrypted or decrypted, finishing an interrupted switch.</summary>
    PictureEncryption,
}

/// <summary>Progress of one phase; <paramref name="Total"/> 0 means the amount is unknown.</summary>
public sealed record CatalogOpenProgress(CatalogOpenPhase Phase, long Done, long Total);

/// <summary>What opening a library will do, read without changing it (<see cref="CatalogRepository.Inspect"/>).</summary>
public sealed record CatalogOpenPlan(long FromVersion, long ToVersion, IReadOnlyList<CatalogOpenPhase> Phases,
    long Media, long Pictures, long CatalogBytes, long FreeBytes, TimeSpan Estimate)
{
    /// <summary>The format changes; the user is asked first.</summary>
    public bool Upgrades => FromVersion is > 0 && FromVersion < ToVersion;
    /// <summary>Anything that takes noticeably long happens.</summary>
    public bool HasWork => Phases.Count > 0;
    /// <summary>Room for the safety copy (the catalog once more) and some headroom.</summary>
    public long RequiredBytes => Upgrades ? CatalogBytes + CatalogBytes / 10 + 64L * 1024 * 1024 : 0;
    public bool HasRoom => FreeBytes >= RequiredBytes;
}

public sealed partial class CatalogRepository
{
    /// <summary>Reads what opening <paramref name="databasePath"/> will do, without changing anything. An encrypted library
    /// needs its <paramref name="key"/>.</summary>
    public static CatalogOpenPlan Inspect(string databasePath, CatalogKey? key = null)
    {
        var path = System.IO.Path.GetFullPath(databasePath);
        long version = 0, media = 0, pictures = 0;
        var bytes = File.Exists(path) ? new FileInfo(path).Length + (File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0) : 0;
        if (bytes > 0)
        {
            using var connection = new SqliteConnection(CatalogEncryption.ConnectionString(path, key, SqliteOpenMode.ReadOnly).ToString());
            connection.Open();
            long Scalar(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar() ?? 0L); }
            bool Has(string table) { using var command = connection.CreateCommand(); command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name"; command.Parameters.AddWithValue("$name", table); return command.ExecuteScalar() is not null; }
            version = Scalar("PRAGMA user_version");
            if (version is > 0 && version < SchemaVersion)
            {
                if (Has("media")) media = Scalar("SELECT count(*) FROM media");
                if (version < 15)
                {
                    if (Has("media_assets")) pictures += Scalar("SELECT count(*) FROM media_assets");
                    if (Has("tag_assets")) pictures += Scalar("SELECT count(*) FROM tag_assets");
                    if (Has("legacy_blobs")) pictures += Scalar("SELECT count(*) FROM legacy_blobs");
                }
            }
        }
        var phases = new List<CatalogOpenPhase>();
        if (version is > 0 && version < SchemaVersion)
        {
            phases.Add(CatalogOpenPhase.SafetyCopy);
            if (pictures > 0) phases.Add(CatalogOpenPhase.Pictures);
            if (media > 0) phases.Add(CatalogOpenPhase.Entries);
        }
        var pending = key is null ? 0 : AssetStore.PendingRewrite(path);
        if (pending > 0) phases.Add(CatalogOpenPhase.PictureEncryption);
        long free;
        try { free = new DriveInfo(System.IO.Path.GetPathRoot(path)!).AvailableFreeSpace; } catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { free = long.MaxValue; }
        // Measured on a takeover catalog (1.8 GB, 125,000 media, 31,000 pictures): about 215 s on an SSD.
        var seconds = (phases.Contains(CatalogOpenPhase.SafetyCopy) ? bytes / 60e6 : 0) + pictures / 800.0 + media / 1000.0 + pending / 800.0;
        return new(version, SchemaVersion, phases, media, pictures + pending, bytes, free, TimeSpan.FromSeconds(Math.Ceiling(seconds)));
    }

    /// <summary>Space in the catalog file that rows no longer use; "Compact catalog" gives it back.</summary>
    public long UnusedCatalogBytes
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                using var command = Command("SELECT freelist_count*page_size FROM pragma_freelist_count(),pragma_page_size()", null);
                return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
            }
        }
    }

    /// <summary>Size of the catalog file.</summary>
    public long CatalogBytes => new FileInfo(DatabasePath).Length;
}
