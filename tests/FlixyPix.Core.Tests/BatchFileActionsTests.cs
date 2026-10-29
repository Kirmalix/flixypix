using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class BatchFileActionsTests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(),"flixypix-batch-tests-" + Guid.NewGuid().ToString("N"));
 public BatchFileActionsTests() => Directory.CreateDirectory(root);
 private string Fixture(string relative,byte[] bytes) { var path = Path.Combine(root,relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path,bytes); return path; }
 private string Target() { var path = Path.Combine(root,"target"); Directory.CreateDirectory(path); return path; }

 [Fact]
 public async Task CopyPreviewDoesNotWriteAndNeverOverwritesExistingOrSameBasenameTargets()
 {
  using var catalog = new CatalogRepository(Path.Combine(root,"catalog.db"));
  var one = catalog.Upsert(new() { Path = Fixture("a/image.jpg",[1,2,3]) }); var two = catalog.Upsert(new() { Path = Fixture("b/image.jpg",[4,5]) });
  var folder = Target(); var occupied = Fixture("target/image.jpg",[9]);
  var cancelled = await catalog.PlanFileBatchAsync([one.Id,two.Id],BatchFileAction.Copy,folder);
  Assert.False(cancelled.CanExecute); Assert.All(cancelled.Items,item => Assert.Equal(BatchFileItemState.Blocked,item.State));
  Assert.Single(Directory.GetFiles(folder));
  var skipped = await catalog.PlanFileBatchAsync([one.Id,two.Id],BatchFileAction.Copy,folder,BatchCollisionPolicy.Skip);
  Assert.All(skipped.Items,item => Assert.Equal(BatchFileItemState.Skipped,item.State));
  var plan = await catalog.PlanFileBatchAsync([one.Id,two.Id],BatchFileAction.Copy,folder,BatchCollisionPolicy.UniqueName);
  Assert.Equal(new[] {"image (2).jpg","image (3).jpg"},plan.Items.Select(item => Path.GetFileName(item.TargetPath)));
  var result = await catalog.ExecuteFileBatchAsync(plan); Assert.All(result.Items,item => Assert.Equal(BatchFileOutcome.Completed,item.Outcome));
  Assert.Equal(new byte[] {9},File.ReadAllBytes(occupied)); Assert.Equal(new byte[] {1,2,3},File.ReadAllBytes(plan.Items[0].TargetPath));
  Assert.Equal(one.Path,catalog.GetMedia(one.Id)!.Path); Assert.True(File.Exists(one.Path)); Assert.True(File.Exists(two.Path));
 }

 [Theory]
 [InlineData(BatchFileAction.Move)]
 [InlineData(BatchFileAction.Rename)]
 public async Task MoveAndRenamePreserveIdentityAndAllDependentReferences(BatchFileAction action)
 {
  using var catalog = new CatalogRepository(Path.Combine(root,"catalog.db"));
  var item = catalog.Upsert(new() { Path = Fixture("a/clip.mp4",[1,2,3]), Rating = 9, Tags = ["Important"] });
  var scene = catalog.UpsertScene(new() { MediaId = item.Id, Title = "Scene", StartSeconds = 0.1, EndSeconds = 1 });
  catalog.UpsertBookmark(new() { MediaId = item.Id, Title = "Bookmark", PositionSeconds = 0.5 });
  catalog.UpsertPlaylist(new() { Name = "Collection", MediaIds = [item.Id], Entries = [new(item.Id,scene.Id)] });
  var plan = await catalog.PlanFileBatchAsync([item.Id],action,action == BatchFileAction.Move ? Target() : null,renameTemplate:"{name}-{index}{ext}");
  var result = await catalog.ExecuteFileBatchAsync(plan); Assert.Equal(BatchFileOutcome.Completed,Assert.Single(result.Items).Outcome);
  Assert.False(File.Exists(item.Path)); var after = catalog.GetMedia(item.Id)!;
  Assert.Equal(plan.Items[0].TargetPath,after.Path); Assert.Equal(9,after.Rating); Assert.Equal(item.Tags,after.Tags);
  Assert.Single(catalog.GetScenes(item.Id)); Assert.Single(catalog.GetBookmarks(item.Id)); Assert.Equal(item.Id,Assert.Single(catalog.GetPlaylists()).Entries[0].MediaId);
  Assert.Equal(new byte[] {1,2,3},File.ReadAllBytes(after.Path)); Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(after.Path)!,".flixypix-file-*"));
 }

 [Fact]
 public async Task SourceOrTargetChangesSincePreviewFailWithoutDeletingEitherFile()
 {
  using var catalog = new CatalogRepository(Path.Combine(root,"catalog.db"));
  var one = catalog.Upsert(new() { Path = Fixture("a/one.jpg",[1,2,3]) }); var two = catalog.Upsert(new() { Path = Fixture("a/two.jpg",[4,5,6]) });
  var plan = await catalog.PlanFileBatchAsync([one.Id,two.Id],BatchFileAction.Move,Target());
  var stamp = File.GetLastWriteTimeUtc(one.Path); File.WriteAllBytes(one.Path,[7,8,9]); File.SetLastWriteTimeUtc(one.Path,stamp);
  File.WriteAllBytes(plan.Items[1].TargetPath,[0]);
  var result = await catalog.ExecuteFileBatchAsync(plan); Assert.All(result.Items,item => Assert.Equal(BatchFileOutcome.Failed,item.Outcome));
  Assert.Equal(new byte[] {7,8,9},File.ReadAllBytes(one.Path)); Assert.Equal(new byte[] {4,5,6},File.ReadAllBytes(two.Path));
  Assert.Equal(new byte[] {0},File.ReadAllBytes(plan.Items[1].TargetPath)); Assert.Equal(one.Path,catalog.GetMedia(one.Id)!.Path);
 }

 [Fact]
 public async Task CancellationReportsCompletedActionsAndLeavesRemainingSourceUntouched()
 {
  using var catalog = new CatalogRepository(Path.Combine(root,"catalog.db"));
  var one = catalog.Upsert(new() { Path = Fixture("a/one.jpg",[1]) }); var two = catalog.Upsert(new() { Path = Fixture("a/two.jpg",[2]) });
  var plan = await catalog.PlanFileBatchAsync([one.Id,two.Id],BatchFileAction.Move,Target()); using var cancellation = new CancellationTokenSource();
  var result = await catalog.ExecuteFileBatchAsync(plan,cancellation.Token,new ImmediateProgress<BatchFileProgress>(_ => cancellation.Cancel()));
  Assert.True(result.Cancelled); Assert.Equal(BatchFileOutcome.Completed,Assert.Single(result.Items).Outcome);
  Assert.False(File.Exists(one.Path)); Assert.True(File.Exists(two.Path)); Assert.Equal(two.Path,catalog.GetMedia(two.Id)!.Path);
 }

 [Fact]
 public async Task ArchiveCacheEntriesAreSkippedAndUnsafeRenameTemplateRejected()
 {
  using var catalog = new CatalogRepository(Path.Combine(root,"catalog.db"));
  var cache = catalog.Upsert(new() { Path = Fixture("cache/image.jpg",[1]), Extra = new() { ["archivePath"] = "unchanged.zip" } });
  var normal = catalog.Upsert(new() { Path = Fixture("a/image.jpg",[2]) });
  var plan = await catalog.PlanFileBatchAsync([cache.Id,normal.Id],BatchFileAction.Copy,Target());
  Assert.Equal(BatchFileItemState.Skipped,plan.Items[0].State); Assert.True(plan.CanExecute);
  await Assert.ThrowsAsync<ArgumentException>(() => catalog.PlanFileBatchAsync([normal.Id],BatchFileAction.Rename,null,renameTemplate:"../escape.jpg"));
  Assert.True(File.Exists(cache.Path));
 }

 [Fact]
 public async Task CatalogUpdateFailureRemovesOnlyVerifiedNewCopyAndRetainsOriginal()
 {
  using var catalog = new CatalogRepository(Path.Combine(root,"catalog.db"));
  var source = Fixture("a/image.jpg",[1,2,3]); var item = catalog.Upsert(new() { Path = source, ThumbnailPath = source });
  var plan = await catalog.PlanFileBatchAsync([item.Id],BatchFileAction.Move,Target());
  using (var connection = new SqliteConnection($"Data Source={catalog.DatabasePath};Pooling=False"))
  {
   connection.Open(); using var command = connection.CreateCommand();
   command.CommandText = "CREATE TRIGGER reject_test_path BEFORE UPDATE ON media BEGIN SELECT RAISE(ABORT,'Test catalog update failure'); END";
   command.ExecuteNonQuery();
  }
  var result = await catalog.ExecuteFileBatchAsync(plan);
  Assert.Equal(BatchFileOutcome.Failed,Assert.Single(result.Items).Outcome);
  Assert.True(File.Exists(source)); Assert.False(File.Exists(plan.Items[0].TargetPath));
  Assert.Equal(source,catalog.GetMedia(item.Id)!.Path); Assert.Equal(source,catalog.GetMedia(item.Id)!.ThumbnailPath);
  Assert.Empty(Directory.GetFiles(Target(),".flixypix-file-*"));
 }

 private sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
 public void Dispose() => Directory.Delete(root,true);
}
