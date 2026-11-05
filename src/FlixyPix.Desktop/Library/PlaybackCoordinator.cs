using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

internal sealed record PlaybackSession(Guid MediaId, MpvPlayer Player, VideoPlayerWindow Window, long? HistoryEntry);

internal static class PlaybackPositionCheckpoint
{
    internal static async Task SaveAsync(Guid mediaId, Func<Task<double?>> read, Func<bool> stillCurrent, Action<Guid, double> save)
    {
        var position = await read();
        if (stillCurrent() && position is { } seconds && double.IsFinite(seconds)) save(mediaId, Math.Max(seconds, 0));
    }
}

/// <summary>Owns the active resources and the revision of an asynchronous player start.</summary>
internal sealed class PlaybackCoordinator
{
    private long revision;
    private VideoPlayerWindow? pendingWindow;
    private Task stopping = Task.CompletedTask;
    internal PlaybackSession? Current { get; private set; }
    internal VideoPlayerWindow? Window => Current?.Window ?? pendingWindow;
    internal bool IsStopping => !stopping.IsCompleted;
    internal long BeginStart() => ++revision;
    internal bool IsCurrentStart(long request) => request == revision;

    internal void Stage(long request, VideoPlayerWindow window)
    {
        if (!IsCurrentStart(request) || IsStopping) throw new InvalidOperationException("The playback start is no longer current.");
        pendingWindow = window;
    }

    internal bool Publish(long request, PlaybackSession session)
    {
        if (!IsCurrentStart(request) || !ReferenceEquals(pendingWindow, session.Window)) return false;
        Current = session;
        pendingWindow = null;
        return true;
    }

    internal Task StopAsync(Func<PlaybackSession?, VideoPlayerWindow?, Task> release)
    {
        ++revision;
        return PrepareStartAsync(release);
    }

    internal Task PrepareStartAsync(Func<PlaybackSession?, VideoPlayerWindow?, Task> release)
    {
        if (IsStopping) return stopping;
        var session = Current;
        var window = Window;
        Current = null;
        pendingWindow = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stopping = completion.Task;
        _ = ReleaseAsync();
        return completion.Task;

        async Task ReleaseAsync()
        {
            try { await release(session, window); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        }
    }
}
