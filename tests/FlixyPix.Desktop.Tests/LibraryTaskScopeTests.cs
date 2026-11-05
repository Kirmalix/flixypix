using Xunit;

namespace FlixyPix.Desktop.Tests;

public sealed class LibraryTaskScopeTests
{
 [Fact]
 public async Task TypedWorkPropagatesResultsAndFailureWhileStopWaitsForCleanup()
 {
  var scope = new LibraryTaskScope(_ => { });
  Assert.Equal(42, await scope.Run<int>(_ => Task.FromResult(42)));
  await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Run<int>(_ => throw new InvalidOperationException("expected")));
  var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
  var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  var work = scope.Run<int>(async token =>
  {
   try { return await release.Task; }
   finally { await cleanup.Task; }
  });
  var stop = scope.StopAsync();
  release.SetException(new InvalidOperationException("expected"));
  Assert.False(stop.IsCompleted);
  cleanup.SetResult();
  await Assert.ThrowsAsync<InvalidOperationException>(() => work);
  await stop.WaitAsync(TimeSpan.FromSeconds(5));
  await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Run<int>(_ => Task.FromResult(99)));
 }
 [Fact]
 public async Task StopCancelsButWaitsForCleanupAndRejectsNewWork()
 {
  var errors = new List<Exception>(); var scope = new LibraryTaskScope(errors.Add);
  var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  var task = scope.Run(async token =>
  {
   using var registration = token.Register(() => cancelled.SetResult());
   await release.Task;
  });
  var stop = scope.StopAsync();
  await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
  Assert.False(stop.IsCompleted); Assert.Same(stop, scope.StopAsync());
  var ran = false; await scope.Run(_ => { ran=true;return Task.CompletedTask; });
  Assert.False(ran);
  release.SetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5)); await task;
  Assert.Empty(errors);
 }
 [Fact]
 public async Task ReportsFailureAndDrainsNestedWork()
 {
  var errors = new List<Exception>(); var scope = new LibraryTaskScope(errors.Add);
  await scope.Run(_ => throw new InvalidOperationException("expected"));
  Assert.Single(errors);
  var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  var parent = scope.Run(async _ =>
  {
   var child = scope.Run(async _ => await release.Task);
   started.SetResult(); await child;
  });
  await started.Task; var stop = scope.StopAsync(); Assert.False(stop.IsCompleted);
  release.SetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5)); await parent;
 }
}
