using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class StashDraftTests:IDisposable
{
 private readonly string root=StashTestWorkspace.Create("flixypix-stash-draft-");
 private string Source=>Path.Combine(root,"source");
 private string Target=>Path.Combine(root,"media");
 public StashDraftTests()=>Directory.CreateDirectory(Source);
 public void Dispose(){SqliteConnection.ClearAllPools();if(Environment.GetEnvironmentVariable("FLIXYPIX_TEST_CAPTURE")!="1")Directory.Delete(root,true);}
 private void Add(string relative,string json)
 {var path=Path.Combine(Source,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,json);}
 private Task<StashExportSnapshot> Read()=>StashExport.ReadDirectoryAsync(Source,Path.Combine(root,"stage"));
 private StashSourceProfile Profile=>new(){Name="Test",SourceDirectory=Source,PathMappings=[new("/library",Target)]};
 private IReadOnlyList<StashImportDraft> Drafts(StashExportSnapshot snapshot,Func<string,bool>? exists=null)=>snapshot.ReadDraftPage(Profile,0,256,exists??(_=>true));

 [Fact]
 public async Task MediaPreservesMultipleFileProposalsMarkersRatingAndUnreviewedState()
 {
  Add("scenes/a.json","""{"title":"Scene","files":["/library/a.mp4","/library/b.mp4"],"rating":95,"organized":true,"date":"2020-02-29","tags":["T"],"markers":[{"title":"Bookmark","seconds":"1.25","primary_tag":"T","tags":["T"]},{"title":"Range","seconds":"3","end_seconds":"5"}]}""");
  Add("tags/t.json","""{"name":"T"}""");
  Add("files/a.json","""{"type":"video","path":"/library/a.mp4","size":123,"width":1920,"height":1080,"duration":12.5,"frame_rate":23.976,"bitrate":12000,"video_codec":"h264"}""");
  Add("files/b.json","""{"type":"video","path":"/library/b.mp4","size":456}""");
  using var snapshot=await Read();var probes=new List<string>();var draft=Drafts(snapshot,path=>{probes.Add(path);return true;}).Single(row=>row.Kind==StashEntityKind.Scene);
  Assert.False(draft.HasErrors);var media=Assert.IsType<StashMediaDraft>(draft.Media);Assert.Equal(10,media.Rating);Assert.True(media.SourceOrganized);Assert.Equal(ReviewState.Unreviewed,media.InitialReviewState);Assert.Equal(new DateOnly(2020,2,29),media.ReleaseDate);
  Assert.Equal(2,media.Files.Count);Assert.All(media.Files,file=>Assert.NotNull(file.FileRecordOrdinal));Assert.Equal(1920,media.Files[0].Width);Assert.Equal(12.5,media.Files[0].DurationSeconds);Assert.Equal("h264",media.Files[0].Technical!.VideoCodec);
  Assert.Equal(new[]{Path.Combine(Target,"a.mp4"),Path.Combine(Target,"b.mp4")},probes);Assert.False(media.Markers[0].IsScene);Assert.Equal(1.25,media.Markers[0].StartSeconds);Assert.Equal(new[]{"T"},media.Markers[0].Tags);Assert.True(media.Markers[1].IsScene);Assert.Equal(5,media.Markers[1].EndSeconds);
  await snapshot.VerifySourceAsync();
 }
 [Fact]
 public async Task MarkerTagsMustResolveEvenWhenAbsentFromSceneTags()
 {
  Add("scenes/a.json","""{"markers":[{"seconds":"1","primary_tag":"Missing","tags":["Known"]}]}""");Add("tags/a.json","""{"name":"Known"}""");
  using var snapshot=await Read();var scene=Drafts(snapshot).Single(row=>row.Kind==StashEntityKind.Scene);Assert.True(scene.HasErrors);Assert.Equal(2,scene.References.Count);Assert.All(scene.References,reference=>Assert.Equal("marker-tag:0",reference.Reference.Relation));Assert.NotNull(scene.References.Single(reference=>reference.Reference.Key=="name:Known").TargetOrdinal);Assert.Null(scene.References.Single(reference=>reference.Reference.Key=="name:Missing").TargetOrdinal);
 }
 [Fact]
 public async Task MissingMappingNeverProbesForeignPathsAndMissingLocalFileIsAWarning()
 {
  Add("scenes/a.json","""{"files":["/unmapped/secret.mp4","/library/missing.mp4"]}""");
  using var snapshot=await Read();var probes=new List<string>();var row=Assert.Single(Drafts(snapshot,path=>{probes.Add(path);return false;}));
  Assert.True(row.HasErrors);Assert.Contains(row.Issues,issue=>issue.Code=="unmapped-path");Assert.Contains(row.Issues,issue=>issue.Code=="missing-file"&&issue.Severity==StashIssueSeverity.Warning);Assert.Equal(Path.Combine(Target,"missing.mp4"),Assert.Single(probes));Assert.Null(row.Media!.Files[0].Exists);
 }
 [Fact]
 public async Task AmbiguousNamedReferencesAreNotResolvedByFilenameOrDisambiguationGuess()
 {
  Add("performers/a.json","""{"name":"Same","disambiguation":"First"}""");Add("performers/b.json","""{"name":"Same","disambiguation":"Second"}""");
  Add("scenes/a.json","""{"performers":["Same"],"tags":["Missing"]}""");
  using var snapshot=await Read();var rows=Drafts(snapshot);var scene=rows.Single(row=>row.Kind==StashEntityKind.Scene);
  Assert.True(scene.HasErrors);Assert.All(scene.References,reference=>Assert.Null(reference.TargetOrdinal));Assert.Contains(scene.Issues,issue=>issue.Code=="ambiguous-reference");Assert.Contains(scene.Issues,issue=>issue.Code=="missing-reference");Assert.Equal(2,rows.Where(row=>row.Kind==StashEntityKind.Performer).Select(row=>row.Directory!.Disambiguation).Distinct().Count());
 }
 [Fact]
 public async Task SharedGalleriesResolveByPathAndGroupsUseSourceSceneIndices()
 {
  Add("galleries/a.json","""{"title":"Same","folder_path":"/library/gallery"}""");Add("galleries/b.json","""{"title":"Same","folder_path":"/library/other"}""");
  Add("images/z.json","""{"files":["/library/a.jpg"],"galleries":[{"folder_path":"/library/gallery"},{"folder_path":"/library/other"}]}""");Add("images/a.json","""{"files":["/library/z.jpg"],"galleries":[{"folder_path":"/library/gallery"}]}""");Add("images/ambiguous.json","""{"galleries":[{"title":"Same"}]}""");
  Add("movies/a.json","""{"name":"Group"}""");Add("movies/b.json","""{"name":"Other"}""");
  Add("scenes/a.json","""{"movies":[{"movieName":"Group","scene_index":2},{"movieName":"Other","scene_index":1}]}""");Add("scenes/b.json","""{"movies":[{"movieName":"Group","scene_index":1}]}""");
  using var snapshot=await Read();var rows=Drafts(snapshot);var gallery=rows.Single(row=>row.RelativePath=="galleries/a.json");Assert.Equal(new[]{"images/z.json","images/a.json"},gallery.Members.Select(member=>member.SourceRelativePath));Assert.Contains(gallery.Issues,issue=>issue.Code=="derived-image-order");Assert.Single(rows.Single(row=>row.RelativePath=="galleries/b.json").Members);Assert.True(rows.Single(row=>row.RelativePath=="images/ambiguous.json").HasErrors);
  var group=rows.Single(row=>row.RelativePath=="movies/a.json");Assert.Equal(new[]{"scenes/b.json","scenes/a.json"},group.Members.Select(member=>member.SourceRelativePath));Assert.Equal(new int?[]{1,2},group.Members.Select(member=>member.SourceOrder));Assert.Single(rows.Single(row=>row.RelativePath=="movies/b.json").Members);
 }
 [Fact]
 public async Task ArchiveMembersAreNotProbedAsExtractedFiles()
 {
  Add("images/a.json","""{"files":["/library/virtual/a.jpg"]}""");Add("files/a.json","""{"type":"image","path":"/library/virtual/a.jpg","size":42,"zip_file":"/library/photos.zip"}""");
  using var snapshot=await Read();var row=Drafts(snapshot,_=>throw new InvalidOperationException("Archive member must not be probed")).Single(row=>row.Kind==StashEntityKind.Image);var file=Assert.Single(row.Media!.Files);
  Assert.Null(file.Exists);Assert.Equal(Path.Combine(Target,"photos.zip"),file.ArchiveTarget);Assert.Contains(row.Issues,issue=>issue.Code=="archive-member");
 }
 [Fact]
 public async Task WindowsReferencesCanonicalizeCaseAndSeparators()
 {
  Add("scenes/a.json","""{"files":["Z:/Media/Clip.mp4"]}""");Add("files/a.json","""{"type":"video","path":"z:\\media\\clip.MP4","size":9}""");
  using var snapshot=await Read();var profile=Profile with{PathMappings=[new("Z:/Media",Target)]};var row=snapshot.ReadDraftPage(profile,0,24,_=>true).Single(row=>row.Kind==StashEntityKind.Scene);Assert.False(row.HasErrors);Assert.NotNull(Assert.Single(row.Media!.Files).FileRecordOrdinal);
 }
 [Fact]
 public async Task ProfilesAreMappedAndInvalidTargetValuesRequireCorrection()
 {
  Add("performers/a.json","""{"name":"Person","gender":"FEMALE","height":"170","weight":60,"birthdate":"1990-01-02","aliases":"One, Two","career_start":"2010","urls":["https://example.test/person"]}""");Add("performers/b.json","""{"name":"Invalid","height":"999","birthdate":"bad","gender":"UNKNOWN"}""");
  Add("studios/a.json","""{"name":"Studio","parent_studio":"Network","urls":["https://example.test/studio"]}""");Add("studios/b.json","""{"name":"Network"}""");
  using var snapshot=await Read();var rows=Drafts(snapshot);var person=rows.Single(row=>row.Name=="Person");Assert.False(person.HasErrors);Assert.Equal(TagRole.Performer,person.Directory!.Role);Assert.Equal(new[]{"One","Two"},person.Directory.Aliases);Assert.Equal(PerformerGender.Female,person.Directory.Profile!.Gender);Assert.Equal(170,person.Directory.Profile.HeightCm);Assert.Equal(60,person.Directory.Profile.WeightKg);
  var invalid=rows.Single(row=>row.Name=="Invalid");Assert.True(invalid.HasErrors);Assert.Contains(invalid.Issues,issue=>issue.Code=="profile-value");Assert.Contains(invalid.Issues,issue=>issue.Code=="date");Assert.Contains(invalid.Issues,issue=>issue.Code=="profile-gender");var studio=rows.Single(row=>row.Name=="Studio");Assert.False(studio.HasErrors);Assert.Equal("Network",studio.Directory!.Profile!.ParentStudio);Assert.NotNull(Assert.Single(studio.References).TargetOrdinal);
 }
 [Fact]
 public async Task InvalidRecordAndUnrepresentableMappedPathDoNotAbortOtherDrafts()
 {
  Add("scenes/a.json","""{"files":["/library/CON.mp4"]}""");Add("scenes/b.json","{broken");Add("tags/a.json","""{"name":"Good"}""");
  using var snapshot=await Read();var rows=Drafts(snapshot,_=>throw new InvalidOperationException("Invalid target must not be probed"));Assert.Equal(3,rows.Count);Assert.Contains(rows.Single(row=>row.RelativePath=="scenes/a.json").Issues,issue=>issue.Code=="mapped-path");Assert.True(rows.Single(row=>row.RelativePath=="scenes/b.json").HasErrors);Assert.False(rows.Single(row=>row.Name=="Good").HasErrors);
 }
 [Fact]
 public async Task PagingCancellationProfileOwnershipAndDisposalAreEnforced()
 {
  for(var i=0;i<5;i++)Add($"tags/{i}.json",JsonSerializer.Serialize(new{name="Tag"+i}));
  using var snapshot=await Read();Assert.Equal("Tag2",snapshot.ReadDraftPage(Profile,2,1)[0].Name);Assert.Throws<ArgumentException>(()=>snapshot.ReadDraftPage(Profile with{SourceDirectory=Target},0));using var cancellation=new CancellationTokenSource();cancellation.Cancel();Assert.Throws<OperationCanceledException>(()=>snapshot.ReadDraftPage(Profile,0,token:cancellation.Token));snapshot.Dispose();Assert.Throws<ObjectDisposedException>(()=>snapshot.ReadDraftPage(Profile,0));
 }
 [Theory][InlineData("width", "-1")][InlineData("height","2147483648")][InlineData("bitrate","\"large\"")]
 public async Task InvalidTechnicalIntegersCannotBecomeSilentNullMetadata(string property,string value)
 {
  Add("files/a.json","{\"type\":\"video\",\"path\":\"/library/a.mp4\",\"size\":1,\""+property+"\":"+value+"}");using var snapshot=await Read();Assert.Contains(Assert.Single(Drafts(snapshot)).Issues,issue=>issue.Code=="file-number");
 }
}
