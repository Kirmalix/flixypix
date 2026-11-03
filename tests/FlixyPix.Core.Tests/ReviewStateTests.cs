using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;
namespace FlixyPix.Core.Tests;
public sealed class ReviewStateTests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-review-" + Guid.NewGuid().ToString("N"));
 public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
 [Theory] [InlineData(false)] [InlineData(true)]
 public async Task ScannerStatusUndoAndReopenPreserveOtherFields(bool encrypted)
 {
  Directory.CreateDirectory(root); var path = Path.Combine(root, "catalog.db"); var file = Path.Combine(root, "new.jpg"); File.WriteAllBytes(file, [1]);
  using (var initial = new CatalogRepository(path)) { }
  if (encrypted) CatalogEncryption.EnableEncryption(path, "synthetic-review-password");
  using var key = encrypted ? CatalogEncryption.Unlock(path, "synthetic-review-password") : null;
  Guid id;
  using (var db = new CatalogRepository(path, key))
  {
   await new MediaScanner(db).ScanAsync([file]); var item = db.FindPath(file)!; id = item.Id;
   Assert.Equal(ReviewState.Unreviewed, item.ReviewState);
   var undo = db.ApplyImmediateEdit([id], ImmediateEditFields.ReviewState, m => m with { ReviewState = ReviewState.Reviewed });
   db.Upsert(db.GetMedia(id)! with { Rating = 8, Description = "keep" });
   Assert.True(db.UndoImmediateEdit(undo)); Assert.Equal(8, db.GetMedia(id)!.Rating); Assert.Equal("keep", db.GetMedia(id)!.Description);
   db.ApplyImmediateEdit([id], ImmediateEditFields.ReviewState, m => m with { ReviewState = ReviewState.NeedsAttention });
   await new MediaScanner(db).ScanAsync([file]); Assert.Equal(ReviewState.NeedsAttention, db.GetMedia(id)!.ReviewState);
   var state = new ViewState { View = "inbox" }; Assert.Single(db.QueryPage(state.ToQuery(DateTimeOffset.Now)).Items);
   db.SaveViewState(state with { ReviewStates = [ReviewState.NeedsAttention] });
  }
  using var reopened = new CatalogRepository(path, key);
  Assert.Equal(ReviewState.NeedsAttention, reopened.GetMedia(id)!.ReviewState);
  Assert.Equal(ReviewState.NeedsAttention, Assert.Single(reopened.GetViewState()!.ReviewStates));
 }
 [Fact]
 public void StructuredStatusConditionsValidateAndStatusBatchRollsBackOnWriteFailure()
 {
  Directory.CreateDirectory(root); var path=Path.Combine(root,"search.db"); using var db=new CatalogRepository(path);
  var a=db.Upsert(new(){Path=Path.Combine(root,"a.jpg"),ReviewState=ReviewState.Unreviewed});
  var b=db.Upsert(new(){Path=Path.Combine(root,"b.jpg"),ReviewState=ReviewState.NeedsAttention});
  SearchExpression Condition(string value,SearchComparison compare=SearchComparison.Equals)=>SearchExpression.Where(new(){Field=SearchField.ReviewState,Comparison=compare,Text=value});
  Assert.Equal(a.Id,Assert.Single(db.QueryPage(new(){Expression=Condition("unreviewed")}).Items).Id);
  Assert.Equal(b.Id,Assert.Single(db.QueryPage(new(){Expression=Condition("Unreviewed",SearchComparison.NotEquals)}).Items).Id);
  Assert.Throws<ArgumentException>(()=>CatalogRepository.ValidateSearchExpression(Condition("99")));
  Assert.Throws<ArgumentException>(()=>db.SaveViewState(new(){ReviewStates=[(ReviewState)99]}));
  Assert.Empty(db.Sanitize(new(){ReviewStates=[(ReviewState)99]},out _).ReviewStates);
  Assert.Throws<ArgumentException>(()=>CatalogRepository.ValidateSearchExpression(Condition("Reviewed",SearchComparison.Contains)));
  using(var sql=new SqliteConnection("Data Source="+path))
  {sql.Open(); using var command=sql.CreateCommand();command.CommandText="CREATE TRIGGER fail_review BEFORE UPDATE ON media WHEN OLD.id='"+b.Id+"' BEGIN SELECT RAISE(ABORT,'injected status failure'); END;";command.ExecuteNonQuery();}
  Assert.Throws<SqliteException>(()=>db.ApplyImmediateEdit([a.Id,b.Id],ImmediateEditFields.ReviewState,m=>m with {ReviewState=ReviewState.Reviewed}));
  Assert.Equal(ReviewState.Unreviewed,db.GetMedia(a.Id)!.ReviewState);Assert.Equal(ReviewState.NeedsAttention,db.GetMedia(b.Id)!.ReviewState);
 }
 [Fact]
 public void CsvCreationAndReimportPreserveReviewStateAndNfoDoesNotChangeIt()
 {
  Directory.CreateDirectory(root); var csv = Path.Combine(root,"source.csv");
  using var source = new CatalogRepository(Path.Combine(root,"source.db"));
  var item = source.Upsert(new() { Path=Path.Combine(root,"a.mp4"), Kind=MediaKind.Video });
  CatalogInterchange.ExportCsv([item],csv);
  using var target = new CatalogRepository(Path.Combine(root,"target.db"));
  Assert.Equal(1,CatalogInterchange.ImportCsv(target,csv).Imported);
  Assert.Equal(ReviewState.Unreviewed,target.GetMedia(item.Id)!.ReviewState);
  target.Upsert(target.GetMedia(item.Id)! with {ReviewState=ReviewState.Reviewed});
  CatalogInterchange.ImportCsv(target,csv);
  Assert.Equal(ReviewState.Reviewed,target.GetMedia(item.Id)!.ReviewState);
  Assert.Equal(ReviewState.Reviewed,CatalogInterchange.ApplyNfo(target.GetMedia(item.Id)!, new NfoMetadata {Title="Updated"}).ReviewState);
 }
 [Fact]
 public void OldDocumentMigrationAndConflictingBatchUndoAreSafe()
 {
  Directory.CreateDirectory(root); var path = Path.Combine(root, "catalog.db"); Guid first, second;
  using (var db = new CatalogRepository(path))
  { first = db.Upsert(new() { Path = Path.Combine(root,"a.jpg") }).Id; second = db.Upsert(new() { Path = Path.Combine(root,"b.jpg") }).Id; }
  using (var sql = new SqliteConnection("Data Source=" + path))
  { sql.Open(); CatalogRepository.AddOrdinalCollation(sql); using var c = sql.CreateCommand(); c.CommandText = "UPDATE media SET document=json_remove(document,'$.reviewState'); PRAGMA user_version=18;"; c.ExecuteNonQuery(); }
  using var migrated = new CatalogRepository(path); Assert.NotNull(migrated.MigrationBackupPath);
  Assert.Equal(2, migrated.QueryPage(new() { ReviewStates = [ReviewState.Unspecified] }).TotalCount);
  var undo = migrated.ApplyImmediateEdit([first,second], ImmediateEditFields.ReviewState, m => m with {ReviewState=ReviewState.Reviewed});
  migrated.Upsert(migrated.GetMedia(second)! with { ReviewState=ReviewState.NeedsAttention });
  Assert.False(migrated.UndoImmediateEdit(undo)); Assert.Equal(ReviewState.Reviewed,migrated.GetMedia(first)!.ReviewState);
  Assert.Throws<ArgumentException>(()=>migrated.Upsert(migrated.GetMedia(first)! with {ReviewState=(ReviewState)99}));
 }
}
