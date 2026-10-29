using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class LocalFeaturesTests : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flixypix-local-tests-" + Guid.NewGuid().ToString("N"));
    public LocalFeaturesTests() => Directory.CreateDirectory(directory);
    private string PathFor(string name) => System.IO.Path.Combine(directory, name);

    [Fact]
    public void SavedSearchesAndAdditiveRulesPersistAndExport()
    {
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        var a = catalog.Upsert(new MediaItem { Path = PathFor("Urlaub 2020.jpg"), Title = "Trip", Tags = ["Manual"], Rating = 9 });
        catalog.Upsert(new MediaItem { Path = PathFor("Other.jpg"), Title = "Other", Rating = 2 });
        var rule = catalog.UpsertFilenameTagRule(new FilenameTagRule { Name = "Travel", FilenameContains = "URLAUB", Tags = ["Holiday", "holiday"] });
        catalog.UpsertFilenameTagRule(new FilenameTagRule { Name = "Disabled", FilenameContains = "Urlaub", Tags = ["Should not appear"], Enabled = false });
        Assert.Equal(1, catalog.ApplyFilenameTagRules());
        Assert.Equal(0, catalog.ApplyFilenameTagRules());
        var saved = catalog.UpsertSavedSearch(new SavedSearch { Name = "Best holidays", Text = "trip", Tag = "HOLIDAY", MinimumRating = 8 });
        var found = Assert.Single(catalog.SearchSaved(saved.Id));
        Assert.Equal(a.Id, found.Id);
        Assert.Contains("Manual", found.Tags);
        Assert.DoesNotContain("Should not appear", found.Tags);
        var export = PathFor("catalog.json");
        catalog.Export(export);
        using var imported = new CatalogRepository(PathFor("target.sqlite"));
        Assert.Empty(imported.ImportJson(export).Errors);
        Assert.Single(imported.SearchSaved(saved.Id));
        Assert.Equal(2, imported.GetFilenameTagRules().Count);
        imported.DeleteSavedSearch(saved.Id);
        imported.DeleteFilenameTagRule(rule.Id);
        Assert.Empty(imported.GetSavedSearches());
        Assert.Single(imported.GetFilenameTagRules());
    }

    [Fact]
    public void CategorizedTagsWatchLaterAndFavoritePreferencesRoundtrip()
    {
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        var media = catalog.Upsert(new MediaItem
        {
            Path = PathFor("a.mp4"), Kind = MediaKind.Video, IsFavorite = true, WatchLater = true, Rating = 1, Category = "Videos",
            Tags = ["Loose"], TagCategories = new() { ["People"] = ["Same"], ["Places"] = ["Same", "Mountain"] }
        });
        catalog.Upsert(media with { Tags = [] });
        var edited = Assert.Single(catalog.Search());
        Assert.Equal(new[] { "Same", "Mountain" }, edited.Tags);
        Assert.Equal("Same", edited.TagCategories["People"][0]);
        edited.TagCategories["People"].Clear();
        Assert.Single(Assert.Single(catalog.Search()).TagCategories["People"]);
        Assert.Single(catalog.Search(isFavorite: true, watchLater: true, category: "VIDEOS", tagCategory: "people", tag: "SAME"));
        catalog.SavePreferences(new CatalogPreferences { VideoFavoriteMinimumRating = 8, ImageFavoriteMinimumRating = 4 });
        catalog.Export(PathFor("catalog.json"));
        using var imported = new CatalogRepository(PathFor("target.sqlite"));
        Assert.Empty(imported.ImportJson(PathFor("catalog.json")).Errors);
        var restored = Assert.Single(imported.Search());
        Assert.True(restored.WatchLater);
        Assert.True(restored.IsFavorite);
        Assert.Equal(2, restored.TagCategories.Count);
        Assert.Equal(8, imported.GetPreferences().VideoFavoriteMinimumRating);
        Assert.Single(imported.Search(isFavorite: true));
        imported.Upsert(restored with { IsFavorite = false, Rating = 7 });
        Assert.Empty(imported.Search(isFavorite: true));
        imported.Upsert(restored with { IsFavorite = false, Rating = 8 });
        Assert.Single(imported.Search(isFavorite: true));
    }

    [Fact]
    public void MissingPortablePreferencesKeepExistingThresholds()
    {
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        catalog.SavePreferences(new CatalogPreferences { VideoFavoriteMinimumRating = 10 });
        File.WriteAllText(PathFor("old.json"), "{\"format\":\"flixypix-catalog\",\"version\":1,\"items\":[]}");
        Assert.Empty(catalog.ImportJson(PathFor("old.json")).Errors);
        Assert.Equal(10, catalog.GetPreferences().VideoFavoriteMinimumRating);
    }

    [Fact]
    public void SearchCacheCannotBeMutatedByCallerAndRefreshesAcrossConnections()
    {
        using var first = new CatalogRepository(PathFor("catalog.sqlite"));
        var item = first.Upsert(new MediaItem { Path = PathFor("a.jpg"), Tags = ["Original"], Extra = new() { ["a"] = "b" } });
        var result = Assert.Single(first.Search());
        result.Tags.Clear();
        result.Extra.Clear();
        Assert.Equal(new[] { "Original" }, Assert.Single(first.Search()).Tags);
        Assert.Equal("b", Assert.Single(first.Search()).Extra["a"]);
        using var second = new CatalogRepository(PathFor("catalog.sqlite"));
        second.Upsert(item with { Title = "Changed externally" });
        Assert.Equal("Changed externally", Assert.Single(first.Search()).Title);
        second.Delete(item.Id);
        Assert.Empty(first.Search());
    }

    [Fact]
    public async Task DuplicateFinderUsesContentAndNeverMutatesFiles()
    {
        var a = PathFor("a.jpg");
        var b = PathFor("b.mp4");
        var c = PathFor("different.jpg");
        File.WriteAllText(a, "same bytes");
        File.WriteAllText(b, "same bytes");
        File.WriteAllText(c, "diff bytes");
        var before = new[] { a, b, c }.ToDictionary(x => x, File.ReadAllBytes);
        var result = await DuplicateFinder.FindAsync(new[] { a, b, c, PathFor("missing.jpg"), a }.Select(p => new MediaItem { Path = p }));
        Assert.Single(result.Errors);
        var duplicate = Assert.Single(result.Groups);
        Assert.Equal(64, duplicate.Sha256.Length);
        Assert.Equal(new[] { a, b }, duplicate.Items.Select(x => x.Path));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task DuplicateFinderHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DuplicateFinder.FindAsync([new MediaItem { Path = PathFor("a.jpg") }], cancellationToken: cancellation.Token));
    }

    [Fact]
    public void ClipPlaylistReferencesAndPointBookmarksRoundtripAndCascade()
    {
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        var media = catalog.Upsert(new MediaItem { Path = PathFor("a.mp4"), Kind = MediaKind.Video });
        var clip = catalog.UpsertScene(new Scene { MediaId = media.Id, StartSeconds = 4, EndSeconds = 9 });
        catalog.UpsertPlaylist(new Playlist { Name = "Clip list", MediaIds = [media.Id, media.Id], Entries = [new(media.Id), new(media.Id, clip.Id)] });
        catalog.UpsertBookmark(new Bookmark { MediaId = media.Id, PositionSeconds = 4.5, Title = "Spot" });
        catalog.UpsertBookmark(new Bookmark { MediaId = media.Id, PositionSeconds = 8, Kind = BookmarkKind.SceneThumbnail });
        catalog.Export(PathFor("catalog.json"));
        using var target = new CatalogRepository(PathFor("target.sqlite"));
        Assert.Empty(target.ImportJson(PathFor("catalog.json")).Errors);
        Assert.Equal(clip.Id, Assert.Single(target.GetPlaylists()).Entries[1].SceneId);
        Assert.Equal(2, target.GetBookmarks(media.Id).Count);
        target.DeleteScene(clip.Id);
        Assert.Single(Assert.Single(target.GetPlaylists()).Entries);
        Assert.Null(Assert.Single(target.GetPlaylists()).Entries[0].SceneId);
        target.Delete(media.Id);
        Assert.Empty(target.GetBookmarks());
    }

    [Fact]
    public void LegacyRecordDuplicateOccurrencesArePreserved()
    {
        var lines = new[]
        {
            "{\"type\":\"header\",\"format\":\"flixypix-legacy\",\"version\":1,\"sourceSha256\":\"" + new string('a', 64) + "\"}",
            "{\"type\":\"schema\",\"table\":\"future\",\"columns\":[{\"name\":\"value\"}]}",
            "{\"type\":\"row\",\"table\":\"future\",\"values\":{\"value\":1}}",
            "{\"type\":\"row\",\"table\":\"future\",\"values\":{\"value\":1}}",
            "{\"type\":\"footer\",\"tables\":1,\"rows\":2,\"tableRows\":{\"future\":2}}"
        };
        File.WriteAllLines(PathFor("legacy.jsonl"), lines);
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        Assert.Empty(catalog.ImportLegacyJsonl(PathFor("legacy.jsonl")).Errors);
        catalog.Export(PathFor("catalog.json"));
        using var exported = JsonDocument.Parse(File.ReadAllText(PathFor("catalog.json")));
        Assert.Equal(lines, exported.RootElement.GetProperty("legacyRecords").EnumerateArray().Select(x => x.GetString()));
        Assert.Empty(catalog.ImportJson(PathFor("catalog.json")).Errors);
        catalog.Export(PathFor("again.json"));
        using var again = JsonDocument.Parse(File.ReadAllText(PathFor("again.json")));
        Assert.Equal(lines.Length, again.RootElement.GetProperty("legacyRecords").GetArrayLength());
    }

    [Fact]
    public void V1LegacyRecordsMigrateWithoutDiscardingData()
    {
        using (var connection = new SqliteConnection($"Data Source={PathFor("old.sqlite")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE legacy_records(id INTEGER PRIMARY KEY,record TEXT NOT NULL UNIQUE); INSERT INTO legacy_records(record) VALUES ('{}'); PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        using var catalog = new CatalogRepository(PathFor("old.sqlite"));
        catalog.Export(PathFor("export.json"));
        using var exported = JsonDocument.Parse(File.ReadAllText(PathFor("export.json")));
        Assert.Equal("{}", exported.RootElement.GetProperty("legacyRecords")[0].GetString());
        catalog.UpsertSavedSearch(new SavedSearch { Name = "Works after migration" });
        Assert.Single(catalog.GetSavedSearches());
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
