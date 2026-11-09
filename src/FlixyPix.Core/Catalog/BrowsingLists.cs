using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>The lists beside the tiles (folders, filters, tags, numbers of the sidebar), kept in the catalog between starts.
/// Reading them anew takes a pass over the whole library and grows with it; these few pages do not. Plain triggers count every
/// change that can affect them, also from other SQLite writers, so lists of an older state are recognized and read anew.</summary>
public sealed partial class CatalogRepository
{
    private const int BrowsingListsFormat = 1;

    private sealed record StoredBrowsingLists(int Format, long MediaCount, List<string> Tags, List<string> Categories, List<string> TagCategories, List<string> Folders,
        Dictionary<string, List<string>> CategorizedTags, Dictionary<string, long>? FolderCounts, Dictionary<string, long>? CategoryCounts, Dictionary<string, long>? TagCategoryCounts,
        List<TagUsage> Usage, NavigationCounts Navigation);

    private void InitializeBrowsingLists(SqliteTransaction transaction)
    {
        const string changed = """
            json_extract(OLD.document,'$.path') IS NOT json_extract(NEW.document,'$.path')
            OR json_extract(OLD.document,'$.category') IS NOT json_extract(NEW.document,'$.category')
            OR json_extract(OLD.document,'$.kind') IS NOT json_extract(NEW.document,'$.kind')
            OR json_extract(OLD.document,'$.reviewState') IS NOT json_extract(NEW.document,'$.reviewState')
            OR json_extract(OLD.document,'$.tags') IS NOT json_extract(NEW.document,'$.tags')
            OR json_extract(OLD.document,'$.tagCategories') IS NOT json_extract(NEW.document,'$.tagCategories')
            """;
        using var create = Command($"""
            CREATE TABLE IF NOT EXISTS browsing_generation(id INTEGER PRIMARY KEY CHECK(id=1), value INTEGER NOT NULL);
            INSERT OR IGNORE INTO browsing_generation VALUES(1,0);
            CREATE TABLE IF NOT EXISTS browsing_lists(id INTEGER PRIMARY KEY CHECK(id=1), generation INTEGER NOT NULL, document TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS browsing_media_added AFTER INSERT ON media BEGIN UPDATE browsing_generation SET value=value+1; END;
            CREATE TRIGGER IF NOT EXISTS browsing_media_removed AFTER DELETE ON media BEGIN UPDATE browsing_generation SET value=value+1; END;
            CREATE TRIGGER IF NOT EXISTS browsing_media_changed AFTER UPDATE OF document ON media WHEN {changed} BEGIN UPDATE browsing_generation SET value=value+1; END;
            """, transaction);
        create.ExecuteNonQuery();
    }

    /// <summary>Takes the lists kept in the catalog, if nothing that affects them changed since. Then no reading of the whole
    /// library is needed at this start.</summary>
    public bool LoadBrowsingLists()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("SELECT l.document FROM browsing_lists l JOIN browsing_generation g ON g.value=l.generation WHERE l.id=1", null);
            if (command.ExecuteScalar() is not string json) return false;
            StoredBrowsingLists? stored;
            try { stored = JsonSerializer.Deserialize<StoredBrowsingLists>(json, Json); }
            catch (JsonException) { return false; } // Costs only the reading anew.
            if (stored is null || stored.Format != BrowsingListsFormat) return false;
            var stamp = ChangeStamp();
            var facets = new CatalogFacets(stored.MediaCount, stored.Tags, stored.Categories, stored.TagCategories, stored.Folders,
                new Dictionary<string, IReadOnlyList<string>>(stored.CategorizedTags.Select(p => KeyValuePair.Create(p.Key, (IReadOnlyList<string>)p.Value)), StringComparer.OrdinalIgnoreCase),
                stored.FolderCounts, stored.CategoryCounts, stored.TagCategoryCounts);
            if (!facetsCache.Install(facets, stamp)) return false;
            cachedTagUsage = stored.Usage; cachedTagUsageStamp = stamp;
            cachedNavigationCounts = stored.Navigation; cachedNavigationCountsStamp = stamp;
            return true;
        }
    }

    /// <summary>After a write that touched no media: the lists in memory stay valid.</summary>
    private void CarryListsAcross((long Changes, long DataVersion) before)
    {
        var after = ChangeStamp();
        if (after.DataVersion != before.DataVersion) return; // another connection committed meanwhile
        facetsCache.CarryAcross(before, after, [], true, true);
        if (asideReadStamp == before) asideReadStamp = after;
        if (cachedTagUsage is not null && cachedTagUsageStamp == before) cachedTagUsageStamp = after;
        if (cachedNavigationCounts is not null && cachedNavigationCountsStamp == before) cachedNavigationCountsStamp = after;
    }

    /// <summary>The lists are in memory and current, so asking for them reads nothing.</summary>
    public bool BrowsingKnown
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                var stamp = ChangeStamp();
                return facetsCache.Known() is not null && cachedNavigationCounts is not null && cachedNavigationCountsStamp == stamp
                    && (cachedTagUsage is not null && cachedTagUsageStamp == stamp || cachedTagStatistics is not null && cachedTagStatisticsStamp == stamp);
            }
        }
    }

    /// <summary>Keeps the current lists for the next start, if they are all known and differ from those kept. Nothing is read for it.</summary>
    public void SaveBrowsingLists()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var before = ChangeStamp();
            if (facetsCache.Known() is not { } facets || cachedNavigationCounts is not { } navigation || cachedNavigationCountsStamp != before) return;
            if (cachedTagUsage is null || cachedTagUsageStamp != before)
            {
                if (cachedTagStatistics is null || cachedTagStatisticsStamp != before) return;
                _ = GetTagUsage();
            }
            using (var current = Command("SELECT EXISTS(SELECT 1 FROM browsing_lists l JOIN browsing_generation g ON g.value=l.generation WHERE l.id=1)", null))
                if (Convert.ToInt64(current.ExecuteScalar()) != 0) return;
            var stored = new StoredBrowsingLists(BrowsingListsFormat, facets.MediaCount, [.. facets.Tags], [.. facets.Categories], [.. facets.TagCategories], [.. facets.Folders],
                facets.CategorizedTags.ToDictionary(p => p.Key, p => p.Value.ToList()),
                facets.FolderCounts?.ToDictionary(p => p.Key, p => p.Value), facets.CategoryCounts?.ToDictionary(p => p.Key, p => p.Value), facets.TagCategoryCounts?.ToDictionary(p => p.Key, p => p.Value),
                [.. cachedTagUsage!], navigation);
            using (var save = Command("INSERT INTO browsing_lists(id,generation,document) SELECT 1,value,$json FROM browsing_generation WHERE id=1 ON CONFLICT(id) DO UPDATE SET generation=excluded.generation,document=excluded.document", null,
                ("$json", JsonSerializer.Serialize(stored, Json))))
                save.ExecuteNonQuery();
            CarryCachesAcross(before, []);
        }
    }
}
