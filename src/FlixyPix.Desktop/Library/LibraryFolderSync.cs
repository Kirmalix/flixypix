using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Keeps the catalog in step with the library folders: a check at start, watching while FlixyPix runs and a
/// quiet banner instead of a question when something changed.</summary>
public partial class MainWindow
{
    private readonly SemaphoreSlim _folderSyncGate = new(1, 1);
    private TaskCompletionSource<bool>? _folderCheckCompletion;
    private bool _checkingFolders, _folderCheckPending, _manualFolderCheckPending;
    private string? _dismissedFolderFindings;
    private DispatcherTimer? _folderPoll;
    private DispatcherTimer? _folderStart;
    private LibraryFolderWatcher? _folderWatcher;
    private CancellationTokenSource? _folderCheck, _backgroundFolderPlan;
    private FolderSyncPlan? _folderPlan;
    private LibraryFoldersWindow? _foldersWindow;
    /// <summary>Time without file system events before a check runs.</summary>
    internal TimeSpan FolderSettleTime { get; set; } = TimeSpan.FromSeconds(2);
    internal bool FolderBannerShown => FolderBanner.Visibility == Visibility.Visible;
    internal LibraryFolderWatcher? FolderWatcher => _folderWatcher;

    private void InitializeFolderSync(bool checkAtStart)
    {
        if (!checkAtStart) return;
        // After the first page is shown, so the start stays fast even with large folders.
        {
            var start = _folderStart = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            start.Tick += async (_, _) => { start.Stop(); if (_closing) return; RestartFolderWatcher(); await CheckLibraryFoldersAsync(background: true); };
            start.Start();
            _folderPoll = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _folderPoll.Tick += async (_, _) => { RestartFolderWatcher(); await CheckLibraryFoldersAsync(background: true); };
            _folderPoll.Start();
        }
    }

    private void StopFolderSync()
    {
        _folderStart?.Stop();
        _folderPoll?.Stop();
        _folderCheck?.Cancel();
        _folderWatcher?.Dispose();
        _folderWatcher = null;
    }

    internal void RestartFolderWatcher()
    {
        _folderWatcher?.Dispose(); _folderWatcher = null;
        if (_closing) return;
        var folders = _repository.GetLibraryFolders();
        if (!folders.Any(f => f.Watch)) return;
        _folderWatcher = new LibraryFolderWatcher(folders, _settings.DataDirectory, FolderSettleTime);
        _folderWatcher.Changed += async () => await CheckLibraryFoldersAsync(background: true);
    }

    /// <summary>Checks all library folders in the background and shows the banner when there is something to take over.</summary>
    internal Task CheckLibraryFoldersAsync(bool background = false) => _libraryTasks.Run(_ => CheckLibraryFoldersRequestAsync(background));
    private async Task CheckLibraryFoldersRequestAsync(bool background)
    {
        if (!background) _dismissedFolderFindings = null;
        if (_checkingFolders) { _folderCheckPending = true; _manualFolderCheckPending |= !background; if (_folderCheckCompletion != null) await _folderCheckCompletion.Task; return; }
        _checkingFolders = true;
        _folderCheckCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            do
            {
                _folderCheckPending = false;
                await CheckLibraryFoldersCoreAsync(background);
                background = !_manualFolderCheckPending; _manualFolderCheckPending = false;
            } while (_folderCheckPending && !_closing);
        }
        finally { _checkingFolders = false; _folderCheckCompletion.TrySetResult(true); }
    }
    internal Task<FolderSyncPlan> BuildFolderPlanAsync(CancellationToken token, IProgress<FolderSyncProgress>? progress = null, bool background = false)
     => _libraryTasks.Run<FolderSyncPlan>(async libraryToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, libraryToken);
        token = linked.Token;
        // A check the user asked for does not wait silently behind one in the background; it gives the newer answer anyway.
        if (background) _backgroundFolderPlan = linked; else _backgroundFolderPlan?.Cancel();
        try
        {
            await _folderSyncGate.WaitAsync(token);
            try
            {
                var folders = _repository.GetLibraryFolders().Where(f => !background || f.Watch).ToList();
                return await Task.Run(() => _repository.PlanFolderSync(folders, token, progress), token);
            }
            finally { _folderSyncGate.Release(); }
        }
        finally { if (_backgroundFolderPlan == linked) _backgroundFolderPlan = null; }
    });
    private async Task CheckLibraryFoldersCoreAsync(bool background)
    {
        if (_closing || _repository.GetLibraryFolders().Count == 0) { HideFolderBanner(); return; }
        var cancellation = _folderCheck = new CancellationTokenSource();
        try
        {
            var folders = _repository.GetLibraryFolders().Where(f => !background || f.Watch).ToList();
            var plan = await BuildFolderPlanAsync(cancellation.Token, background: background);
            if (background && (_operation == null || _preparingMedia) && _foldersWindow == null)
            {
                var automatic = plan.Actionable.Where(c => c.Kind is FolderChangeKind.New or FolderChangeKind.Modified && folders.Any(f => f.Id == c.FolderId && f.AutoApply)).ToList();
                if (automatic.Count > 0) { await ApplyFolderSyncAsync(plan, automatic, false); plan = await Task.Run(() => _repository.PlanFolderSync(folders, cancellation.Token), cancellation.Token); }
            }
            if (_closing || cancellation.IsCancellationRequested) return;
            _folderPlan = plan;
            if (plan.Errors.Count > 0 || plan.Unreachable.Count > 0) _repository.SaveFolderSyncReport(plan, new(0, 0, 0, []), applied: false);
            if (plan.Changes.Count == 0 && plan.Errors.Count == 0 && plan.Unreachable.Count == 0) { HideFolderBanner(); return; }
            FolderBannerApplyAction.Visibility = plan.Actionable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FolderBannerText.Text = T("Media folders: ") + LibraryFoldersWindow.Describe(plan);
            FolderBanner.Visibility = FolderFindingKey(plan) == _dismissedFolderFindings ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Status.Text = error.Message; }
    }

    private static string FolderFindingKey(FolderSyncPlan plan) => string.Join("|", plan.Changes.Select(c => $"{c.Kind}:{c.Path}:{c.Size}:{c.ModifiedAtUtc}")) + string.Join("|", plan.Errors) + string.Join("|", plan.Unreachable.Select(f => f.Path));
    internal Task<int> RemoveMissingFolderFilesAsync(FolderSyncPlan plan, IReadOnlyList<FolderChange> chosen)
     => _libraryTasks.Run<int>(async libraryToken =>
    {
        var beside = _preparingMedia;
        if (_operation != null && !beside) throw new InvalidOperationException(T("An operation is already running. You can cancel it below."));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(libraryToken); if (!beside) _operation = cancellation;
        try { return await Task.Run(() => _repository.RemoveMissingFolderFiles(plan, chosen, cancellation.Token), cancellation.Token); }
        finally { if (!beside) CompleteLibraryOperation(); }
    });
    internal void CancelFolderSync() { _operation?.Cancel(); _folderCheck?.Cancel(); }
    private void HideFolderBanner() { FolderBanner.Visibility = Visibility.Collapsed; }

    private void ShowLibraryFolders() => ShowLibraryFolders(null);
    private async void SyncFoldersClicked(object sender, RoutedEventArgs e) { ShowLibraryFolders(); if (_foldersWindow is { } window) await window.CheckAsync(); }
    private void ShowLibraryFolders(FolderSyncPlan? plan)
    {
        if (_foldersWindow is { IsLoaded: true }) { _foldersWindow.Activate(); return; }
        _foldersWindow = new LibraryFoldersWindow(this, _repository, plan, RestartFolderWatcher, ApplyFolderSyncAsync, _libraryTasks);
        _foldersWindow.Closed += async (_, _) => { _foldersWindow = null; await CheckLibraryFoldersAsync(); };
        _foldersWindow.Show();
    }

    internal Task<FolderSyncResult> ApplyFolderSyncAsync(FolderSyncPlan plan, IReadOnlyList<FolderChange> chosen, bool ignoreDeclined)
     => _libraryTasks.Run<FolderSyncResult>(async libraryToken =>
    {
        HideFolderBanner();
        // Preparing earlier media keeps the operation slot; taking over beside it is safe, it is one short transaction.
        var beside = _preparingMedia;
        if (_operation != null && !beside) throw new InvalidOperationException(T("An operation is already running. You can cancel it below."));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(libraryToken);
        if (!beside) { _operation = cancellation; Progress.Visibility = CancelButton.Visibility = Visibility.Visible; }
        var progress = new Progress<FolderSyncProgress>(p => { _foldersWindow?.ReportProgress(p); if (beside) return; Progress.IsIndeterminate = p.Total == null; Progress.Maximum = Math.Max(1, p.Total ?? 1); Progress.Value = p.Done; Status.Text = T(p.Phase) + " · " + p.Done + (p.Total is { } total ? " / " + total : "") + " · " + Path.GetFileName(p.Path); });
        FolderSyncResult result; var arrived = new List<MediaItem>(); var adopted = 0;
        try
        {
            result = await Task.Run(() => _repository.ApplyFolderSync(plan, chosen, ignoreDeclined, cancellation.Token, progress), cancellation.Token);
            if (result.Imported > 0 || result.Updated > 0)
            {
                arrived = chosen.Where(c => c.Kind is FolderChangeKind.New or FolderChangeKind.Modified).Select(c => c.MediaId is { } id ? _repository.GetMedia(id) : _repository.GetMediaByPath(c.Path)).OfType<MediaItem>().ToList();
                // What was taken over stays, also when reading the files' own metadata is cancelled.
                try { adopted = await AdoptFileMetadataAsync(arrived.Select(i => i.Id).ToList(), cancellation.Token, (done, total) => ((IProgress<FolderSyncProgress>)progress).Report(new("Read metadata", done, total, ""))); }
                catch (OperationCanceledException) { }
            }
        }
        finally { if (!beside) { CompleteLibraryOperation(); Progress.IsIndeterminate = true; Progress.Visibility = CancelButton.Visibility = Visibility.Collapsed; } }

        _repository.SaveFolderSyncReport(plan with { Changes = chosen }, result);
        Status.Text = T("Media folders · ") + LibraryFoldersWindow.Result(result) + (adopted > 0 ? " · " + Tn(adopted, "Metadata imported for 1 item", "Metadata imported for {0:N0} items") : "");
        await RefreshAsync();
        // New and changed media get previews and their analysis right away, one queue for all of them.
        if (arrived.Count > 0) QueueMediaPreparation(arrived);
        return result;
    });

    private readonly List<Guid> _preparationQueue = [];
    private readonly List<string> _preparationErrors = [];
    private bool _preparingMedia;
    internal bool IsPreparingMedia => _preparingMedia;
    private int _preparationDone, _preparationTotal;
    /// <summary>Raised on the window thread whenever the preparation of new media moves on.</summary>
    internal event Action? MediaPreparationChanged;
    internal (int Done, int Total, bool Running, IReadOnlyList<string> Errors) MediaPreparation
    {
        get { lock (_preparationErrors) return (_preparationDone, _preparationTotal, _preparingMedia || _preparationQueue.Count > 0, _preparationErrors.ToList()); }
    }

    /// <summary>Adds media to the preparation queue and works on it unless another operation runs; that one starts the
    /// queue when it ends.</summary>
    internal void QueueMediaPreparation(IEnumerable<MediaItem> items)
    {
        if (!_preparingMedia && _preparationQueue.Count == 0) { _preparationDone = _preparationTotal = 0; lock (_preparationErrors) _preparationErrors.Clear(); }
        foreach (var item in items)
            if (!_preparationQueue.Contains(item.Id)) { _preparationQueue.Add(item.Id); _preparationTotal++; }
        MediaPreparationChanged?.Invoke();
        _ = PrepareQueuedMediaAsync();
    }

    private async Task PrepareQueuedMediaAsync()
    {
        if (_preparingMedia || _operation != null || _closing || _preparationQueue.Count == 0) return;
        _preparingMedia = true;
        try
        {
            await RunOperation(T("Prepare new media"), async token =>
            {
                try
                {
                    while (_preparationQueue.Count > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        var batch = _preparationQueue.ToList(); _preparationQueue.Clear();
                        // Videos first: they wait longest for a preview and are what one looks for after an import.
                        var items = batch.Select(_repository.GetMedia).OfType<MediaItem>().OrderBy(i => i.Kind != MediaKind.Video).ToList();
                        Interlocked.Add(ref _preparationDone, batch.Count - items.Count);
                        void Report(IEnumerable<string> errors) { lock (_preparationErrors) _preparationErrors.AddRange(errors); }
                        void Done() { Interlocked.Increment(ref _preparationDone); _ = Dispatcher.InvokeAsync(() => MediaPreparationChanged?.Invoke()); }
                        if (_settings.AutoGeneratePreviews) await PrepareMediaAsync(token, items, Report, Done);
                        else
                            foreach (var image in items)
                            {
                                try
                                {
                                    if (image.Kind != MediaKind.Image) continue;
                                    var info = await DesktopImaging.Service.GetInfoAsync(image.Path, token);
                                    if (_repository.GetMedia(image.Id) is { } current && current.FileModifiedAtUtc == image.FileModifiedAtUtc)
                                        _repository.Upsert(current with { Width = info.Width, Height = info.Height });
                                }
                                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { Report([image.Path + ": " + error.Message]); }
                                finally { Done(); }
                            }
                        MediaPreparationChanged?.Invoke();
                        // Codecs and other technical details, fingerprints and clips follow the previews.
                        if (ToolExists(_settings.Ffprobe, "ffprobe")) Report((await ReadTechnicalDataAsync(items.Where(i => i.Kind != MediaKind.Image).ToList(), token)).Errors);
                        if (_repository.GetPreferences().Previews.FingerprintsOnImport)
                        {
                            var run = new FingerprintRun(_repository, ToolExists(_settings.Ffmpeg, "ffmpeg") ? _tools : null);
                            await run.ComputeAsync(run.Missing(null, token, items), token);
                        }
                        if (_repository.GetPreferences().Previews.ClipsOnImport && ToolExists(_settings.Ffmpeg, "ffmpeg")) await CreatePreviewClipsAsync(items.Where(i => i.Kind == MediaKind.Video).ToList(), token);
                    }
                }
                catch (OperationCanceledException) { _preparationQueue.Clear(); throw; }
                catch (Exception error) { ErrorLog.Write("Folder sync preparation", error); lock (_preparationErrors) _preparationErrors.Add(error.Message); }
            });
        }
        finally { _preparingMedia = false; MediaPreparationChanged?.Invoke(); }
        if (_preparationQueue.Count > 0) _ = PrepareQueuedMediaAsync();
        // Without the folder window nobody sees the list, so the files that failed are named once.
        else if (MediaPreparation.Errors.Count is > 0 and var failed && _foldersWindow == null)
            ShowToast(Tn(failed, "1 file could not be prepared.", "{0:N0} files could not be prepared."), MessageBoxImage.Warning);
    }

    private async void FolderBannerApply(object sender, RoutedEventArgs e)
    {
        if (_folderPlan is not { } plan) return;
        if (plan.Actionable.Count == 0) { ShowLibraryFolders(plan); return; }
        try { await ApplyFolderSyncAsync(plan, plan.Actionable, ignoreDeclined: false); }
        catch (OperationCanceledException) { Status.Text = T("Operation cancelled"); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException) { Status.Text = error.Message; }
        await CheckLibraryFoldersAsync();
    }

    private void FolderBannerReview(object sender, RoutedEventArgs e) { HideFolderBanner(); ShowLibraryFolders(_folderPlan); }

    /// <summary>Hidden until the next change in a watched folder or the next check.</summary>
    private void FolderBannerDismiss(object sender, RoutedEventArgs e) { _dismissedFolderFindings = _folderPlan == null ? null : FolderFindingKey(_folderPlan); HideFolderBanner(); }

    /// <summary>A file removed from the catalog stays out even though it is still in a library folder.</summary>
    private void ForgetInLibraryFolders(string path)
    {
        try { _repository.IgnoreInLibraryFolders([path]); }
        catch (Exception error) when (error is IOException or ArgumentException) { Status.Text = T("Hiding in the media folder failed: ") + error.Message; }
    }
}
