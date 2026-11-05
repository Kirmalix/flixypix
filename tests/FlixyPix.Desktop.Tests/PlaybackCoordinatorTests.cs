using Xunit;

namespace FlixyPix.Desktop.Tests;

public sealed class PlaybackCoordinatorTests
{
 [Fact]
 public async Task RepeatedStopsShareCleanupAndInvalidatePendingStarts()
 {
  var playback = new PlaybackCoordinator();
  var start = playback.BeginStart();
  var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  var calls = 0;
  Task Cleanup(PlaybackSession? session, VideoPlayerWindow? window) { ++calls; return release.Task; }
  var first = playback.StopAsync(Cleanup);
  Assert.False(playback.IsCurrentStart(start));
  var next = playback.BeginStart();
  Assert.Same(first, playback.PrepareStartAsync(Cleanup));
  Assert.True(playback.IsCurrentStart(next));
  Assert.Same(first, playback.StopAsync(Cleanup));
  Assert.False(playback.IsCurrentStart(next));
  Assert.Equal(1, calls); Assert.True(playback.IsStopping);
  release.SetResult(); await first;
  Assert.False(playback.IsStopping);
  var newest = playback.BeginStart();
  await playback.PrepareStartAsync((_, _) => Task.CompletedTask);
  Assert.True(playback.IsCurrentStart(newest));
 }

 [Fact]
 public async Task FailedCleanupIsObservedAndCanBeRetried()
 {
  var playback = new PlaybackCoordinator();
  await Assert.ThrowsAsync<InvalidOperationException>(() => playback.StopAsync((_, _) => throw new InvalidOperationException("expected")));
  Assert.False(playback.IsStopping);
  await playback.StopAsync((_, _) => Task.CompletedTask);
 }

 [Fact]
 public async Task LatePositionOfAIsDiscardedAndFinalSaveKeepsItsCapturedIdentity()
 {
  var a = Guid.NewGuid(); var b = Guid.NewGuid(); var current = a;
  var delayed = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
  var saved = new List<(Guid Id, double Seconds)>();
  var old = PlaybackPositionCheckpoint.SaveAsync(a, () => delayed.Task, () => current == a, (id, seconds) => saved.Add((id, seconds)));
  current = b;
  await PlaybackPositionCheckpoint.SaveAsync(b, () => Task.FromResult<double?>(7), () => current == b, (id, seconds) => saved.Add((id, seconds)));
  delayed.SetResult(42); await old;
  Assert.Equal(new[] {(b, 7d)}, saved);
  await PlaybackPositionCheckpoint.SaveAsync(a, () => Task.FromResult<double?>(42), () => true, (id, seconds) => saved.Add((id, seconds)));
  Assert.Equal((a, 42d), saved[1]);
 }
}
