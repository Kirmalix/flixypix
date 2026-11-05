using Xunit;

namespace FlixyPix.Desktop.Tests;

public sealed class LibraryTaskScopeTests
{
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
