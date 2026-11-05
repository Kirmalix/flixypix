namespace FlixyPix.Desktop;

/// <summary>Owns background work until the library has stopped accepting work and drained every request.</summary>
internal sealed class LibraryTaskScope(Action<Exception> report)
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly HashSet<Task> pending = [];
    private Task? draining;
    private bool stopping;
    internal CancellationToken Token => stop.Token;

    /// <summary>Awaited commands retain their failure contract; background Run jobs report failures instead.</summary>
    internal Task Track(Func<CancellationToken, Task> action) => Run(async token => { await action(token); return true; });

    internal Task<T> Run<T>(Func<CancellationToken, Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (stopping) return Task.FromCanceled<T>(new CancellationToken(true));
            pending.Add(completion.Task);
        }
        _ = ExecuteAsync();
        return completion.Task;

        async Task ExecuteAsync()
        {
            try { completion.TrySetResult(await action(stop.Token)); }
            catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
            finally { lock (gate) pending.Remove(completion.Task); }
        }
    }

    internal Task Run(Func<CancellationToken, Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (stopping) return Task.CompletedTask;
            pending.Add(completion.Task);
        }
        _ = ExecuteAsync();
        return completion.Task;

        async Task ExecuteAsync()
        {
            try { await action(stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error) { report(error); }
            finally
            {
                lock (gate) pending.Remove(completion.Task);
                completion.TrySetResult();
            }
        }
    }

    internal Task StopAsync()
    {
        TaskCompletionSource? completion;
        Task[] tasks;
        lock (gate)
        {
            if (draining is not null) return draining;
            stopping = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            draining = completion.Task;
            tasks = pending.ToArray();
        }
        _ = DrainAsync();
        return completion.Task;

        async Task DrainAsync()
        {
            try
            {
                try { stop.Cancel(); }
                catch (Exception error) { report(error); }
                // Typed jobs propagate failures to their caller; draining still owns their cleanup.
                try { await Task.WhenAll(tasks); }
                catch { }
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
    }
}
