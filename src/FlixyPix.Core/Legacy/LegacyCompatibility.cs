namespace FlixyPix.Core;

public enum LegacyAreaStatus { Complete, Partial, Missing }

/// <summary>One area of the original catalog as found: tables or columns FlixyPix reads that are not there.</summary>
public sealed record LegacyAreaCompatibility(string Area, LegacyAreaStatus Status, IReadOnlyList<string> MissingTables, IReadOnlyList<string> MissingColumns);

/// <summary>How a catalog compares with what FlixyPix reads from EazyFlixPix 4. Catalogs of other versions are imported as
/// far as they can be read; the report says what stayed empty and which unknown tables were only kept raw.</summary>
public sealed record LegacyCompatibilityReport(IReadOnlyList<LegacyAreaCompatibility> Areas, IReadOnlyList<string> UnknownTables)
{
    public bool IsComplete => Areas.All(a => a.Status == LegacyAreaStatus.Complete) && UnknownTables.Count == 0;
}

public static class LegacyCompatibility
{
    private sealed record Area(string Name, (string Table, string[] Columns)[] Tables);

    /// <summary>Tables and columns the import reads, grouped as the user knows them. Areas of later 4.x versions (watch
    /// later, player views, artwork) are simply absent in older catalogs.</summary>
    private static readonly Area[] Areas =
    [
        new(TKey("Media"), [("movies", ["ID", "fullpath", "filename", "title", "rating", "width", "height", "length", "lastposition", "plays", "views", "addedDate"]),
            ("pictures", ["ID", "fullpath", "filename", "title", "rating", "width", "height"])]),
        new(TKey("Tags"), [("keyword", ["ID", "type", "remoteID", "keytags"])]),
        new(TKey("Categories and folders"), [("categories", ["ID", "category", "mtype"]), ("paths", ["ID", "categoryid", "path", "mtype"]), ("favoritefolders", ["fullpath"]), ("searchcriteria", [])]),
        new(TKey("Collections and scenes"), [("videoplaylists", ["bookmarkid", "videoid", "description", "position"]), ("videoclips", ["ID", "videoid", "description", "starttime", "endtime"])]),
        new(TKey("Bookmarks"), [("videobookmarks", ["videoid", "bookmarktime"])]),
        new(TKey("Pictures"), [("videoscenes", ["videoid", "secs", "img"]), ("videopreview", ["videoid", "secs", "img"]), ("thumbnails", ["video", "img"]), ("mediaart", ["remoteid", "mediatype", "img"])]),
        new(TKey("Watch later"), [("videowatchlater", ["videoid"])]),
        new(TKey("Player views"), [("videotransform", ["videoid", "secs", "boundsx", "boundsy", "boundsw", "boundsh"])]),
        new(TKey("Settings"), [("configsettings", ["parameter", "value1"])]),
    ];

    /// <summary>Tables of EazyFlixPix 4 that FlixyPix knows but does not need; they are kept raw without a mention.</summary>
    private static readonly HashSet<string> KnownUnused = new(StringComparer.OrdinalIgnoreCase)
    {
        "keyword_old", "keywordtablefix", "favoriteperformers", "registration", "tmp_keyword", "tmp_keyword1", "tmp_keyword2", "search_settings",
        "cliptovideo_queue", "image_archive_main", "image_archive_detail", "imagecache", "videosettings", "customplayersettings",
        "customimageviewersettings", "qa_imagefolders", "descriptions", "actor_detail", "video_tools_queue", "foldersort", "properties_main",
        "properties_domain", "properties_assignment", "display_settings", "properties_video_tempid", "properties_image_tempid",
    };

    public static LegacyCompatibilityReport Assess(IReadOnlyDictionary<string, HashSet<string>> schemas)
    {
        var tables = new Dictionary<string, HashSet<string>>(schemas, StringComparer.OrdinalIgnoreCase);
        var areas = Areas.Select(area =>
        {
            var missingTables = area.Tables.Where(t => !tables.ContainsKey(t.Table)).Select(t => t.Table).ToList();
            var missingColumns = area.Tables.Where(t => tables.ContainsKey(t.Table))
                .SelectMany(t => t.Columns.Where(c => !tables[t.Table].Contains(c)).Select(c => t.Table + "." + c)).ToList();
            var status = missingTables.Count == area.Tables.Length ? LegacyAreaStatus.Missing
                : missingTables.Count > 0 || missingColumns.Count > 0 ? LegacyAreaStatus.Partial : LegacyAreaStatus.Complete;
            return new LegacyAreaCompatibility(area.Name, status, missingTables, missingColumns);
        }).ToList();
        var read = Areas.SelectMany(a => a.Tables.Select(t => t.Table)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = tables.Keys.Where(t => !read.Contains(t) && !KnownUnused.Contains(t)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new LegacyCompatibilityReport(areas, unknown);
    }
}
