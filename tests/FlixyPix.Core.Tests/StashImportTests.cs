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

 [Theory][InlineData(false)]
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

 private const string UsageScene="""{"title":"Scene A","files":["/library/a.mp4"],"play_history":["2024-03-02T20:15:00Z","2024-01-05T10:00:00+01:00","not a time"],"o_history":["2024-03-02T20:40:00Z"]}""";
 private void AddUsageLibrary()
 {
  File.WriteAllText(Path.Combine(Target,"a.mp4"),"synthetic video");File.WriteAllText(Path.Combine(Target,"b.jpg"),"synthetic image");
  Add("scenes/a.json",UsageScene);Add("images/b.json","""{"title":"Image B","files":["/library/b.jpg"],"o_counter":3}""");
 }

 [Fact]
 public async Task FirstImportTakesCountersAndHistoryAndARepeatAddsNothing()
 {
  AddUsageLibrary();using var db=Open(false);var source=Profile(db);using var snapshot=await Read();var rows=Preview(db,snapshot,source);
  Assert.Contains(rows.Single(row=>row.Draft.Kind==StashEntityKind.Scene).Draft.Issues,issue=>issue.Code=="usage-time"&&issue.Severity==StashIssueSeverity.Warning);
  await db.ApplyStashImportAsync(snapshot,Request(source,rows.Select(row=>new StashImportItem(row.Draft.SourceOrdinal))));
  var video=db.Search().Single(item=>item.Kind==MediaKind.Video);var image=db.Search().Single(item=>item.Kind==MediaKind.Image);
  Assert.Equal((2L,1L,new DateTimeOffset(2024,3,2,20,15,0,TimeSpan.Zero)),(video.PlayCount,video.OCount,video.LastPlayedAt));
  Assert.Equal((null,3L),(image.PlayCount,image.OCount));
  var history=db.GetHistory(video.Id);
  Assert.Equal(new[]{new DateTimeOffset(2024,1,5,9,0,0,TimeSpan.Zero),new DateTimeOffset(2024,3,2,20,15,0,TimeSpan.Zero)},history.Where(entry=>entry.Kind==HistoryKind.Play).Select(entry=>entry.At).Order());
  Assert.Equal(new DateTimeOffset(2024,3,2,20,40,0,TimeSpan.Zero),history.Single(entry=>entry.Kind==HistoryKind.O).At);
  Assert.Empty(db.GetHistory(image.Id));

  var repeat=Preview(db,snapshot,source);Assert.All(repeat.SelectMany(row=>row.Fields),field=>Assert.Equal(StashFieldStatus.Equal,field.Status));
  var again=await db.ApplyStashImportAsync(snapshot,Request(source,Confirmed(repeat)));Assert.Equal(2,again.Unchanged);Assert.Equal(3,db.CountHistory());
 }

 [Fact]
 public async Task LaterLocalPlaybackStaysUnlessTheCounterIsTakenAndHistoryIsOnlyAdded()
 {
  AddUsageLibrary();using var db=Open(false);var source=Profile(db);
  using(var first=await Read())await db.ApplyStashImportAsync(first,Request(source,Preview(db,first,source).Select(row=>new StashImportItem(row.Draft.SourceOrdinal))));
  var video=db.Search().Single(item=>item.Kind==MediaKind.Video);var local=new DateTimeOffset(2025,6,1,12,0,0,TimeSpan.Zero);
  db.BeginPlayback(video.Id,local);db.Upsert(db.GetMedia(video.Id)! with {PlayCount=4,LastPlayedAt=local});
  Add("scenes/a.json",UsageScene.Replace("\"not a time\"","\"2024-05-05T05:05:05Z\"").Replace("\"o_history\":[","\"o_history\":[\"2024-06-06T06:06:06Z\","));
  using var snapshot=await Read();var rows=Preview(db,snapshot,source);var scene=rows.Single(row=>row.Draft.Kind==StashEntityKind.Scene);
  Assert.Equal(StashFieldStatus.Conflict,scene.Fields.Single(field=>field.Field=="playCount").Status);
  Assert.Equal(StashFieldStatus.SourceChanged,scene.Fields.Single(field=>field.Field=="oCount").Status);
  var items=Confirmed(rows).Select(item=>item.SourceOrdinal==scene.Draft.SourceOrdinal?item with {Fields=[new("playCount",StashFieldDecision.KeepLocal),new("oCount",StashFieldDecision.UseSource)]}:item).ToArray();
  await db.ApplyStashImportAsync(snapshot,Request(source,items));
  var kept=db.GetMedia(video.Id)!;Assert.Equal((4L,2L,local),(kept.PlayCount,kept.OCount,kept.LastPlayedAt));
  Assert.Equal((3,2),(db.GetHistory(video.Id).Count(entry=>entry.Kind==HistoryKind.Play),db.GetHistory(video.Id).Count(entry=>entry.Kind==HistoryKind.O)));

  var taken=Confirmed(Preview(db,snapshot,source)).Select(item=>item.SourceOrdinal==scene.Draft.SourceOrdinal?item with {Fields=[new("playCount",StashFieldDecision.UseSource)]}:item).ToArray();
  await db.ApplyStashImportAsync(snapshot,Request(source,taken));
  var merged=db.GetMedia(video.Id)!;Assert.Equal((3L,new DateTimeOffset(2024,5,5,5,5,5,TimeSpan.Zero)),(merged.PlayCount,merged.LastPlayedAt));
  // The local playback stays in the history beside the three of the source.
  Assert.Equal(4,db.GetHistory(video.Id).Count(entry=>entry.Kind==HistoryKind.Play));
 }

 [Fact]
 public async Task SwitchedOffHistoryTakesOnlyTheCounters()
 {
  AddUsageLibrary();using var db=Open(false);var source=Profile(db);db.SaveHistoryPreferences(db.GetPreferences().History with {Enabled=false});
  using var snapshot=await Read();await db.ApplyStashImportAsync(snapshot,Request(source,Preview(db,snapshot,source).Select(row=>new StashImportItem(row.Draft.SourceOrdinal))));
  var video=db.Search().Single(item=>item.Kind==MediaKind.Video);Assert.Equal((2L,1L),(video.PlayCount,video.OCount));Assert.Equal(0,db.CountHistory());
 }

 /// <summary>Bytes with a PNG signature; the import only looks at the signature.</summary>
 private static byte[] Png(byte marker)=>[137,80,78,71,13,10,26,10,marker];
 private void AddPictureLibrary(byte cover,byte portrait)
 {
  File.WriteAllText(Path.Combine(Target,"a.mp4"),"synthetic video");
  Add("performers/jane.json",$$"""{"name":"Jane","image":"{{Convert.ToBase64String(Png(portrait))}}"}""");
  Add("tags/outdoor.json",$$"""{"name":"Outdoor","image":"{{Convert.ToBase64String(Png(3))}}"}""");
  Add("scenes/a.json",$$"""{"title":"Scene A","files":["/library/a.mp4"],"performers":["Jane"],"tags":["Outdoor"],"cover":"{{Convert.ToBase64String(Png(cover))}}"}""");
 }

 [Fact]
 public async Task FirstImportTakesTheCoverAndThePerformerImageAndARepeatAddsNothing()
 {
  AddPictureLibrary(1,2);using var db=Open(false);var source=Profile(db);using var snapshot=await Read();var rows=Preview(db,snapshot,source);
  Assert.Equal(".png",rows.Single(row=>row.Draft.Kind==StashEntityKind.Scene).Draft.Picture!.Format);
  var result=await db.ApplyStashImportAsync(snapshot,Request(source,rows.Select(row=>new StashImportItem(row.Draft.SourceOrdinal))));
  // A plain tag has no category and so no image; the note names it.
  Assert.Contains(result.Warnings,warning=>warning.Contains("Outdoor"));
  var video=Assert.Single(db.Search());var asset=Assert.Single(db.GetMediaAssets(video.Id));
  Assert.Equal((MediaAssetKind.Thumbnail,true),(asset.Kind,asset.IsCustom));Assert.Equal(Png(1),asset.Data);
  Assert.Null(video.ThumbnailPath);Assert.Equal(asset.Id.ToString(),video.Extra["thumbnail.assetId"]);
  var jane=new TagKey(Performers,"Jane");Assert.Equal(Png(2),db.GetTagAsset(db.GetTagPictureIds()[jane])!.Data);Assert.Single(db.GetTagPictureIds());

  var repeat=Preview(db,snapshot,source);Assert.All(repeat.SelectMany(row=>row.Fields),field=>Assert.Equal(StashFieldStatus.Equal,field.Status));
  Assert.Contains(repeat.SelectMany(row=>row.Fields),field=>field.Field=="cover");Assert.Contains(repeat.SelectMany(row=>row.Fields),field=>field.Field=="picture");
  var again=await db.ApplyStashImportAsync(snapshot,Request(source,Confirmed(repeat)));Assert.Equal(3,again.Unchanged);
  Assert.Single(db.GetMediaAssets(video.Id));Assert.Single(db.GetTagAssetSummaries(jane));
 }

 [Fact]
 public async Task ChangedPicturesStayLocalUntilTheyAreTakenAndTheEarlierOnesAreKept()
 {
  AddPictureLibrary(1,2);using var db=Open(false);var source=Profile(db);
  using(var first=await Read())await db.ApplyStashImportAsync(first,Request(source,Preview(db,first,source).Select(row=>new StashImportItem(row.Draft.SourceOrdinal))));
  var video=Assert.Single(db.Search());var jane=new TagKey(Performers,"Jane");
  // The app has written the stored cover to a thumbnail file in the meantime.
  var shown=Path.Combine(root,"shown.png");File.WriteAllBytes(shown,Png(1));db.Upsert(video with {ThumbnailPath=shown});
  AddPictureLibrary(11,12);using var snapshot=await Read();var rows=Preview(db,snapshot,source);
  var scene=rows.Single(row=>row.Draft.Kind==StashEntityKind.Scene);var performer=rows.Single(row=>row.Draft.Kind==StashEntityKind.Performer);
  Assert.Equal(StashFieldStatus.SourceChanged,scene.Fields.Single(field=>field.Field=="cover").Status);
  Assert.Equal(StashFieldStatus.SourceChanged,performer.Fields.Single(field=>field.Field=="picture").Status);
  StashImportItem[] Decide(IReadOnlyList<StashImportPreviewRow> preview,StashFieldDecision decision)=>Confirmed(preview).Select(item=>
   item.SourceOrdinal==scene.Draft.SourceOrdinal?item with {Fields=[new("cover",decision)]}:item.SourceOrdinal==performer.Draft.SourceOrdinal?item with {Fields=[new("picture",decision)]}:item).ToArray();

  await db.ApplyStashImportAsync(snapshot,Request(source,Decide(rows,StashFieldDecision.KeepLocal)));
  Assert.Equal(shown,db.GetMedia(video.Id)!.ThumbnailPath);Assert.Equal(Png(1),Assert.Single(db.GetMediaAssets(video.Id)).Data);
  Assert.Equal(Png(2),db.GetTagAsset(db.GetTagPictureIds()[jane])!.Data);
  var kept=Preview(db,snapshot,source);Assert.Equal(StashFieldStatus.PreviouslyKept,kept.Single(row=>row.Draft.Kind==StashEntityKind.Scene).Fields.Single(field=>field.Field=="cover").Status);

  await db.ApplyStashImportAsync(snapshot,Request(source,Decide(kept,StashFieldDecision.UseSource)));
  var taken=db.GetMedia(video.Id)!;var assets=db.GetMediaAssets(video.Id);Assert.Equal(2,assets.Count);
  Assert.Null(taken.ThumbnailPath);Assert.Equal(Png(11),assets.Single(asset=>asset.Id.ToString()==taken.Extra["thumbnail.assetId"]).Data);
  Assert.Equal(Png(12),db.GetTagAsset(db.GetTagPictureIds()[jane])!.Data);Assert.Single(db.GetTagAssetSummaries(jane));
  // The earlier image is released, not deleted.
  var released=Assert.Single(db.GetTagAssetSummaries(unassignedOnly:true));Assert.Equal(jane,Assert.Single(released.PreviousTags));
  Assert.All(Preview(db,snapshot,source).SelectMany(row=>row.Fields),field=>Assert.Equal(StashFieldStatus.Equal,field.Status));
 }

 [Fact]
 public async Task AnUnreadablePictureIsLeftOutWithANote()
 {
  File.WriteAllText(Path.Combine(Target,"a.mp4"),"synthetic video");
  Add("scenes/a.json",$$"""{"title":"Scene A","files":["/library/a.mp4"],"cover":"{{Convert.ToBase64String("not a picture"u8)}}"}""");
  Add("performers/jane.json","""{"name":"Jane","image":"***"}""");
  using var db=Open(false);var source=Profile(db);using var snapshot=await Read();var rows=Preview(db,snapshot,source);
  Assert.All(rows,row=>{Assert.Null(row.Draft.Picture);Assert.Contains(row.Draft.Issues,issue=>issue.Code=="picture"&&issue.Severity==StashIssueSeverity.Warning);});
  await db.ApplyStashImportAsync(snapshot,Request(source,rows.Select(row=>new StashImportItem(row.Draft.SourceOrdinal))));
  Assert.Empty(db.GetMediaAssets(Assert.Single(db.Search()).Id));Assert.Empty(db.GetTagPictureIds());
 }

 [Fact]
 public async Task AnExportOfStash032IsReadLikeTheOneBefore()
 {
  // 0.32.0 adds "production_date" to scenes and writes the perceptual hash of a file as text. The hash is not read;
  // the production date stands in where the release date is missing.
  File.WriteAllText(Path.Combine(Target,"a.mp4"),"synthetic video");File.WriteAllText(Path.Combine(Target,"b.mp4"),"another video");
  Add("scenes/b.json","""{"title":"Scene B","files":["/library/b.mp4"],"production_date":"2020-11-30"}""");
  Add("files/a.json","""{"type":"video","path":"/library/a.mp4","size":15,"mod_time":"2026-11-02T10:00:00Z","fingerprints":[{"type":"oshash","fingerprint":"0f1e2d3c4b5a6978"},{"type":"phash","fingerprint":"f0e1d2c3b4a59687"}],"duration":12.5,"width":1920,"height":1080}""");
  Add("scenes/a.json","""{"title":"Scene A","files":["/library/a.mp4"],"date":"2021-05-01","production_date":"2020-11-30"}""");
  using var db=Open(false);var source=Profile(db);using var snapshot=await Read();Assert.Equal(0,snapshot.ErrorDocuments);
  var scenes=Preview(db,snapshot,source).Where(row=>row.Draft.Kind==StashEntityKind.Scene).ToArray();
  Assert.Empty(scenes.Single(row=>row.Draft.Name=="Scene A").Draft.Issues);
  await db.ApplyStashImportAsync(snapshot,Request(source,scenes.Select(row=>new StashImportItem(row.Draft.SourceOrdinal))));
  var video=db.Search().Single(item=>item.Title=="Scene A");Assert.Equal((new DateOnly(2021,5,1),12.5,1920),(video.ReleaseDate,video.DurationSeconds,video.Width));
  Assert.Equal(new DateOnly(2020,11,30),db.Search().Single(item=>item.Title=="Scene B").ReleaseDate);
  // The source record keeps both dates.
  Assert.Contains(db.GetStashBindings(source.Id),binding=>System.Text.Encoding.UTF8.GetString(db.ReadStashProvenanceBytes(binding.ProvenanceId)!).Contains("\"date\":\"2021-05-01\",\"production_date\""));
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

 private void AddOrganization()
 {
  foreach(var name in new[]{"a.jpg","b.jpg","s1.mp4","s2.mp4"})File.WriteAllText(Path.Combine(Target,name),"synthetic "+name);
  Add("galleries/holiday.json","""{"title":"Holiday","details":"Beach week","folder_path":"/library/holiday"}""");
  Add("images/b.json","""{"title":"B","files":["/library/b.jpg"],"galleries":[{"folder_path":"/library/holiday"}]}""");
  Add("images/a.json","""{"title":"A","files":["/library/a.jpg"],"galleries":[{"folder_path":"/library/holiday"}]}""");
  Add("movies/series.json","""{"name":"Series","synopsis":"Two parts"}""");
  Add("scenes/s1.json","""{"title":"Part two","files":["/library/s1.mp4"],"movies":[{"movieName":"Series","scene_index":2}]}""");
  Add("scenes/s2.json","""{"title":"Part one","files":["/library/s2.mp4"],"movies":[{"movieName":"Series","scene_index":1}]}""");
 }

 [Theory][InlineData(false)]
 public async Task GalleriesAndGroupsBecomeOrderedImageSetsAndPublicationGroups(bool encrypted)
 {
  AddOrganization();using var db=Open(encrypted);var source=Profile(db);using var snapshot=await Read();var rows=Preview(db,snapshot,source);
  var result=await db.ApplyStashImportAsync(snapshot,Request(source,rows.Select(row=>new StashImportItem(row.Draft.SourceOrdinal))));Assert.Equal(6,result.Created);
  var media=db.Search().ToDictionary(item=>item.Title);
  var set=Assert.Single(db.GetImageSets());Assert.Equal(("Holiday","Beach week"),(set.Name,set.Description));
  Assert.Equal(new[]{media["A"].Id,media["B"].Id},set.MediaIds);Assert.Equal(media["A"].Id,set.CoverId);
  var group=Assert.Single(db.GetPublicationGroups());Assert.Equal(("Series","Two parts"),(group.Name,group.Description));
  Assert.Equal(new[]{media["Part one"].Id,media["Part two"].Id},group.Members.Select(member=>member.TargetId));
  Assert.All(group.Members,member=>Assert.Equal(PublicationGroupMemberKind.Video,member.Kind));
  var again=await db.ApplyStashImportAsync(snapshot,Request(source,Confirmed(Preview(db,snapshot,source))));Assert.Equal((0,0,6),(again.Created,again.Updated,again.Unchanged));
  Assert.Single(db.GetImageSets());Assert.Single(db.GetPublicationGroups());
 }

 [Fact]
 public async Task MembersMustBeSelectedOrImportedEarlierAndChangedMembershipIsOnlyTakenWhenChosen()
 {
  AddOrganization();using var db=Open(false);var source=Profile(db);using var snapshot=await Read();var rows=Preview(db,snapshot,source);
  var gallery=rows.Single(row=>row.Draft.Kind==StashEntityKind.Gallery);
  var missing=await Assert.ThrowsAsync<ArgumentException>(()=>db.ApplyStashImportAsync(snapshot,Request(source,[new(gallery.Draft.SourceOrdinal)])));
  Assert.Contains("images/",missing.Message,StringComparison.Ordinal);RequireNothingImported(db,source);Assert.Empty(db.GetImageSets());
  await db.ApplyStashImportAsync(snapshot,Request(source,rows.Where(row=>row.Draft.Kind==StashEntityKind.Image).Select(row=>new StashImportItem(row.Draft.SourceOrdinal))));
  await db.ApplyStashImportAsync(snapshot,Request(source,[new(gallery.Draft.SourceOrdinal)]));
  var set=Assert.Single(db.GetImageSets());var a=db.Search().Single(item=>item.Title=="A");
  // A local reorder is kept; the changed source order shows as a conflict and is only taken when chosen.
  db.UpsertImageSet(set with {MediaIds=set.MediaIds.AsEnumerable().Reverse().ToList()});
  Add("images/a.json","""{"title":"A","files":["/library/a.jpg"]}""");
  using var changed=await Read();var row=Preview(db,changed,source).Single(row=>row.Draft.Kind==StashEntityKind.Gallery);
  Assert.Equal(StashFieldStatus.Conflict,row.Fields.Single(field=>field.Field=="mediaIds").Status);
  var images=Preview(db,changed,source).Where(row=>row.Draft.Kind==StashEntityKind.Image);
  await db.ApplyStashImportAsync(changed,Request(source,Confirmed([row,..images])));
  Assert.Equal(2,Assert.Single(db.GetImageSets()).MediaIds.Count);
  var choice=Confirmed([row]).Single() with {Fields=[new("mediaIds",StashFieldDecision.UseSource)]};
  await db.ApplyStashImportAsync(changed,Request(source,[choice]));
  var updated=Assert.Single(db.GetImageSets());Assert.Equal(new[]{db.Search().Single(item=>item.Title=="B").Id},updated.MediaIds);Assert.NotEqual(a.Id,updated.CoverId);
 }

 [Fact]
 public async Task FurtherFilesBecomeVersionsOnlyWhenConfirmedAndAreNeverRemoved()
 {
  foreach(var name in new[]{"main.mp4","4k.mp4"})File.WriteAllText(Path.Combine(Target,name),"synthetic "+name);
  Add("scenes/a.json","""{"title":"Versions","files":["/library/main.mp4","/library/4k.mp4"]}""");
  using var db=Open(false);var source=Profile(db);using var snapshot=await Read();
  var first=await db.ApplyStashImportAsync(snapshot,Request(source,Preview(db,snapshot,source).Select(row=>new StashImportItem(row.Draft.SourceOrdinal))));
  Assert.Contains(first.Warnings,warning=>warning.Contains("Versions",StringComparison.Ordinal));Assert.Empty(Assert.Single(db.Search()).Files);
  var confirmed=await db.ApplyStashImportAsync(snapshot,Request(source,Confirmed(Preview(db,snapshot,source)).Select(item=>item with {ConfirmVersions=true})));
  Assert.Equal(1,confirmed.Updated);var media=Assert.Single(db.Search());
  Assert.Equal((Path.Combine(Target,"main.mp4"),Path.Combine(Target,"4k.mp4")),(media.Path,Assert.Single(media.Files).Path));
  var again=await db.ApplyStashImportAsync(snapshot,Request(source,Confirmed(Preview(db,snapshot,source)).Select(item=>item with {ConfirmVersions=true})));
  Assert.Equal(1,again.Unchanged);Assert.Single(Assert.Single(db.Search()).Files);
  Add("scenes/a.json","""{"title":"Versions","files":["/library/main.mp4"]}""");
  using var fewer=await Read();await db.ApplyStashImportAsync(fewer,Request(source,Confirmed(Preview(db,fewer,source)).Select(item=>item with {ConfirmVersions=true})));
  Assert.Single(Assert.Single(db.Search()).Files);
 }

 [Fact]
 public async Task ArchiveImagesAreResolvedOutsideTheCatalogAndRefusedWithoutAResolver()
 {
  Add("images/a.json","""{"title":"Inside","files":["/library/photos.zip/inner/a.jpg"]}""");
  Add("files/a.json","""{"type":"image","path":"/library/photos.zip/inner/a.jpg","size":42,"width":640,"height":480,"zip_file":"/library/photos.zip"}""");
  using var db=Open(false);var source=Profile(db);using var snapshot=await Read();var row=Preview(db,snapshot,source).Single(row=>row.Draft.Kind==StashEntityKind.Image);
  await Assert.ThrowsAsync<ArgumentException>(()=>db.ApplyStashImportAsync(snapshot,Request(source,[new(row.Draft.SourceOrdinal)])));RequireNothingImported(db,source);
  var cache=Path.Combine(root,"archive-cache","a.jpg");var calls=new List<(string,string)>();var hash=new string('a',64);
  await db.ApplyStashImportAsync(snapshot,Request(source,[new(row.Draft.SourceOrdinal)]),archives:(archive,entry,_)=>
  {calls.Add((archive,entry));return Task.FromResult(new StashArchiveMember(archive,entry,cache,hash,42));});
  Assert.Equal((Path.Combine(Target,"photos.zip"),"inner/a.jpg"),Assert.Single(calls));
  var media=Assert.Single(db.Search());
  Assert.Equal((cache,"Inside",MediaKind.Image,42L,640),(media.Path,media.Title,media.Kind,media.FileSize!.Value,media.Width!.Value));
  Assert.Equal((Path.Combine(Target,"photos.zip"),"inner/a.jpg",hash),(media.Extra["archivePath"],media.Extra["entryName"],media.Extra["sha256"]));
  // A resolver answering for another member is not trusted.
  await Assert.ThrowsAsync<InvalidDataException>(()=>db.ApplyStashImportAsync(snapshot,Request(source,Confirmed([Preview(db,snapshot,source).Single(row=>row.Draft.Kind==StashEntityKind.Image)])),
   archives:(archive,_,_)=>Task.FromResult(new StashArchiveMember(archive,"other.jpg",cache,hash,42))));
 }

 [Theory]
 [InlineData(@"C:\Lib\Photos.zip\Sub\A.jpg",@"c:\lib\photos.zip","Sub/A.jpg")]
 [InlineData("/lib/photos.zip/Sub/A.jpg","/lib/photos.zip","Sub/A.jpg")]
 [InlineData("/lib/Photos.zip/a.jpg","/lib/photos.zip",null)]
 [InlineData("/lib/photos.zipped/a.jpg","/lib/photos.zip",null)]
 [InlineData("/lib/photos.zip","/lib/photos.zip",null)]
 [InlineData(@"C:\lib\photos.zip\a.jpg","/lib/photos.zip",null)]
 public void ArchiveEntriesFollowTheSourceSystemForTheArchiveAndAreExactInside(string member,string archive,string? expected)
  =>Assert.Equal(expected,StashPaths.ArchiveEntry(member,archive));

 [Fact]
 public async Task TagParentsGoIntoTheHierarchyAndCyclesRefuseTheImport()
 {
  Add("tags/outdoor.json","""{"name":"Outdoor","parents":["Place"]}""");Add("tags/place.json","""{"name":"Place"}""");
  using var db=Open(false);var source=Profile(db);
  using(var snapshot=await Read())
  {
   await db.ApplyStashImportAsync(snapshot,Request(source,Preview(db,snapshot,source).Select(row=>new StashImportItem(row.Draft.SourceOrdinal))));
   Assert.Equal(new[]{new TagKey("","Place")},db.GetTagParents(new TagKey("","Outdoor")));
   Assert.Equal(StashFieldStatus.Equal,Preview(db,snapshot,source).Single(row=>row.Draft.Name=="Outdoor").Fields.Single(field=>field.Field=="parents").Status);
  }
  Add("tags/place.json","""{"name":"Place","parents":["Outdoor"]}""");
  using var cyclic=await Read();var rows=Preview(db,cyclic,source);var place=rows.Single(row=>row.Draft.Name=="Place");
  // The first export had no parents field for Place, so it was never decided and appears as new.
  Assert.Equal(StashFieldStatus.New,place.Fields.Single(field=>field.Field=="parents").Status);
  var revisions=db.GetStashBindings(source.Id).Select(binding=>(binding.Id,binding.Revision)).Order().ToArray();
  var choice=Confirmed([place]).Single() with {Fields=[new("parents",StashFieldDecision.UseSource)]};
  var cycle=await Assert.ThrowsAsync<ArgumentException>(()=>db.ApplyStashImportAsync(cyclic,Request(source,[choice])));
  Assert.Contains(Texts.T("The tag hierarchy cannot contain a cycle."),cycle.Message,StringComparison.Ordinal);
  Assert.Empty(db.GetTagParents(new TagKey("","Place")));Assert.Equal(revisions,db.GetStashBindings(source.Id).Select(binding=>(binding.Id,binding.Revision)).Order());
 }
}
