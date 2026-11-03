using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class PublicationGroupMaintenanceTests:IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-group-check-"+Guid.NewGuid().ToString("N"));
 private string Database=>Path.Combine(root,"catalog.db");
 public PublicationGroupMaintenanceTests()=>Directory.CreateDirectory(root);
 public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
 private CatalogKey? Prepare(bool encrypted)
 {
  using(var empty=new CatalogRepository(Database)){}
  if(!encrypted)return null;CatalogEncryption.EnableEncryption(Database,"synthetic-group-check-password");return CatalogEncryption.Unlock(Database,"synthetic-group-check-password");
 }
 private MediaItem Media(CatalogRepository db,string name,MediaKind kind)
 {var path=Path.Combine(root,name);File.WriteAllBytes(path,[1,2,3]);return db.Upsert(new(){Path=path,Kind=kind});}
 private object? Sql(CatalogKey? key,string text,params(string Name,object Value)[] parameters)
 {
  var builder=CatalogEncryption.ConnectionString(Database,key);builder.Pooling=false;
  using var connection=new SqliteConnection(builder.ToString());connection.Open();CatalogRepository.AddOrdinalCollation(connection);
  using(var foreign=connection.CreateCommand()){foreign.CommandText="PRAGMA foreign_keys=OFF";foreign.ExecuteNonQuery();}
  using var command=connection.CreateCommand();command.CommandText=text;foreach(var(name,value)in parameters)command.Parameters.AddWithValue(name,value);return command.ExecuteScalar();
 }
 private string Document(CatalogKey? key,Guid id)=>(string)Sql(key,"SELECT document FROM publication_groups WHERE id=$id",("$id",id.ToString()))!;
 private static PublicationGroupMember Video(Guid id)=>new(PublicationGroupMemberKind.Video,id);
 private static PublicationGroupMember Set(Guid id)=>new(PublicationGroupMemberKind.ImageSet,id);
 [Theory][InlineData(false)][InlineData(true)]
 public void RepairRestoresTypedReferencesFromCurrentDocumentsAndKeepsSafetyCopy(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var video=Media(db,"video.mp4",MediaKind.Video);var image=Media(db,"image.png",MediaKind.Image);
  var set=db.UpsertImageSet(new(){Id=video.Id,Name="Same ID set",MediaIds=[image.Id]});
  var first=db.UpsertPublicationGroup(new(){Name="First",Members=[Set(set.Id)]});
  var second=db.UpsertPublicationGroup(new(){Name="Second",Members=[Video(video.Id)]});
  var original=Document(key,first.Id);var orphan=Guid.NewGuid();
  Sql(key,"UPDATE publication_group_members SET image_set_id=NULL,video_id=$video WHERE group_id=$first; UPDATE publication_group_members SET position=2.5 WHERE group_id=$second; INSERT INTO publication_group_members VALUES($orphan,0,$video,NULL)",("$video",video.Id.ToString()),("$first",first.Id.ToString()),("$second",second.Id.ToString()),("$orphan",orphan.ToString()));
  var report=db.CheckCatalog(root);Assert.Equal(0,report[CatalogFindingKind.PublicationGroupDocuments].Count);Assert.Equal(3,report[CatalogFindingKind.PublicationGroupMembership].Count);Assert.Equal(1,report[CatalogFindingKind.OrphanRows].Count);
  second=db.UpsertPublicationGroup(second with {Name="Newer",Members=[Set(set.Id),Video(video.Id)]});var current=Document(key,second.Id);
  var result=db.RepairCatalog(report,new HashSet<CatalogFindingKind>{CatalogFindingKind.PublicationGroupMembership},Path.Combine(root,"backups"));
  Assert.Equal(2,result.Repaired[CatalogFindingKind.PublicationGroupMembership]);Assert.Equal(original,Document(key,first.Id));Assert.Equal(current,Document(key,second.Id));
  Assert.Equal(set.Id.ToString(),Sql(key,"SELECT image_set_id FROM publication_group_members WHERE group_id=$id",("$id",first.Id.ToString())));
  Assert.Equal(DBNull.Value,Sql(key,"SELECT video_id FROM publication_group_members WHERE group_id=$id",("$id",first.Id.ToString())));
  var after=db.CheckCatalog(root);Assert.Equal(0,after[CatalogFindingKind.PublicationGroupMembership].Count);Assert.Equal(0,after[CatalogFindingKind.OrphanRows].Count);
  var builder=CatalogEncryption.ConnectionString(result.BackupPath,key);builder.Pooling=false;using var copy=new SqliteConnection(builder.ToString());copy.Open();using var old=copy.CreateCommand();old.CommandText="SELECT video_id FROM publication_group_members WHERE group_id=$id";old.Parameters.AddWithValue("$id",first.Id.ToString());Assert.Equal(video.Id.ToString(),old.ExecuteScalar());
  Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(video.Path));Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(image.Path));
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void InvalidGroupDocumentsRemainUnchangedAndRepairFailureRollsBackAllWrites(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var video=Media(db,"video.mp4",MediaKind.Video);var image=Media(db,"image.png",MediaKind.Image);var invalid=new Dictionary<Guid,string>();
  for(var i=0;i<6;i++)
  {
   var group=db.UpsertPublicationGroup(new(){Name="Invalid "+i,Members=[Video(video.Id)]});
   var json=i switch
   {
    0=>"{broken",
    1=>JsonSerializer.Serialize(group with {Members=[Video(Guid.NewGuid())]},CatalogRepository.Json),
    2=>JsonSerializer.Serialize(group with {Members=[Video(image.Id)]},CatalogRepository.Json),
    3=>JsonSerializer.Serialize(group with {Members=[Video(video.Id),Video(video.Id)]},CatalogRepository.Json),
    4=>JsonSerializer.Serialize(group with {Members=[new((PublicationGroupMemberKind)99,video.Id)]},CatalogRepository.Json),
    _=>JsonSerializer.Serialize(group with {Members=[Set(Guid.NewGuid())]},CatalogRepository.Json)
   };
   Sql(key,"UPDATE publication_groups SET document=$json WHERE id=$id",("$json",json),("$id",group.Id.ToString()));invalid.Add(group.Id,json);
  }
  var first=db.UpsertPublicationGroup(new(){Id=Guid.Parse("00000000-0000-0000-0000-000000000001"),Name="First",Members=[Video(video.Id)]});
  var second=db.UpsertPublicationGroup(new(){Id=Guid.Parse("00000000-0000-0000-0000-000000000002"),Name="Second",Members=[Video(video.Id)]});
  Sql(key,"DELETE FROM publication_group_members WHERE group_id IN($first,$second); CREATE TRIGGER fail_group_repair BEFORE INSERT ON publication_group_members WHEN NEW.group_id='"+second.Id+"' BEGIN SELECT RAISE(ABORT,'injected group repair failure'); END;",("$first",first.Id.ToString()),("$second",second.Id.ToString()));
  var report=db.CheckCatalog(root);Assert.Equal(6,report[CatalogFindingKind.PublicationGroupDocuments].Count);Assert.False(report[CatalogFindingKind.PublicationGroupDocuments].Repairable);Assert.Equal(2,report[CatalogFindingKind.PublicationGroupMembership].Count);
  Assert.Throws<SqliteException>(()=>db.RepairCatalog(report,new HashSet<CatalogFindingKind>{CatalogFindingKind.PublicationGroupMembership},Path.Combine(root,"backups")));
  Assert.Equal(0L,Sql(key,"SELECT count(*) FROM publication_group_members WHERE group_id IN($first,$second)",("$first",first.Id.ToString()),("$second",second.Id.ToString())));
  using(var cancel=new CancellationTokenSource()){cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>db.RepairCatalog(report,new HashSet<CatalogFindingKind>{CatalogFindingKind.PublicationGroupMembership},Path.Combine(root,"backups"),cancel.Token));}
  foreach(var(id,json)in invalid)Assert.Equal(json,Document(key,id));
  Sql(key,"DROP TRIGGER fail_group_repair");db.RepairCatalog(report,new HashSet<CatalogFindingKind>{CatalogFindingKind.PublicationGroupMembership},Path.Combine(root,"backups"));
  Assert.Equal(6,db.CheckCatalog(root)[CatalogFindingKind.PublicationGroupDocuments].Count);Assert.Equal(0,db.CheckCatalog(root)[CatalogFindingKind.PublicationGroupMembership].Count);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void TargetDeletionFailureRollsBackTargetAndGroupDocuments(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var video=Media(db,"video.mp4",MediaKind.Video);var image=Media(db,"image.png",MediaKind.Image);var set=db.UpsertImageSet(new(){Name="Set",MediaIds=[image.Id]});
  var group=db.UpsertPublicationGroup(new(){Name="Group",Members=[Video(video.Id),Set(set.Id)]});var original=Document(key,group.Id);
  Sql(key,"CREATE TRIGGER fail_group_delete BEFORE INSERT ON publication_group_members BEGIN SELECT RAISE(ABORT,'injected delete failure'); END;");
  Assert.Throws<SqliteException>(()=>db.DeleteImageSet(set.Id));Assert.NotNull(db.GetImageSet(set.Id));Assert.Equal(original,Document(key,group.Id));
  Assert.Throws<SqliteException>(()=>db.Delete(video.Id));Assert.NotNull(db.GetMedia(video.Id));Assert.Equal(original,Document(key,group.Id));
  Assert.Throws<SqliteException>(()=>db.DeleteMedia([video.Id]));Assert.NotNull(db.GetMedia(video.Id));Assert.Equal(2L,Sql(key,"SELECT count(*) FROM publication_group_members"));
  Assert.True(File.Exists(video.Path));Assert.True(File.Exists(image.Path));
 }
}
