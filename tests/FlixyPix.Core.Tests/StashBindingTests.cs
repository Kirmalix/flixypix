using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class StashBindingTests:IDisposable
{
 private readonly string root=StashTestWorkspace.Create("flixypix-stash-binding-");
 private string Database=>Path.Combine(root,"catalog.db");
 private const string Secret="synthetic-stash-binding-password";
 public StashBindingTests()=>Directory.CreateDirectory(root);
 public void Dispose(){SqliteConnection.ClearAllPools();if(Environment.GetEnvironmentVariable("FLIXYPIX_TEST_CAPTURE")!="1")Directory.Delete(root,true);}
 private static JsonElement J<T>(T value)=>JsonSerializer.SerializeToElement(value,CatalogRepository.Json);
 private CatalogKey? Prepare(bool encrypted)
 {using(var db=new CatalogRepository(Database)){}if(!encrypted)return null;CatalogEncryption.EnableEncryption(Database,Secret);return CatalogEncryption.Unlock(Database,Secret);}
 private StashSourceProfile Source(CatalogRepository db)=>db.SaveStashSource(new(){Name="Source",SourceDirectory=Path.Combine(root,"source")});
 private static (StashIdentity Identity,StashProvenance Provenance) Identity(CatalogRepository db,StashSourceProfile source,StashEntityKind kind)
 {
  var identity=Assert.Single(db.StoreStashIdentities(source.Id,source.Revision,[new(new(){SourceId=source.Id,Kind=kind,Keys=[new(StashIdentityKeyKind.Natural,Guid.NewGuid().ToString())]},null)]));
  var section=kind switch{StashEntityKind.Scene=>"scenes",StashEntityKind.Image=>"images",StashEntityKind.Gallery=>"galleries",StashEntityKind.Performer=>"performers",StashEntityKind.Studio=>"studios",StashEntityKind.Tag=>"tags",StashEntityKind.Group=>"movies",StashEntityKind.SavedFilter=>"saved_filters",_=>"files"};
  var bytes=Encoding.UTF8.GetBytes("{}");var document=new StashSourceDocument(1,kind,section+"/"+identity.Id+".json",Convert.ToHexStringLower(SHA256.HashData(bytes)),null,bytes,null,[]);var provenance=Assert.Single(db.CaptureStashProvenance(source.Id,source.Revision,[new(identity.Id,identity.Revision,document)]));return(identity,provenance);
 }
 private static StashBinding Binding((StashIdentity Identity,StashProvenance Provenance) entry,StashTargetReference target,List<StashFieldBaseline>? fields=null)=>new(){Id=entry.Identity.Id,ProvenanceId=entry.Provenance.Id,Target=target,Fields=fields??[]};
 private static StashBinding Save(CatalogRepository db,StashSourceProfile source,StashBinding binding,long? expected=null,bool retarget=false)=>Assert.Single(db.StoreStashBindings(source.Id,source.Revision,[new(binding,expected,retarget)]));
 [Theory][InlineData(false)][InlineData(true)]
 public async Task FieldDecisionsAndTypedTargetsSurviveReopenPortableAndActualBackup(bool encrypted)
 {
  using var key=Prepare(encrypted);Guid bindingId;using(var db=new CatalogRepository(Database,key))
  {
   var source=Source(db);source=db.SaveStashSource(source with{PathMappings=[new("/media",Path.Combine(root,"mapped"))]},source.Revision);var entry=Identity(db,source,StashEntityKind.Scene);
   var raw=Enumerable.Repeat((byte)' ',StashProvenance.ChunkBytes+17).ToArray();raw[0]=(byte)'{';raw[1]=(byte)'}';var document=new StashSourceDocument(1,StashEntityKind.Scene,"scenes/renamed.json",Convert.ToHexStringLower(SHA256.HashData(raw)),null,raw,null,[]);
   var captured=Assert.Single(db.CaptureStashProvenance(source.Id,source.Revision,[new(entry.Identity.Id,entry.Identity.Revision,document)]));entry=(Assert.Single(db.StoreStashIdentities(source.Id,source.Revision,[new(entry.Identity with{Keys=entry.Identity.Keys.Append(new(StashIdentityKeyKind.ExportPath,"scenes/old.json")).Append(new(StashIdentityKeyKind.ExportPath,"scenes/renamed.json")).ToList()},entry.Identity.Revision)])),captured);
   var media=db.Upsert(new(){Path=Path.Combine(root,"clip.mp4"),Kind=MediaKind.Video,Title="Manual",ReviewState=ReviewState.Reviewed});var fields=StashFieldConflicts.Record([new("title",J("External"),J("Manual"))],[new("title",StashFieldDecision.KeepLocal)]).ToList();var saved=Save(db,source,Binding(entry,new(StashTargetKind.Video,media.Id),fields));bindingId=saved.Id;
   Assert.Equal(1,saved.Revision);Assert.Equal(1,Save(db,source,saved,saved.Revision).Revision);Assert.Equal("Manual",db.GetMedia(media.Id)!.Title);Assert.Equal(ReviewState.Reviewed,db.GetMedia(media.Id)!.ReviewState);
   var portable=Path.Combine(root,"portable.json");db.Export(portable);using(var target=new CatalogRepository(Path.Combine(root,"target.db")))
   {
    for(var i=0;i<2;i++)Assert.Empty(target.ImportJson(portable,CatalogRepository.MaximumPortableEntityBytes,secret:encrypted?Secret:null).Errors);Assert.Equal(source.PathMappings,target.GetStashSource(source.Id)!.PathMappings);Assert.Equal(entry.Identity.Keys,target.GetStashIdentity(entry.Identity.Id)!.Keys);Assert.Equal(raw,target.ReadStashProvenanceBytes(entry.Provenance.Id));var copy=target.GetStashBinding(saved.Id)!;Assert.Equal(saved.Target,copy.Target);Assert.False(copy.Detached);Assert.Equal(1,copy.Revision);Assert.Equal("Manual",Assert.Single(copy.Fields).Local.GetString());Assert.Equal(StashFieldStatus.PreviouslyKept,Assert.Single(StashFieldConflicts.Compare([new("title",J("External"),J("Manual"))],copy.Fields)).Status);
   }
   var package=Path.Combine(root,"backup.zip");await db.CreateBackupAsync(package,false);var restore=await CatalogRepository.RestoreBackupAsync(package,Path.Combine(root,"restored"),secret:encrypted?Secret:null);using var restoredKey=encrypted?CatalogEncryption.Unlock(restore.DatabasePath,Secret):null;using var restored=new CatalogRepository(restore.DatabasePath,restoredKey);Assert.Equal(source.PathMappings,restored.GetStashSource(source.Id)!.PathMappings);Assert.Equal(entry.Identity.Keys,restored.GetStashIdentity(entry.Identity.Id)!.Keys);Assert.Equal(raw,restored.ReadStashProvenanceBytes(entry.Provenance.Id));Assert.Equal(saved.Target,restored.GetStashBinding(saved.Id)!.Target);Assert.Equal(StashFieldDecision.KeepLocal,Assert.Single(restored.GetStashBinding(saved.Id)!.Fields).Decision);
  }
  using var reopened=new CatalogRepository(Database,key);Assert.NotNull(reopened.GetStashBinding(bindingId));
 }
 [Theory][InlineData(false)]
 public void StaleBindingAndLateBatchFailureCannotReplaceDecisionsOrPublishPartialBindings(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var source=Source(db);var entry=Identity(db,source,StashEntityKind.Scene);var media=db.Upsert(new(){Path=Path.Combine(root,"clip.mp4"),Kind=MediaKind.Video});var binding=Save(db,source,Binding(entry,new(StashTargetKind.Video,media.Id)));using var second=new CatalogRepository(Database,key);var updated=Save(second,source,binding with{Fields=[new("title",J("Remote"),J("Local"),StashFieldDecision.KeepLocal)]},binding.Revision);Assert.Equal(2,updated.Revision);
  var other=Binding(Identity(db,source,StashEntityKind.SavedFilter),new(StashTargetKind.ProvenanceOnly));Assert.Throws<InvalidOperationException>(()=>db.StoreStashBindings(source.Id,source.Revision,[new(other,null),new(binding,binding.Revision)]));Assert.Null(db.GetStashBinding(other.Id));Assert.Equal(2,db.GetStashBinding(binding.Id)!.Revision);Assert.Equal("Local",Assert.Single(db.GetStashBinding(binding.Id)!.Fields).Local.GetString());
  var changedSource=second.SaveStashSource(source with{Name="Changed"},source.Revision);Assert.Throws<InvalidOperationException>(()=>Save(db,source,other));Assert.Null(db.GetStashBinding(other.Id));Assert.Equal(changedSource.Revision,db.GetStashSource(source.Id)!.Revision);
 }
 [Fact]
 public void RebindingToAnotherTargetRequiresExplicitConfirmation()
 {
  using var db=new CatalogRepository(Database);var source=Source(db);var entry=Identity(db,source,StashEntityKind.Scene);var first=db.Upsert(new(){Path=Path.Combine(root,"first.mp4"),Kind=MediaKind.Video});var other=db.Upsert(new(){Path=Path.Combine(root,"other.mp4"),Kind=MediaKind.Video});var binding=Save(db,source,Binding(entry,new(StashTargetKind.Video,first.Id)));var changed=binding with{Target=new(StashTargetKind.Video,other.Id)};Assert.Throws<InvalidOperationException>(()=>Save(db,source,changed,binding.Revision));Assert.Equal(first.Id,db.GetStashBinding(binding.Id)!.Target.Id);Assert.Equal(other.Id,Save(db,source,changed,binding.Revision,true).Target.Id);
 }
 [Theory][InlineData("kind")][InlineData("missing")][InlineData("provenance")][InlineData("target-shape")][InlineData("duplicate-fields")]
 public void InvalidTargetOrFieldStateCannotBeRegistered(string invalid)
 {
  using var db=new CatalogRepository(Database);var source=Source(db);var entry=Identity(db,source,StashEntityKind.Scene);var media=db.Upsert(new(){Path=Path.Combine(root,"clip.mp4"),Kind=MediaKind.Video});var binding=Binding(entry,new(StashTargetKind.Video,media.Id));
  binding=invalid switch{"kind"=>binding with{Target=new(StashTargetKind.Image,media.Id)},"missing"=>binding with{Target=new(StashTargetKind.Video,Guid.NewGuid())},"provenance"=>binding with{ProvenanceId=Identity(db,source,StashEntityKind.Scene).Provenance.Id},"target-shape"=>binding with{Target=new(StashTargetKind.Video,media.Id,new("","Tag"))},_=>binding with{Fields=[new("title",J("a"),J("b"),StashFieldDecision.KeepLocal),new("title",J("x"),J("y"),StashFieldDecision.UseSource)]}};
  Assert.Throws<ArgumentException>(()=>Save(db,source,binding));Assert.Null(db.GetStashBinding(binding.Id));
 }
 [Theory][InlineData(false)]
 public void DeletedTargetStaysDetachedAcrossExchangeAndReusedGuidNeedsConfirmation(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var source=Source(db);var entry=Identity(db,source,StashEntityKind.Scene);var media=db.Upsert(new(){Path=Path.Combine(root,"clip.mp4"),Kind=MediaKind.Video});var binding=Save(db,source,Binding(entry,new(StashTargetKind.Video,media.Id)));db.Delete(media.Id);var detached=db.GetStashBinding(binding.Id)!;Assert.True(detached.Detached);Assert.Equal(2,detached.Revision);var portable=Path.Combine(root,"detached.json");db.Export(portable);
  using(var target=new CatalogRepository(Path.Combine(root,"target.db"))){Assert.Empty(target.ImportJson(portable,CatalogRepository.MaximumPortableEntityBytes,secret:encrypted?Secret:null).Errors);Assert.True(target.GetStashBinding(binding.Id)!.Detached);Assert.Null(target.GetMedia(media.Id));}
  db.Upsert(media with{Title="Explicitly recreated"});Assert.True(db.GetStashBinding(binding.Id)!.Detached);Assert.Throws<InvalidOperationException>(()=>Save(db,source,detached with{Detached=false},detached.Revision));Assert.False(Save(db,source,detached with{Detached=false},detached.Revision,true).Detached);
 }
 [Fact]
 public void TypedDeletionNeverDetachesVideoWithSameGuidAsImageSet()
 {
  using var db=new CatalogRepository(Database);var source=Source(db);var video=db.Upsert(new(){Path=Path.Combine(root,"clip.mp4"),Kind=MediaKind.Video});var set=db.UpsertImageSet(new(){Id=video.Id,Name="Same GUID"});var videoBinding=Save(db,source,Binding(Identity(db,source,StashEntityKind.Scene),new(StashTargetKind.Video,video.Id)));var setBinding=Save(db,source,Binding(Identity(db,source,StashEntityKind.Gallery),new(StashTargetKind.ImageSet,set.Id)));db.DeleteImageSet(set.Id);Assert.True(db.GetStashBinding(setBinding.Id)!.Detached);Assert.False(db.GetStashBinding(videoBinding.Id)!.Detached);Assert.Equal(1,db.GetStashBinding(videoBinding.Id)!.Revision);
 }
 [Fact]
 public void DirectoryRoleAndNamespacedTagKeyAreCheckedAndLocalRenameCannotRecreateOldEntry()
 {
  using var db=new CatalogRepository(Database);var source=Source(db);var entry=Identity(db,source,StashEntityKind.Performer);db.SaveDirectoryEntry("Person","People",new("Person","People",TagRole.Performer,0),categoryRole:TagRole.Performer);var binding=Save(db,source,Binding(entry,new(StashTargetKind.DirectoryEntry,Tag:new("People","Person"))));Assert.False(binding.Detached);db.SaveDirectoryEntry("Person","Generic",new("Person","Generic",TagRole.Tag,0),categoryRole:TagRole.Tag);Assert.Throws<ArgumentException>(()=>Save(db,source,binding with{Target=new(StashTargetKind.DirectoryEntry,Tag:new("Generic","Person"))},binding.Revision,true));
  db.SaveDirectoryEntry("Person","People",new("Renamed","People",TagRole.Performer,0));Assert.True(db.GetStashBinding(binding.Id)!.Detached);Assert.DoesNotContain(db.GetDirectory(),entry=>entry.Tag=="Person"&&entry.Category=="People");Assert.Equal("Renamed",Save(db,source,binding with{Target=new(StashTargetKind.DirectoryEntry,Tag:new("People","Renamed"))},binding.Revision,true).Target.Tag!.Value);
 }
 [Theory][InlineData(false)]
 public void PortablePathCollisionRemapsTypedTargetsAndReferenceBaselines(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var source=Source(db);var video=db.Upsert(new(){Path=Path.Combine(root,"clip.mp4"),Kind=MediaKind.Video});var image=db.Upsert(new(){Path=Path.Combine(root,"photo.jpg"),Kind=MediaKind.Image});var set=db.UpsertImageSet(new(){Id=video.Id,Name="Set",MediaIds=[image.Id],CoverId=image.Id});var group=db.UpsertPublicationGroup(new(){Name="Group",Members=[new(PublicationGroupMemberKind.ImageSet,set.Id),new(PublicationGroupMemberKind.Video,video.Id)]});
  var videoBinding=Save(db,source,Binding(Identity(db,source,StashEntityKind.Scene),new(StashTargetKind.Video,video.Id)));var setBinding=Save(db,source,Binding(Identity(db,source,StashEntityKind.Gallery),new(StashTargetKind.ImageSet,set.Id),[new("mediaIds",J(set.MediaIds),J(set.MediaIds),StashFieldDecision.UseSource),new("coverId",J(set.CoverId),J(set.CoverId),StashFieldDecision.UseSource)]));var groupBinding=Save(db,source,Binding(Identity(db,source,StashEntityKind.Group),new(StashTargetKind.PublicationGroup,group.Id),[new("members",J(group.Members),J(group.Members),StashFieldDecision.UseSource)]));
  var portable=Path.Combine(root,"typed.json");db.Export(portable);using var target=new CatalogRepository(Path.Combine(root,"target.db"));var localVideo=target.Upsert(new(){Path=video.Path,Kind=MediaKind.Video});var localImage=target.Upsert(new(){Path=image.Path,Kind=MediaKind.Image});for(var i=0;i<2;i++)Assert.Empty(target.ImportJson(portable,CatalogRepository.MaximumPortableEntityBytes,secret:encrypted?Secret:null).Errors);
  Assert.Equal(localVideo.Id,target.GetStashBinding(videoBinding.Id)!.Target.Id);var mappedSet=target.GetStashBinding(setBinding.Id)!;Assert.Equal(set.Id,mappedSet.Target.Id);Assert.Equal(new[]{localImage.Id},mappedSet.Fields.Single(field=>field.Field=="mediaIds").Local.Deserialize<Guid[]>());Assert.Equal(localImage.Id,mappedSet.Fields.Single(field=>field.Field=="coverId").Source.GetGuid());var members=target.GetStashBinding(groupBinding.Id)!.Fields.Single().Local.Deserialize<List<PublicationGroupMember>>(CatalogRepository.Json)!;Assert.Equal(new[]{new PublicationGroupMember(PublicationGroupMemberKind.ImageSet,set.Id),new(PublicationGroupMemberKind.Video,localVideo.Id)},members);
 }
 [Theory][InlineData("conflict")][InlineData("duplicate")][InlineData("missing-target")][InlineData("provenance")][InlineData("cover-type")]
 public void InvalidPortableBindingRollsBackEarlierMediaAndSourceState(string damage)
 {
  using var db=new CatalogRepository(Database);var source=Source(db);var image=db.Upsert(new(){Path=Path.Combine(root,"photo.jpg"),Kind=MediaKind.Image,Title="Keep"});var set=db.UpsertImageSet(new(){Name="Set",MediaIds=[image.Id],CoverId=image.Id});var binding=Save(db,source,Binding(Identity(db,source,StashEntityKind.Gallery),new(StashTargetKind.ImageSet,set.Id),[new("coverId",J(set.CoverId),J(set.CoverId),StashFieldDecision.UseSource)]));var portable=Path.Combine(root,"portable.json");db.Export(portable);var doc=JsonNode.Parse(File.ReadAllText(portable))!.AsObject();doc["items"]![0]!["title"]="Rollback";var bindings=doc["stashBindings"]!.AsArray();
  switch(damage){case "conflict":bindings[0]!["fields"]![0]!["decision"]="KeepLocal";break;case "duplicate":bindings.Add(bindings[0]!.DeepClone());break;case "missing-target":bindings[0]!["target"]!["id"]=Guid.NewGuid();break;case "provenance":bindings[0]!["provenanceId"]=Guid.NewGuid();break;case "cover-type":bindings[0]!["fields"]![0]!["local"]=true;break;}
  File.WriteAllText(portable,doc.ToJsonString());Assert.NotEmpty(db.ImportJson(portable).Errors);Assert.Equal("Keep",db.GetMedia(image.Id)!.Title);Assert.Equal(StashFieldDecision.UseSource,Assert.Single(db.GetStashBinding(binding.Id)!.Fields).Decision);
  if(damage!="conflict"){using var empty=new CatalogRepository(Path.Combine(root,"empty.db"));Assert.NotEmpty(empty.ImportJson(portable).Errors);Assert.Null(empty.GetMedia(image.Id));Assert.Null(empty.GetStashSource(source.Id));Assert.Null(empty.GetStashBinding(binding.Id));}
 }
 [Fact]
 public void CancellationPagingAndPortableSizeBoundLeaveNoPartialDecisionRecords()
 {
  using var db=new CatalogRepository(Database);var source=Source(db);var binding=Binding(Identity(db,source,StashEntityKind.SavedFilter),new(StashTargetKind.ProvenanceOnly));using var cancellation=new CancellationTokenSource();cancellation.Cancel();Assert.Throws<OperationCanceledException>(()=>db.StoreStashBindings(source.Id,source.Revision,[new(binding,null)],cancellation.Token));Assert.Null(db.GetStashBinding(binding.Id));Assert.Throws<ArgumentOutOfRangeException>(()=>db.GetStashBindings(source.Id,0,257));
  var large=J(new string('x',1024*1024));Assert.Throws<ArgumentException>(()=>Save(db,source,binding with{Fields=Enumerable.Range(0,40).Select(i=>new StashFieldBaseline("f"+i,large,large,StashFieldDecision.KeepLocal)).ToList()}));Assert.Null(db.GetStashBinding(binding.Id));Assert.Empty(db.GetStashBindings(source.Id));Save(db,source,binding);Assert.Equal(binding.Id,Assert.Single(db.GetStashBindings(source.Id)).Id);Assert.Empty(db.GetStashBindings(source.Id,1));
 }
}
