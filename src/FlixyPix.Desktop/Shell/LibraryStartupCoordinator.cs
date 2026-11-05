namespace FlixyPix.Desktop;

internal enum LibraryStartupState { Loading, Ready, Cancelling, Cancelled, Failed }
internal sealed class LibraryStartupCoordinator
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal LibraryStartupState State { get; private set; }
    internal CancellationToken Token => cancellation.Token;
    internal Task Completion => completion.Task;
    internal Func<CancellationToken, Task>? BeforeOpen { get; set; }
    internal Func<CancellationToken, Task>? AfterOpen { get; set; }
    internal async Task OpenedAsync()
    {
        Token.ThrowIfCancellationRequested();
        if (AfterOpen != null) await AfterOpen(Token);
        Token.ThrowIfCancellationRequested();
    }
    internal async Task EnterAsync()
    {
        Token.ThrowIfCancellationRequested();
        if (BeforeOpen != null) await BeforeOpen(Token);
        Token.ThrowIfCancellationRequested();
    }
    internal void RequestCancellation()
    {
        if (State is LibraryStartupState.Ready or LibraryStartupState.Cancelled) return;
        State = LibraryStartupState.Cancelling;
        cancellation.Cancel();
    }
    internal void Ready()
    {
        Token.ThrowIfCancellationRequested();
        State = LibraryStartupState.Ready;
        completion.TrySetResult();
    }
    internal void Cancelled()
    {
        State = LibraryStartupState.Cancelled;
        completion.TrySetCanceled();
    }
    internal void Failed(Exception error)
    {
        State = LibraryStartupState.Failed;
        completion.TrySetException(error);
        _ = Completion.Exception;
    }
}
