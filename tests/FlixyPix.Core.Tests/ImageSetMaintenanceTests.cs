using System.Text;
using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class ImageSetMaintenanceTests:IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-set-check-"+Guid.NewGuid().ToString("N"));
 private string Database=>Path.Combine(root,"catalog.db");
 private string Backups=>Path.Combine(root,"backups");
 public ImageSetMaintenanceTests()=>Directory.CreateDirectory(root);
 public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
 private CatalogKey? Prepare(bool encrypted)
 {
  using(var empty=new CatalogRepository(Database)){}
  if(!encrypted)return null;
  CatalogEncryption.EnableEncryption(Database,"synthetic-set-check-password");
  return CatalogEncryption.Unlock(Database,"synthetic-set-check-password");
 }
 private MediaItem Image(CatalogRepository db,string name)
 {var path=Path.Combine(root,name);File.WriteAllBytes(path,[1,2,3]);return db.Upsert(new(){Path=path,Kind=MediaKind.Image});}
 private object? Sql(CatalogKey? key,string sql,params(string Name,object Value)[] parameters)
 {
  var builder=CatalogEncryption.ConnectionString(Database,key);builder.Pooling=false;
  using var connection=new SqliteConnection(builder.ToString());connection.Open();CatalogRepository.AddOrdinalCollation(connection);
  using(var pragma=connection.CreateCommand()){pragma.CommandText="PRAGMA foreign_keys=OFF";pragma.ExecuteNonQuery();}
  using var command=connection.CreateCommand();command.CommandText=sql;
  foreach(var(name,value)in parameters)command.Parameters.AddWithValue(name,value);
  return command.ExecuteScalar();
 }
 private string Document(CatalogKey? key,Guid id)=>(string)Sql(key,"SELECT document FROM image_sets WHERE id=$id",("$id",id.ToString()))!;

 [Theory][InlineData(false)]
 public void RepairRebuildsOrderedProjectionWithoutRewritingSourceAndRetainsSafetyCopy(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var a=Image(db,"a.jpg");var b=Image(db,"b.jpg");
  var set=db.UpsertImageSet(new(){Name="Confirmed",Description="Keep exact document",MediaIds=[b.Id,a.Id],CoverId=a.Id});
  var before=Document(key,set.Id);var orphan=Guid.NewGuid();
  Sql(key,"DELETE FROM image_set_members; INSERT INTO image_set_members VALUES($set,$media,9); INSERT INTO image_set_members VALUES($orphan,$media,0)",("$set",set.Id.ToString()),("$media",a.Id.ToString()),("$orphan",orphan.ToString()));
  var report=db.CheckCatalog(root);Assert.Equal(0,report[CatalogFindingKind.ImageSetDocuments].Count);Assert.Equal(2,report[CatalogFindingKind.ImageSetMembership].Count);
  Assert.True(report[CatalogFindingKind.ImageSetMembership].Repairable);Assert.Equal(1,report[CatalogFindingKind.OrphanRows].Count);
  var result=db.RepairCatalog(report,new HashSet<CatalogFindingKind>{CatalogFindingKind.ImageSetMembership},Backups);
  Assert.True(File.Exists(result.BackupPath));Assert.Equal(2,result.Repaired[CatalogFindingKind.ImageSetMembership]);Assert.Equal(before,Document(key,set.Id));
  Assert.Equal(b.Id.ToString(),Sql(key,"SELECT media_id FROM image_set_members WHERE set_id=$set AND position=0",("$set",set.Id.ToString())));
  Assert.Equal(a.Id.ToString(),Sql(key,"SELECT media_id FROM image_set_members WHERE set_id=$set AND position=1",("$set",set.Id.ToString())));
  var after=db.CheckCatalog(root);Assert.Equal(0,after[CatalogFindingKind.ImageSetMembership].Count);Assert.Equal(0,after[CatalogFindingKind.OrphanRows].Count);
  var builder=CatalogEncryption.ConnectionString(result.BackupPath,key);builder.Pooling=false;
  using var backup=new SqliteConnection(builder.ToString());backup.Open();using var old=backup.CreateCommand();old.CommandText="SELECT count(*) FROM image_set_members WHERE position=9";Assert.Equal(1L,old.ExecuteScalar());
  Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(a.Path));Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(b.Path));
 }

 [Theory][InlineData(false)]
 public void InvalidDocumentsStayUntouchedAndRepairUsesCurrentValidDocuments(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var a=Image(db,"a.jpg");var b=Image(db,"b.jpg");var video=db.Upsert(new(){Path=Path.Combine(root,"video.mp4"),Kind=MediaKind.Video});
  var invalid=new Dictionary<Guid,string>();
  for(var i=0;i<5;i++)
  {
   var set=db.UpsertImageSet(new(){Name="Invalid "+i,MediaIds=[a.Id]});
   var json=i switch
   {
    0=>"{broken",
    1=>JsonSerializer.Serialize(set with {MediaIds=[Guid.NewGuid()]},CatalogRepository.Json),
    2=>JsonSerializer.Serialize(set with {MediaIds=[video.Id]},CatalogRepository.Json),
    3=>JsonSerializer.Serialize(set with {MediaIds=[a.Id,a.Id]},CatalogRepository.Json),
    _=>JsonSerializer.Serialize(set with {CoverId=b.Id},CatalogRepository.Json)
   };
   Sql(key,"UPDATE image_sets SET document=$json WHERE id=$id",("$json",json),("$id",set.Id.ToString()));invalid.Add(set.Id,json);
  }
  var valid=db.UpsertImageSet(new(){Name="Valid",MediaIds=[a.Id,b.Id],CoverId=a.Id});
  Sql(key,"DELETE FROM image_set_members WHERE set_id=$id",("$id",valid.Id.ToString()));
  var report=db.CheckCatalog(root);Assert.Equal(5,report[CatalogFindingKind.ImageSetDocuments].Count);Assert.False(report[CatalogFindingKind.ImageSetDocuments].Repairable);Assert.Equal(1,report[CatalogFindingKind.ImageSetMembership].Count);
  valid=db.UpsertImageSet(valid with {Name="Newer",MediaIds=[b.Id,a.Id],CoverId=b.Id});
  Sql(key,"UPDATE image_set_members SET position=2.5 WHERE set_id=$id AND position=1",("$id",valid.Id.ToString()));
  var current=Document(key,valid.Id);
  db.RepairCatalog(report,new HashSet<CatalogFindingKind>{CatalogFindingKind.ImageSetMembership},Backups);
  Assert.Equal(current,Document(key,valid.Id));Assert.Equal(b.Id.ToString(),Sql(key,"SELECT media_id FROM image_set_members WHERE set_id=$id AND position=0",("$id",valid.Id.ToString())));
  foreach(var(id,json)in invalid)Assert.Equal(json,Document(key,id));
  var after=db.CheckCatalog(root);Assert.Equal(5,after[CatalogFindingKind.ImageSetDocuments].Count);Assert.Equal(0,after[CatalogFindingKind.ImageSetMembership].Count);
 }

 [Theory][InlineData(false)]
 public void RepairFailureRollsBackAllProjectionWrites(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var a=Image(db,"a.jpg");
  var first=db.UpsertImageSet(new(){Id=Guid.Parse("00000000-0000-0000-0000-000000000001"),Name="First",MediaIds=[a.Id]});
  var second=db.UpsertImageSet(new(){Id=Guid.Parse("00000000-0000-0000-0000-000000000002"),Name="Second",MediaIds=[a.Id]});
  var firstJson=Document(key,first.Id);var secondJson=Document(key,second.Id);
  Sql(key,"DELETE FROM image_set_members; CREATE TRIGGER fail_repair BEFORE INSERT ON image_set_members WHEN NEW.set_id='"+second.Id+"' BEGIN SELECT RAISE(ABORT,'injected repair failure'); END;");
  var report=db.CheckCatalog(root);
  Assert.Throws<SqliteException>(()=>db.RepairCatalog(report,new HashSet<CatalogFindingKind>{CatalogFindingKind.ImageSetMembership},Backups));
  Assert.Equal(0L,Sql(key,"SELECT count(*) FROM image_set_members"));Assert.Equal(firstJson,Document(key,first.Id));Assert.Equal(secondJson,Document(key,second.Id));
  using var cancelled=new CancellationTokenSource();cancelled.Cancel();
  Assert.Throws<OperationCanceledException>(()=>db.RepairCatalog(report,new HashSet<CatalogFindingKind>{CatalogFindingKind.ImageSetMembership},Backups,cancelled.Token));
  Assert.Equal(0L,Sql(key,"SELECT count(*) FROM image_set_members"));
 }

 [Theory][InlineData(false)]
 public void MergeAndBulkDeletionMaintainMembershipEvenWhenProjectionWasIncomplete(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var keep=Image(db,"Caf\u00e9.jpg");db.Upsert(keep with {AddedAt=DateTimeOffset.UtcNow.AddDays(-2)});
  var duplicate=db.Upsert(new(){Path=keep.Path.Normalize(NormalizationForm.FormD),Kind=MediaKind.Image});var other=Image(db,"other.jpg");
  var set=db.UpsertImageSet(new(){Name="Ordered",MediaIds=[other.Id,duplicate.Id,keep.Id],CoverId=duplicate.Id});
  var shared=db.UpsertImageSet(new(){Name="Shared",MediaIds=[duplicate.Id],CoverId=duplicate.Id});
  Sql(key,"DELETE FROM image_set_members WHERE media_id=$id",("$id",duplicate.Id.ToString()));
  var report=db.CheckCatalog(root);Assert.Equal(1,report[CatalogFindingKind.SamePath].Count);
  db.RepairCatalog(report,new HashSet<CatalogFindingKind>{CatalogFindingKind.SamePath},Backups);
  Assert.Equal([other.Id,keep.Id],db.GetImageSet(set.Id)!.MediaIds);Assert.Equal(keep.Id,db.GetImageSet(set.Id)!.CoverId);
  Assert.Equal([keep.Id],db.GetImageSet(shared.Id)!.MediaIds);
  Sql(key,"DELETE FROM image_set_members");
  db.Delete(other.Id);Assert.Equal([keep.Id],db.GetImageSet(set.Id)!.MediaIds);
  db.DeleteMedia([keep.Id]);
  Assert.All(db.GetImageSets(),s=>{Assert.Empty(s.MediaIds);Assert.Null(s.CoverId);});Assert.Equal(0L,Sql(key,"SELECT count(*) FROM image_set_members"));
  Assert.True(File.Exists(keep.Path));Assert.True(File.Exists(other.Path));
 }
}
