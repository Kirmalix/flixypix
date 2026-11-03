using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>The scene wall lists scenes and bookmarks of all videos with their video, filters by text and tags of mark
/// or video, and orders by import, then by place.</summary>
public sealed class SceneWallTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-wall-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogRepository catalog;

    public SceneWallTests()
    {
        Directory.CreateDirectory(directory);
        catalog = new CatalogRepository(Path.Combine(directory, "catalog.db"));
    }

    public void Dispose() { catalog.Dispose(); SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }

    private MediaItem Add(string title, MediaKind kind, DateTimeOffset added, params string[] tags) => catalog.Upsert(new MediaItem
    {
        Path = Path.Combine(directory, title + (kind == MediaKind.Video ? ".mp4" : ".mp3")), Title = title, Kind = kind, DurationSeconds = 600, AddedAt = added, Tags = [.. tags],
    });

    [Fact]
    public void TextHitCacheUsesTheCurrentCulture()
    {
        var media = Add("Istanbul", MediaKind.Video, DateTimeOffset.UtcNow);
        catalog.UpsertBookmark(new() { MediaId = media.Id, Title = "Moment" });
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            Assert.Single(catalog.QueryWallPage(new(Text: "istanbul")).Items);
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            Assert.Empty(catalog.QueryWallPage(new(Text: "istanbul")).Items);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void TextHitCacheInvalidatesForLocalAndExternalEdits()
    {
        var media = Add("Cache video", MediaKind.Video, DateTimeOffset.UtcNow);
        var mark = catalog.UpsertBookmark(new() { MediaId = media.Id, Title = "Initial" });
        Assert.Empty(catalog.QueryWallPage(new(Text: "Needle")).Items);
        mark = catalog.UpsertBookmark(mark with { Title = "Needle" });
        Assert.Single(catalog.QueryWallPage(new(Text: "Needle")).Items);
        using (var other = new CatalogRepository(catalog.DatabasePath)) other.UpsertBookmark(mark with { Title = "Changed externally" });
        Assert.Empty(catalog.QueryWallPage(new(Text: "Needle")).Items);
        catalog.Upsert(media with { Description = "Needle" });
        Assert.Single(catalog.QueryWallPage(new(Text: "Needle")).Items);
    }

    [Fact]
    public void ProjectedTextSearchMatchesLegacyAcrossMediaMarksAndKindMasks()
    {
        var first = Add("Shared text", MediaKind.Video, DateTimeOffset.UtcNow);
        var second = Add("Other video", MediaKind.Video, DateTimeOffset.UtcNow);
        catalog.UpsertBookmark(new() { MediaId = first.Id, Title = "Bookmark", PositionSeconds = 1 });
        catalog.UpsertScene(new() { MediaId = second.Id, Title = "SHARED mark", StartSeconds = 2, EndSeconds = 3 });
        catalog.UpsertBookmark(new() { MediaId = second.Id, Title = "Unrelated", Description = "Detail match", PositionSeconds = 4 });
        foreach (var text in new[] { "shared", "Detail", "video", "unmatched" })
        foreach (var mask in new[] { (true, true), (true, false), (false, true), (false, false) })
        {
            var query = new WallQuery(Text: text, Scenes: mask.Item1, Bookmarks: mask.Item2);
            var expected = catalog.GetWallMarks(query);
            var page = catalog.QueryWallPage(query, limit: 1);
            Assert.Equal(expected.Count, page.TotalCount);
            Assert.Equal(expected.Take(1).Select(m => m.Id), page.Items.Select(m => m.Id));
        }
    }

    [Fact]
    public void MediaKindChangesRefreshEligibleMarksWithoutLosingSources()
    {
        var media = Add("Type", MediaKind.Video, DateTimeOffset.UtcNow);
        catalog.UpsertBookmark(new() { MediaId = media.Id });
        Assert.Equal(1, catalog.QueryWallPage(new()).TotalCount);
        catalog.Upsert(media with { Kind = MediaKind.Audio });
        Assert.Equal(0, catalog.QueryWallPage(new()).TotalCount); Assert.Single(catalog.GetBookmarks(media.Id));
        catalog.Upsert(media);
        Assert.Equal(1, catalog.QueryWallPage(new()).TotalCount);
    }

    [Fact]
    public void Schema17MigrationRebuildsProjectionAndPreservesSourceDocuments()
    {
        var media = Add("Migration", MediaKind.Video, DateTimeOffset.UtcNow);
        var bookmark = catalog.UpsertBookmark(new() { MediaId = media.Id, Title = "Recover projection", Tags = ["Keep"] });
        catalog.Dispose();
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(directory, "catalog.db")))
        {
            db.Open(); CatalogRepository.AddOrdinalCollation(db); using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM wall_tags; DELETE FROM wall_marks; DELETE FROM wall_media; PRAGMA user_version=17;";
            command.ExecuteNonQuery();
        }
        using var reopened = new CatalogRepository(Path.Combine(directory, "catalog.db"));
        Assert.NotNull(reopened.MigrationBackupPath);
        Assert.Equal(bookmark.Id, Assert.Single(reopened.QueryWallPage(new(Tags: ["Keep"])).Items).Id);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(bookmark), System.Text.Json.JsonSerializer.Serialize(Assert.Single(reopened.GetBookmarks(media.Id))));
    }

    [Fact]
    public void DatabasePagingMatchesFullWallAndUpdatesAfterEdits()
    {
        var media = Add("Paged", MediaKind.Video, DateTimeOffset.UtcNow, "Performer");
        for (var index = 0; index < 9; index++) catalog.UpsertBookmark(new() { MediaId = media.Id, Title = "Mark " + index, PositionSeconds = index, Tags = [index % 2 == 0 ? "Even" : "Odd"] });
        var query = new WallQuery(Tags: ["performer", "even"]);
        var expected = catalog.GetWallMarks(query);
        var first = catalog.QueryWallPage(query, limit: 2);
        var second = catalog.QueryWallPage(query, offset: 2, limit: 2);
        Assert.Equal(5, first.TotalCount); Assert.Equal(5, second.TotalCount);
        Assert.Equal(expected.Take(4).Select(m => m.Id), first.Items.Concat(second.Items).Select(m => m.Id));
        var bookmark = catalog.GetBookmarks(media.Id).First();
        catalog.UpsertBookmark(bookmark with { Title = "Changed title", Tags = ["Unique"] });
        Assert.Equal(bookmark.Id, Assert.Single(catalog.QueryWallPage(new(Text: "changed TITLE", Tags: ["unique"])).Items).Id);
        catalog.DeleteBookmark(bookmark.Id);
        Assert.Empty(catalog.QueryWallPage(new(Tags: ["unique"])).Items);
    }

    [Fact]
    public void DatabasePagingPreservesTickAndOffsetOrderingAndCulture()
    {
        var when = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234500);
        var first = Add("Istanbul", MediaKind.Video, when.ToOffset(TimeSpan.FromHours(2)), "ISIK");
        var second = Add("Newer", MediaKind.Video, when.AddTicks(1));
        catalog.UpsertBookmark(new() { MediaId = first.Id, Title = "First", PositionSeconds = 1 });
        catalog.UpsertScene(new() { MediaId = second.Id, Title = "Second", StartSeconds = 1, EndSeconds = 2 });
        Assert.Equal(catalog.GetWallMarks(new()).Select(m => m.Id), catalog.QueryWallPage(new()).Items.Select(m => m.Id));
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            var query = new WallQuery(Text: "ıstanbul", Tags: ["ısık"]);
            Assert.Equal(catalog.GetWallMarks(query).Select(m => m.Id), catalog.QueryWallPage(query).Items.Select(m => m.Id));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void BookmarkOwnTagsAreSearchableWithoutTaggingTheVideo()
    {
        var media = Add("Video", MediaKind.Video, DateTimeOffset.UtcNow);
        var bookmark = catalog.UpsertBookmark(new Bookmark { MediaId = media.Id, Title = "Moment", Tags = ["Unique"], PositionSeconds = 12 });
        Assert.Equal(bookmark.Id, Assert.Single(catalog.GetWallMarks(new WallQuery(Tags: ["unique"]))).Id);
        Assert.Empty(catalog.GetMedia(media.Id)!.Tags);
    }

    [Fact]
    public void ListsMarksOfVideosNewestFirstThenByPlace()
    {
        var older = Add("Alt", MediaKind.Video, DateTimeOffset.UtcNow.AddDays(-2), "Jane Doe");
        var newer = Add("Neu", MediaKind.Video, DateTimeOffset.UtcNow, "Acme");
        var audio = Add("Hörbuch", MediaKind.Audio, DateTimeOffset.UtcNow);
        catalog.UpsertScene(new Scene { MediaId = older.Id, Title = "Anfang", StartSeconds = 10, EndSeconds = 40, Tags = ["Strand"] });
        catalog.UpsertBookmark(new Bookmark { MediaId = newer.Id, Title = "Spät", PositionSeconds = 300 });
        catalog.UpsertScene(new Scene { MediaId = newer.Id, Title = "Früh", Description = "mit Sonnenuntergang", StartSeconds = 20, EndSeconds = 60 });
        catalog.UpsertBookmark(new Bookmark { MediaId = audio.Id, Title = "Kapitel", PositionSeconds = 5 });

        var all = catalog.GetWallMarks(new WallQuery());
        Assert.Equal(["Früh", "Spät", "Anfang"], all.Select(m => m.Title));
        Assert.Equal([WallMarkKind.Scene, WallMarkKind.Bookmark, WallMarkKind.Scene], all.Select(m => m.Kind));
        Assert.Equal(newer.Id, all[0].Media.Id); Assert.Equal(60, all[0].EndSeconds); Assert.Null(all[1].EndSeconds);

        Assert.Equal(["Früh"], catalog.GetWallMarks(new WallQuery(Text: "sonnenUNTERgang")).Select(m => m.Title));
        Assert.Equal(["Anfang"], catalog.GetWallMarks(new WallQuery(Text: "alt")).Select(m => m.Title));
        Assert.Equal(["Anfang"], catalog.GetWallMarks(new WallQuery(Tags: ["jane doe", "Strand"])).Select(m => m.Title));
        Assert.Empty(catalog.GetWallMarks(new WallQuery(Tags: ["Acme", "Strand"])));
        Assert.Equal(["Spät"], catalog.GetWallMarks(new WallQuery(Scenes: false)).Select(m => m.Title));
        Assert.Equal(["Früh", "Anfang"], catalog.GetWallMarks(new WallQuery(Bookmarks: false)).Select(m => m.Title));
    }
}
