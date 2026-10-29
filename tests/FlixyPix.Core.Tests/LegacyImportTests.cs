using System.Security.Cryptography;
using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class LegacyImportTests : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flixypix-legacy-tests-" + Guid.NewGuid().ToString("N"));
    public LegacyImportTests() => Directory.CreateDirectory(directory);
    private string PathFor(string name) => System.IO.Path.Combine(directory, name);
    private string Header => JsonSerializer.Serialize(new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('a', 64), sourceName = "fixture.sdf" });
    private static string Schema(string table, params string[] columns) => JsonSerializer.Serialize(new { type = "schema", table,
        columns = columns.Select((name, ordinal) => new { name, dataType = "nvarchar", nullable = true, ordinal }) });
    private static string Row(string table, object values) => JsonSerializer.Serialize(new { type = "row", table, values });
    private static string Footer(Dictionary<string, int> counts) => JsonSerializer.Serialize(new { type = "footer", tables = counts.Count, rows = counts.Values.Sum(), tableRows = counts });
    private string Write(params string[] lines)
    {
        var path = PathFor(Guid.NewGuid() + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void CatalogOfAnotherVersionImportsWhatIsReadableAndNamesTheRest()
    {
        // An older catalog: no pictures, watch later or player views; movies without play counts, keyword in other casing, one unknown table.
        var movie = new { id = 5, FULLPATH = directory + System.IO.Path.DirectorySeparatorChar, FileName = "Alt.mp4", title = "Alt", rating = 6, length = 60 };
        var path = Write(Header,
            Schema("Movies", "id", "FULLPATH", "FileName", "title", "rating", "length"), Row("Movies", movie),
            Schema("keyword", "ID", "type", "remoteID", "keytags"), Row("keyword", new { ID = 1, type = 2, remoteID = 5, keytags = "\"Alt\"" }),
            Schema("legacy_extras", "payload"), Row("legacy_extras", new { payload = "x" }),
            Footer(new() { ["Movies"] = 1, ["keyword"] = 1, ["legacy_extras"] = 1 }));
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        var result = catalog.ImportLegacyJsonl(path);
        Assert.Empty(result.Errors);
        var item = Assert.Single(catalog.Search());
        Assert.Equal(("Alt", 6, 60d), (item.Title, item.Rating, item.DurationSeconds!.Value));
        Assert.Equal(["Alt"], item.Tags);
        var report = Assert.IsType<LegacyCompatibilityReport>(result.Compatibility);
        Assert.False(report.IsComplete);
        var media = report.Areas.Single(a => a.Area == "Media");
        Assert.Equal(LegacyAreaStatus.Partial, media.Status);
        Assert.Contains("pictures", media.MissingTables);
        Assert.Contains("movies.plays", media.MissingColumns);
        Assert.DoesNotContain("movies.fullpath", media.MissingColumns);
        Assert.Equal(LegacyAreaStatus.Complete, report.Areas.Single(a => a.Area == "Tags").Status);
        Assert.Equal(LegacyAreaStatus.Missing, report.Areas.Single(a => a.Area == "Watch later").Status);
        Assert.Equal(["legacy_extras"], report.UnknownTables);
    }

    [Fact]
    public void CatalogWithoutMediaTableStillImportsItsOtherAreas()
    {
        var path = Write(Header,
            Schema("configsettings", "ID", "parameter", "value1"), Row("configsettings", new { ID = 1, parameter = "ezfpwheelposition", value1 = "1" }),
            Footer(new() { ["configsettings"] = 1 }));
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        var result = catalog.ImportLegacyJsonl(path);
        Assert.Empty(result.Errors);
        Assert.Empty(catalog.Search());
        Assert.Equal(LegacyAreaStatus.Missing, result.Compatibility!.Areas.Single(a => a.Area == "Media").Status);
        Assert.Equal(LegacyAreaStatus.Complete, result.Compatibility.Areas.Single(a => a.Area == "Settings").Status);
    }

    [Fact]
    public void LegacyImportPreservesUnknownTypedRecordsAndProjectsProvenPathsTagsAndPositions()
    {
        var movie = new { ID = 42, fullpath = directory + System.IO.Path.DirectorySeparatorChar, filename = "Film.mp4", title = "Film", width = 1920, height = 1080,
            length = 120, lastposition = 31, rating = 9, unknown = new Dictionary<string, string> { ["$decimal"] = "123456.789123" } };
        var path = Write(Header,
            Schema("movies", "ID", "fullpath", "filename", "title", "width", "height", "length", "lastposition", "rating", "unknown"), Row("movies", movie),
            Schema("keyword", "ID", "type", "remoteID", "keytags"), Row("keyword", new { ID = 7, type = 2, remoteID = 42, keytags = "\"Reise\" \"Sommer Urlaub\"" }),
            Schema("future_table", "payload"), Row("future_table", new { payload = new Dictionary<string, string> { ["$binary"] = "AQIDBA==" } }),
            Footer(new() { ["movies"] = 1, ["keyword"] = 1, ["future_table"] = 1 }));
        var original = File.ReadAllBytes(path);
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        var result = catalog.ImportLegacyJsonl(path);
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.Imported);
        var item = Assert.Single(catalog.Search());
        Assert.Equal(PathFor("Film.mp4"), item.Path);
        Assert.Equal(120, item.DurationSeconds);
        Assert.Equal(31, item.PlaybackPositionSeconds);
        Assert.Equal(9, item.Rating);
        Assert.Equal(new[] { "Reise", "Sommer Urlaub" }, item.Tags);
        Assert.False(item.Extra.ContainsKey("legacy.values")); // the raw row stays in legacy_records only (checked below)
        Assert.Equal(original, File.ReadAllBytes(path));
        catalog.Export(PathFor("export.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(PathFor("export.json")));
        var raw = doc.RootElement.GetProperty("legacyRecords").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(File.ReadAllLines(path), raw);
        Assert.Contains(result.Warnings, x => x.Contains("future_table"));
        // Repeated import is idempotent for media identities and retained raw records.
        var firstId = item.Id;
        Assert.Empty(catalog.ImportLegacyJsonl(path).Errors);
        Assert.Equal(firstId, Assert.Single(catalog.Search()).Id);
        catalog.Export(PathFor("again.json"));
        using var repeated = JsonDocument.Parse(File.ReadAllText(PathFor("again.json")));
        Assert.Equal(raw.Length, repeated.RootElement.GetProperty("legacyRecords").GetArrayLength());
    }

    [Fact]
    public void LegacyTagVocabularyDoesNotBecomeItemTags()
    {
        var path = Write(Header, Schema("pictures", "ID", "fullpath", "filename"),
            Row("pictures", new { ID = 42, fullpath = directory + System.IO.Path.DirectorySeparatorChar, filename = "pic.jpg" }),
            Schema("searchcriteria", "ID", "movie", "category", "value1"),
            Row("searchcriteria", new { ID = 1, movie = "SEARCHTAGINFO", category = "Unassigned tag", value1 = "42" }),
            Footer(new() { ["pictures"] = 1, ["searchcriteria"] = 1 }));
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        Assert.Empty(catalog.ImportLegacyJsonl(path).Errors);
        Assert.Empty(Assert.Single(catalog.Search()).Tags);
    }

    [Fact]
    public void LegacyStagingRecordLimitRejectsCleanlyAndAllowsLargerExplicitLimit()
    {
        var path = Write(Header, Schema("future_art", "payload"), Row("future_art", new { payload = new Dictionary<string, string> { ["$binary"] = new string('A', 12_000) } }),
            Footer(new() { ["future_art"] = 1 }));
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        var failed = catalog.ImportLegacyJsonl(path, 2048);
        Assert.Contains("maximumRecordCharacters", Assert.Single(failed.Errors));
        catalog.Export(PathFor("failed.json"));
        using var failedExport = JsonDocument.Parse(File.ReadAllText(PathFor("failed.json")));
        Assert.Equal(0, failedExport.RootElement.GetProperty("legacyRecords").GetArrayLength());
        Assert.Empty(catalog.ImportLegacyJsonl(path, 16_384).Errors);
        catalog.Export(PathFor("success.json"));
        using var success = JsonDocument.Parse(File.ReadAllText(PathFor("success.json")));
        Assert.Equal(4, success.RootElement.GetProperty("legacyRecords").GetArrayLength());
    }

    [Fact]
    public void CancelledLegacyImportRollsBackAndAllowsRetry()
    {
        var path = Write(Header, Schema("pictures", "ID", "fullpath", "filename"),
            Row("pictures", new { ID = 1, fullpath = directory, filename = "picture.png" }), Footer(new() { ["pictures"] = 1 }));
        using var catalog = new CatalogRepository(PathFor("cancelled.sqlite"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => catalog.ImportLegacyJsonl(path, 16 * 1024 * 1024, cancellation.Token));
        Assert.Empty(catalog.Search());
        Assert.Empty(catalog.ImportLegacyJsonl(path).Errors);
        Assert.Single(catalog.Search());
    }

    [Fact]
    public void LegacyDiskStagingSupportsTotalStreamLargerThanPerRecordLimit()
    {
        const int records = 6;
        var path = PathFor("artwork.jsonl");
        using (var output = new StreamWriter(path))
        {
            output.WriteLine(Header);
            output.WriteLine(Schema("future_art", "ID", "payload"));
            for (var id = 0; id < records; id++)
                output.WriteLine(Row("future_art", new { ID = id, payload = new Dictionary<string, string> { ["$binary"] = new string('A', 1024 * 1024) } }));
            output.WriteLine(Footer(new() { ["future_art"] = records }));
        }
        var before = SHA256.HashData(File.ReadAllBytes(path));
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        Assert.Empty(catalog.ImportLegacyJsonl(path, 2 * 1024 * 1024).Errors);
        catalog.Export(PathFor("export.json"));
        using var exported = JsonDocument.Parse(File.ReadAllText(PathFor("export.json")));
        Assert.Equal(records + 3, exported.RootElement.GetProperty("legacyRecords").GetArrayLength());
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void LegacyCategoriesUseVocabularyAndMediaCategoryIdsRatherThanKeywordFilename()
    {
        var path = Write(Header,
            Schema("movies", "ID", "fullpath", "filename", "categoryid", "rating"),
            Row("movies", new { ID = 1, fullpath = directory + System.IO.Path.DirectorySeparatorChar, filename = "Film.mp4", categoryid = 10, rating = 7 }),
            Schema("pictures", "ID", "fullpath", "filename", "categoryid", "rating"),
            Row("pictures", new { ID = 2, fullpath = directory + System.IO.Path.DirectorySeparatorChar, filename = "Pic.jpg", categoryid = 20, rating = 5 }),
            Schema("categories", "ID", "category", "mtype"),
            Row("categories", new { ID = 10, category = "Movies", mtype = 0 }), Row("categories", new { ID = 20, category = "Pictures", mtype = 1 }),
            Schema("keyword", "ID", "type", "remoteID", "keyword", "keytags"),
            Row("keyword", new { ID = 3, type = 2, remoteID = 1, keyword = "Film.mp4", keytags = "\"Same\" \"Loose\"" }),
            Schema("searchcriteria", "ID", "movie", "category", "value1"),
            Row("searchcriteria", new { ID = 4, movie = "SEARCHTAGINFO", category = "Places", value1 = "Same" }),
            Row("searchcriteria", new { ID = 5, movie = "SEARCHTAGINFO", category = "People", value1 = "Same" }),
            Row("searchcriteria", new { ID = 6, movie = "SEARCHTAGINFO", category = "Unused", value1 = "Orphan" }),
            Schema("videowatchlater", "ID", "videoid"), Row("videowatchlater", new { ID = 7, videoid = 1 }),
            Schema("configsettings", "parameter", "value1"),
            Row("configsettings", new { parameter = "favrating", value1 = "4" }), Row("configsettings", new { parameter = "picfavrating", value1 = "2" }),
            Schema("descriptions", "ID", "remoteid", "keyword", "mtype", "description"),
            Row("descriptions", new { ID = 8, remoteid = 1, keyword = "", mtype = 8, description = "Movie description" }),
            Footer(new() { ["movies"] = 1, ["pictures"] = 1, ["categories"] = 2, ["keyword"] = 1, ["searchcriteria"] = 3,
                ["videowatchlater"] = 1, ["configsettings"] = 2, ["descriptions"] = 1 }));
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        var result = catalog.ImportLegacyJsonl(path);
        Assert.Empty(result.Errors);
        var movie = Assert.Single(catalog.Search(category: "Movies"));
        Assert.True(movie.WatchLater);
        Assert.Equal("Movie description", movie.Description);
        Assert.Equal(new[] { "Same" }, movie.TagCategories["People"]);
        Assert.Equal(new[] { "Same" }, movie.TagCategories["Places"]);
        Assert.False(movie.TagCategories.ContainsKey("Film.mp4"));
        Assert.DoesNotContain("Orphan", movie.Tags);
        Assert.False(catalog.IsFavorite(movie));
        var picture = Assert.Single(catalog.Search(category: "Pictures"));
        Assert.True(catalog.IsFavorite(picture));
        Assert.Equal(8, catalog.GetPreferences().VideoFavoriteMinimumRating);
        Assert.Equal(4, catalog.GetPreferences().ImageFavoriteMinimumRating);
        Assert.Contains(result.Warnings, x => x.Contains("multiple vocabulary categories"));
    }

    [Fact]
    public void ProvenLegacyClipsOrderedPlaylistsAndSceneTimepointsAreProjected()
    {
        var path = Write(Header,
            Schema("movies", "ID", "fullpath", "filename"), Row("movies", new { ID = 42, fullpath = directory + System.IO.Path.DirectorySeparatorChar, filename = "Film.mp4" }),
            Schema("videoplaylists", "ID", "bookmarkid", "videoid", "description", "position"),
            Row("videoplaylists", new { ID = 1, bookmarkid = 9, videoid = 42, description = "Test list", position = 2 }),
            Row("videoplaylists", new { ID = 2, bookmarkid = 0, videoid = 42, description = "Test list", position = 1 }),
            Row("videoplaylists", new { ID = 3, bookmarkid = 999, videoid = 42, description = "Test list", position = 3 }),
            Schema("videoclips", "ID", "videoid", "description", "starttime", "endtime"),
            Row("videoclips", new { ID = 9, videoid = 42, description = "Excerpt", starttime = 3, endtime = 17 }),
            Schema("videobookmarks", "ID", "videoid", "bookmarktime"), Row("videobookmarks", new { ID = 4, videoid = 42, bookmarktime = 10 }),
            Schema("videoscenes", "ID", "videoid", "secs", "img"),
            Row("videoscenes", new { ID = 7, videoid = 42, secs = 8, img = new Dictionary<string, string> { ["$binary"] = "AQID" } }),
            Footer(new() { ["movies"] = 1, ["videoplaylists"] = 3, ["videoclips"] = 1, ["videobookmarks"] = 1, ["videoscenes"] = 1 }));
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        var result = catalog.ImportLegacyJsonl(path);
        Assert.Empty(result.Errors);
        var clip = Assert.Single(catalog.GetScenes());
        Assert.Equal(3, clip.StartSeconds);
        Assert.Equal(17, clip.EndSeconds);
        Assert.Equal("Excerpt", clip.Title);
        var playlist = Assert.Single(catalog.GetPlaylists());
        Assert.Equal(2, playlist.Entries.Count);
        Assert.Null(playlist.Entries[0].SceneId);
        Assert.Equal(clip.Id, playlist.Entries[1].SceneId);
        Assert.Contains(result.Warnings, x => x.Contains("unresolved clip"));
        var points = catalog.GetBookmarks();
        Assert.Equal(2, points.Count);
        Assert.Equal(8, points[0].PositionSeconds);
        Assert.Equal(BookmarkKind.SceneThumbnail, points[0].Kind);
        Assert.Equal(10, points[1].PositionSeconds);
        Assert.Equal(BookmarkKind.Bookmark, points[1].Kind);
    }

    [Theory]
    [InlineData("missing-footer")]
    [InlineData("invalid-json")]
    [InlineData("row-without-schema")]
    [InlineData("wrong-row-count")]
    [InlineData("wrong-table-count")]
    [InlineData("wrong-table-rows")]
    [InlineData("schema-mismatch")]
    public void MalformedLegacyStreamIsAtomic(string variant)
    {
        var lines = new List<string> { Header };
        if (variant != "row-without-schema") lines.Add(Schema("future", "value"));
        lines.Add(Row("future", variant == "schema-mismatch" ? new { unexpected = 1 } : (object)new { value = 1 }));
        if (variant == "invalid-json") lines.Add("{bad");
        if (variant != "missing-footer") lines.Add(variant switch
        {
            "wrong-row-count" => "{\"type\":\"footer\",\"tables\":1,\"rows\":2,\"tableRows\":{\"future\":1}}",
            "wrong-table-count" => "{\"type\":\"footer\",\"tables\":2,\"rows\":1,\"tableRows\":{\"future\":1}}",
            "wrong-table-rows" => "{\"type\":\"footer\",\"tables\":1,\"rows\":1,\"tableRows\":{\"future\":2}}",
            _ => Footer(new() { ["future"] = 1 })
        });
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        var path = Write(lines.ToArray());
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        Assert.NotEmpty(catalog.ImportLegacyJsonl(path).Errors);
        Assert.Empty(catalog.Search());
        catalog.Export(PathFor("empty.json"));
        using var exported = JsonDocument.Parse(File.ReadAllText(PathFor("empty.json")));
        Assert.Equal(0, exported.RootElement.GetProperty("legacyRecords").GetArrayLength());
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
