using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class PortableOrganizationTests:IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-portable-organization-"+Guid.NewGuid().ToString("N"));
 private const string Secret="synthetic-organization-password";
 public PortableOrganizationTests()=>Directory.CreateDirectory(root);
 public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
 private string PathOf(string name)=>Path.Combine(root,name);
 private CatalogKey? Prepare(string path,bool encrypted)
 {using(var empty=new CatalogRepository(path)){}if(!encrypted)return null;CatalogEncryption.EnableEncryption(path,Secret);return CatalogEncryption.Unlock(path,Secret);}
 private (MediaItem Video,MediaItem Image,ImageSet Set,PublicationGroup Group) Populate(CatalogRepository db)
 {
  var video=db.Upsert(new(){Path=PathOf("video.mp4"),Kind=MediaKind.Video,ReviewState=ReviewState.Reviewed,Playback=new(){Speed=1.5,AudioDelaySeconds=.3},Tags=["Root","Child"]});
  var image=db.Upsert(new(){Path=PathOf("image.png"),Kind=MediaKind.Image});
  var set=db.UpsertImageSet(new(){Id=video.Id,Name="Typed set",MediaIds=[image.Id],CoverId=image.Id,Description="Set description"});
  var group=db.UpsertPublicationGroup(new(){Name="Mixed group",Description="Group description",Members=[new(PublicationGroupMemberKind.ImageSet,set.Id),new(PublicationGroupMemberKind.Video,video.Id)]});
  db.AddTagParent(new("","Child"),new("","Root"));return(video,image,set,group);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void Format5RoundtripIncludesTypedOrganizationPlayerAndReviewWithFreshReopen(bool encrypted)
 {
  var source=PathOf("source.db");using var key=Prepare(source,encrypted);using var db=new CatalogRepository(source,key);var fixture=Populate(db);var export=PathOf("portable.json");db.Export(export);
  if(!encrypted){using var document=JsonDocument.Parse(File.ReadAllBytes(export));Assert.Equal(5,document.RootElement.GetProperty("version").GetInt32());Assert.Single(document.RootElement.GetProperty("imageSets").EnumerateArray());}
  var target=PathOf("target.db");using var targetKey=Prepare(target,encrypted);
  using(var destination=new CatalogRepository(target,targetKey))
  {
   Assert.Empty(destination.ImportJson(export,CatalogRepository.MaximumPortableEntityBytes,secret:encrypted?Secret:null).Errors);
   Assert.Equal(fixture.Set.MediaIds,destination.GetImageSet(fixture.Set.Id)!.MediaIds);Assert.Equal(fixture.Set.CoverId,destination.GetImageSet(fixture.Set.Id)!.CoverId);
   Assert.Equal(fixture.Group.Members,destination.GetPublicationGroup(fixture.Group.Id)!.Members);Assert.Equal(db.GetTagHierarchy(),destination.GetTagHierarchy());
   Assert.Equal(fixture.Video.Playback,destination.GetMedia(fixture.Video.Id)!.Playback);Assert.Equal(ReviewState.Reviewed,destination.GetMedia(fixture.Video.Id)!.ReviewState);
   Assert.Empty(destination.ImportJson(export,CatalogRepository.MaximumPortableEntityBytes,secret:encrypted?Secret:null).Errors);Assert.Single(destination.GetImageSets());Assert.Single(destination.GetPublicationGroups());Assert.Single(destination.GetTagHierarchy());
  }
  using var reopened=new CatalogRepository(target,targetKey);Assert.Equal("Set description",reopened.GetImageSet(fixture.Set.Id)!.Description);Assert.Equal("Group description",reopened.GetPublicationGroup(fixture.Group.Id)!.Description);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void PathCollisionRemapsMediaButNeverSetIdentityInTypedGroup(bool encrypted)
 {
  var source=PathOf("source.db");using var key=Prepare(source,encrypted);using var db=new CatalogRepository(source,key);var fixture=Populate(db);var export=PathOf("portable.json");db.Export(export);
  using var destination=new CatalogRepository(PathOf("target.db"));var video=destination.Upsert(new(){Path=fixture.Video.Path,Kind=MediaKind.Video});var image=destination.Upsert(new(){Path=fixture.Image.Path,Kind=MediaKind.Image});
  Assert.Empty(destination.ImportJson(export,CatalogRepository.MaximumPortableEntityBytes,secret:encrypted?Secret:null).Errors);
  var set=destination.GetImageSet(fixture.Set.Id)!;Assert.Equal([image.Id],set.MediaIds);Assert.Equal(image.Id,set.CoverId);
  Assert.Equal(new[]{new PublicationGroupMember(PublicationGroupMemberKind.ImageSet,fixture.Set.Id),new PublicationGroupMember(PublicationGroupMemberKind.Video,video.Id)},destination.GetPublicationGroup(fixture.Group.Id)!.Members);
  Assert.Null(destination.GetMedia(fixture.Video.Id));Assert.NotNull(destination.GetImageSet(fixture.Video.Id));
 }
 [Theory][InlineData("bad-member")][InlineData("bad-cover")][InlineData("bad-group")][InlineData("duplicate-set")][InlineData("duplicate-graph")][InlineData("cycle")][InlineData("duplicate-edge")]
 public void InvalidOrganizationRollsBackEarlierMediaAndAllSections(string kind)
 {
  using var db=new CatalogRepository(PathOf("source.db"));var fixture=Populate(db);var export=PathOf("bad.json");db.Export(export);var json=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(export))!;
  json["items"]![0]!["title"]="Must not survive";
  switch(kind)
  {
   case "bad-member":json["imageSets"]![0]!["mediaIds"]![0]=Guid.NewGuid().ToString();break;
   case "bad-cover":json["imageSets"]![0]!["coverId"]=Guid.NewGuid().ToString();break;
   case "bad-group":json["publicationGroups"]![0]!["members"]![0]!["targetId"]=Guid.NewGuid().ToString();break;
   case "duplicate-set":json["imageSets"]!.AsArray().Add(json["imageSets"]![0]!.DeepClone());break;
   case "duplicate-graph":json["tagHierarchies"]!.AsArray().Add(json["tagHierarchies"]![0]!.DeepClone());break;
   case "cycle":var edge=json["tagHierarchies"]![0]!["edges"]![0]!.DeepClone();var parent=edge["parent"]!.DeepClone();edge["parent"]=edge["child"]!.DeepClone();edge["child"]=parent;json["tagHierarchies"]![0]!["edges"]!.AsArray().Add(edge);break;
   case "duplicate-edge":json["tagHierarchies"]![0]!["edges"]!.AsArray().Add(json["tagHierarchies"]![0]!["edges"]![0]!.DeepClone());break;
  }
  File.WriteAllText(export,json.ToJsonString());using var destination=new CatalogRepository(PathOf("target.db"));
  var retained=destination.Upsert(new(){Path=PathOf("retained.png"),Title="Keep",Kind=MediaKind.Image});
  var result=destination.ImportJson(export);Assert.NotEmpty(result.Errors);Assert.Equal(0,result.Imported);Assert.Single(destination.Search());Assert.Equal("Keep",destination.GetMedia(retained.Id)!.Title);Assert.Empty(destination.GetImageSets());Assert.Empty(destination.GetPublicationGroups());Assert.Empty(destination.GetTagHierarchy());
 }
 [Theory][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)]
 public void OlderFormatsRemainReadableAndDoNotClearExistingOrganization(int version)
 {
  using var db=new CatalogRepository(PathOf("target.db"));var fixture=Populate(db);var export=PathOf("old.json");File.WriteAllText(export,JsonSerializer.Serialize(new {format="flixypix-catalog",version,items=Array.Empty<MediaItem>()},CatalogRepository.Json));
  Assert.Empty(db.ImportJson(export).Errors);Assert.NotNull(db.GetImageSet(fixture.Set.Id));Assert.NotNull(db.GetPublicationGroup(fixture.Group.Id));Assert.Single(db.GetTagHierarchy());
 }
 [Fact]
 public void ImportedEdgesMergeWithLocalGraphAndRejectCyclesAcrossBothGraphs()
 {
  using var db=new CatalogRepository(PathOf("target.db"));Populate(db);db.Upsert(new(){Path=PathOf("third.png"),Tags=["Leaf"]});db.AddTagParent(new("","Leaf"),new("","Child"));
  var export=PathOf("edges.json");var graph=new TagHierarchy{Edges=[new(new("","Root"),new("","Leaf"))]};
  void Write()=>File.WriteAllText(export,JsonSerializer.Serialize(new {format="flixypix-catalog",version=5,tagHierarchies=new[]{graph}},CatalogRepository.Json));
  Write();Assert.Empty(db.ImportJson(export).Errors);Assert.Equal(3,db.GetTagHierarchy().Count);
  graph=new(){Edges=[new(new("","Leaf"),new("","Root"))]};Write();Assert.NotEmpty(db.ImportJson(export).Errors);Assert.Equal(3,db.GetTagHierarchy().Count);
 }
 [Fact]
 public void LateGroupProjectionFailureRollsBackPreviouslyImportedSets()
 {
  using var source=new CatalogRepository(PathOf("source.db"));Populate(source);var export=PathOf("portable.json");source.Export(export);
  var target=PathOf("target.db");using var db=new CatalogRepository(target);
  using var raw=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=target,Pooling=false}.ToString());raw.Open();CatalogRepository.AddOrdinalCollation(raw);using var command=raw.CreateCommand();command.CommandText="CREATE TRIGGER fail_group BEFORE INSERT ON publication_group_members BEGIN SELECT RAISE(ABORT,'synthetic projection failure'); END;";command.ExecuteNonQuery();
  Assert.NotEmpty(db.ImportJson(export).Errors);Assert.Empty(db.Search());Assert.Empty(db.GetImageSets());Assert.Empty(db.GetPublicationGroups());
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void InvalidGraphPreservesExistingMediaSetsGroupsAndLocalHierarchy(bool encrypted)
 {
  var target=PathOf("target.db");using var key=Prepare(target,encrypted);using var db=new CatalogRepository(target,key);var fixture=Populate(db);
  var invalid=PathOf("invalid.json");File.WriteAllText(invalid,JsonSerializer.Serialize(new {format="flixypix-catalog",version=5,items=new[]{fixture.Video with {Title="Must roll back"}},
   imageSets=new[]{fixture.Set with {Name="Must roll back"}},publicationGroups=new[]{fixture.Group with {Name="Must roll back"}},tagHierarchies=new[]{new TagHierarchy{Edges=[new(new("","Child"),new("","Root"))]}}},CatalogRepository.Json));
  Assert.NotEmpty(db.ImportJson(invalid).Errors);Assert.Equal(fixture.Video.Title,db.GetMedia(fixture.Video.Id)!.Title);Assert.Equal(fixture.Set.Name,db.GetImageSet(fixture.Set.Id)!.Name);Assert.Equal(fixture.Group.Name,db.GetPublicationGroup(fixture.Group.Id)!.Name);Assert.Equal(new[]{new TagParentEdge(new("","Root"),new("","Child"))},db.GetTagHierarchy());
 }
 [Fact]
 public void CancellationLeavesOrganizationAndLaterImportsReusable()
 {
  using var source=new CatalogRepository(PathOf("source.db"));var fixture=Populate(source);var export=PathOf("portable.json");source.Export(export);using var db=new CatalogRepository(PathOf("target.db"));using var stop=new CancellationTokenSource();stop.Cancel();
  Assert.ThrowsAny<OperationCanceledException>(()=>db.ImportJson(export,stop.Token));Assert.Empty(db.Search());Assert.Empty(db.GetImageSets());Assert.Empty(db.GetPublicationGroups());
  Assert.Empty(db.ImportJson(export).Errors);Assert.NotNull(db.GetImageSet(fixture.Set.Id));
 }
 [Fact]
 public void OrganizerSectionsRequireFormat5AndFutureVersionsAreRejected()
 {
  using var db=new CatalogRepository(PathOf("target.db"));var input=PathOf("bad-version.json");
  var retained=db.Upsert(new(){Path=PathOf("keep.png"),Title="Keep",Kind=MediaKind.Image});
  foreach(var section in new[]{"imageSets","publicationGroups","tagHierarchies","stashSources","stashIdentities","stashProvenance","stashProvenanceChunks","stashBindings"})
  {
   var payload=new Dictionary<string,object>{["format"]="flixypix-catalog",["version"]=4,["items"]=new[]{retained with{Title="Must roll back"}},[section]=new[]{new{}}};
   File.WriteAllText(input,JsonSerializer.Serialize(payload,CatalogRepository.Json));var result=db.ImportJson(input);
   Assert.Contains(result.Errors,error=>error=="Organizer sections require catalog format5.");Assert.Equal("Keep",db.GetMedia(retained.Id)!.Title);Assert.Single(db.Search());Assert.Empty(db.GetImageSets());Assert.Empty(db.GetPublicationGroups());Assert.Empty(db.GetTagHierarchy());Assert.Empty(db.GetStashSources());
  }
  File.WriteAllText(input,JsonSerializer.Serialize(new{format="flixypix-catalog",version=6,items=Array.Empty<MediaItem>()},CatalogRepository.Json));
  Assert.Contains(db.ImportJson(input).Errors,error=>error.StartsWith("Unsupported or missing portable catalog format/version",StringComparison.Ordinal));Assert.Equal("Keep",db.GetMedia(retained.Id)!.Title);
 }
}
