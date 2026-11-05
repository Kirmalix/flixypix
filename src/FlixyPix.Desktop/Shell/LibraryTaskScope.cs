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
    await Task.WhenAll(tasks);
    completion.TrySetResult();
   }
   catch (Exception error) { completion.TrySetException(error); }
  }
 }
}
