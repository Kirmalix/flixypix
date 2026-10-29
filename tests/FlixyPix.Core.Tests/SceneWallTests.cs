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
