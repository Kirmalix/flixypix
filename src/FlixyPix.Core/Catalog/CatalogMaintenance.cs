using System.Text.Json;

namespace FlixyPix.Core;

/// <summary>Counts of a library for the overview of the database management. Sizes of graphics, tag images and legacy
/// binary values are those of their bytes (kept in files beside the catalog), not of exported files.</summary>
public sealed record CatalogStatistics(long Videos, long Images, long Audio, long Tags, long Collections, long SavedSearches, long Scenes, long Bookmarks,
    long MediaGraphics, long MediaGraphicBytes, long TagPictures, long TagPictureBytes, long LegacyRecords, long LegacyBlobBytes, long DatabaseBytes)
{
    public long Media => Videos + Images + Audio;
}

/// <summary>When the library was last backed up and checked, kept in the catalog so a restored or moved library keeps it.</summary>
public sealed record CatalogMaintenanceLog
{
    public DateTimeOffset? LastBackupAt { get; init; }
    public string? LastBackupPath { get; init; }
    public long? LastBackupBytes { get; init; }
    public DateTimeOffset? LastCheckAt { get; init; }
    /// <summary>Problems the last check found; 0 means the catalog was in order.</summary>
    public int? LastCheckIssues { get; init; }
    /// <summary>The last change of the catalog format when opening: when, from which to which version, how long it took.</summary>
    public DateTimeOffset? LastUpgradeAt { get; init; }
    public long? LastUpgradeFrom { get; init; }
    public long? LastUpgradeTo { get; init; }
    public double? LastUpgradeSeconds { get; init; }
}

public sealed partial class CatalogRepository
{
    private const string MaintenanceKey = "maintenance";

    public CatalogStatistics GetStatistics()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            long Scalar(string sql) { using var command = Command(sql, null); return Convert.ToInt64(command.ExecuteScalar() ?? 0L); }
            var kinds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using (var command = Command("SELECT kind,count(*) FROM media_query GROUP BY kind", null))
            using (var reader = command.ExecuteReader()) while (reader.Read()) kinds[reader.GetString(0)] = reader.GetInt64(1);
            return new(kinds.GetValueOrDefault(nameof(MediaKind.Video)), kinds.GetValueOrDefault(nameof(MediaKind.Image)), kinds.GetValueOrDefault(nameof(MediaKind.Audio)),
                Scalar("SELECT count(DISTINCT tag COLLATE NOCASE) FROM media_tags"), Scalar("SELECT count(*) FROM playlists"), Scalar("SELECT count(*) FROM saved_searches"),
                Scalar("SELECT count(*) FROM scenes"), Scalar("SELECT count(*) FROM bookmarks"),
                Scalar("SELECT count(*) FROM media_assets"), Scalar("SELECT coalesce(sum(json_extract(document,'$.byteLength')),0) FROM media_assets"),
                Scalar("SELECT count(*) FROM tag_assets"), Scalar("SELECT coalesce(sum(json_extract(document,'$.byteLength')),0) FROM tag_assets"),
                Scalar("SELECT count(*) FROM legacy_records"), Scalar("SELECT coalesce(sum(length),0) FROM legacy_blobs"),
                Scalar("SELECT page_count*page_size FROM pragma_page_count(),pragma_page_size()"));
        }
    }

    public CatalogMaintenanceLog GetMaintenanceLog()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("SELECT document FROM ui_state WHERE id=$id", null, ("$id", MaintenanceKey));
            return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CatalogMaintenanceLog>(json, Json) ?? new() : new();
        }
    }

    /// <summary>Notes a finished backup (package or catalog file) for the overview.</summary>
    public void RecordBackup(string path, long bytes) =>
        UpdateMaintenanceLog(log => log with { LastBackupAt = DateTimeOffset.Now, LastBackupPath = Path.GetFullPath(path), LastBackupBytes = bytes });

    /// <summary>Notes a finished check of the catalog and how many problems it found.</summary>
    public void RecordCheck(int issues) => UpdateMaintenanceLog(log => log with { LastCheckAt = DateTimeOffset.Now, LastCheckIssues = Math.Max(0, issues) });

    private void UpdateMaintenanceLog(Func<CatalogMaintenanceLog, CatalogMaintenanceLog> change)
    {
        var updated = change(GetMaintenanceLog());
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("INSERT INTO ui_state(id,document) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET document=excluded.document", null,
                ("$id", MaintenanceKey), ("$json", JsonSerializer.Serialize(updated, Json)));
            command.ExecuteNonQuery();
        }
    }
}
