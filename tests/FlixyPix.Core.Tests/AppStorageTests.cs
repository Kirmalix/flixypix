using FlixyPix.Core;
using Xunit;
namespace FlixyPix.Core.Tests;
public sealed class AppStorageTests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(), "storage-tests-" + Guid.NewGuid().ToString("N"));
 public AppStorageTests() => Directory.CreateDirectory(root);
 public void Dispose() => Directory.Delete(root,true);
 [Fact] public void MarkerDefaultsToPortableButStoredChoiceAndExplicitModeWin()
 {
  var profile = Path.Combine(root,"profile");
  Assert.Equal(StorageMode.Profile,AppStoragePaths.Resolve(root,profile).Mode);
  File.WriteAllText(Path.Combine(root,"portable.flag"),"");
  var paths = AppStoragePaths.Resolve(root,profile);
  Assert.Equal(Path.Combine(root,"Data"), paths.Root);
  paths.RememberMode(StorageMode.Profile);
  Assert.Equal(profile,AppStoragePaths.Resolve(root,profile).Root);
  Assert.Equal(StorageMode.Portable,AppStoragePaths.Resolve(root,profile,StorageMode.Portable).Mode);
 }
 [Fact] public void InvalidSelectionFailsInsteadOfSilentlyOpeningAnotherDataSet()
 {
  File.WriteAllText(Path.Combine(root,"storage-mode.json"),"{\"mode\":\"unknown\"}");
  Assert.Throws<InvalidDataException>(()=>AppStoragePaths.Resolve(root,Path.Combine(root,"profile")));
  Assert.Equal(StorageMode.Profile,AppStoragePaths.Resolve(root,root,StorageMode.Profile).Mode);
 }

 [Fact] public void PortableDriveChangeRemapsVersionsAndFoldersButKeepsOtherDrives()
 {
  using var catalog = new CatalogRepository(Path.Combine(root,"catalog.db"));
  var id = Guid.NewGuid();
  catalog.Upsert(new MediaItem { Id=id, Path=@"E:\Media\film.mp4", ThumbnailPath=@"E:\App\Data\thumb.jpg", SubtitlePath=@"E:\Media\film.srt",
   Files=[new MediaFile {Path=@"E:\Media\film-hd.mp4"}], Extra=new() {{"migration.originalPath",@"E:\Original\film.mp4"}} });
  var external=Guid.NewGuid(); catalog.Upsert(new MediaItem {Id=external,Path=@"Z:\External\other.mp4"});
  catalog.SaveLibraryFolders([new LibraryFolder {Path=@"E:\Media",IgnoredPaths=[@"E:\Media\ignored.mp4"]}]);
  catalog.PreparePortableLocation(@"E:\App",true);
  catalog.PreparePortableLocation(@"F:\App",true);
  var item = catalog.GetMedia(id)!;
  Assert.Equal(@"F:\Media\film.mp4",item.Path);
  Assert.Equal(@"F:\Media\film-hd.mp4",item.Files[0].Path);
  Assert.Equal(@"F:\Media\film.srt",item.SubtitlePath);
  Assert.Equal(@"F:\App\Data\thumb.jpg",item.ThumbnailPath);
  Assert.Equal(@"E:\Original\film.mp4",item.Extra["migration.originalPath"]);
  Assert.Equal(@"Z:\External\other.mp4",catalog.GetMedia(external)!.Path);
  Assert.Equal(@"F:\Media",catalog.GetLibraryFolders()[0].Path);
  Assert.Equal(@"F:\Media\ignored.mp4",catalog.GetLibraryFolders()[0].IgnoredPaths[0]);
  catalog.PreparePortableLocation(@"F:\App",true);
  Assert.Equal(@"F:\Media\film.mp4",catalog.GetMedia(id)!.Path);
 }
}
