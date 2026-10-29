using System.Security.Cryptography;
using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class CatalogTests : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flixypix-tests-" + Guid.NewGuid().ToString("N"));
    public CatalogTests() => Directory.CreateDirectory(directory);
    private string PathFor(string name) => System.IO.Path.Combine(directory, name);
    private CatalogRepository Open(string name = "catalog.sqlite") => new(PathFor(name));
    private MediaItem Item(string name = "sample.jpg") => new() { Path = PathFor(name), Title = "Ärger am See", Kind = MediaKind.Image };

    [Fact]
    public void CatalogPersistsUnicodeMetadataAndCaseInsensitiveSearch()
    {
        Guid id;
        using (var catalog = Open())
        {
            var item = catalog.Upsert(Item() with { Tags = ["Natur", "natur", "  Straße  ", ""], Rating = 9,
                Description = "Sommerurlaub", Extra = new() { ["unknown"] = "äöü" }, PlaybackPositionSeconds = 3.25 });
            id = item.Id;
            Assert.Equal(2, item.Tags.Count);
        }
        using var reopened = Open();
        var result = Assert.Single(reopened.Search("ärGER Sommer", "NATUR", 8));
        Assert.Equal(id, result.Id);
        Assert.Equal("äöü", result.Extra["unknown"]);
        Assert.Equal(3.25, result.PlaybackPositionSeconds);
        Assert.Empty(reopened.Search(minimumRating: 10));
        Assert.Equal(new[] { "Natur", "Straße" }, reopened.GetTags());
    }

    [Fact]
    public async Task RescanDoesNotDuplicateOrReplaceUserMetadata()
    {
        File.WriteAllText(PathFor("sample.jpg"), "synthetic media fixture");
        File.WriteAllText(PathFor("ignored.txt"), "not media");
        using var catalog = Open();
        var scanner = new MediaScanner(catalog);
        var first = await scanner.ScanAsync([directory]);
        Assert.Equal(1, first.Imported);
        var item = Assert.Single(catalog.Search());
        catalog.Upsert(item with { Title = "Edited", Rating = 7, Tags = ["Keep"] });
        var second = await scanner.ScanAsync([directory, PathFor("sample.jpg")]);
        Assert.Equal(0, second.Imported);
        var saved = Assert.Single(catalog.Search());
        Assert.Equal("Edited", saved.Title);
        Assert.Equal(7, saved.Rating);
        Assert.Equal(new[] { "Keep" }, saved.Tags);
        Assert.Equal(item.Id, saved.Id);
    }

    [Fact]
    public async Task ConcurrentScansImportEachPathOnce()
    {
        File.WriteAllText(PathFor("movie.mp4"), "synthetic media fixture");
        using var catalog = Open();
        var scanner = new MediaScanner(catalog);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => scanner.ScanAsync([PathFor("movie.mp4")])));
        Assert.Equal(1, results.Sum(x => x.Imported));
        Assert.Single(catalog.Search());
    }

    [Fact]
    public async Task ScannerReportsMissingInputAndHonorsCancellation()
    {
        using var catalog = Open();
        var scanner = new MediaScanner(catalog);
        Assert.Single((await scanner.ScanAsync([PathFor("missing.jpg")])).Errors);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync([directory], cancellationToken: cancellation.Token));
        Assert.Empty(catalog.Search());
    }

    [Fact]
    public void PortableRoundtripRetainsCollectionsScenesAndUnknownValuesWithoutChangingSource()
    {
        using var source = Open();
        var image = source.Upsert(Item());
        var video = source.Upsert(Item("movie.mp4") with { Kind = MediaKind.Video, DurationSeconds = 123.75, LegacyId = "movies:42",
            Extra = new() { ["legacy.values"] = "{\"unknown\":42}" } });
        source.UpsertPlaylist(new Playlist { Name = "Diashow", Kind = PlaylistKind.Slideshow, SlideSeconds = 2.5, MediaIds = [video.Id, image.Id, video.Id] });
        source.UpsertScene(new Scene { MediaId = video.Id, Title = "Kapitel", StartSeconds = 1.25, EndSeconds = 9.75 });
        var path = PathFor("portable.json");
        source.Export(path);
        var before = SHA256.HashData(File.ReadAllBytes(path));
        using var target = Open("target.sqlite");
        var result = target.ImportJson(path);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Imported);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        var playlist = Assert.Single(target.GetPlaylists());
        Assert.Equal(new[] { video.Id, image.Id, video.Id }, playlist.MediaIds);
        Assert.Equal(2.5, playlist.SlideSeconds);
        Assert.Equal(9.75, Assert.Single(target.GetScenes(video.Id)).EndSeconds);
        Assert.Equal("{\"unknown\":42}", target.Search().Single(x => x.Id == video.Id).Extra["legacy.values"]);
        target.Delete(video.Id);
        Assert.Empty(target.GetScenes());
        Assert.Equal(new[] { image.Id }, Assert.Single(target.GetPlaylists()).MediaIds);
    }

    [Fact]
    public void ImportRemapsCollectionReferencesWhenPathAlreadyExists()
    {
        var item = Item();
        using var catalog = Open();
        var existing = catalog.Upsert(item);
        var importedId = Guid.NewGuid();
        var doc = new CatalogDocument { Items = [item with { Id = importedId, Title = "Imported" }],
            Playlists = [new Playlist { Name = "List", MediaIds = [importedId] }],
            Scenes = [new Scene { MediaId = importedId, StartSeconds = 0, EndSeconds = 1 }] };
        var path = PathFor("import.json");
        File.WriteAllText(path, JsonSerializer.Serialize(doc));
        var result = catalog.ImportJson(path);
        Assert.Empty(result.Errors);
        Assert.Equal(existing.Id, Assert.Single(catalog.Search()).Id);
        Assert.Equal(existing.Id, Assert.Single(catalog.GetPlaylists()).MediaIds[0]);
        Assert.Equal(existing.Id, Assert.Single(catalog.GetScenes()).MediaId);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"format\":\"other\",\"version\":1}")]
    [InlineData("{\"format\":\"flixypix-catalog\",\"version\":99}")]
    [InlineData("{\"format\":\"flixypix-catalog\",\"version\":1,\"items\":null}")]
    public void InvalidPortableDocumentLeavesCatalogIntact(string json)
    {
        using var catalog = Open();
        var item = catalog.Upsert(Item());
        File.WriteAllText(PathFor("bad.json"), json);
        Assert.NotEmpty(catalog.ImportJson(PathFor("bad.json")).Errors);
        Assert.Equal(item.Id, Assert.Single(catalog.Search()).Id);
    }

    [Fact]
    public void InvalidPortableEntriesProduceActionableErrorsAndRollBackEntireImport()
    {
        var path = PathFor("partial.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new CatalogDocument { Items = [Item(), Item("invalid.mp4") with { Rating = 99 }] }));
        using var catalog = Open();
        var result = catalog.ImportJson(path);
        Assert.Equal(0, result.Imported);
        Assert.Equal(2, result.Skipped);
        Assert.Contains("rating", Assert.Single(result.Errors));
        Assert.Empty(catalog.Search());
    }

    [Fact]
    public void InvalidEntityReferencesAndSceneRangesAreRejected()
    {
        using var catalog = Open();
        Assert.Throws<ArgumentException>(() => catalog.UpsertPlaylist(new Playlist { Name = "Bad", MediaIds = [Guid.NewGuid()] }));
        Assert.Throws<ArgumentException>(() => catalog.UpsertScene(new Scene { MediaId = Guid.NewGuid(), StartSeconds = 4, EndSeconds = 3 }));
        Assert.Throws<ArgumentException>(() => catalog.Upsert(Item() with { PlaybackPositionSeconds = double.NaN }));
        Assert.Throws<ArgumentException>(() => catalog.Upsert(Item() with { Rating = 11 }));
        Assert.Throws<ArgumentException>(() => catalog.Export(catalog.DatabasePath));
        Assert.Empty(catalog.GetPlaylists());
    }

    [Fact]
    public void NewerDatabaseSchemaIsNotDowngraded()
    {
        using (var connection = new SqliteConnection($"Data Source={PathFor("future.sqlite")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=99";
            command.ExecuteNonQuery();
        }
        Assert.Throws<NotSupportedException>(() => Open("future.sqlite"));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
