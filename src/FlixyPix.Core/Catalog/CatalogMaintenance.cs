using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FlixyPix.Core;

/// <summary>Counts of a library for the overview of the database management. Sizes of graphics, tag images and legacy
/// binary values are those of their bytes (kept in files beside the catalog), not of exported files.</summary>
public sealed record CatalogStatistics(long Videos, long Images, long Audio, long Tags, long Collections, long SavedSearches, long Scenes, long Bookmarks,
    long MediaGraphics, long MediaGraphicBytes, long TagPictures, long TagPictureBytes, long LegacyRecords, long LegacyBlobBytes, long DatabaseBytes)
{
    public long Media => Videos + Images + Audio;
}

/// <summary>What the sidebar needs to decide which library entries it shows: audio only with audio media, the review
/// inbox only while reviewing is in use. Imports mark every new medium unreviewed, so unreviewed media alone do not
/// prove that anybody reviews.</summary>
public sealed record NavigationCounts(long Audio, long Unreviewed, long NeedsAttention, long Reviewed)
{
    /// <summary>Media the review inbox lists.</summary>
    public long Inbox => Unreviewed + NeedsAttention;

    /// <summary>The counts after an edit of one medium.</summary>
    internal NavigationCounts Moved(MediaItem old, MediaItem updated)
    {
        long Delta(Func<MediaItem, bool> counted) => (counted(updated) ? 1 : 0) - (counted(old) ? 1 : 0);
        return new(Audio + Delta(m => m.Kind == MediaKind.Audio), Unreviewed + Delta(m => m.ReviewState == ReviewState.Unreviewed),
            NeedsAttention + Delta(m => m.ReviewState == ReviewState.NeedsAttention), Reviewed + Delta(m => m.ReviewState == ReviewState.Reviewed));
    }

    public bool ShowsReviewInbox(ReviewInboxMode mode) => mode switch
    {
        ReviewInboxMode.Always => true,
        ReviewInboxMode.Never => false,
        _ => NeedsAttention > 0 || Reviewed > 0 && Unreviewed > 0,
    };
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

    /// <summary>Counts for the sidebar; both queries run on indexes of the query table.</summary>
    public NavigationCounts GetNavigationCounts()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var stamp = ChangeStamp();
            if (cachedNavigationCounts is { } known && cachedNavigationCountsStamp == stamp) return known;
            cachedNavigationCounts = ReadNavigationCounts(connection); cachedNavigationCountsStamp = stamp;
            return cachedNavigationCounts;
        }
    }
    private NavigationCounts? cachedNavigationCounts;
    private (long Changes, long DataVersion) cachedNavigationCountsStamp;

    private static NavigationCounts ReadNavigationCounts(SqliteConnection source)
    {
        var states = new Dictionary<long, long>();
        using (var command = source.CreateCommand())
        {
            command.CommandText = "SELECT review_state,count(*) FROM media_query GROUP BY review_state";
            using var reader = command.ExecuteReader();
            while (reader.Read()) states[reader.GetInt64(0)] = reader.GetInt64(1);
        }
        using var audio = source.CreateCommand();
        audio.CommandText = "SELECT count(*) FROM media_query WHERE kind=$kind"; audio.Parameters.AddWithValue("$kind", nameof(MediaKind.Audio));
        return new(Convert.ToInt64(audio.ExecuteScalar() ?? 0L), states.GetValueOrDefault((long)ReviewState.Unreviewed),
            states.GetValueOrDefault((long)ReviewState.NeedsAttention), states.GetValueOrDefault((long)ReviewState.Reviewed));
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
