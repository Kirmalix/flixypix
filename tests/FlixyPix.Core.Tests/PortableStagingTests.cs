using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class PortableStagingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-portable-stage-" + Guid.NewGuid().ToString("N"));
    private CatalogRepository Open() => new(Path.Combine(directory, "catalog.db"));
    private string Write(string content, Encoding? encoding = null)
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, Guid.NewGuid() + ".json"); File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false)); return path;
    }
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private MediaItem Item(string name) => new() { Path = Path.Combine(directory, name + ".jpg"), Title = name };

    [Fact]
    public void TotalDocumentMayExceedEntityLimitAndUtf8ChunksWithoutLoadingAllRecords()
    {
        using var catalog = Open();
        var path = Path.Combine(directory, "many.json");
        using (var output = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            output.Write("{\"items\":[");
            for (var index = 0; index < 2500; index++)
            {
                if (index != 0) output.Write(',');
                output.Write(JsonSerializer.Serialize(Item($"München 漢字 🙂 {index}") with { Description = new string('ä', 120) }, Options));
            }
            output.Write("],\"futureProperty\":{\"nested\":[true,null,1]},\"format\":\"flixypix-catalog\",\"version\":2}");
        }
        Assert.True(new FileInfo(path).Length > 64 * 1024);
        var sourceHash = SHA256.HashData(File.ReadAllBytes(path));
        var result = catalog.ImportJson(path, 2048);
        Assert.Empty(result.Errors); Assert.Equal(2500, result.Imported);
        Assert.Equal(2500, catalog.QueryPage(new()).TotalCount);
        Assert.Equal(2500, catalog.QueryPage(new() { Text = "漢字 🙂" }).TotalCount);
        Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void LargeAssetAndReferencesWorkAcrossReorderedSectionsAndExistingPathIdRemapping()
    {
        using var catalog = Open();
        var original = Item("Image"); var existing = catalog.Upsert(original with { Id = Guid.NewGuid() });
        var data = new byte[180_000]; new Random(42).NextBytes(data);
        var scene = new Scene { MediaId = original.Id, Title = "Clip", StartSeconds = 1.5, EndSeconds = 4 };
        var asset = new MediaAsset { MediaId = original.Id, Data = data, Kind = MediaAssetKind.ScenePreview, PositionSeconds = 2.5 };
        scene = scene with { PreviewAssetId = asset.Id, Tags = [" Portrait ", "portrait"] };
        var playlist = new Playlist { Name = "Ordered", MediaIds = [original.Id], Entries = [new(original.Id, scene.Id)] };
        var bookmark = new Bookmark { MediaId = original.Id, Title = "Point", PositionSeconds = 2, PreviewAssetId = asset.Id };
        var content = "{\"format\":\"flixypix-catalog\",\"version\":2,\"mediaAssets\":[" + JsonSerializer.Serialize(asset, Options) +
            "],\"bookmarks\":[" + JsonSerializer.Serialize(bookmark, Options) + "],\"playlists\":[" + JsonSerializer.Serialize(playlist, Options) +
            "],\"scenes\":[" + JsonSerializer.Serialize(scene, Options) + "],\"items\":[" + JsonSerializer.Serialize(original, Options) + "]}";
        var result = catalog.ImportJson(Write(content)); Assert.Empty(result.Errors);
        Assert.Equal(existing.Id, Assert.Single(catalog.GetMediaAssets(existing.Id)).MediaId);
        Assert.Equal(data, Assert.Single(catalog.GetMediaAssets(existing.Id)).Data);
        Assert.Equal(existing.Id, Assert.Single(catalog.GetScenes()).MediaId);
        Assert.Equal(asset.Id, Assert.Single(catalog.GetScenes()).PreviewAssetId);
        Assert.Equal("Portrait", Assert.Single(Assert.Single(catalog.GetScenes()).Tags));
        Assert.Equal(existing.Id, Assert.Single(catalog.GetBookmarks()).MediaId);
        Assert.Equal(existing.Id, Assert.Single(catalog.GetPlaylists()).MediaIds[0]);
        Assert.Equal(scene.Id, Assert.Single(catalog.GetPlaylists()).Entries[0].SceneId);
    }

    [Theory]
    [InlineData("utf8bom")] [InlineData("utf16le")] [InlineData("utf16be")] [InlineData("utf32le")] [InlineData("utf32be")]
    public void BomEncodingsAndCaseInsensitiveLastPropertyWinsRemainCompatible(string encodingName)
    {
        using var catalog = Open();
        var encoding = encodingName switch { "utf8bom" => new UTF8Encoding(true), "utf16le" => Encoding.Unicode, "utf16be" => Encoding.BigEndianUnicode,
            "utf32le" => Encoding.UTF32, _ => new UTF32Encoding(true, true) };
        var item = Item("Straße 漢字");
        var content = "{\"FORMAT\":\"wrong\",\"Format\":\"flixypix-catalog\",\"version\":1,\"VERSION\":2,\"items\":[null],\"Items\":[" + JsonSerializer.Serialize(item, Options) + "]}";
        var result = catalog.ImportJson(Write(content, encoding)); Assert.Empty(result.Errors);
        Assert.Equal(item.Title, Assert.Single(catalog.QueryPage(new()).Items).Title);
    }

    [Fact]
    public void LegacyBatchHashesRepeatedRowsAndIdempotenceMatchPreviousPortableImport()
    {
        using var catalog = Open();
        var groups = new[]
        {
            new[] { "{\"type\":\"header\",\"future\":\"ä\"}", "{\"type\":\"row\",\"same\":1}", "{\"type\":\"row\",\"same\":1}", "{\"type\":\"footer\"}" },
            new[] { "{\"type\":\"unknown\",\"future\":true}", "{\"type\":\"unknown\",\"future\":true}" }
        };
        var raw = groups.SelectMany(x => x).ToArray();
        var file = Write(JsonSerializer.Serialize(new CatalogDocument { LegacyRecords = raw.ToList() }, Options));
        Assert.Empty(catalog.ImportJson(file).Errors); Assert.Empty(catalog.ImportJson(file).Errors);
        using var verify = new SqliteConnection($"Data Source={catalog.DatabasePath};Mode=ReadOnly;Pooling=False"); verify.Open();
        using var command = verify.CreateCommand(); command.CommandText = "SELECT batch,count(*) FROM legacy_records GROUP BY batch ORDER BY batch";
        using var reader = command.ExecuteReader(); var actual = new Dictionary<string, int>();
        while (reader.Read()) actual[reader.GetString(0)] = reader.GetInt32(1);
        foreach (var group in groups)
        {
            var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(group))));
            Assert.Equal(group.Length, actual[expected]);
        }
        Assert.Equal(2, actual.Count);
    }

    [Theory]
    [InlineData("entity")] [InlineData("truncated")] [InlineData("trailing")] [InlineData("versiontype")] [InlineData("nullarray")]
    public void MalformedOrOversizedInputRollsBackAndLeavesReaderReusable(string variant)
    {
        using var catalog = Open(); var old = catalog.Upsert(Item("Existing"));
        var item = Item("Added");
        var valid = JsonSerializer.Serialize(new CatalogDocument { Items = [item] }, Options);
        var malformed = variant switch
        {
            "entity" => JsonSerializer.Serialize(new CatalogDocument { Items = [item with { Description = new string('x', 5000) }] }, Options),
            "truncated" => valid[..^2], "trailing" => valid + "[]", "versiontype" => valid.Replace("\"version\":4", "\"version\":\"4\""),
            _ => "{\"format\":\"flixypix-catalog\",\"version\":2,\"bookmarks\":null}"
        };
        var result = catalog.ImportJson(Write(malformed), 2048); Assert.NotEmpty(result.Errors);
        Assert.Equal(old.Id, Assert.Single(catalog.QueryPage(new()).Items).Id);
        Assert.Empty(catalog.ImportJson(Write(valid), 2048).Errors); Assert.Equal(2, catalog.QueryPage(new()).TotalCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MissingOrWrongMediaPreviewReferencesRollBackEveryProjectedSection(bool wrongMedia)
    {
        using var catalog = Open(); var existing = catalog.Upsert(Item("Existing"));
        var incoming = Item("Incoming"); var preview = new MediaAsset { MediaId = wrongMedia ? existing.Id : incoming.Id, Data = [1, 2, 3] };
        var scene = new Scene { MediaId = incoming.Id, Title = "Bad preview", StartSeconds = 0, EndSeconds = 1, PreviewAssetId = preview.Id };
        var document = new CatalogDocument { Items = [incoming], Scenes = [scene], MediaAssets = wrongMedia ? [preview] : [] };
        var result = catalog.ImportJson(Write(JsonSerializer.Serialize(document, Options)));
        Assert.NotEmpty(result.Errors); Assert.Equal(existing.Id, Assert.Single(catalog.QueryPage(new()).Items).Id);
        Assert.Empty(catalog.GetScenes()); Assert.Empty(catalog.GetMediaAssets(existing.Id));
    }

    [Fact]
    public void CancellationDuringStagingPublishesNothingAndRetryWorks()
    {
        using var catalog = Open(); var existing = catalog.Upsert(Item("Existing"));
        var file = Path.Combine(directory, "cancel.json");
        using (var output = new StreamWriter(file))
        {
            output.Write("{\"format\":\"flixypix-catalog\",\"version\":2,\"items\":[");
            for (var index = 0; index < 10_000; index++)
            { if (index != 0) output.Write(','); output.Write(JsonSerializer.Serialize(Item("Cancel" + index), Options)); }
            output.Write("]}");
        }
        using var cancellation = new CancellationTokenSource(); cancellation.CancelAfter(TimeSpan.FromMilliseconds(10));
        Assert.Throws<OperationCanceledException>(() => catalog.ImportJson(file, cancellation.Token));
        Assert.Equal(existing.Id, Assert.Single(catalog.QueryPage(new()).Items).Id);
        Assert.Empty(catalog.ImportJson(Write(JsonSerializer.Serialize(new CatalogDocument(), Options))).Errors);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
