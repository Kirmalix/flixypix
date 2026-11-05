using System.IO;
using Xunit;
namespace FlixyPix.Desktop.Tests;
public sealed class LibraryStartupCoordinatorTests
{
    [Fact]
    public async Task CancellationDoesNotSignalCompletionBeforeCleanup()
    {
        var startup = new LibraryStartupCoordinator();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        startup.BeforeOpen = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        var opening = startup.EnterAsync();
        await entered.Task;
        startup.RequestCancellation(); startup.RequestCancellation();
        Assert.False(startup.Completion.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        startup.Cancelled();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.Completion);
    }
    [Fact]
    public async Task ReadinessRejectsCancelledStartsAndFailuresRemainObservable()
    {
        var cancelled = new LibraryStartupCoordinator(); cancelled.RequestCancellation();
        Assert.ThrowsAny<OperationCanceledException>(cancelled.Ready);
        var failed = new LibraryStartupCoordinator(); failed.Failed(new IOException("synthetic"));
        await Assert.ThrowsAsync<IOException>(() => failed.Completion);
        var ready = new LibraryStartupCoordinator(); ready.Ready(); ready.RequestCancellation();
        await ready.Completion; Assert.Equal(LibraryStartupState.Ready, ready.State);
    }
}
