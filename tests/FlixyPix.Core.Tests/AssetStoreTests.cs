using System.Security.Cryptography;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Picture bytes as files beside the catalog (schema 15): stored once, removed when unused, encrypted in an
/// encrypted library, carried by backups, checked by the catalog check and moved out of older catalogs.</summary>
public sealed class AssetStoreTests : IDisposable
{
 private const string Password = "geheimes-passwort";
 private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-store-" + Guid.NewGuid().ToString("N"));
 private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aHucAAAAASUVORK5CYII=");
 public AssetStoreTests() => Directory.CreateDirectory(root);
 private string Database => Path.Combine(root, "catalog.db");
 private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
 private static FileInfo[] Files(string directory) => Directory.Exists(directory)
  ? new DirectoryInfo(directory).GetFiles("*", SearchOption.AllDirectories).Where(f => f.Name != "store.json").ToArray() : [];

 private string Scalar(string sql)
 {
  using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
  connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
  return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
 }

 [Fact]
 public void SameBytesAreOneFileTheRowKeepsOnlyTheHashAndUnusedFilesGo()
 {
  string directory; Guid mediaId;
  using (var catalog = new CatalogRepository(Database))
  {
   directory = catalog.AssetDirectory; Assert.Equal(Path.Combine(root, "catalog-assets"), directory);
   var media = catalog.Upsert(new() { Path = Path.Combine(root, "a.mp4"), Kind = MediaKind.Video }); mediaId = media.Id;
   var first = catalog.UpsertMediaAsset(new() { MediaId = media.Id, Data = Png, Kind = MediaAssetKind.ScenePreview });
   catalog.UpsertMediaAsset(new() { MediaId = media.Id, Data = Png, Kind = MediaAssetKind.Artwork });
   Assert.Equal(Hash(Png), first.Sha256); Assert.Equal(Png.Length, first.ByteLength);
   var file = Assert.Single(Files(directory)); Assert.Equal(Hash(Png), file.Name); Assert.Equal(Png, File.ReadAllBytes(file.FullName));
   Assert.All(catalog.GetMediaAssets(media.Id), asset => Assert.Equal(Png, asset.Data));
   Assert.Equal(Png.Length, catalog.GetMediaAssetSummaries(media.Id)[0].ByteLength);
   catalog.DeleteMediaAsset(first.Id);
   Assert.Single(Files(directory)); // still used by the artwork
  }
  Assert.Equal("0", Scalar("SELECT count(*) FROM media_assets WHERE json_type(document,'$.data') IS NOT NULL"));
  using (var catalog = new CatalogRepository(Database)) catalog.Delete(mediaId); // the graphic goes by cascade
  Assert.Empty(Files(directory));
 }

 [Fact]
 public void LeftoverFilesAreFoundByTheCheckAndRemovedByCompacting()
 {
  using var catalog = new CatalogRepository(Database);
  var media = catalog.Upsert(new() { Path = Path.Combine(root, "a.mp4"), Kind = MediaKind.Video });
  catalog.UpsertMediaAsset(new() { MediaId = media.Id, Data = Png });
  var stray = new byte[] { 1, 2, 3 }; var strayPath = Path.Combine(catalog.AssetDirectory, Hash(stray)[..2], Hash(stray));
  Directory.CreateDirectory(Path.GetDirectoryName(strayPath)!); File.WriteAllBytes(strayPath, stray);
  File.SetLastWriteTimeUtc(strayPath, DateTime.UtcNow.AddHours(-1));
  var report = catalog.CheckCatalog(root);
  Assert.Contains(report[CatalogFindingKind.OrphanFiles].Examples, example => example.EndsWith(Hash(stray), StringComparison.Ordinal));
  Assert.Equal(0, report[CatalogFindingKind.MissingPictures].Count);
  catalog.Compact();
  Assert.False(File.Exists(strayPath)); Assert.Equal(Png, Assert.Single(catalog.GetMediaAssets(media.Id)).Data);
 }

 [Fact]
 public void MissingOrDamagedFilesAreReportedAndReadAsEmpty()
 {
  using var catalog = new CatalogRepository(Database);
  var media = catalog.Upsert(new() { Path = Path.Combine(root, "a.mp4"), Kind = MediaKind.Video });
  var asset = catalog.UpsertMediaAsset(new() { MediaId = media.Id, Data = Png });
  var file = Assert.Single(Files(catalog.AssetDirectory)); var bytes = File.ReadAllBytes(file.FullName); bytes[^1] ^= 1; File.WriteAllBytes(file.FullName, bytes);
  Assert.Empty(catalog.GetMediaAsset(asset.Id)!.Data);
  var finding = catalog.CheckCatalog(root)[CatalogFindingKind.MissingPictures];
  Assert.Equal(1, finding.Count); Assert.True(finding.IsProblem); Assert.Contains(asset.Sha256, Assert.Single(finding.Examples));
  file.Delete();
  Assert.Equal(1, catalog.CheckCatalog(root)[CatalogFindingKind.MissingPictures].Count);
 }

 [Fact]
 public void EncryptedLibraryEncryptsPicturesHidesTheirHashesAndCanSwitchBack()
 {
  var large = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17); // several chunks
  Guid mediaId; string directory;
  using (var catalog = new CatalogRepository(Database))
  {
   var media = catalog.Upsert(new() { Path = Path.Combine(root, "a.mp4"), Kind = MediaKind.Video }); mediaId = media.Id; directory = catalog.AssetDirectory;
   catalog.UpsertMediaAsset(new() { MediaId = media.Id, Data = Png, Position = 0 });
   catalog.UpsertMediaAsset(new() { MediaId = media.Id, Data = large, Position = 1 });
   Assert.False(catalog.EncryptsPictures);
  }
  CatalogEncryption.EnableEncryption(Database, Password);
  var names = new[] { Hash(Png), Hash(large) };
  void AssertSealed()
  {
   var files = Files(directory); Assert.Equal(2, files.Length);
   Assert.All(files, file => { Assert.DoesNotContain(file.Name, names); Assert.Equal("FXPA01"u8.ToArray(), File.ReadAllBytes(file.FullName)[..6]); });
  }
  AssertSealed();
  using (var key = CatalogEncryption.Unlock(Database, Password))
  using (var catalog = new CatalogRepository(Database, key))
  {
   Assert.True(catalog.EncryptsPictures);
   var assets = catalog.GetMediaAssets(mediaId); Assert.Equal(Png, assets[0].Data); Assert.Equal(large, assets[1].Data);
   catalog.SetPictureEncryption(false);
   Assert.False(catalog.EncryptsPictures); Assert.Equal(names.Order(), Files(directory).Select(f => f.Name).Order());
   catalog.SetPictureEncryption(true); AssertSealed();
   Assert.Equal(large, catalog.GetMediaAssets(mediaId)[1].Data);
  }
  // A truncated chunk is detected, not read as shorter bytes.
  var bigFile = Files(directory).OrderByDescending(f => f.Length).First(); var original = File.ReadAllBytes(bigFile.FullName);
  File.WriteAllBytes(bigFile.FullName, original[..^(16 + 100)]);
  using (var key = CatalogEncryption.Unlock(Database, Password))
  using (var catalog = new CatalogRepository(Database, key))
  {
   Assert.Empty(catalog.GetMediaAssets(mediaId)[1].Data);
   Assert.Equal(1, catalog.CheckCatalog(root)[CatalogFindingKind.MissingPictures].Count);
  }
  File.WriteAllBytes(bigFile.FullName, original);
  CatalogEncryption.DisableEncryption(Database, Password);
  Assert.Equal(names.Order(), Files(directory).Select(f => f.Name).Order());
  using (var catalog = new CatalogRepository(Database)) Assert.Equal(large, catalog.GetMediaAssets(mediaId)[1].Data);
 }

 [Fact]
 public void PicturesChosenUnencryptedStayPlainWhenEncryptionIsRemovedAndAddedAgain()
 {
  new CatalogRepository(Database).Dispose(); CatalogEncryption.EnableEncryption(Database, Password);
  Guid mediaId; string directory;
  using (var key = CatalogEncryption.Unlock(Database, Password))
  using (var catalog = new CatalogRepository(Database, key))
  {
   var media = catalog.Upsert(new() { Path = Path.Combine(root, "a.mp4"), Kind = MediaKind.Video }); mediaId = media.Id; directory = catalog.AssetDirectory;
   catalog.UpsertMediaAsset(new() { MediaId = media.Id, Data = Png });
   Assert.NotEqual(Hash(Png), Assert.Single(Files(directory)).Name);
   catalog.SetPictureEncryption(false);
  }
  CatalogEncryption.DisableEncryption(Database, Password);
  CatalogEncryption.EnableEncryption(Database, Password);
  Assert.Equal(Hash(Png), Assert.Single(Files(directory)).Name);
  using (var key = CatalogEncryption.Unlock(Database, Password))
  using (var catalog = new CatalogRepository(Database, key)) { Assert.False(catalog.EncryptsPictures); Assert.Equal(Png, Assert.Single(catalog.GetMediaAssets(mediaId)).Data); }
 }

 [Fact]
 public async Task EncryptedBackupCarriesThePictureFilesAndRestoresThem()
 {
  new CatalogRepository(Database).Dispose(); CatalogEncryption.EnableEncryption(Database, Password);
  var package = Path.Combine(root, "backup.flixypixbackup"); Guid mediaId;
  using (var key = CatalogEncryption.Unlock(Database, Password))
  using (var catalog = new CatalogRepository(Database, key))
  {
   var media = catalog.Upsert(new() { Path = Path.Combine(root, "a.mp4"), Kind = MediaKind.Video }); mediaId = media.Id;
   catalog.UpsertMediaAsset(new() { MediaId = media.Id, Data = Png });
   catalog.UpsertTagAsset(new() { Tag = new("People", "Alex"), Data = Png });
   var result = await catalog.CreateBackupAsync(package, false);
   Assert.Empty(result.Warnings);
  }
  var restored = await CatalogRepository.RestoreBackupAsync(package, Path.Combine(root, "restored"), secret: Password);
  using (var key = CatalogEncryption.Unlock(restored.DatabasePath, Password))
  using (var catalog = new CatalogRepository(restored.DatabasePath, key))
  {
   Assert.True(catalog.EncryptsPictures); Assert.Equal(Png, Assert.Single(catalog.GetMediaAssets(mediaId)).Data);
   Assert.Single(Files(catalog.AssetDirectory)); // one file for both rows
   Assert.Equal(0, catalog.CheckCatalog(Path.GetDirectoryName(restored.DatabasePath)!)[CatalogFindingKind.MissingPictures].Count);
  }
 }

 [Fact]
 public void PortableExportStillCarriesTheBytesInline()
 {
  var export = Path.Combine(root, "export.json"); Guid mediaId;
  using (var catalog = new CatalogRepository(Database))
  {
   var media = catalog.Upsert(new() { Path = Path.Combine(root, "a.mp4"), Kind = MediaKind.Video }); mediaId = media.Id;
   catalog.UpsertMediaAsset(new() { MediaId = media.Id, Data = Png });
   catalog.UpsertTagAsset(new() { Tag = new("People", "Alex"), Data = Png });
   catalog.Export(export);
  }
  Assert.Contains(Convert.ToBase64String(Png)[..40], File.ReadAllText(export)); // the JSON writer escapes "+" further on
  using var target = new CatalogRepository(Path.Combine(root, "target.db"));
  Assert.Empty(target.ImportJson(export).Errors);
  Assert.Equal(Png, Assert.Single(target.GetMediaAssets(mediaId)).Data);
  Assert.Single(Files(target.AssetDirectory));
 }

 [Fact]
 public void Version14CatalogMovesItsBytesIntoFiles()
 {
  Guid mediaId, assetId;
  using (var catalog = new CatalogRepository(Database))
  {
   var media = catalog.Upsert(new() { Path = Path.Combine(root, "a.mp4"), Kind = MediaKind.Video }); mediaId = media.Id;
   assetId = catalog.UpsertMediaAsset(new() { MediaId = media.Id, Data = Png }).Id;
   catalog.UpsertTagAsset(new() { Tag = new("People", "Alex"), Data = Png });
  }
  // Back to the form of schema 14: base64 text in the documents, a blob column for the original's binary values.
  var blob = RandomNumberGenerator.GetBytes(5000);
  using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()))
  {
   connection.Open(); connection.CreateCollation("EFP_ORDINAL", string.CompareOrdinal); using var command = connection.CreateCommand();
   command.CommandText = """
    UPDATE media_assets SET document=json_set(json_remove(document,'$.sha256','$.byteLength'),'$.data',$png);
    UPDATE tag_assets SET document=json_set(document,'$.data',$png);
    DROP TABLE legacy_blobs; CREATE TABLE legacy_blobs(sha256 TEXT NOT NULL UNIQUE,length INTEGER NOT NULL,data BLOB NOT NULL);
    INSERT INTO legacy_blobs VALUES($hash,$length,$blob);
    PRAGMA user_version=14;
    """;
   command.Parameters.AddWithValue("$png", Convert.ToBase64String(Png)); command.Parameters.AddWithValue("$hash", Hash(blob));
   command.Parameters.AddWithValue("$length", blob.Length); command.Parameters.AddWithValue("$blob", blob);
   command.ExecuteNonQuery();
  }
  Directory.Delete(Path.Combine(root, "catalog-assets"), true);
  using (var catalog = new CatalogRepository(Database))
  {
   Assert.Contains("before-v17-v14", catalog.MigrationBackupPath!);
   Assert.Equal(Png, catalog.GetMediaAsset(assetId)!.Data); Assert.Equal(Hash(Png), catalog.GetMediaAsset(assetId)!.Sha256);
   Assert.Equal(Png, catalog.GetTagAsset(catalog.GetTagAssetSummaries().Single().Id)!.Data);
   var exported = Path.Combine(root, "blob.bin"); catalog.ExportLegacyBlob(Hash(blob), exported); Assert.Equal(blob, File.ReadAllBytes(exported));
   Assert.Equal(2, Files(catalog.AssetDirectory).Length);
  }
  Assert.Equal("0", Scalar("SELECT count(*) FROM pragma_table_info('legacy_blobs') WHERE name='data'"));
  Assert.Equal("0", Scalar("SELECT count(*) FROM media_assets WHERE json_type(document,'$.data') IS NOT NULL"));
  Assert.Equal("17", Scalar("PRAGMA user_version"));
 }

 public void Dispose()
 {
  try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
 }
}
