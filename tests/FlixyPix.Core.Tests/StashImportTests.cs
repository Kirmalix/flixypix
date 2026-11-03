using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class StashImportTests:IDisposable
{
 private readonly string root=StashTestWorkspace.Create("flixypix-stash-import-");
 private string Source=>Path.Combine(root,"source");
 private string Target=>Path.Combine(root,"media");
 private string Database=>Path.Combine(root,"catalog.db");
 private const string Secret="synthetic-stash-import-password";
 private const string Performers="Performers",Studios="Studios";
 public StashImportTests(){Directory.CreateDirectory(Source);Directory.CreateDirectory(Target);}
 public void Dispose(){SqliteConnection.ClearAllPools();if(Environment.GetEnvironmentVariable("FLIXYPIX_TEST_CAPTURE")!="1")Directory.Delete(root,true);}
 private void Add(string relative,string json)
 {var path=Path.Combine(Source,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,json);}
 private CatalogRepository Open(bool encrypted)
 {
  using(var db=new CatalogRepository(Database)){}
  if(!encrypted)return new(Database);
  CatalogEncryption.EnableEncryption(Database,Secret);return new(Database,CatalogEncryption.Unlock(Database,Secret));
 }
 private StashSourceProfile Profile(CatalogRepository db)=>db.SaveStashSource(new(){Name="Stash",SourceDirectory=Source,PathMappings=[new("/library",Target)]});
 private Task<StashExportSnapshot> Read()=>StashExport.ReadDirectoryAsync(Source,Path.Combine(root,"stage"));
 private static IReadOnlyList<StashImportPreviewRow> Preview(CatalogRepository db,StashExportSnapshot snapshot,StashSourceProfile source)=>db.PreviewStashImport(snapshot,source.Id,Performers,Studios,0,256);
 private static StashImportRequest Request(StashSourceProfile source,IEnumerable<StashImportItem> items)=>new(source.Id,source.Revision,Performers,Studios,items.ToArray());
 /// <summary>Selects the given rows, confirming the identity the preview found for each.</summary>
 private static IEnumerable<StashImportItem> Confirmed(IEnumerable<StashImportPreviewRow> rows)=>rows.Select(row=>row.Identity.Candidates is [var only]
  ?new StashImportItem(row.Draft.SourceOrdinal){IdentityId=only.Id,ExpectedIdentityRevision=only.Revision}:new StashImportItem(row.Draft.SourceOrdinal));
 private void AddLibrary()
 {
  File.WriteAllText(Path.Combine(Target,"a.mp4"),"synthetic video");
  Add("performers/jane.json","""{"name":"Jane","aliases":"J. Doe","details":"Bio","favorite":true}""");
  Add("studios/acme.json","""{"name":"Acme"}""");
  Add("tags/outdoor.json","""{"name":"Outdoor"}""");
  Add("tags/unused.json","""{"name":"Unused"}""");
  Add("scenes/a.json","""{"title":"Scene A","files":["/library/a.mp4"],"rating":80,"date":"2021-05-01","tags":["Outdoor"],"performers":["Jane"],"studio":"Acme","markers":[{"title":"Mark","seconds":"1.5","primary_tag":"Outdoor"},{"title":"Range","seconds":"3","end_seconds":"6"}]}""");
 }
 private static void RequireNothingImported(CatalogRepository db,StashSourceProfile source)
 {Assert.Empty(db.Search());Assert.Empty(db.GetStashIdentities(source.Id));Assert.Empty(db.GetStashBindings(source.Id));Assert.Empty(db.GetScenes());Assert.Empty(db.GetBookmarks());}

 [Theory][InlineData(false)][InlineData(true)]
 public async Task FirstImportCreatesMediaDirectoryAndMarksAndAnUnchangedRepeatIsANoOp(bool encrypted)
 {
  AddLibrary();using var db=Open(encrypted);var source=Profile(db);using var snapshot=await Read();
  var rows=Preview(db,snapshot,source);Assert.All(rows,row=>Assert.Equal(StashIdentityState.New,row.Identity.State));Assert.Empty(db.GetStashIdentities(source.Id));
  var result=await db.ApplyStashImportAsync(snapshot,Request(source,rows.Select(row=>new StashImportItem(row.Draft.SourceOrdinal))));
  Assert.Equal((5,0,0),(result.Created,result.Updated,result.Unchanged));
  var media=Assert.Single(db.Search());
  Assert.Equal(("Scene A",8,new DateOnly(2021,5,1),ReviewState.Unreviewed,MediaKind.Video),(media.Title,media.Rating,media.ReleaseDate,media.ReviewState,media.Kind));
  Assert.Equal(new[]{"Jane"},media.TagCategories[Performers]);Assert.Equal(new[]{"Acme"},media.TagCategories[Studios]);Assert.Contains("Outdoor",media.Tags);
  var scene=Assert.Single(db.GetScenes(media.Id));Assert.Equal((3d,6d,"Range"),(scene.StartSeconds,scene.EndSeconds,scene.Title));
  var bookmark=Assert.Single(db.GetBookmarks(media.Id));Assert.Equal((1.5,"Mark"),(bookmark.PositionSeconds,bookmark.Title));Assert.Equal(new[]{"Outdoor"},bookmark.Tags);
  var preferences=db.GetPreferences();var jane=preferences.TagVocabulary.Single(entry=>entry.Tag=="Jane");
  Assert.Equal((Performers,"Bio",true),(jane.Category,jane.Description,jane.IsFavorite));Assert.Equal(new[]{"J. Doe"},jane.Aliases);
  Assert.Equal(TagRole.Performer,TagRoles.Of(preferences,Performers));Assert.Equal(TagRole.Studio,TagRoles.Of(preferences,Studios));
  var bindings=db.GetStashBindings(source.Id);Assert.Equal(5,bindings.Count);Assert.All(bindings,binding=>Assert.False(binding.Detached));
  // Plain tags have no directory entry; both tag records keep only their source provenance.
  Assert.Equal(2,bindings.Count(binding=>binding.Target==new StashTargetReference(StashTargetKind.ProvenanceOnly)));
  Assert.Contains(bindings,binding=>binding.Target==new StashTargetReference(StashTargetKind.DirectoryEntry,Tag:new(Performers,"Jane")));
  var sceneBinding=bindings.Single(binding=>binding.Target==new StashTargetReference(StashTargetKind.Video,media.Id));
  Assert.Equal(File.ReadAllBytes(Path.Combine(Source,"scenes","a.json")),db.ReadStashProvenanceBytes(sceneBinding.ProvenanceId));
  Assert.All(sceneBinding.Fields,field=>Assert.Equal(StashFieldDecision.UseSource,field.Decision));

  var repeat=Preview(db,snapshot,source);Assert.All(repeat,row=>Assert.Equal(StashIdentityState.Matched,row.Identity.State));
  Assert.All(repeat.Single(row=>row.Draft.Kind==StashEntityKind.Scene).Fields,field=>Assert.Equal(StashFieldStatus.Equal,field.Status));
  var again=await db.ApplyStashImportAsync(snapshot,Request(source,Confirmed(repeat)));
  Assert.Equal((0,0,5),(again.Created,again.Updated,again.Unchanged));
  Assert.Equal(bindings.Select(b=>(b.Id,b.Revision)).Order(),db.GetStashBindings(source.Id).Select(b=>(b.Id,b.Revision)).Order());
  Assert.Single(db.GetScenes(media.Id));Assert.Single(db.GetBookmarks(media.Id));Assert.Single(db.Search());
 }

 [Fact]
 public async Task ChangedSourceKeepsLocalValuesUnlessAFieldIsChosen()
 {
  AddLibrary();using var db=Open(false);var source=Profile(db);
  using(var first=await Read())await db.ApplyStashImportAsync(first,Request(source,Preview(db,first,source).Select(row=>new StashImportItem(row.Draft.SourceOrdinal))));
  var media=Assert.Single(db.Search());db.Upsert(media with {Title="Local title"});
  Add("scenes/a.json","""{"title":"Scene B","files":["/library/a.mp4"],"rating":60,"tags":["Outdoor"],"performers":["Jane"],"studio":"Acme","markers":[{"title":"Mark","seconds":"1.5","primary_tag":"Outdoor"},{"title":"Range","seconds":"3","end_seconds":"6"},{"title":"New","seconds":"9"}]}""");
  using var snapshot=await Read();var rows=Preview(db,snapshot,source);var scene=rows.Single(row=>row.Draft.Kind==StashEntityKind.Scene);
  Assert.Equal(StashFieldStatus.Conflict,scene.Fields.Single(field=>field.Field=="title").Status);
  Assert.Equal(StashFieldStatus.SourceChanged,scene.Fields.Single(field=>field.Field=="rating").Status);
  Assert.DoesNotContain(scene.Fields,field=>field.Field=="releaseDate");
  var items=Confirmed(rows).Select(item=>item.SourceOrdinal==scene.Draft.SourceOrdinal?item with {Fields=[new("rating",StashFieldDecision.UseSource),new("markers",StashFieldDecision.UseSource),new("title",StashFieldDecision.KeepLocal)]}:item);
  var result=await db.ApplyStashImportAsync(snapshot,Request(source,items));Assert.Equal(1,result.Updated);
  media=Assert.Single(db.Search());Assert.Equal(("Local title",6,new DateOnly(2021,5,1)),(media.Title,media.Rating,media.ReleaseDate));
  Assert.Equal(new[]{1.5,9},db.GetBookmarks(media.Id).Select(b=>b.PositionSeconds).Order());Assert.Single(db.GetScenes(media.Id));
  var after=Preview(db,snapshot,source).Single(row=>row.Draft.Kind==StashEntityKind.Scene).Fields;
  Assert.Equal(StashFieldStatus.PreviouslyKept,after.Single(field=>field.Field=="title").Status);Assert.Equal(StashFieldStatus.Equal,after.Single(field=>field.Field=="rating").Status);
 }

 [Fact]
 public async Task ExistingLocalMediumIsOnlyBoundWhenChosenAndKeepsItsValues()
 {
  File.WriteAllText(Path.Combine(Target,"a.mp4"),"synthetic video");Add("scenes/a.json","""{"title":"Scene A","files":["/library/a.mp4"],"rating":80}""");
  using var db=Open(false);var source=Profile(db);var local=db.Upsert(new MediaItem{Path=Path.Combine(Target,"a.mp4"),Kind=MediaKind.Video,Title="Mine",Rating=3});
  using var snapshot=await Read();var row=Assert.Single(Preview(db,snapshot,source));var match=Assert.Single(row.LocalMatches);Assert.Equal(local.Id,match.Target.Id);
  Assert.Equal(StashFieldStatus.New,row.Fields.Single(field=>field.Field=="title").Status);
  await Assert.ThrowsAsync<InvalidOperationException>(()=>db.ApplyStashImportAsync(snapshot,Request(source,[new(row.Draft.SourceOrdinal)])));
  Assert.Empty(db.GetStashIdentities(source.Id));
  var result=await db.ApplyStashImportAsync(snapshot,Request(source,[new(row.Draft.SourceOrdinal){Target=match.Target}]));Assert.Equal(1,result.Updated);
  var kept=Assert.Single(db.Search());Assert.Equal((local.Id,"Mine",3),(kept.Id,kept.Title,kept.Rating));
  Assert.Equal(match.Target,Assert.Single(db.GetStashBindings(source.Id)).Target);
 }

 [Fact]
 public async Task UnselectedPerformerSourceChangeAndLateMarkerFailureLeaveNothingBehind()
 {
  AddLibrary();Add("scenes/b.json","""{"files":["/library/b.mp4"],"markers":[{"title":"Backwards","seconds":"5","end_seconds":"2"}]}""");
  using var db=Open(false);var source=Profile(db);using var snapshot=await Read();var rows=Preview(db,snapshot,source);
  var sceneA=rows.Single(row=>row.Draft.RelativePath=="scenes/a.json");var sceneB=rows.Single(row=>row.Draft.RelativePath=="scenes/b.json");
  var missing=await Assert.ThrowsAsync<ArgumentException>(()=>db.ApplyStashImportAsync(snapshot,Request(source,[new(sceneA.Draft.SourceOrdinal)])));
  Assert.Contains("Jane",missing.Message,StringComparison.Ordinal);RequireNothingImported(db,source);
  // The valid scene and its directory entries are written first; the second scene's marker fails afterwards.
  await Assert.ThrowsAsync<ArgumentException>(()=>db.ApplyStashImportAsync(snapshot,Request(source,rows.Select(row=>new StashImportItem(row.Draft.SourceOrdinal)))));
  RequireNothingImported(db,source);Assert.DoesNotContain(db.GetPreferences().TagVocabulary,entry=>entry.Tag=="Jane");
  File.AppendAllText(Path.Combine(Source,"scenes","b.json")," ");
  await Assert.ThrowsAsync<IOException>(()=>db.ApplyStashImportAsync(snapshot,Request(source,rows.Where(row=>row!=sceneB).Select(row=>new StashImportItem(row.Draft.SourceOrdinal)))));
  RequireNothingImported(db,source);
 }

 [Fact]
 public async Task SameNamedPerformersNeedSeparateLocalNames()
 {
  Add("performers/a.json","""{"name":"Same","disambiguation":"First"}""");Add("performers/b.json","""{"name":"Same","disambiguation":"Second"}""");
  using var db=Open(false);var source=Profile(db);using var snapshot=await Read();var rows=Preview(db,snapshot,source);
  var clash=await Assert.ThrowsAsync<InvalidOperationException>(()=>db.ApplyStashImportAsync(snapshot,Request(source,rows.Select(row=>new StashImportItem(row.Draft.SourceOrdinal)))));
  Assert.Contains("Same",clash.Message,StringComparison.Ordinal);Assert.Empty(db.GetStashIdentities(source.Id));Assert.Empty(db.GetPreferences().TagVocabulary);
  var second=rows.Single(row=>row.Draft.RelativePath=="performers/b.json").Draft.SourceOrdinal;
  await db.ApplyStashImportAsync(snapshot,Request(source,rows.Select(row=>new StashImportItem(row.Draft.SourceOrdinal){TargetName=row.Draft.SourceOrdinal==second?"Same (Second)":null})));
  Assert.Equal(new[]{"Same","Same (Second)"},db.GetPreferences().TagVocabulary.Where(entry=>entry.Category==Performers).Select(entry=>entry.Tag).Order());
  // A repeat of an unconfirmed identity is refused rather than registering a duplicate source identity.
  await Assert.ThrowsAsync<InvalidOperationException>(()=>db.ApplyStashImportAsync(snapshot,Request(source,[new(second)])));
 }
}
