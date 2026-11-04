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
 [Fact] public void ThumbnailFilterAnswersWithoutACheckAndForgetsResultsOfAnOldThumbnail()
 {
  var directory=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  try {using var catalog=new CatalogRepository(Path.Combine(directory,"catalog.db"));
   MediaItem Add(string name,MediaKind kind,string? thumbnail=null)=>catalog.Upsert(new(){Path=Path.Combine(directory,name),Kind=kind,ThumbnailPath=thumbnail});
   var bare=Add("bare.mp4",MediaKind.Video);var empty=Add("empty.mp4",MediaKind.Video,"");var done=Add("done.mp4",MediaKind.Video,Path.Combine(directory,"done.jpg"));
   var image=Add("image.jpg",MediaKind.Image);var song=Add("song.mp3",MediaKind.Audio);var cover=Add("cover.mp3",MediaKind.Audio,Path.Combine(directory,"cover.jpg"));
   HashSet<Guid> With(bool has)=>catalog.QueryPage(new ViewState {Filter=new(){HasThumbnail=has}}.ToQuery(DateTimeOffset.Now)).Items.Select(i=>i.Id).ToHashSet();
   Assert.Equal(new[]{bare.Id,empty.Id}.ToHashSet(),With(false));
   Assert.Equal(new[]{done.Id,image.Id,cover.Id}.ToHashSet(),With(true));
   // Audio checked as "no thumbnail" by an earlier version stays out of the list.
   catalog.SaveThumbnailHealth([(done.Id,"old",false),(song.Id,"",false)]);
   Assert.Equal(new[]{bare.Id,empty.Id,done.Id}.ToHashSet(),With(false));
   catalog.Upsert(done with {Title="renamed"});Assert.Contains(done.Id,With(false));
   catalog.Upsert(catalog.GetMedia(done.Id)! with {ThumbnailPath=Path.Combine(directory,"new.jpg")});
   Assert.Null(catalog.GetThumbnailHealth(done.Id));Assert.Contains(done.Id,With(true));
  } finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
 }
 [Fact] public void KeptMediaStayInPagesCountsAndPositionsWithoutMatching()
 {
  var directory=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  try {using var catalog=new CatalogRepository(Path.Combine(directory,"catalog.db"));
   var open=catalog.Upsert(new MediaItem {Title="a",Path=Path.Combine(directory,"a.mp4"),Kind=MediaKind.Video});
   var rated=catalog.Upsert(new MediaItem {Title="b",Path=Path.Combine(directory,"b.mp4"),Kind=MediaKind.Video,Rating=4});
   var other=catalog.Upsert(new MediaItem {Title="c",Path=Path.Combine(directory,"c.mp4"),Kind=MediaKind.Video,Rating=4});
   var query=new ViewState {Filter=new(){UnratedOnly=true}}.ToQuery(DateTimeOffset.Now);
   Assert.Equal(1,catalog.QueryPage(query).TotalCount);
   var kept=query with {KeepIds=[rated.Id]};
   var page=catalog.QueryPage(kept);Assert.Equal(2,page.TotalCount);Assert.Equal(new[]{open.Id,rated.Id},page.Items.Select(i=>i.Id));
   Assert.Equal(1,catalog.GetPosition(kept,rated.Id));
   var missing=Guid.NewGuid();
   Assert.Equal(new[]{open.Id},catalog.MatchingMedia(kept,[open.Id,rated.Id,other.Id,missing]));
   Assert.Equal(new[]{open.Id,rated.Id}.ToHashSet(),catalog.MatchingMedia(new MediaQuery(),[open.Id,rated.Id,missing]).ToHashSet());
  } finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
 }
 [Theory]
 [InlineData(null,new string[0])]
 [InlineData(@"D:\Medien\Archiv",new[]{@"D:\Medien\Archiv\Filme",@"D:\Medien\Archiv\Serien\Staffel 1"})]
 [InlineData(@"D:\",new[]{@"D:\Filme",@"D:\Serien"})]
 [InlineData(@"D:\Medien",new[]{@"D:\Medien\Archiv"})]
 [InlineData(@"D:\Medien",new[]{@"D:\Medien\Archiv",@"D:\Medien\Archiv\Filme"})]
 [InlineData(null,new[]{@"D:\",@"D:\Filme"})]
 [InlineData(null,new[]{@"C:\Filme",@"D:\Filme"})]
 [InlineData(@"D:\Medien",new[]{@"D:\Medien\Film",@"d:\medien\Filme"})]
 public void FolderTreeLeavesOutOnlyTheSharedLevelsWithoutMedia(string? expected,string[] folders)=>Assert.Equal(expected,FolderPaths.HiddenBase(folders));
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
