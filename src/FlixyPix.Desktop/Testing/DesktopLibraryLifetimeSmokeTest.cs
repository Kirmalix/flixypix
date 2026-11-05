using System.Reflection;
using System.IO;
using System.Windows;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
    internal async Task RunLibraryLifetimeProbeAsync(string directory)
    {
        ConfigureSmokeTools();
        await RunViewRestoreHostSmokeAsync(directory);
        await RunOpeningSmokeTestAsync(directory);
        var video = Path.Combine(directory, "fixtures", "playback-lifetime.mp4");
        if (File.Exists(video))
        {
            var original = _repository.Upsert(new MediaItem { Path = video, Kind = MediaKind.Video, Title = "Synthetic playback fixture", DurationSeconds = 1 });
            await RunPlaybackLifetimeSmokeAsync(original, directory);
        }
        _repository.Upsert(new MediaItem { Path = Path.Combine(directory, "synthetic.jpg"), Kind = MediaKind.Image, Title = "Synthetic lifetime fixture" });
        await RunLibraryLifetimeSmokeAsync(directory);
    }
    private async Task RunLibraryLifetimeSmokeAsync(string directory)
    {
        await RunStartupLoadingSmokeAsync(directory);
        ApplyViewState(new ViewState());
        await _lastRefresh;
        var card = MediaList.Items.Cast<MediaCard>().First();
        var original = card.Item;
        var rejectedThumbnail = false;
        try { await ApplyThumbnailFrom(new MediaItem { Path = Path.Combine(directory, "missing-thumbnail-" + Guid.NewGuid().ToString("N") + ".png") }, original); }
        catch (FileNotFoundException) { rejectedThumbnail = true; }
        if (!rejectedThumbnail) throw new InvalidOperationException("Awaited thumbnail command swallowed its failure.");
        var selection = _offWindowSelection;
        var gate = typeof(CatalogRepository).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_repository)!;
        try
        {
            _repository.Upsert(original with { Title = "Synthetic delayed tag refresh" });
            foreach (var changeView in new[] { false, true })
            {
                var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var release = new ManualResetEventSlim();
                var blocker = Task.Run(() =>
                {
                    lock (gate)
                    {
                        held.SetResult();
                        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Catalog gate fixture was not released.");
                    }
                });
                await held.Task;
                Task refresh;
                try
                {
                    refresh = RefreshTagsInPlaceAsync([original.Id]);
                    if (changeView) _browse.Invalidate();
                    else _offWindowSelection = [new MediaItem { Path = original.Path + ".selection" }];
                }
                finally { release.Set(); }
                await blocker;
                await refresh;
                if (card.Item.Title != original.Title || !changeView && _offWindowSelection.Count != 1)
                    throw new InvalidOperationException("Delayed tag refresh overwrote a newer view or selection.");
                _offWindowSelection = selection;
            }
        }
        finally
        {
            _offWindowSelection = selection;
            _repository.Upsert(original);
            await RefreshAsync(original.Id);
        }
        var window = new MainWindow(new DesktopSettings { DataDirectory = Path.Combine(directory, "folder-operation-close") }, persistPreferences: false);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await window.Startup;
        var plan = window._repository.PlanFolderSync();
        var operationGate = typeof(CatalogRepository).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window._repository)!;
        var operationHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseOperation = new ManualResetEventSlim();
        var operationBlocker = Task.Run(() =>
        {
            lock (operationGate)
            {
                operationHeld.SetResult();
                if (!releaseOperation.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Folder operation fixture was not released.");
            }
        });
        await operationHeld.Task;
        Task<int>? operation = null;
        try
        {
            operation = window.RemoveMissingFolderFilesAsync(plan, []);
            window.Close();
            if (!window._closeRequested || window._closing || window._operation?.IsCancellationRequested != true)
                throw new InvalidOperationException("Close did not wait for and cancel the folder operation.");
        }
        finally { releaseOperation.Set(); }
        await operationBlocker;
        try { await operation!; } catch (OperationCanceledException) { }
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await window.CloseTask;
        if (LibraryInstance.IsOpen(window._settings.DataDirectory)) throw new InvalidOperationException("Folder operation completion did not finish the pending close.");
    }
}
