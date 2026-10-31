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
 private LibraryFolderWatcher? _folderWatcher;
 private CancellationTokenSource? _folderCheck;
 private FolderSyncPlan? _folderPlan;
 private LibraryFoldersWindow? _foldersWindow;
 /// <summary>Time without file system events before a check runs.</summary>
 internal TimeSpan FolderSettleTime { get; set; } = TimeSpan.FromSeconds(2);
 internal bool FolderBannerShown => FolderBanner.Visibility == Visibility.Visible;
 internal LibraryFolderWatcher? FolderWatcher => _folderWatcher;

 private void InitializeFolderSync(bool checkAtStart)
 {
  Closing += (_, _) => { _folderPoll?.Stop(); _folderCheck?.Cancel(); _folderWatcher?.Dispose(); _folderWatcher = null; };
  if (!checkAtStart) return;
  // After the first page is shown, so the start stays fast even with large folders.
  Loaded += (_, _) =>
  {
   var start = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
   start.Tick += async (_, _) => { start.Stop(); RestartFolderWatcher(); await CheckLibraryFoldersAsync(background: true); };
   start.Start();
   _folderPoll = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
   _folderPoll.Tick += async (_, _) => { RestartFolderWatcher(); await CheckLibraryFoldersAsync(background: true); };
   _folderPoll.Start();
  };
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
 internal async Task CheckLibraryFoldersAsync(bool background = false)
 {
  if (!background) _dismissedFolderFindings = null;
  if (_checkingFolders) { _folderCheckPending = true; _manualFolderCheckPending |= !background; if (_folderCheckCompletion != null) await _folderCheckCompletion.Task; return; }
  _checkingFolders = true;
  _folderCheckCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
  try {
   do {
    _folderCheckPending = false;
    await CheckLibraryFoldersCoreAsync(background);
    background = !_manualFolderCheckPending; _manualFolderCheckPending = false;
   } while (_folderCheckPending && !_closing);
  } finally { _checkingFolders = false; _folderCheckCompletion.TrySetResult(true); }
 }
 internal async Task<FolderSyncPlan> BuildFolderPlanAsync(CancellationToken token, IProgress<FolderSyncProgress>? progress = null, bool background = false)
 {
  await _folderSyncGate.WaitAsync(token);
  try {
   var folders = _repository.GetLibraryFolders().Where(f => !background || f.Watch).ToList();
   return await Task.Run(() => { _repository.InitializeFolderReferences(folders, token); return _repository.PlanFolderSync(folders, token, progress); }, token);
  } finally { _folderSyncGate.Release(); }
 }
 private async Task CheckLibraryFoldersCoreAsync(bool background)
 {
  if (_closing || _repository.GetLibraryFolders().Count == 0) { HideFolderBanner(); return; }
  var cancellation = _folderCheck = new CancellationTokenSource();
  try
  {
   var folders = _repository.GetLibraryFolders().Where(f => !background || f.Watch).ToList();
   var plan = await BuildFolderPlanAsync(cancellation.Token, background: background);
   if (background && _operation == null && _foldersWindow == null)
   {
    var automatic = plan.Actionable.Where(c => c.Kind is FolderChangeKind.New or FolderChangeKind.Modified && folders.Any(f => f.Id == c.FolderId && f.AutoApply)).ToList();
    if (automatic.Count > 0) { await ApplyFolderSyncAsync(plan, automatic, false); plan = await Task.Run(() => _repository.PlanFolderSync(folders, cancellation.Token), cancellation.Token); }
   }
   if (_closing || cancellation.IsCancellationRequested) return;
   _folderPlan = plan;
   if (plan.Errors.Count > 0 || plan.Unreachable.Count > 0) _repository.SaveFolderSyncReport(plan, new(0,0,0,[]), applied: false);
   if (plan.Changes.Count == 0 && plan.Errors.Count == 0 && plan.Unreachable.Count == 0) { HideFolderBanner(); return; }
   FolderBannerApplyAction.Visibility = plan.Actionable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
   FolderBannerText.Text = T("Media folders: ") + LibraryFoldersWindow.Describe(plan);
   FolderBanner.Visibility = FolderFindingKey(plan) == _dismissedFolderFindings ? Visibility.Collapsed : Visibility.Visible;
  }
  catch (OperationCanceledException) { }
  catch (ObjectDisposedException) { }
  catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Status.Text = error.Message; }
 }

 private static string FolderFindingKey(FolderSyncPlan plan) => string.Join("|", plan.Changes.Select(c => $"{c.Kind}:{c.Path}:{c.Size}:{c.ModifiedAtUtc}")) + string.Join("|",plan.Errors) + string.Join("|",plan.Unreachable.Select(f => f.Path));
 internal async Task<int> RemoveMissingFolderFilesAsync(FolderSyncPlan plan, IReadOnlyList<FolderChange> chosen)
 {
  if (_operation != null) throw new InvalidOperationException(T("An operation is already running. You can cancel it below."));
  using var cancellation = new CancellationTokenSource(); _operation = cancellation;
  try { return await Task.Run(() => _repository.RemoveMissingFolderFiles(plan, chosen, cancellation.Token), cancellation.Token); }
  finally { _operation = null; }
 }
 internal void CancelFolderSync() { _operation?.Cancel(); _folderCheck?.Cancel(); }
 private void HideFolderBanner() { FolderBanner.Visibility = Visibility.Collapsed; }

 private void ShowLibraryFolders() => ShowLibraryFolders(null);
 private async void SyncFoldersClicked(object sender, RoutedEventArgs e) { ShowLibraryFolders(); if (_foldersWindow is { } window) await window.CheckAsync(); }
 private void ShowLibraryFolders(FolderSyncPlan? plan)
 {
  if (_foldersWindow is { IsLoaded: true }) { _foldersWindow.Activate(); return; }
  _foldersWindow = new LibraryFoldersWindow(this, _repository, plan, RestartFolderWatcher, ApplyFolderSyncAsync);
  _foldersWindow.Closed += async (_, _) => { _foldersWindow = null; await CheckLibraryFoldersAsync(); };
  _foldersWindow.Show();
 }

 internal async Task<FolderSyncResult> ApplyFolderSyncAsync(FolderSyncPlan plan, IReadOnlyList<FolderChange> chosen, bool ignoreDeclined)
 {
  HideFolderBanner();
  if (_operation != null) throw new InvalidOperationException(T("An operation is already running. You can cancel it below."));
  using var cancellation = new CancellationTokenSource();
  _operation = cancellation;
  Progress.Visibility = CancelButton.Visibility = Visibility.Visible;
  var progress = new Progress<FolderSyncProgress>(p => { _foldersWindow?.ReportProgress(p); Progress.IsIndeterminate = p.Total == null; Progress.Maximum = Math.Max(1, p.Total ?? 1); Progress.Value = p.Done; Status.Text = T(p.Phase) + " · " + p.Done + (p.Total is { } total ? " / " + total : "") + " · " + Path.GetFileName(p.Path); });
  FolderSyncResult result;
  try { result = await Task.Run(() => _repository.ApplyFolderSync(plan, chosen, ignoreDeclined, cancellation.Token, progress), cancellation.Token); }
  finally { _operation = null; Progress.IsIndeterminate = true; Progress.Visibility = CancelButton.Visibility = Visibility.Collapsed; }

  Status.Text = T("Media folders · ") + LibraryFoldersWindow.Result(result);
  if ((result.Imported > 0 || result.Updated > 0) && _operation == null) await RunOperation(T("Prepare new media"), async token => {
   try {
   var items = chosen.Where(c => c.Kind == FolderChangeKind.New || c.Kind == FolderChangeKind.Modified).Select(c => c.MediaId is { } id ? _repository.GetMedia(id) : _repository.GetMediaByPath(c.Path)).OfType<MediaItem>().DistinctBy(i => i.Id).ToList();
   if (ToolExists(_settings.Ffprobe, "ffprobe")) {
    var (_, errors) = await ReadTechnicalDataAsync(items.Where(i => i.Kind != MediaKind.Image).ToList(), token);
    result = result with { Skipped = result.Skipped.Concat(errors).ToList() };
   }
   foreach (var image in items.Where(i => i.Kind == MediaKind.Image)) {
    try {
     var info = await DesktopImaging.Service.GetInfoAsync(image.Path, token);
     if (_repository.GetMedia(image.Id) is { } current && current.FileModifiedAtUtc == image.FileModifiedAtUtc)
      _repository.Upsert(current with { Width = info.Width, Height = info.Height });
    } catch(Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { result = result with { Skipped = result.Skipped.Append(image.Path + ": " + error.Message).ToList() }; }
   }
   if (_settings.AutoGeneratePreviews) await PrepareMediaAsync(token, items, errors => result = result with { Skipped = result.Skipped.Concat(errors).ToList() });
   if (_repository.GetPreferences().Previews.FingerprintsOnImport) {
    var run = new FingerprintRun(_repository, ToolExists(_settings.Ffmpeg, "ffmpeg") ? _tools : null);
    await run.ComputeAsync(run.Missing(null, token, items), token);
   }
   if (_repository.GetPreferences().Previews.ClipsOnImport && ToolExists(_settings.Ffmpeg, "ffmpeg")) await CreatePreviewClipsAsync(items.Where(i => i.Kind == MediaKind.Video).ToList(), token);
   } catch(OperationCanceledException) { result = result with { Skipped = result.Skipped.Append(T("Operation cancelled")).ToList() }; }
   catch(Exception error) { ErrorLog.Write("Folder sync preparation",error); result = result with { Skipped = result.Skipped.Append(error.Message).ToList() }; }
  });
  else await RefreshAsync();
  _repository.SaveFolderSyncReport(plan with { Changes = chosen }, result);
  Status.Text = T("Media folders · ") + LibraryFoldersWindow.Result(result);
  return result;
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
