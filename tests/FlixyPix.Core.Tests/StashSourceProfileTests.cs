using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class StashSourceProfileTests:IDisposable
{
 private readonly string root=StashTestWorkspace.Create("flixypix-stash-sources-");
 private const string Secret="synthetic-stash-source-password";
 private string Database=>Path.Combine(root,"catalog.db");
 public StashSourceProfileTests()=>Directory.CreateDirectory(root);
 public void Dispose(){SqliteConnection.ClearAllPools();if(Environment.GetEnvironmentVariable("FLIXYPIX_TEST_CAPTURE")!="1")Directory.Delete(root,true);}
 private CatalogKey? Prepare(bool encrypted)
 {using(var empty=new CatalogRepository(Database)){}if(!encrypted)return null;CatalogEncryption.EnableEncryption(Database,Secret);return CatalogEncryption.Unlock(Database,Secret);}
 private StashSourceProfile Profile()=>new(){Name="Stash source",SourceDirectory=Path.Combine(root,"export"),PathMappings=[new("/media",Path.Combine(root,"library"))]};
 [Theory][InlineData(false)]
 public void SourceProfilesHaveStableIdentityAndRejectStaleOrImplicitUpdates(bool encrypted)
 {
  using var key=Prepare(encrypted);Guid id;using(var db=new CatalogRepository(Database,key))
  {
   var saved=db.SaveStashSource(Profile());id=saved.Id;Assert.Equal(1,saved.Revision);using var second=new CatalogRepository(Database,key);
   var changed=second.SaveStashSource(saved with {Name="Changed"},saved.Revision);Assert.Equal(2,changed.Revision);
   Assert.Throws<InvalidOperationException>(()=>db.SaveStashSource(saved));Assert.Throws<InvalidOperationException>(()=>db.SaveStashSource(saved with {Name="Stale"},saved.Revision));
   Assert.Equal("Changed",db.GetStashSource(id)!.Name);Assert.Equal(2,db.GetStashSource(id)!.Revision);
  }
  using var reopened=new CatalogRepository(Database,key);Assert.Equal(id,Assert.Single(reopened.GetStashSources()).Id);Assert.Equal("Changed",reopened.GetStashSource(id)!.Name);
 }
 [Theory][InlineData(false)]
 public void ConflictingPortableProfileDoesNotOverwriteLocalConfigurationOrEarlierMedia(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var source=db.SaveStashSource(Profile());var item=db.Upsert(new(){Path=Path.Combine(root,"image.jpg"),Title="Keep"});var path=Path.Combine(root,"conflict.json");
  File.WriteAllText(path,JsonSerializer.Serialize(new {format="flixypix-catalog",version=5,items=new[]{item with {Title="Must roll back"}},stashSources=new[]{source with {Name="Different source"}}},CatalogRepository.Json));
  Assert.NotEmpty(db.ImportJson(path).Errors);Assert.Equal("Keep",db.GetMedia(item.Id)!.Title);Assert.Equal(source.Name,db.GetStashSource(source.Id)!.Name);
 }
 [Fact]
 public void RelocationChangesOnlyCurrentLocalPathsAndInvalidatesProfileRevision()
 {
  using var db=new CatalogRepository(Database);var source=db.SaveStashSource(Profile());var target=Path.Combine(root,"moved");db.RelocateStoragePaths([new(root,target)]);var after=db.GetStashSource(source.Id)!;
  Assert.Equal(Path.Combine(target,"export"),after.SourceDirectory);Assert.Equal("/media",after.PathMappings[0].SourcePrefix);Assert.Equal(Path.Combine(target,"library"),after.PathMappings[0].TargetPrefix);Assert.Equal(source.Revision+1,after.Revision);
  Assert.Throws<InvalidOperationException>(()=>db.SaveStashSource(source,source.Revision));
 }
 [Fact]
 public void PrefixMappingUsesBoundariesLongestMatchAndSourceSpecificCaseRulesWithoutIo()
 {
  var mappings=new[]{new StashPathPrefix("/media",Path.Combine(root,"all")),new StashPathPrefix("/media/special",Path.Combine(root,"special")),new StashPathPrefix("D:/Media",Path.Combine(root,"windows")),new StashPathPrefix("//server/share",Path.Combine(root,"unc"))};
  Assert.Equal(Path.Combine(root,"special","clip.mp4"),StashPaths.Map("/media/special/clip.mp4",mappings).TargetPath);Assert.Equal(1,StashPaths.Map("/media/special/clip.mp4",mappings).MappingIndex);
  Assert.Null(StashPaths.Map("/media-other/a",mappings).TargetPath);Assert.Null(StashPaths.Map("/Media/a",mappings).TargetPath);
  Assert.Equal(Path.Combine(root,"windows","folder","a.mp4"),StashPaths.Map(@"d:\MEDIA\folder\a.mp4",mappings).TargetPath);
  Assert.Equal(Path.Combine(root,"unc","a.mp4"),StashPaths.Map(@"\\SERVER\SHARE\a.mp4",mappings).TargetPath);
  Assert.Equal(Path.Combine(root,"all"),StashPaths.Map("/media",mappings).TargetPath);Assert.False(Directory.Exists(Path.Combine(root,"special")));
 }
 [Theory][InlineData("relative/file")][InlineData("D:relative")][InlineData("/media/../escape")][InlineData("/media/./file")][InlineData("/media//file")][InlineData("//server")][InlineData("//?/C:/secret")][InlineData("/media/CON.txt")][InlineData("/media/file:stream")][InlineData("/media/trailing.")]
 public void UnsafeOrAmbiguousPathsNeverProduceMappedDestination(string path)
 {Assert.Throws<ArgumentException>(()=>StashPaths.Map(path,[new("/media",Path.Combine(root,"target"))]));}
 [Fact]
 public void DuplicateWindowsPrefixesAndInvalidProfileFieldsAreRejected()
 {
  Assert.Throws<ArgumentException>(()=>StashPaths.Map("D:/a",[new("D:/",root),new("d:/",root)]));
  using var db=new CatalogRepository(Database);var profile=Profile();
  foreach(var invalid in new[]{profile with {Id=Guid.Empty},profile with {ExportVersion="latest"},profile with {SourceDirectory="relative"},profile with {Name=""},profile with {PathMappings=[new("/media","relative")]}})Assert.Throws<ArgumentException>(()=>db.SaveStashSource(invalid));
  Assert.Empty(db.GetStashSources());
 }
 [Fact]
 public void RootMappingsAndTrailingSeparatorsKeepRelativeTailWithinTarget()
 {
  Assert.Equal(Path.Combine(root,"unix","folder","file.mp4"),StashPaths.Map("/folder/file.mp4",[new("/",Path.Combine(root,"unix"))]).TargetPath);
  Assert.Equal(Path.Combine(root,"drive","folder","file.mp4"),StashPaths.Map("c:/folder/file.mp4",[new(@"C:\",Path.Combine(root,"drive"))]).TargetPath);
  Assert.Equal(Path.Combine(root,"trailing","file.mp4"),StashPaths.Map("/media/file.mp4",[new("/media/",Path.Combine(root,"trailing"))]).TargetPath);
  Assert.Throws<ArgumentException>(()=>StashPaths.Map(@"\folder\file.mp4",[new("/",root)]));
  Assert.Throws<ArgumentException>(()=>StashPaths.Map("/file",[new("/",@"\\.\pipe\") ]));
 }
 [Fact]
 public void DuplicateImportedProfilesRejectAndNoImplicitSourceIsCreatedForOlderFormats()
 {
  using var db=new CatalogRepository(Database);var profile=Profile();var input=Path.Combine(root,"duplicate.json");
  File.WriteAllText(input,JsonSerializer.Serialize(new {format="flixypix-catalog",version=5,stashSources=new[]{profile,profile}},CatalogRepository.Json));Assert.NotEmpty(db.ImportJson(input).Errors);Assert.Empty(db.GetStashSources());
  File.WriteAllText(input,"{\"format\":\"flixypix-catalog\",\"version\":4,\"items\":[]}");Assert.Empty(db.ImportJson(input).Errors);Assert.Empty(db.GetStashSources());
 }
}
