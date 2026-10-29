using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Bookmark tags and the jump mark search (U6), collection sets, quick and target collection (U7), schema 13.</summary>
public sealed class CollectionTreeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-collections-" + Guid.NewGuid().ToString("N"));
    private readonly string path;
    private CatalogRepository catalog;
    private readonly MediaItem first, second, third;

    public CollectionTreeTests()
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "catalog.db");
        catalog = new CatalogRepository(path);
        MediaItem Video(string title) => catalog.Upsert(new MediaItem { Path = Path.Combine(directory, title + ".mp4"), Title = title, Kind = MediaKind.Video, DurationSeconds = 600 });
        first = Video("Eins"); second = Video("Zwei"); third = Video("Drei");
    }
    public void Dispose() { catalog.Dispose(); SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }

    private string[] Titles(SearchCondition condition) =>
        catalog.QueryPage(new MediaQuery { Expression = SearchExpression.Where(condition) }).Items.Select(i => i.Title).Order(StringComparer.Ordinal).ToArray();
    private static SearchCondition MarkTag(SearchComparison comparison, string? text = null) => new() { Field = SearchField.MarkTag, Comparison = comparison, Text = text };

    [Fact]
    public void BookmarkTagsAreCleanedAndKept()
    {
        var saved = catalog.UpsertBookmark(new Bookmark { MediaId = first.Id, Title = "Kuss", PositionSeconds = 12, Tags = [" Kuss ", "kuss", "", "Strand"] });
        Assert.Equal(["Kuss", "Strand"], saved.Tags);
        Assert.Equal(["Kuss", "Strand"], catalog.GetBookmarks(first.Id).Single().Tags);
        Assert.Throws<ArgumentException>(() => catalog.UpsertBookmark(saved with { Tags = [new string('x', 257)] }));
    }

    [Fact]
    public void JumpMarkTagsOfBookmarksAndScenesFindTheirMedia()
    {
        catalog.UpsertBookmark(new Bookmark { MediaId = first.Id, Title = "a", PositionSeconds = 1, Tags = ["Kuss"] });
        catalog.UpsertScene(new Scene { MediaId = second.Id, Title = "b", StartSeconds = 1, EndSeconds = 9, Tags = ["kuss", "Tanz"] });
        catalog.UpsertBookmark(new Bookmark { MediaId = third.Id, Title = "ohne", PositionSeconds = 1 });

        Assert.Equal(["Eins", "Zwei"], Titles(MarkTag(SearchComparison.Equals, "KUSS")));
        Assert.Equal(["Zwei"], Titles(MarkTag(SearchComparison.StartsWith, "ta")));
        Assert.Equal(["Drei"], Titles(MarkTag(SearchComparison.NotEquals, "Kuss")));
        Assert.Equal(["Eins", "Zwei"], Titles(MarkTag(SearchComparison.IsKnown)));
        Assert.Equal(["Drei"], Titles(MarkTag(SearchComparison.IsUnknown)));
        Assert.Equal([new MarkTagCount("Kuss", 2), new MarkTagCount("Tanz", 1)], catalog.GetMarkTags());

        // The filter bar asks for every ticked tag.
        string[] Filtered(params string[] tags) => catalog.QueryPage(new ViewState { Filter = new() { MarkTags = tags.ToList() } }.ToQuery(DateTimeOffset.Now))
            .Items.Select(i => i.Title).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Zwei"], Filtered("Kuss", "Tanz"));
        Assert.Contains("Tanz", Assert.Single(new MediaFilter { MarkTags = ["Tanz"] }.Describe()));
    }

    [Fact]
    public void SetsNestKeepTheirContentAndRefuseCycles()
    {
        var outer = catalog.UpsertCollectionSet(new CollectionSet { Name = " Urlaub " });
        var inner = catalog.UpsertCollectionSet(new CollectionSet { Name = "2026", ParentId = outer.Id });
        Assert.Equal("Urlaub", outer.Name);
        Assert.Throws<ArgumentException>(() => catalog.UpsertCollectionSet(outer with { ParentId = inner.Id }));
        Assert.Throws<ArgumentException>(() => catalog.UpsertCollectionSet(new CollectionSet { Name = "lost", ParentId = Guid.NewGuid() }));

        var playlist = catalog.UpsertPlaylist(new Playlist { Name = "Strand", ParentId = inner.Id, MediaIds = [first.Id] });
        var smart = catalog.UpsertSavedSearch(new SavedSearch { Name = "Neu", ParentId = inner.Id, State = new ViewState() });
        Assert.Throws<ArgumentException>(() => catalog.UpsertPlaylist(playlist with { ParentId = Guid.NewGuid() }));

        catalog.DeleteCollectionSet(inner.Id);
        Assert.Equal(outer.Id, catalog.GetPlaylists().Single(p => p.Id == playlist.Id).ParentId);
        Assert.Equal(outer.Id, catalog.GetSavedSearches().Single(s => s.Id == smart.Id).ParentId);
        catalog.DeleteCollectionSet(outer.Id);
        Assert.Null(catalog.GetPlaylists().Single(p => p.Id == playlist.Id).ParentId);
        Assert.Empty(catalog.GetCollectionSets());
    }

    [Fact]
    public void KeyBTogglesTheTargetCollection()
    {
        var quick = catalog.GetQuickCollection();
        Assert.Equal(PlaylistKind.Quick, quick.Kind);
        Assert.Equal(quick.Id, catalog.GetQuickCollection().Id);
        Assert.Equal(quick.Id, catalog.GetTargetCollection().Id);
        Assert.Throws<ArgumentException>(() => catalog.UpsertPlaylist(new Playlist { Name = "second", Kind = PlaylistKind.Quick }));

        var (collection, added) = catalog.ToggleInCollection(quick.Id, [first.Id, second.Id]);
        Assert.True(added); Assert.Equal([first.Id, second.Id], collection.MediaIds);
        // Not all are in it: B adds the missing one; then all are in it and B takes them out.
        (collection, added) = catalog.ToggleInCollection(quick.Id, [second.Id, third.Id]);
        Assert.True(added); Assert.Equal([first.Id, second.Id, third.Id], collection.MediaIds);
        (collection, added) = catalog.ToggleInCollection(quick.Id, [second.Id, third.Id]);
        Assert.False(added); Assert.Equal([first.Id], collection.MediaIds);

        var holiday = catalog.UpsertPlaylist(new Playlist { Name = "Urlaub" });
        catalog.SetTargetCollection(holiday.Id);
        Assert.Equal(holiday.Id, catalog.GetTargetCollection().Id);
        catalog.DeletePlaylist(holiday.Id);
        Assert.Equal(quick.Id, catalog.GetTargetCollection().Id);
        Assert.Throws<ArgumentException>(() => catalog.SetTargetCollection(Guid.NewGuid()));
    }

    [Fact]
    public void ExportAndImportKeepTagsSetsAndOneQuickCollection()
    {
        var set = catalog.UpsertCollectionSet(new CollectionSet { Name = "Sammlungen" });
        catalog.UpsertPlaylist(new Playlist { Name = "Strand", ParentId = set.Id, MediaIds = [first.Id] });
        catalog.UpsertSavedSearch(new SavedSearch { Name = "Neu", ParentId = set.Id, State = new ViewState() });
        catalog.ToggleInCollection(catalog.GetQuickCollection().Id, [second.Id]);
        catalog.UpsertBookmark(new Bookmark { MediaId = first.Id, Title = "a", PositionSeconds = 1, Tags = ["Kuss"] });
        var export = Path.Combine(directory, "export.json");
        catalog.Export(export);

        using var target = new CatalogRepository(Path.Combine(directory, "target.db"));
        var otherQuick = target.GetQuickCollection();
        var result = target.ImportJson(export);
        Assert.Empty(result.Errors);
        Assert.Equal(["Kuss"], target.GetBookmarks().Single().Tags);
        var importedSet = Assert.Single(target.GetCollectionSets());
        Assert.Equal(importedSet.Id, target.GetPlaylists().Single(p => p.Name == "Strand").ParentId);
        Assert.Equal(importedSet.Id, target.GetSavedSearches().Single().ParentId);
        // The library already had a quick collection; the imported one becomes a normal collection.
        Assert.Single(target.GetPlaylists(), p => p.Kind == PlaylistKind.Quick);
        Assert.Equal(otherQuick.Id, target.GetQuickCollection().Id);
    }

    [Fact]
    public void UpgradesAVersion12Library()
    {
        catalog.UpsertBookmark(new Bookmark { MediaId = first.Id, Title = "alt", PositionSeconds = 5 });
        catalog.Dispose(); SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "DROP INDEX bookmarks_media; DROP INDEX scenes_media; DROP TABLE collection_sets; PRAGMA user_version=12;";
            downgrade.ExecuteNonQuery();
        }
        catalog = new CatalogRepository(path);
        Assert.Contains("before-v16-v12", catalog.MigrationBackupPath!);
        Assert.Empty(catalog.GetBookmarks().Single().Tags);
        Assert.Empty(catalog.GetCollectionSets());
        Assert.Equal(["Drei", "Eins", "Zwei"], Titles(MarkTag(SearchComparison.IsUnknown)));
    }
}
