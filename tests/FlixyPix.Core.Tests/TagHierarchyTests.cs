using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class TagHierarchyTests:IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-tag-dag-"+Guid.NewGuid().ToString("N"));
 private string Database=>Path.Combine(root,"catalog.db");
 public TagHierarchyTests()=>Directory.CreateDirectory(root);
 public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
 private CatalogKey? Prepare(bool encrypted)
 {
  using(var empty=new CatalogRepository(Database)){}
  if(!encrypted)return null;CatalogEncryption.EnableEncryption(Database,"synthetic-tag-dag-password");return CatalogEncryption.Unlock(Database,"synthetic-tag-dag-password");
 }
 private static TagKey Tag(CatalogRepository db,string value,string category="Genre")
 {db.UpsertVocabularyTag(new(value,category));return new(category,value);}
 private void Sql(CatalogKey? key,string text)
 {
  var builder=CatalogEncryption.ConnectionString(Database,key);builder.Pooling=false;using var connection=new SqliteConnection(builder.ToString());connection.Open();CatalogRepository.AddOrdinalCollation(connection);
  using var command=connection.CreateCommand();command.CommandText=text;command.ExecuteNonQuery();
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void DiamondAndTypedIdentityPersistWithoutImplicitSearchExpansion(bool encrypted)
 {
  using var key=Prepare(encrypted);
  using(var db=new CatalogRepository(Database,key))
  {
   var a=Tag(db,"A");var b=Tag(db,"B");var c=Tag(db,"C");var d=Tag(db,"D");var other=Tag(db,"D","Other");
   db.AddTagParent(b,a);db.AddTagParent(c,a);db.SetTagParents(d,[b,c]);db.AddTagParent(other,a);
   Assert.Equal([b,c],db.GetTagParents(d));Assert.Equal([a],db.GetTagParents(other));Assert.Equal(4,db.GetTagDescendants(a).Count);
   Assert.True(db.AddTagParent(new(" genre ","d"),new("GENRE","b")).IsEmpty);Assert.Equal(5,db.GetTagHierarchy().Count);
   Assert.Throws<ArgumentException>(()=>db.AddTagParent(a,d));Assert.Throws<ArgumentException>(()=>db.AddTagParent(a,a));
   Assert.Throws<ArgumentException>(()=>db.AddTagParent(d,new("Genre","Missing")));Assert.Equal(5,db.GetTagHierarchy().Count);
   db.Upsert(new(){Path=Path.Combine(root,"one.mp4"),Kind=MediaKind.Video,TagCategories=new(){["Genre"]=["D"],["Other"]=["D"]}});
   Assert.Equal(0,db.QueryPage(new(){Expression=CatalogRepository.HasCategorizedTag(a)}).TotalCount);
   var explicitTree=SearchExpression.Any(db.GetTagDescendants(a,true).Select(k=>CatalogRepository.HasCategorizedTag(k)).ToArray());
   Assert.Equal(1,db.QueryPage(new(){Expression=explicitTree}).TotalCount);
  }
  using var reopened=new CatalogRepository(Database,key);Assert.Equal(5,reopened.GetTagHierarchy().Count);Assert.Equal(2,reopened.GetTagParents(new("Genre","D")).Count);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void DirectoryRenameAndCategoryMoveCarryHierarchyAndStackedUndo(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var a=Tag(db,"A");var c=Tag(db,"C");var other=Tag(db,"C","Other");db.AddTagParent(c,a);db.AddTagParent(other,a);
  var media=db.Upsert(new(){Path=Path.Combine(root,"one.mp4"),Kind=MediaKind.Video,TagCategories=new(){["Genre"]=["C"],["Other"]=["C"]},Rating=9,ReviewState=ReviewState.Reviewed});
  var entry=db.GetDirectory().Single(e=>e.Tag=="C"&&e.Category=="Genre");
  var rename=db.SaveDirectoryEntry("C","Genre",entry with {Tag="Renamed"});Assert.NotNull(rename.Undo);
  Assert.Equal([a],db.GetTagParents(new("Genre","Renamed")));Assert.Equal([a],db.GetTagParents(new("Other","Renamed")));Assert.Empty(db.GetTagParents(c));
  var renamed=db.GetDirectory().Single(e=>e.Tag=="Renamed"&&e.Category=="Genre");
  var move=db.SaveDirectoryEntry("Renamed","Genre",renamed with {Category="Moved"});Assert.NotNull(move.Undo);
  Assert.Equal([a],db.GetTagParents(new("Moved","Renamed")));Assert.Equal([a],db.GetTagParents(new("Other","Renamed")));
  Assert.True(db.UndoTagChange(move.Undo!));Assert.True(db.UndoTagChange(rename.Undo!));
  Assert.Equal([a],db.GetTagParents(c));Assert.Equal([a],db.GetTagParents(other));Assert.Equal(["C"],db.GetMedia(media.Id)!.TagCategories["Genre"]);
  Assert.Equal(9,db.GetMedia(media.Id)!.Rating);Assert.Equal(ReviewState.Reviewed,db.GetMedia(media.Id)!.ReviewState);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void CycleCreatingMergeAndLaterWriteFailureRollbackAllTagData(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var a=Tag(db,"A");var b=Tag(db,"B");var c=Tag(db,"C");db.AddTagParent(b,a);db.AddTagParent(c,b);
  var media=db.Upsert(new(){Path=Path.Combine(root,"one.mp4"),Kind=MediaKind.Video,Tags=["A"]});var original=db.GetTagHierarchy();
  Assert.Throws<ArgumentException>(()=>db.MergeTags(["A"],"C"));Assert.Equal(original,db.GetTagHierarchy());Assert.Contains("A",db.GetMedia(media.Id)!.Tags);
  Sql(key,"CREATE TRIGGER fail_dag_media BEFORE UPDATE ON media WHEN OLD.id='"+media.Id+"' BEGIN SELECT RAISE(ABORT,'injected hierarchy rename failure'); END;");
  Assert.Throws<SqliteException>(()=>db.ChangeTag("A","Renamed"));Assert.Equal(original,db.GetTagHierarchy());Assert.Contains(db.GetDirectory(),e=>e.Tag=="A");Assert.DoesNotContain(db.GetDirectory(),e=>e.Tag=="Renamed");
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void HierarchyUndoRejectsConcurrentEdgesAndDetachedNodeRenames(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var a=Tag(db,"A");var b=Tag(db,"B");var c=Tag(db,"C");var d=Tag(db,"D");
  var first=db.AddTagParent(d,b);var second=db.AddTagParent(d,c);Assert.False(db.UndoTagChange(first));Assert.Equal([b,c],db.GetTagParents(d));
  Assert.True(db.UndoTagChange(second));Assert.True(db.UndoTagChange(first));Assert.Empty(db.GetTagParents(d));
  db.AddTagParent(b,a);var deletion=db.DeleteTags(["A"]);Assert.NotNull(deletion.Undo);Assert.Empty(db.GetTagHierarchy());
  db.ChangeTag("B","Renamed");Assert.False(db.UndoTagChange(deletion.Undo!));Assert.Empty(db.GetTagHierarchy());
  Assert.DoesNotContain(db.GetDirectory(),e=>e.Tag=="A");Assert.Contains(db.GetDirectory(),e=>e.Tag=="Renamed");
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void PlainGraphNodesRemainKnownAfterLastAssignmentAndParentRemovalCanBeUndone(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var parent=new TagKey("","Plain parent");var child=new TagKey("","Plain child");
  var item=db.Upsert(new(){Path=Path.Combine(root,"one.jpg"),Kind=MediaKind.Image,Tags=[parent.Value,child.Value]});db.AddTagParent(child,parent);db.Delete(item.Id);
  Assert.Contains(db.GetDirectory(),e=>e.Tag==parent.Value&&e.Category==""&&e.MediaCount==0);Assert.Contains(db.GetDirectory(),e=>e.Tag==child.Value&&e.MediaCount==0);
  var undo=db.RemoveTagParent(child,parent);Assert.Empty(db.GetTagHierarchy());Assert.True(db.UndoTagChange(undo));Assert.Equal([parent],db.GetTagParents(child));Assert.Null(db.GetMedia(item.Id));
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void CategoryCollapseCycleAndCancelledWritesKeepOriginalGraph(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var a=Tag(db,"Same","First");var b=Tag(db,"Same","Second");db.AddTagParent(b,a);var original=db.GetTagHierarchy();
  Assert.Throws<ArgumentException>(()=>db.MoveTagCategory("Same","First","Second"));Assert.Equal(original,db.GetTagHierarchy());Assert.Contains(db.GetDirectory(),e=>e.Tag=="Same"&&e.Category=="First");
  using var cancelled=new CancellationTokenSource();cancelled.Cancel();Assert.Throws<OperationCanceledException>(()=>db.SetTagParents(b,[],cancelled.Token));Assert.Equal(original,db.GetTagHierarchy());
  Assert.Throws<OperationCanceledException>(()=>db.GetTagDescendants(a,true,cancelled.Token));
 }
 [Theory][InlineData(false)][InlineData(true)]
 public async Task MigrationAndBackupKeepGraphGroupsAndTypedTags(bool encrypted)
 {
  using var key=Prepare(encrypted);Guid groupId;
  using(var db=new CatalogRepository(Database,key)){Tag(db,"A");Tag(db,"B");groupId=db.UpsertPublicationGroup(new(){Name="Preserved group"}).Id;}
  Sql(key,"DROP TABLE tag_hierarchies; PRAGMA user_version=21;");
  using var migrated=new CatalogRepository(Database,key);Assert.True(File.Exists(migrated.MigrationBackupPath));Assert.Empty(migrated.GetTagHierarchy());Assert.NotNull(migrated.GetPublicationGroup(groupId));
  migrated.AddTagParent(new("Genre","B"),new("Genre","A"));
  var package=Path.Combine(root,"graph.backup");await migrated.CreateBackupAsync(package,false);
  var restored=await CatalogRepository.RestoreBackupAsync(package,Path.Combine(root,"restored"),secret:encrypted?"synthetic-tag-dag-password":null);
  using var restoredKey=encrypted?CatalogEncryption.Unlock(restored.DatabasePath,"synthetic-tag-dag-password"):null;using var copy=new CatalogRepository(restored.DatabasePath,restoredKey);
  Assert.Equal(migrated.GetTagHierarchy(),copy.GetTagHierarchy());Assert.NotNull(copy.GetPublicationGroup(groupId));Assert.Equal([new TagKey("Genre","A")],copy.GetTagParents(new("Genre","B")));
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void ValidMergeDeduplicatesEdgesAndDeepCycleIsRejectedWithoutPartialChanges(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var a=Tag(db,"A");var b=Tag(db,"B");var d=Tag(db,"D");
  db.SetTagParents(d,[a,b]);var merged=db.MergeTags(["A"],"B");Assert.Equal([b],db.GetTagParents(d));Assert.Single(db.GetTagHierarchy());
  Assert.NotNull(merged.Undo);Assert.True(db.UndoTagChange(merged.Undo!));Assert.Equal([a,b],db.GetTagParents(d));
  var names=Enumerable.Range(0,128).Select(i=>"Node"+i.ToString("D3")).ToList();
  db.Upsert(new(){Path=Path.Combine(root,"chain.mp4"),Kind=MediaKind.Video,TagCategories=new(){["Chain"]=names}});
  for(var i=1;i<names.Count;i++)db.AddTagParent(new("Chain",names[i]),new("Chain",names[i-1]));
  var before=db.GetTagHierarchy();Assert.Equal(127,db.GetTagDescendants(new("Chain",names[0])).Count);
  Assert.Throws<ArgumentException>(()=>db.AddTagParent(new("Chain",names[0]),new("Chain",names[^1])));Assert.Equal(before,db.GetTagHierarchy());
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void AssigningCategoryToPlainNodeUpdatesOnlyThatKeyAndCanBeUndone(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var parent=new TagKey("","Parent");var child=new TagKey("","Child");
  var media=db.Upsert(new(){Path=Path.Combine(root,"plain.jpg"),Kind=MediaKind.Image,Tags=[parent.Value,child.Value]});db.AddTagParent(child,parent);
  var entry=db.GetDirectory().Single(e=>e.Category==""&&e.Tag=="Child");var result=db.SaveDirectoryEntry("Child","",entry with {Category="Typed"});
  Assert.Equal([parent],db.GetTagParents(new("Typed","Child")));Assert.Empty(db.GetTagParents(child));Assert.Equal(["Child"],db.GetMedia(media.Id)!.TagCategories["Typed"]);
  Assert.NotNull(result.Undo);Assert.True(db.UndoTagChange(result.Undo!));Assert.Equal([parent],db.GetTagParents(child));Assert.Empty(db.GetMedia(media.Id)!.TagCategories);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void ParentEditsDoNotInvalidateImmediateTagUndo(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var parent=Tag(db,"Parent");var child=Tag(db,"Child");Tag(db,"A");
  var media=db.Upsert(new(){Path=Path.Combine(root,"one.jpg"),Kind=MediaKind.Image});var undo=db.UpdateTagsWithUndo([media.Id],["A"],[]);
  db.AddTagParent(child,parent);Assert.True(db.UndoImmediateEdit(undo));Assert.Empty(db.GetMedia(media.Id)!.Tags);Assert.Equal([parent],db.GetTagParents(child));
 }
 [Theory][InlineData(false,0)][InlineData(true,0)][InlineData(false,1)][InlineData(true,1)][InlineData(false,2)][InlineData(true,2)]
 public void AllImmediateTagUndoPathsRejectNamesChangedWhileTagWasRemoved(bool encrypted,int mode)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);Tag(db,"A");
  var media=db.Upsert(new(){Path=Path.Combine(root,"one.jpg"),Kind=MediaKind.Image,TagCategories=new(){["Genre"]=["A"]}});
  var undo=mode switch
  {
   0=>db.ApplyImmediateEdit([media.Id],ImmediateEditFields.Tags,m=>m with {Tags=[],TagCategories=[]}),
   1=>db.UpdateTagsWithUndo([media.Id],[],["A"]),
   _=>db.UpdateCategoryTagsWithUndo([media.Id],"Genre",[],["A"])
  };
  var rating=db.ApplyImmediateEdit([media.Id],ImmediateEditFields.All,m=>m with {Rating=9});
  db.ChangeTag("A","Renamed");Assert.False(db.UndoImmediateEdit(undo));Assert.Empty(db.GetMedia(media.Id)!.Tags);
  Assert.True(db.UndoImmediateEdit(rating));Assert.Equal(0,db.GetMedia(media.Id)!.Rating);Assert.Contains(db.GetDirectory(),e=>e.Tag=="Renamed");
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void UndoingGlobalNameChangeMakesEarlierTagUndoValidAgain(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);Tag(db,"A");
  var media=db.Upsert(new(){Path=Path.Combine(root,"one.jpg"),Kind=MediaKind.Image,TagCategories=new(){["Genre"]=["A"]}});
  var tags=db.UpdateTagsWithUndo([media.Id],[],["A"]);var entry=db.GetDirectory().Single(e=>e.Tag=="A"&&e.Category=="Genre");
  var rename=db.SaveDirectoryEntry("A","Genre",entry with {Tag="Renamed"});Assert.False(db.UndoImmediateEdit(tags));
  Assert.NotNull(rename.Undo);Assert.True(db.UndoTagChange(rename.Undo!));Assert.True(db.UndoImmediateEdit(tags));Assert.Contains("A",db.GetMedia(media.Id)!.Tags);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void ParentEditorRejectsStaleParentsButKeepsUnrelatedEdges(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var a=Tag(db,"A");var b=Tag(db,"B");var c=Tag(db,"C");var d=Tag(db,"D");var e=Tag(db,"E");
  db.AddTagParent(d,a);var expected=db.GetTagParents(d);db.AddTagParent(d,b);
  Assert.Throws<InvalidOperationException>(()=>db.SetTagParents(d,[c],expected));Assert.Equal([a,b],db.GetTagParents(d));
  expected=db.GetTagParents(d);db.AddTagParent(e,c);db.SetTagParents(d,[c],expected);Assert.Equal([c],db.GetTagParents(d));Assert.Equal([c],db.GetTagParents(e));
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void CategoryMoveAndItsRoleChangeUndoAtomicallyWithHierarchy(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var parent=Tag(db,"Parent");var child=Tag(db,"Actor","Cast");db.SetTagRole("Cast",TagRole.Performer);db.AddTagParent(child,parent);
  var media=db.Upsert(new(){Path=Path.Combine(root,"actor.mp4"),Kind=MediaKind.Video,TagCategories=new(){["Cast"]=["Actor"]}});
  var entry=db.GetDirectory().Single(e=>e.Category=="Cast"&&e.Tag=="Actor");
  var change=db.SaveDirectoryEntry("Actor","Cast",entry with {Category="Ensemble"},categoryRole:TagRole.Performer);
  Assert.Equal(TagRole.Performer,db.GetTagRole("Ensemble"));Assert.Equal([parent],db.GetTagParents(new("Ensemble","Actor")));Assert.NotNull(change.Undo);
  Assert.True(db.UndoTagChange(change.Undo!));Assert.Equal(TagRole.Tag,db.GetTagRole("Ensemble"));Assert.Equal([parent],db.GetTagParents(child));Assert.Contains("Cast",db.GetMedia(media.Id)!.TagCategories.Keys);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void RoleOnlyUndoPreservesConcurrentMediaChanges(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var node=Tag(db,"Entry");var media=db.Upsert(new(){Path=Path.Combine(root,"one.jpg"),Kind=MediaKind.Image});
  var entry=db.GetDirectory().Single(e=>e.Tag==node.Value&&e.Category==node.Category);var change=db.SaveDirectoryEntry(entry.Tag,entry.Category,entry,categoryRole:TagRole.Studio);Assert.NotNull(change.Undo);
  db.Upsert(media with {Rating=9});Assert.True(db.UndoTagChange(change.Undo!));Assert.Equal(TagRole.Tag,db.GetTagRole(node.Category));Assert.Equal(9,db.GetMedia(media.Id)!.Rating);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void CatalogCheckReportsInvalidHierarchyAndRepairsNeverGuessEdges(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);
  var a=Tag(db,"A");var b=Tag(db,"B");db.AddTagParent(b,a);
  Assert.Equal(0,db.CheckCatalog(root)[CatalogFindingKind.TagHierarchyDocuments].Count);
  var edge=new TagParentEdge(a,b);
  var invalid=new[]{"{broken","null","{\"edges\":null}","{\"edges\":[null]}","{\"edges\":[{\"parent\":null,\"child\":null}]}",
   System.Text.Json.JsonSerializer.Serialize(new TagHierarchy{Edges=[edge,new(b,a)]},CatalogRepository.Json),
   System.Text.Json.JsonSerializer.Serialize(new TagHierarchy{Edges=[edge,edge]},CatalogRepository.Json),
   System.Text.Json.JsonSerializer.Serialize(new TagHierarchy{Revision=-1},CatalogRepository.Json)};
  foreach(var document in invalid)
  {
   var builder=CatalogEncryption.ConnectionString(Database,key);builder.Pooling=false;
   using var connection=new SqliteConnection(builder.ToString());connection.Open();
   using(var update=connection.CreateCommand()){update.CommandText="UPDATE tag_hierarchies SET document=$json";update.Parameters.AddWithValue("$json",document);update.ExecuteNonQuery();}
   var report=db.CheckCatalog(root);Assert.Equal(1,report[CatalogFindingKind.TagHierarchyDocuments].Count);Assert.False(report[CatalogFindingKind.TagHierarchyDocuments].Repairable);
   var repair=db.RepairCatalog(report,new HashSet<CatalogFindingKind>{CatalogFindingKind.TagHierarchyDocuments,CatalogFindingKind.QueryTable},Path.Combine(root,"backups"));Assert.True(File.Exists(repair.BackupPath));
   using var read=connection.CreateCommand();read.CommandText="SELECT document FROM tag_hierarchies";Assert.Equal(document,read.ExecuteScalar());
  }
 }
 [Theory][InlineData(false)][InlineData(true)]
 public async Task IndependentRepositoriesAddingParentsDoNotLoseEdges(bool encrypted)
 {
  using var key=Prepare(encrypted);using var first=new CatalogRepository(Database,key);var a=Tag(first,"A");var b=Tag(first,"B");var d=Tag(first,"D");
  using var second=new CatalogRepository(Database,key);
  await Task.WhenAll(Task.Run(()=>first.AddTagParent(d,a)),Task.Run(()=>second.AddTagParent(d,b)));
  Assert.Equal([a,b],first.GetTagParents(d));
 }
}
