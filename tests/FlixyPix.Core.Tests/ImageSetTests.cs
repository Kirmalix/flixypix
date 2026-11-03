using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;
namespace FlixyPix.Core.Tests;
public sealed class ImageSetTests:IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-imagesets-"+Guid.NewGuid().ToString("N"));
 [Theory][InlineData(false)][InlineData(true)]
 public void PartialMembershipOverlapIsReportedWithoutBlockingConfirmedManyToManySets(bool encrypted)
 {
  Directory.CreateDirectory(root);var path=Path.Combine(root,"overlaps.db");using(var empty=new CatalogRepository(path)){}
  if(encrypted)CatalogEncryption.EnableEncryption(path,"synthetic-set-overlaps-password");
  using var key=encrypted?CatalogEncryption.Unlock(path,"synthetic-set-overlaps-password"):null;
  using var db=new CatalogRepository(path,key);
  var a=db.Upsert(new(){Path=Path.Combine(root,"folder","a.jpg"),Kind=MediaKind.Image});
  var b=db.Upsert(new(){Path=Path.Combine(root,"folder","b.jpg"),Kind=MediaKind.Image});
  var c=db.Upsert(new(){Path=Path.Combine(root,"elsewhere","c.jpg"),Kind=MediaKind.Image});
  var existing=db.UpsertImageSet(new(){Name="Shared selection",MediaIds=[a.Id,c.Id],CoverId=c.Id});
  var proposal=Assert.Single(db.GetImageSetProposals(),p=>p.MediaIds.Count==2);
  Assert.Empty(proposal.ExistingSetIds);Assert.Equal([existing.Id],proposal.OverlappingSetIds);
  var created=Assert.Single(db.ApplyImageSetProposals([new(proposal,"Confirmed folder")]));
  Assert.Equal([a.Id,b.Id],created.MediaIds);Assert.Equal([a.Id,c.Id],db.GetImageSet(existing.Id)!.MediaIds);Assert.Equal(c.Id,db.GetImageSet(existing.Id)!.CoverId);
  var refreshed=Assert.Single(db.GetImageSetProposals(),p=>p.MediaIds.Count==2);
  Assert.Equal([created.Id],refreshed.ExistingSetIds);Assert.Equal([existing.Id],refreshed.OverlappingSetIds);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void CurrentMembershipAndSavedSelectionSurviveStaleMenuAndDeletedSet(bool encrypted)
 {
  Directory.CreateDirectory(root);var path=Path.Combine(root,"state.db");using(var empty=new CatalogRepository(path)){}
  if(encrypted)CatalogEncryption.EnableEncryption(path,"synthetic-set-state-password");
  using var key=encrypted?CatalogEncryption.Unlock(path,"synthetic-set-state-password"):null;
  Guid id;
  using(var db=new CatalogRepository(path,key))
  {
   var a=db.Upsert(new(){Path=Path.Combine(root,"a.jpg"),Kind=MediaKind.Image});
   var b=db.Upsert(new(){Path=Path.Combine(root,"b.jpg"),Kind=MediaKind.Image});
   var c=db.Upsert(new(){Path=Path.Combine(root,"c.jpg"),Kind=MediaKind.Image});
   var old=db.UpsertImageSet(new(){Name="Set",MediaIds=[a.Id]});id=old.Id;
   db.UpsertImageSet(old with {Name="Updated",MediaIds=[a.Id,b.Id],CoverId=b.Id});
   var saved=db.AddImageSetMembers(old.Id,[c.Id,a.Id]);
   Assert.Equal([a.Id,b.Id,c.Id],saved.MediaIds);Assert.Equal(b.Id,saved.CoverId);Assert.Equal("Updated",saved.Name);
   Assert.Throws<ArgumentException>(()=>db.AddImageSetMembers(id,[Guid.NewGuid()]));
   Assert.Equal(saved.MediaIds,db.GetImageSet(id)!.MediaIds);
   var state=new ViewState{View="image-sets",ImageSetId=id};db.SaveViewState(state);
   Assert.Equal(id,db.Sanitize(state,out var dropped).ImageSetId);Assert.Empty(dropped);
   Assert.Equal(id,state.Restricted(RestoreScope.View).ImageSetId);
   Assert.False(state.SameView(state with {ImageSetId=null}));
  }
  using var reopened=new CatalogRepository(path,key);var restored=reopened.GetViewState()!;
  Assert.Equal(id,restored.ImageSetId);Assert.Equal("image-sets",restored.View);
  reopened.DeleteImageSet(id);
  var sanitized=reopened.Sanitize(restored,out var removed);Assert.Null(sanitized.ImageSetId);Assert.Equal("image-sets",sanitized.View);Assert.NotEmpty(removed);
  Assert.Throws<InvalidOperationException>(()=>reopened.AddImageSetMembers(id,[]));Assert.Null(reopened.GetImageSet(id));
 }
 public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
 [Theory][InlineData(false)][InlineData(true)]
 public async Task IndependentRepositoriesAddingMembersDoNotLoseEachOthersChanges(bool encrypted)
 {
  Directory.CreateDirectory(root);var path=Path.Combine(root,"concurrent.db");using(var empty=new CatalogRepository(path)){}
  if(encrypted)CatalogEncryption.EnableEncryption(path,"synthetic-set-concurrency-password");
  using var key=encrypted?CatalogEncryption.Unlock(path,"synthetic-set-concurrency-password"):null;
  using var first=new CatalogRepository(path,key);using var second=new CatalogRepository(path,key);
  var ids=Enumerable.Range(0,20).Select(i=>first.Upsert(new(){Path=Path.Combine(root,i+".jpg"),Kind=MediaKind.Image}).Id).ToArray();
  var set=first.UpsertImageSet(new(){Name="Concurrent"});
  await Task.WhenAll(Task.Run(()=>{foreach(var id in ids.Take(10))first.AddImageSetMembers(set.Id,[id]);}),Task.Run(()=>{foreach(var id in ids.Skip(10))second.AddImageSetMembers(set.Id,[id]);}));
  Assert.True(first.GetImageSet(set.Id)!.MediaIds.ToHashSet().SetEquals(ids));
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void Schema19MigrationPreservesMediaAndDraftsAndStartsWithEmptySets(bool encrypted)
 {
  Directory.CreateDirectory(root);var path=Path.Combine(root,"schema19.db");using(var empty=new CatalogRepository(path)){}
  if(encrypted)CatalogEncryption.EnableEncryption(path,"synthetic-set-migration-password");
  using var key=encrypted?CatalogEncryption.Unlock(path,"synthetic-set-migration-password"):null;
  MediaItem original;MetadataDraft draft;
  using(var source=new CatalogRepository(path,key))
  {
   original=source.Upsert(new(){Path=Path.Combine(root,"preserved.jpg"),Kind=MediaKind.Image,Title="Preserved",Rating=9,Tags=["Keep"],ReviewState=ReviewState.Reviewed});
   draft=new(original.Id,"Unsaved","","Draft remains","",MetadataValues.From(original));source.SaveMetadataDrafts([draft]);
  }
  var builder=CatalogEncryption.ConnectionString(path,key);builder.Pooling=false;
  using(var sql=new SqliteConnection(builder.ToString()))
  {
   sql.Open();CatalogRepository.AddOrdinalCollation(sql);using var command=sql.CreateCommand();
   command.CommandText="DROP TABLE image_set_members; DROP TABLE image_sets; PRAGMA user_version=19;";command.ExecuteNonQuery();
  }
  using var migrated=new CatalogRepository(path,key);Assert.NotNull(migrated.MigrationBackupPath);Assert.True(File.Exists(migrated.MigrationBackupPath));
  Assert.Empty(migrated.GetImageSets());var after=migrated.GetMedia(original.Id)!;
  Assert.Equal(original.Title,after.Title);Assert.Equal(original.Rating,after.Rating);Assert.Equal(original.Tags,after.Tags);Assert.Equal(original.ReviewState,after.ReviewState);
  Assert.Equal(draft,Assert.Single(migrated.GetMetadataDrafts()));
  Assert.Single(migrated.UpsertImageSet(new(){Name="New set",MediaIds=[after.Id]}).MediaIds);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public async Task BackupRestorePreservesOrderedSharedMembersCoverAndSourceBytes(bool encrypted)
 {
  Directory.CreateDirectory(root);var path=Path.Combine(root,"backup-source.db");using(var empty=new CatalogRepository(path)){}
  const string secret="synthetic-set-backup-password";
  if(encrypted)CatalogEncryption.EnableEncryption(path,secret);
  using var key=encrypted?CatalogEncryption.Unlock(path,secret):null;
  using var source=new CatalogRepository(path,key);
  var aPath=Path.Combine(root,"a.jpg");var bPath=Path.Combine(root,"b.jpg");File.WriteAllBytes(aPath,[1,2,3]);File.WriteAllBytes(bPath,[4,5,6]);
  var a=source.Upsert(new(){Path=aPath,Kind=MediaKind.Image});var b=source.Upsert(new(){Path=bPath,Kind=MediaKind.Image});
  var set=source.UpsertImageSet(new(){Name="Ordered",Description="Preserved description",MediaIds=[b.Id,a.Id],CoverId=a.Id});
  source.UpsertImageSet(new(){Name="Shared",MediaIds=[a.Id]});source.SaveViewState(new(){View="image-sets",ImageSetId=set.Id});
  var package=Path.Combine(root,"sets.backup");await source.CreateBackupAsync(package,true);
  var restored=await CatalogRepository.RestoreBackupAsync(package,Path.Combine(root,"restored"),secret:encrypted?secret:null);
  using var restoredKey=encrypted?CatalogEncryption.Unlock(restored.DatabasePath,secret):null;
  using var destination=new CatalogRepository(restored.DatabasePath,restoredKey);
  Assert.Equal(2,destination.GetImageSets().Count);var copy=destination.GetImageSet(set.Id)!;
  Assert.Equal(set.MediaIds,copy.MediaIds);Assert.Equal(set.CoverId,copy.CoverId);Assert.Equal(set.Description,copy.Description);
  Assert.Equal(set.Id,destination.GetViewState()!.ImageSetId);
  Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(destination.GetMedia(a.Id)!.Path));Assert.Equal(new byte[]{4,5,6},File.ReadAllBytes(destination.GetMedia(b.Id)!.Path));
  Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(aPath));Assert.Equal(new byte[]{4,5,6},File.ReadAllBytes(bPath));
  await Assert.ThrowsAsync<IOException>(()=>CatalogRepository.RestoreBackupAsync(package,Path.Combine(root,"restored"),secret:encrypted?secret:null));
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void FolderAndArchiveProposalsRequireSelectionAndRejectStaleSources(bool encrypted)
 {
  Directory.CreateDirectory(root);var path=Path.Combine(root,"proposals.db");using(var empty=new CatalogRepository(path)){}
  if(encrypted)CatalogEncryption.EnableEncryption(path,"synthetic-set-proposals-password");
  using var key=encrypted?CatalogEncryption.Unlock(path,"synthetic-set-proposals-password"):null;
  using var db=new CatalogRepository(path,key);
  var a=db.Upsert(new(){Path=Path.Combine(root,"pages","a.jpg"),Kind=MediaKind.Image});
  var b=db.Upsert(new(){Path=Path.Combine(root,"pages","b.jpg"),Kind=MediaKind.Image});
  var archivePath=Path.Combine(root,"source.zip");
  var x=db.Upsert(new(){Path=Path.Combine(root,"cache","x.jpg"),Kind=MediaKind.Image,Extra=new(){["archivePath"]=archivePath,["entryName"]="b.jpg"}});
  var y=db.Upsert(new(){Path=Path.Combine(root,"other-cache","y.jpg"),Kind=MediaKind.Image,Extra=new(){["archivePath"]=archivePath,["entryName"]="a.jpg"}});
  var otherArchivePath=Path.Combine(root,"another.zip");
  var z=db.Upsert(new(){Path=Path.Combine(root,"third-cache","a.jpg"),Kind=MediaKind.Image,Extra=new(){["archivePath"]=otherArchivePath,["entryName"]="a.jpg"}});
  db.Upsert(new(){Path=Path.Combine(root,"pages","c.mp4"),Kind=MediaKind.Video});
  var proposals=db.GetImageSetProposals();Assert.Equal(3,proposals.Count);Assert.Empty(db.GetImageSets());
  var folder=Assert.Single(proposals,p=>!p.IsArchive);var archive=Assert.Single(proposals,p=>p.SourcePath==archivePath);
  Assert.Equal([z.Id],Assert.Single(proposals,p=>p.SourcePath==otherArchivePath).MediaIds);
  Assert.Equal([a.Id,b.Id],folder.MediaIds);Assert.Equal([y.Id,x.Id],archive.MediaIds);
  Assert.Empty(db.ApplyImageSetProposals([]));
  using(var cancel=new CancellationTokenSource()){cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>db.ApplyImageSetProposals([new(folder,"Cancelled")],cancel.Token));}
  db.Upsert(a with {Rating=8}); // Unrelated metadata changes do not stale the source proposal.
  var saved=Assert.Single(db.ApplyImageSetProposals([new(folder,"Selected folder")]));Assert.Equal("Selected folder",saved.Name);
  Assert.Equal([a.Id,b.Id],saved.MediaIds);Assert.Single(db.GetImageSets());
  var refreshed=db.GetImageSetProposals();Assert.Equal([saved.Id],refreshed.Single(p=>!p.IsArchive).ExistingSetIds);
  db.Upsert(b with {Path=Path.Combine(root,"pages","c.jpg")});
  Assert.Throws<InvalidOperationException>(()=>db.ApplyImageSetProposals([new(archive,"Must rollback"),new(refreshed.Single(p=>!p.IsArchive),"Stale")]));
  Assert.Single(db.GetImageSets());
  Assert.Throws<InvalidOperationException>(()=>db.ApplyImageSetProposals([new(archive with {MediaIds=[a.Id]},"Forged")]));
  db.Upsert(x with {Extra=new(){["archivePath"]=archivePath,["entryName"]="changed.jpg"}});
  Assert.Throws<InvalidOperationException>(()=>db.ApplyImageSetProposals([new(archive,"Stale archive")]));
  Assert.Single(db.GetImageSets());
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void SecondMembershipFailureRollsBackSetAndConfirmedProposalBatch(bool encrypted)
 {
  Directory.CreateDirectory(root);var path=Path.Combine(root,"rollback.db");using(var empty=new CatalogRepository(path)){}
  if(encrypted)CatalogEncryption.EnableEncryption(path,"synthetic-set-rollback-password");
  using var key=encrypted?CatalogEncryption.Unlock(path,"synthetic-set-rollback-password"):null;
  using var db=new CatalogRepository(path,key);
  var a=db.Upsert(new(){Path=Path.Combine(root,"first","a.jpg"),Kind=MediaKind.Image});
  var b=db.Upsert(new(){Path=Path.Combine(root,"second","b.jpg"),Kind=MediaKind.Image});
  var set=db.UpsertImageSet(new(){Name="Before",MediaIds=[a.Id],CoverId=a.Id});
  var proposals=db.GetImageSetProposals();
  var builder=CatalogEncryption.ConnectionString(path,key);builder.Pooling=false;
  using(var sql=new SqliteConnection(builder.ToString()))
  {sql.Open();CatalogRepository.AddOrdinalCollation(sql);using var command=sql.CreateCommand();command.CommandText="CREATE TRIGGER fail_set_member BEFORE INSERT ON image_set_members WHEN NEW.media_id='"+b.Id+"' BEGIN SELECT RAISE(ABORT,'injected membership failure'); END;";command.ExecuteNonQuery();}
  Assert.Throws<SqliteException>(()=>db.UpsertImageSet(set with {Name="After",MediaIds=[a.Id,b.Id]}));
  var current=db.GetImageSet(set.Id)!;Assert.Equal("Before",current.Name);Assert.Equal([a.Id],current.MediaIds);Assert.Equal(a.Id,current.CoverId);
  Assert.Throws<SqliteException>(()=>db.ApplyImageSetProposals(proposals.Select(p=>new ImageSetProposalChoice(p,p.Name)).ToArray()));
  Assert.Single(db.GetImageSets());
  using var verify=new SqliteConnection(builder.ToString());verify.Open();using var count=verify.CreateCommand();count.CommandText="SELECT count(*) FROM image_set_members";Assert.Equal(1L,count.ExecuteScalar());
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void OrderedMembershipCoverAndSharedImagesSurviveReopenAndDeletion(bool encrypted)
 {
  Directory.CreateDirectory(root);var path=Path.Combine(root,"catalog.db");using(var empty=new CatalogRepository(path)){}
  if(encrypted)CatalogEncryption.EnableEncryption(path,"synthetic-imageset-password");
  using var key=encrypted?CatalogEncryption.Unlock(path,"synthetic-imageset-password"):null;
  Guid first,second,setId,otherId;
  using(var db=new CatalogRepository(path,key))
  {
   var file=Path.Combine(root,"a.jpg");File.WriteAllBytes(file,[1,2,3]);
   first=db.Upsert(new(){Path=file,Kind=MediaKind.Image}).Id;second=db.Upsert(new(){Path=Path.Combine(root,"b.jpg"),Kind=MediaKind.Image}).Id;
   var set=db.UpsertImageSet(new(){Name="Set",MediaIds=[second,first],CoverId=first});setId=set.Id;
   otherId=db.UpsertImageSet(new(){Name="Other",MediaIds=[first]}).Id;
   var video=db.Upsert(new(){Path=Path.Combine(root,"c.mp4"),Kind=MediaKind.Video});
   Assert.Throws<ArgumentException>(()=>db.UpsertImageSet(set with {MediaIds=[video.Id],CoverId=null}));
   Assert.Throws<ArgumentException>(()=>db.UpsertImageSet(set with {MediaIds=[first,first]}));
   Assert.Throws<ArgumentException>(()=>db.UpsertImageSet(set with {MediaIds=[Guid.NewGuid()],CoverId=null}));
   Assert.Throws<ArgumentException>(()=>db.UpsertImageSet(set with {CoverId=video.Id}));
   Assert.Throws<ArgumentException>(()=>db.UpsertImageSet(set with {Id=Guid.Empty}));
   Assert.Throws<ArgumentException>(()=>db.Upsert(db.GetMedia(first)! with {Kind=MediaKind.Video}));
  }
  using var reopened=new CatalogRepository(path,key);var restored=reopened.GetImageSets().Single(s=>s.Id==setId);
  Assert.Equal([second,first],restored.MediaIds);Assert.Equal(first,restored.CoverId);
  reopened.DeleteImageSet(otherId);Assert.NotNull(reopened.GetMedia(first));Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(Path.Combine(root,"a.jpg")));
  reopened.Delete(first);restored=Assert.Single(reopened.GetImageSets());Assert.Equal([second],restored.MediaIds);Assert.Null(restored.CoverId);Assert.True(File.Exists(Path.Combine(root,"a.jpg")));
 }
}
