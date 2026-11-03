using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class PublicationGroupTests:IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-groups-"+Guid.NewGuid().ToString("N"));
 private string Database=>Path.Combine(root,"catalog.db");
 public PublicationGroupTests()=>Directory.CreateDirectory(root);
 public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
 private CatalogKey? Prepare(bool encrypted)
 {
  using(var empty=new CatalogRepository(Database)){}
  if(!encrypted)return null;
  CatalogEncryption.EnableEncryption(Database,"synthetic-publication-password");return CatalogEncryption.Unlock(Database,"synthetic-publication-password");
 }
 private MediaItem Media(CatalogRepository db,string name,MediaKind kind)
 {var path=Path.Combine(root,name);File.WriteAllBytes(path,[1,2,3]);return db.Upsert(new(){Path=path,Kind=kind});}
 private static PublicationGroupMember Video(Guid id)=>new(PublicationGroupMemberKind.Video,id);
 private static PublicationGroupMember Set(Guid id)=>new(PublicationGroupMemberKind.ImageSet,id);
 private object? Sql(CatalogKey? key,string text)
 {
  var builder=CatalogEncryption.ConnectionString(Database,key);builder.Pooling=false;
  using var connection=new SqliteConnection(builder.ToString());connection.Open();CatalogRepository.AddOrdinalCollation(connection);
  using var command=connection.CreateCommand();command.CommandText=text;return command.ExecuteScalar();
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void GroupSelectionPersistsAndDeletedSelectionIsSanitized(bool encrypted)
 {
  using var key=Prepare(encrypted);Guid id;
  using(var db=new CatalogRepository(Database,key))
  {
   id=db.UpsertPublicationGroup(new(){Name="Selected"}).Id;var state=new ViewState{View="publication-groups",PublicationGroupId=id};
   db.SaveViewState(state);Assert.Equal(id,db.Sanitize(state,out var removed).PublicationGroupId);Assert.Empty(removed);
   Assert.Equal(id,state.Restricted(RestoreScope.View).PublicationGroupId);Assert.False(state.SameView(state with {PublicationGroupId=null}));
  }
  using var reopened=new CatalogRepository(Database,key);var restored=reopened.GetViewState()!;Assert.Equal(id,restored.PublicationGroupId);Assert.Equal("publication-groups",restored.View);
  reopened.DeletePublicationGroup(id);var clean=reopened.Sanitize(restored,out var dropped);Assert.Null(clean.PublicationGroupId);Assert.Equal("publication-groups",clean.View);Assert.NotEmpty(dropped);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void Schema20MigrationRetainsSetsMediaAndDrafts(bool encrypted)
 {
  using var key=Prepare(encrypted);Guid videoId,setId;MetadataDraft draft;
  using(var db=new CatalogRepository(Database,key))
  {
   var video=Media(db,"video.mp4",MediaKind.Video);videoId=video.Id;
   video=db.Upsert(video with {Title="Preserved",Rating=9,ReviewState=ReviewState.Reviewed});
   var image=Media(db,"image.jpg",MediaKind.Image);setId=db.UpsertImageSet(new(){Name="Preserved set",MediaIds=[image.Id],CoverId=image.Id}).Id;
   draft=new(video.Id,"Unsaved","","Keep draft","",MetadataValues.From(video));db.SaveMetadataDrafts([draft]);
  }
  Sql(key,"DROP TABLE publication_group_members; DROP TABLE publication_groups; PRAGMA user_version=20;");
  using var upgraded=new CatalogRepository(Database,key);Assert.True(File.Exists(upgraded.MigrationBackupPath));Assert.Empty(upgraded.GetPublicationGroups());
  Assert.Single(upgraded.GetImageSet(setId)!.MediaIds);Assert.Equal(draft,Assert.Single(upgraded.GetMetadataDrafts()));
  Assert.Equal(ReviewState.Reviewed,upgraded.GetMedia(videoId)!.ReviewState);Assert.Equal(9,upgraded.GetMedia(videoId)!.Rating);
  Assert.Equal([Video(videoId),Set(setId)],upgraded.UpsertPublicationGroup(new(){Name="New group",Members=[Video(videoId),Set(setId)]}).Members);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void TypedOrderSharedReferencesAndDeletionSurviveReopen(bool encrypted)
 {
  using var key=Prepare(encrypted);Guid groupId,sharedId,videoId,secondId,setId;string videoPath,imagePath;
  using(var db=new CatalogRepository(Database,key))
  {
   var video=Media(db,"video.mp4",MediaKind.Video);videoId=video.Id;videoPath=video.Path;
   var second=Media(db,"second.mp4",MediaKind.Video);secondId=second.Id;
   var image=Media(db,"image.png",MediaKind.Image);imagePath=image.Path;
   // Equal GUIDs in different target tables must remain distinct typed references.
   var set=db.UpsertImageSet(new(){Id=video.Id,Name="Images",MediaIds=[image.Id]});setId=set.Id;
   var group=db.UpsertPublicationGroup(new(){Name="Publication",Description="Description",Members=[Set(set.Id),Video(second.Id),Video(video.Id)]});groupId=group.Id;
   sharedId=db.UpsertPublicationGroup(new(){Name="Shared",Members=[Video(video.Id),Set(set.Id)]}).Id;
   Assert.Throws<ArgumentException>(()=>db.UpsertPublicationGroup(group with {Members=[Video(image.Id)]}));
   Assert.Throws<ArgumentException>(()=>db.UpsertPublicationGroup(group with {Members=[Set(second.Id)]}));
   Assert.Throws<ArgumentException>(()=>db.UpsertPublicationGroup(group with {Members=[Video(video.Id),Video(video.Id)]}));
   Assert.Throws<ArgumentException>(()=>db.UpsertPublicationGroup(group with {Members=[new((PublicationGroupMemberKind)99,video.Id)]}));
   Assert.Throws<ArgumentException>(()=>db.Upsert(video with {Kind=MediaKind.Audio}));
  }
  using var reopened=new CatalogRepository(Database,key);var restored=reopened.GetPublicationGroup(groupId)!;
  Assert.Equal([Set(setId),Video(secondId),Video(videoId)],restored.Members);Assert.Equal("Description",restored.Description);
  reopened.DeleteImageSet(setId);Assert.Equal([Video(secondId),Video(videoId)],reopened.GetPublicationGroup(groupId)!.Members);
  Assert.Equal([Video(videoId)],reopened.GetPublicationGroup(sharedId)!.Members);
  reopened.DeleteMedia([videoId]);Assert.Equal([Video(secondId)],reopened.GetPublicationGroup(groupId)!.Members);Assert.Empty(reopened.GetPublicationGroup(sharedId)!.Members);
  reopened.DeletePublicationGroup(groupId);Assert.NotNull(reopened.GetMedia(secondId));Assert.True(File.Exists(videoPath));Assert.True(File.Exists(imagePath));
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void MergeVersionsRedirectsAndDeduplicatesOnlyVideoReferences(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var a=Media(db,"a.mp4",MediaKind.Video);var b=Media(db,"b.mp4",MediaKind.Video);var image=Media(db,"image.jpg",MediaKind.Image);
  var set=db.UpsertImageSet(new(){Id=b.Id,Name="Same ID different type",MediaIds=[image.Id]});
  var group=db.UpsertPublicationGroup(new(){Name="Merged",Members=[Video(b.Id),Set(set.Id),Video(a.Id)]});
  var merged=db.MergeVersions(a.Id,[b.Id]);
  Assert.Equal([Video(a.Id),Set(set.Id)],db.GetPublicationGroup(group.Id)!.Members);Assert.Null(db.GetMedia(b.Id));Assert.NotNull(db.GetImageSet(set.Id));
  Assert.Single(merged.Files);Assert.True(File.Exists(b.Path));
  db.Delete(a.Id);Assert.Equal([Set(set.Id)],db.GetPublicationGroup(group.Id)!.Members);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void SameFileMaintenanceMergeKeepsTypedGroupReferences(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var a=Media(db,"Caf\u00e9.mp4",MediaKind.Video);db.Upsert(a with {AddedAt=DateTimeOffset.UtcNow.AddDays(-2)});
  var b=db.Upsert(new(){Path=a.Path.Normalize(System.Text.NormalizationForm.FormD),Kind=MediaKind.Video});
  var image=Media(db,"image.jpg",MediaKind.Image);var set=db.UpsertImageSet(new(){Id=b.Id,Name="Same ID set",MediaIds=[image.Id]});
  var group=db.UpsertPublicationGroup(new(){Name="Publication",Members=[Video(b.Id),Set(set.Id),Video(a.Id)]});
  Sql(key,"DELETE FROM publication_group_members");
  var report=db.CheckCatalog(root);Assert.Equal(1,report[CatalogFindingKind.SamePath].Count);
  db.RepairCatalog(report,new HashSet<CatalogFindingKind>{CatalogFindingKind.SamePath},Path.Combine(root,"backups"));
  Assert.Equal([Video(a.Id),Set(set.Id)],db.GetPublicationGroup(group.Id)!.Members);Assert.Null(db.GetMedia(b.Id));Assert.NotNull(db.GetImageSet(set.Id));
  Assert.Equal(2L,Sql(key,"SELECT count(*) FROM publication_group_members"));Assert.True(File.Exists(a.Path));
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void SecondMembershipFailureRollsBackDocumentAndFreshAddKeepsCurrentChanges(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var a=Media(db,"a.mp4",MediaKind.Video);var b=Media(db,"b.mp4",MediaKind.Video);var image=Media(db,"image.jpg",MediaKind.Image);
  var set=db.UpsertImageSet(new(){Name="Set",MediaIds=[image.Id]});
  var stale=db.UpsertPublicationGroup(new(){Name="Old",Members=[Video(a.Id)]});
  db.UpsertPublicationGroup(stale with {Name="Newer",Members=[Set(set.Id),Video(a.Id)]});
  var saved=db.AddPublicationGroupMembers(stale.Id,[Video(b.Id),Video(a.Id)]);
  Assert.Equal("Newer",saved.Name);Assert.Equal([Set(set.Id),Video(a.Id),Video(b.Id)],saved.Members);
  Sql(key,"CREATE TRIGGER fail_group BEFORE INSERT ON publication_group_members WHEN NEW.position=1 BEGIN SELECT RAISE(ABORT,'injected group failure'); END;");
  Assert.Throws<SqliteException>(()=>db.UpsertPublicationGroup(saved with {Name="Must rollback",Members=[Video(b.Id),Set(set.Id)]}));
  Assert.Equal(saved.Members,db.GetPublicationGroup(saved.Id)!.Members);Assert.Equal("Newer",db.GetPublicationGroup(saved.Id)!.Name);
  Assert.Equal(3L,Sql(key,"SELECT count(*) FROM publication_group_members"));
 }
 [Theory][InlineData(false)][InlineData(true)]
 public async Task BackupRestoreRetainsMixedGroupsAndMediaSources(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var a=Media(db,"a.mp4",MediaKind.Video);var image=Media(db,"image.jpg",MediaKind.Image);
  var set=db.UpsertImageSet(new(){Name="Set",MediaIds=[image.Id],CoverId=image.Id});
  var group=db.UpsertPublicationGroup(new(){Name="Mixed",Description="Preserved",Members=[Set(set.Id),Video(a.Id)]});
  db.SaveViewState(new(){View="publication-groups",PublicationGroupId=group.Id});
  var package=Path.Combine(root,"backup.zip");await db.CreateBackupAsync(package,true);
  var restored=await CatalogRepository.RestoreBackupAsync(package,Path.Combine(root,"restore"),secret:encrypted?"synthetic-publication-password":null);
  using var restoredKey=encrypted?CatalogEncryption.Unlock(restored.DatabasePath,"synthetic-publication-password"):null;
  using var destination=new CatalogRepository(restored.DatabasePath,restoredKey);
  var after=Assert.Single(destination.GetPublicationGroups());Assert.Equal(group.Members,after.Members);Assert.Equal(group.Description,after.Description);
  Assert.Equal(group.Id,destination.GetViewState()!.PublicationGroupId);
  Assert.Equal(image.Id,destination.GetImageSet(set.Id)!.CoverId);Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(destination.GetMedia(a.Id)!.Path));Assert.True(File.Exists(a.Path));
 }
}
