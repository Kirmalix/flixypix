using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class MediaAssetTests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-assets-" + Guid.NewGuid().ToString("N"));
 private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aHucAAAAASUVORK5CYII=");
 public MediaAssetTests() => Directory.CreateDirectory(root);

 [Fact]
 public void RegistryAliasesAndIsoImageBrandsAreRecognizedWithoutTreatingMp4AsAnImage()
 {
  Assert.Equal(MediaKind.Image,MediaFormats.GetKind("photo.JFIF"));
  Assert.Equal(MediaKind.Image,MediaFormats.GetKind("animation.apng"));
  Assert.Equal(MediaKind.Video,MediaFormats.GetKind("movie.3g2"));
  Assert.Null(MediaFormats.GetKind("script.ps1"));
  byte[] Container(string major,string compatible)
  {
   var bytes = new byte[20]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes,20);
   System.Text.Encoding.ASCII.GetBytes("ftyp"+major).CopyTo(bytes,4);
   System.Text.Encoding.ASCII.GetBytes(compatible).CopyTo(bytes,16); return bytes;
  }
  Assert.Equal(".avif",CatalogRepository.ImageExtension(Container("mif1","avif")));
  Assert.Equal(".heic",CatalogRepository.ImageExtension(Container("mif1","heic")));
  Assert.Null(CatalogRepository.ImageExtension(Container("isom","mp42")));
 }

 [Fact]
 public void AssetRoundtripIncludesBytesAndRebuildsCacheWithoutSourceFiles()
 {
  using var source = new CatalogRepository(Path.Combine(root, "source.db"));
  var media = source.Upsert(new() { Path = Path.Combine(root, "missing.mp4"), Kind = MediaKind.Video });
  var asset = source.UpsertMediaAsset(new() { MediaId = media.Id, Data = Png, Kind = MediaAssetKind.ScenePreview, PositionSeconds = 12.5, Label = "Moment" });
  var output = source.MaterializeMediaAsset(asset, Path.Combine(root, "cache"));
  Assert.Equal(Png, File.ReadAllBytes(output)); Assert.Equal(output, source.MaterializeMediaAsset(asset, Path.Combine(root, "cache")));
  var json = Path.Combine(root, "backup.json"); source.Export(json);
  using var destination = new CatalogRepository(Path.Combine(root, "destination.db"));
  Assert.Empty(destination.ImportJson(json).Errors);
  var restored = Assert.Single(destination.GetMediaAssets(media.Id)); Assert.Equal(Png, restored.Data); Assert.Equal(12.5, restored.PositionSeconds);
  var summary=Assert.Single(destination.GetMediaAssetSummaries(media.Id));Assert.Equal(Png.Length,summary.ByteLength);Assert.Equal(restored.Id,summary.Id);Assert.Equal(Png,destination.GetMediaAsset(summary.Id)!.Data);
  File.Delete(output); Assert.Equal(Png, File.ReadAllBytes(destination.MaterializeMediaAsset(restored, Path.Combine(root, "cache"))));
  destination.Delete(media.Id); Assert.Empty(destination.GetMediaAssets(media.Id));
 }

 [Fact]
 public void CorruptCacheAndInvalidReferencesAreRejectedWithoutLosingOriginalAsset()
 {
  using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
  var media = catalog.Upsert(new() { Path = Path.Combine(root, "x.jpg") });
  var asset = catalog.UpsertMediaAsset(new() { MediaId = media.Id, Data = Png });
  Assert.Throws<ArgumentException>(() => catalog.UpsertMediaAsset(asset with { MediaId = Guid.NewGuid() }));
  var output = catalog.MaterializeMediaAsset(asset, Path.Combine(root, "cache")); File.WriteAllText(output, "corrupt");
  Assert.Throws<InvalidDataException>(() => catalog.MaterializeMediaAsset(asset, Path.Combine(root, "cache")));
  Assert.Equal(Png, Assert.Single(catalog.GetMediaAssets(media.Id)).Data);
  Assert.Throws<ArgumentException>(() => catalog.UpsertMediaAsset(asset with { Data = [] }));
 }

 [Fact]
 public void LegacyArtworkAndScenePreviewJoinOnlyKnownMediaAndPreserveRawPayload()
 {
  var path = Path.Combine(root, "source.jsonl");
  object Schema(string table, params string[] names) => new { type = "schema", table, columns = names.Select(name => new { name }) };
  object Row(string table, object values) => new { type = "row", table, values };
  var binary = new Dictionary<string, string> { ["$binary"] = Convert.ToBase64String(Png) };
  object[] records = [
   new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('a',64) },
   Schema("movies", "ID", "fullpath", "filename"), Row("movies", new { ID = 42, fullpath = root, filename = "film.mp4" }),
   Schema("mediaart", "ID", "remoteid", "mediatype", "flag", "img", "position"),
   Row("mediaart", new { ID = 1, remoteid = 42, mediatype = 2, flag = "M", img = binary, position = 3 }),
   Row("mediaart", new { ID = 2, remoteid = 999, mediatype = 2, flag = "M", img = binary, position = 0 }),
   Row("mediaart", new { ID = 3, remoteid = 42, mediatype = 2, flag = "T", img = binary, position = 0 }),
   Schema("videoscenes", "ID", "videoid", "secs", "img"), Row("videoscenes", new { ID = 4, videoid = 42, secs = 12, img = binary }),
   new { type = "footer", tables = 3, rows = 5, tableRows = new Dictionary<string,int> { ["movies"]=1,["mediaart"]=3,["videoscenes"]=1 } }
  ];
  File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(record)));
  using var catalog = new CatalogRepository(Path.Combine(root, "imported.db"));
  var result = catalog.ImportLegacyJsonl(path); Assert.Empty(result.Errors);
  var media = Assert.Single(catalog.Search()); var assets = catalog.GetMediaAssets(media.Id);
  Assert.Equal(2, assets.Count); Assert.All(assets, asset => Assert.Equal(Png, asset.Data));
  Assert.Contains(assets, a => a.Kind == MediaAssetKind.ScenePreview && a.PositionSeconds == 12);
  Assert.Contains(result.Warnings, w => w.Contains("unresolved"));
  Assert.Empty(catalog.ImportLegacyJsonl(path).Errors); Assert.Equal(2, catalog.GetMediaAssets(media.Id).Count);
  catalog.Export(Path.Combine(root, "backup.json")); Assert.Contains("$binary", File.ReadAllText(Path.Combine(root, "backup.json")));
 }

 [Fact]
 public void InvalidAssetInPortableBackupRollsBackMediaAndAssetChanges()
 {
  var json = Path.Combine(root, "invalid.json"); var item = new MediaItem { Path = Path.Combine(root, "good.jpg") };
  File.WriteAllText(json, JsonSerializer.Serialize(new CatalogDocument { Items = [item], MediaAssets = [new() { MediaId = Guid.NewGuid(), Data = Png }] }));
  using var catalog = new CatalogRepository(Path.Combine(root, "rollback.db"));
  Assert.NotEmpty(catalog.ImportJson(json).Errors); Assert.Empty(catalog.Search());
 }
 public void Dispose() => Directory.Delete(root, true);
}
