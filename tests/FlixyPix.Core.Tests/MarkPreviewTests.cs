using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class MarkPreviewTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-marks-" + Guid.NewGuid().ToString("N"));
    public MarkPreviewTests() => Directory.CreateDirectory(directory);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }

    // Smallest JPEG markers are enough for the asset check.
    private static byte[] Jpeg(byte tag) => [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, tag, 0xFF, 0xD9];

    [Fact]
    public void MissingAndMovedPicturesAreMadeAndHandChosenOnesStay()
    {
        using var catalog = new CatalogRepository(Path.Combine(directory, "catalog.db"));
        var video = catalog.Upsert(new() { Path = Path.Combine(directory, "a.mp4"), Kind = MediaKind.Video, DurationSeconds = 600 });
        var bookmark = catalog.UpsertBookmark(new() { MediaId = video.Id, Title = "Stelle", PositionSeconds = 100 });
        var scene = catalog.UpsertScene(new() { MediaId = video.Id, Title = "Szene", StartSeconds = 200, EndSeconds = 260 });
        var needs = catalog.GetMarkPreviewNeeds(video.Id);
        Assert.Equal([(bookmark.Id, false, 100.0), (scene.Id, true, 200.0)], needs.Select(n => (n.Id, n.IsScene, n.PositionSeconds)));
        Assert.All(needs, need => Assert.True(catalog.SetGeneratedMarkPreview(need, Jpeg(1))));
        Assert.Empty(catalog.GetMarkPreviewNeeds(video.Id));
        var first = catalog.GetBookmarks(video.Id).Single().PreviewAssetId!.Value;
        Assert.Equal(100, catalog.GetMediaAsset(first)!.PositionSeconds);

        // A moved bookmark gets a new picture; the old generated one is removed.
        catalog.UpsertBookmark(catalog.GetBookmarks(video.Id).Single() with { PositionSeconds = 150 });
        var moved = Assert.Single(catalog.GetMarkPreviewNeeds(video.Id));
        Assert.False(catalog.SetGeneratedMarkPreview(moved with { PositionSeconds = 300 }, Jpeg(2)));
        Assert.True(catalog.SetGeneratedMarkPreview(moved, Jpeg(2)));
        Assert.Null(catalog.GetMediaAsset(first));
        Assert.Empty(catalog.GetMarkPreviewNeeds(video.Id));

        // A picture chosen by hand (not generated) is kept even after the scene moves.
        var chosen = catalog.UpsertMediaAsset(new() { MediaId = video.Id, Kind = MediaAssetKind.ScenePreview, Label = "eigenes Bild", Data = Jpeg(3) });
        catalog.SetScenePreview(scene.Id, chosen.Id);
        catalog.UpsertScene(catalog.GetScenes(video.Id).Single() with { StartSeconds = 210, EndSeconds = 260 });
        Assert.Empty(catalog.GetMarkPreviewNeeds(video.Id));
        Assert.Equal(chosen.Id, catalog.GetScenes(video.Id).Single().PreviewAssetId);
    }
}
