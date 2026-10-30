using FlixyPix.Core;
using Xunit;
namespace FlixyPix.Core.Tests;
public sealed class LibraryNavigationTests
{
 [Fact] public void FolderSyncCanReadVersionPathsDuringCatalogWrites()
 {
  var directory=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  try {using var catalog=new CatalogRepository(Path.Combine(directory,"catalog.db"));catalog.SaveLibraryFolders([new(){Path=directory}]);
   Parallel.Invoke(()=> {for(int i=0;i<40;i++)catalog.Upsert(new(){Path=Path.Combine(directory,i+".mp4"),Kind=MediaKind.Video});},()=>{for(int i=0;i<40;i++)catalog.PlanFolderSync();});
  }finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
 }
 [Fact] public void HealthFiltersAndPreferencesSurviveReopen()
 {
  var directory=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var path=Path.Combine(directory,"catalog.db");Guid id;
  try {
   using(var catalog=new CatalogRepository(path)) {
    var missing=catalog.Upsert(new MediaItem {Path=Path.Combine(directory,"a.mp4"),Kind=MediaKind.Video});id=missing.Id;
    var rated=catalog.Upsert(new MediaItem {Path=Path.Combine(directory,"b.mp4"),Kind=MediaKind.Video,Rating=1});
    catalog.SaveThumbnailHealth(missing.Id,"missing",false);catalog.SaveThumbnailHealth(rated.Id,"valid",true);
    var filter=new MediaFilter {UnratedOnly=true,HasThumbnail=false};
    Assert.Equal(id,Assert.Single(catalog.QueryPage(new ViewState {Filter=filter}.ToQuery(DateTimeOffset.Now)).Items).Id);
    Assert.Equal(rated.Id,Assert.Single(catalog.QueryPage(new ViewState {Filter=new(){HasThumbnail=true}}.ToQuery(DateTimeOffset.Now)).Items).Id);
    catalog.SavePreferences(catalog.GetPreferences() with {General=catalog.GetPreferences().General with {CropThumbnails=true},QuickTags=["old",null,"new"],Sidebar=new(){FolderHeight=340,FolderHorizontalOffset=45,FolderVerticalOffset=90,CollapsedSections=["tags"],IncludeSubfolders=false,ExpandedFolders=[directory]}});
    catalog.ChangeTag("old","new");Assert.Equal(new string?[]{"new",null,null},catalog.GetPreferences().QuickTags);
   }
   using(var catalog=new CatalogRepository(path)) {Assert.False(catalog.GetThumbnailHealth(id)!.Value.Available);Assert.True(catalog.GetPreferences().General.CropThumbnails);Assert.Contains("tags",catalog.GetPreferences().Sidebar.CollapsedSections);Assert.False(catalog.GetPreferences().Sidebar.IncludeSubfolders);Assert.Equal(340,catalog.GetPreferences().Sidebar.FolderHeight);Assert.Equal(45,catalog.GetPreferences().Sidebar.FolderHorizontalOffset);Assert.Equal(90,catalog.GetPreferences().Sidebar.FolderVerticalOffset);}
  } finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
 }
 [Fact] public void FolderScopeUsesExactAndDescendantBoundaries()
 {
  var directory=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  try {using var catalog=new CatalogRepository(Path.Combine(directory,"catalog.db"));
   foreach(var relative in new[]{"root/a.mp4","root/child/b.mp4","root-other/c.mp4"})catalog.Upsert(new(){Path=Path.Combine(directory,relative),Kind=MediaKind.Video});
   var state=new ViewState {Folder=Path.Combine(directory,"root")};Assert.Equal(2,catalog.QueryPage(state.ToQuery(DateTimeOffset.Now)).TotalCount);
   Assert.Single(catalog.QueryPage((state with {IncludeSubfolders=false}).ToQuery(DateTimeOffset.Now)).Items);
  } finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
 }
}
