using System.IO.Compression;
using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class CatalogBackupTests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(),"flixypix-backup-tests-" + Guid.NewGuid().ToString("N"));
 public CatalogBackupTests() => Directory.CreateDirectory(root);

 [Fact]
 public async Task SnapshotIncludesWalMetadataAssetsAndRemapsSameBasenameMediaIntoNewDirectory()
 {
  using var source = new CatalogRepository(Path.Combine(root,"source.db"));
  var media1 = Fixture("a/same.mp4",[1,2,3]); var media2 = Fixture("b/same.mp4",[4,5,6]); var thumbnail = Fixture("a/preview.png",[7,8,9]);
  var first = source.Upsert(new() { Path = media1, Kind = MediaKind.Video, Title = "WAL change", ThumbnailPath = thumbnail, Rating = 9, Tags = ["Tag"] });
  var second = source.Upsert(new() { Path = media2, Kind = MediaKind.Video });
  source.UpsertScene(new() { MediaId = first.Id, StartSeconds = 0.1, EndSeconds = 1, Title = "Scene" });
  source.UpsertBookmark(new() { MediaId = first.Id, PositionSeconds = 0.5, Title = "Bookmark" });
  source.UpsertMediaAsset(new() { MediaId = first.Id, Data = [1,2,3], Kind = MediaAssetKind.Artwork });
  var package = Path.Combine(root,"backup.flixypixbackup"); var result = await source.CreateBackupAsync(package,true);
  // Catalog, two media files, the thumbnail, the graphic's file beside the catalog and the state of that folder.
  Assert.Empty(result.Warnings); Assert.Equal(6,result.FileCount);
  var target = Path.Combine(root,"restored"); var restored = await CatalogRepository.RestoreBackupAsync(package,target);
  Assert.Equal(2,restored.RestoredMediaFiles);
  using var destination = new CatalogRepository(restored.DatabasePath);
  var after = destination.GetMedia(first.Id)!; var after2 = destination.GetMedia(second.Id)!;
  Assert.Equal("WAL change",after.Title); Assert.Equal(9,after.Rating); Assert.Equal(new[] {"Tag"},after.Tags);
  Assert.NotEqual(after.Path,after2.Path); Assert.StartsWith(target,after.Path); Assert.Equal(new byte[] {1,2,3},File.ReadAllBytes(after.Path));
  Assert.Equal(new byte[] {4,5,6},File.ReadAllBytes(after2.Path)); Assert.Equal(new byte[] {7,8,9},File.ReadAllBytes(after.ThumbnailPath!));
  Assert.Single(destination.GetScenes(first.Id)); Assert.Single(destination.GetBookmarks(first.Id)); Assert.Equal(new byte[] {1,2,3},Assert.Single(destination.GetMediaAssets(first.Id)).Data);
  Assert.Equal(media1,source.GetMedia(first.Id)!.Path); Assert.Equal(new byte[] {1,2,3},File.ReadAllBytes(media1));
  await Assert.ThrowsAsync<IOException>(() => CatalogRepository.RestoreBackupAsync(package,target));
  await Assert.ThrowsAsync<IOException>(() => source.CreateBackupAsync(package,true));
 }

 [Fact]
 public async Task CatalogOnlyKeepsExternalPathsAndMissingMediaAreReportedWithoutDroppingCatalogRows()
 {
  using var source = new CatalogRepository(Path.Combine(root,"source.db"));
  var path = Fixture("media.mp4",[1]); var item = source.Upsert(new() { Path = path });
  var missing = source.Upsert(new() { Path = Path.Combine(root,"missing.mp4") });
  var package = Path.Combine(root,"catalog-only.flixypixbackup"); await source.CreateBackupAsync(package,false);
  var restored = await CatalogRepository.RestoreBackupAsync(package,Path.Combine(root,"catalog-only"));
  using var catalog = new CatalogRepository(restored.DatabasePath); Assert.Equal(path,catalog.GetMedia(item.Id)!.Path);
  Assert.Equal(0,restored.RestoredMediaFiles);
  var complete = Path.Combine(root,"with-media.flixypixbackup"); var result = await source.CreateBackupAsync(complete,true);
  Assert.Single(result.Warnings); var restore = await CatalogRepository.RestoreBackupAsync(complete,Path.Combine(root,"with-media"));
  Assert.Single(restore.Warnings); using var restoredCatalog = new CatalogRepository(restore.DatabasePath);
  Assert.Equal(missing.Path,restoredCatalog.GetMedia(missing.Id)!.Path);
 }

 [Theory]
 [InlineData("corrupt")]
 [InlineData("traversal")]
 [InlineData("extra")]
 [InlineData("manifest")]
 public async Task InvalidPackagesPublishNoDestinationAndCleanTemporaryState(string mode)
 {
  using var source = new CatalogRepository(Path.Combine(root,"source.db")); source.Upsert(new() { Path = Fixture("image.png",[1,2,3]) });
  var package = Path.Combine(root,"bad.flixypixbackup"); await source.CreateBackupAsync(package,true);
  using (var zip = ZipFile.Open(package,ZipArchiveMode.Update))
  {
   if (mode == "corrupt") { var entry = zip.Entries.Single(e => e.FullName.StartsWith("media/")); var name = entry.FullName; entry.Delete(); using var output = zip.CreateEntry(name).Open(); output.Write([9,9,9]); }
   if (mode is "traversal" or "extra") { using var output = zip.CreateEntry(mode == "traversal" ? "../outside.txt" : "extra.txt").Open(); output.WriteByte(1); }
   if (mode == "manifest") { zip.GetEntry("manifest.ndjson")!.Delete(); using var output = new StreamWriter(zip.CreateEntry("manifest.ndjson").Open()); output.Write("{}"); }
  }
  var target = Path.Combine(root,"not-published"); await Assert.ThrowsAsync<InvalidDataException>(() => CatalogRepository.RestoreBackupAsync(package,target));
  Assert.False(Directory.Exists(target)); Assert.False(File.Exists(Path.Combine(root,"outside.txt"))); Assert.Empty(Directory.GetDirectories(root,".flixypix-*"));
 }

 [Fact]
 public async Task CancellationDuringBackupAndRestoreLeavesNoPublishedOrTemporaryResults()
 {
  using var source = new CatalogRepository(Path.Combine(root,"source.db")); source.Upsert(new() { Path = Fixture("a.jpg",[1,2,3]) });
  var target = Path.Combine(root,"cancelled.flixypixbackup"); using var cancel = new CancellationTokenSource();
  await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.CreateBackupAsync(target,true,cancel.Token,new ImmediateProgress<CatalogBackupProgress>(_ => cancel.Cancel())));
  Assert.False(File.Exists(target)); Assert.Empty(Directory.GetDirectories(root,".flixypix-*"));
  var complete = Path.Combine(root,"complete.flixypixbackup"); await source.CreateBackupAsync(complete,true); using var restoreCancel = new CancellationTokenSource();
  var directory = Path.Combine(root,"cancelled-restore");
  await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CatalogRepository.RestoreBackupAsync(complete,directory,restoreCancel.Token,new ImmediateProgress<CatalogBackupProgress>(_ => restoreCancel.Cancel())));
  Assert.False(Directory.Exists(directory)); Assert.Empty(Directory.GetDirectories(root,".flixypix-*"));
 }

 private string Fixture(string relative,byte[] bytes) { var path = Path.Combine(root,relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path,bytes); return path; }
 private sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
 public void Dispose() => Directory.Delete(root,true);
}
