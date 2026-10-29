using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Keeps the catalog in step with the library folders: a check at start, watching while FlixyPix runs and a
/// quiet banner instead of a question when something changed.</summary>
public partial class MainWindow
{
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
  Closing += (_, _) => { _folderCheck?.Cancel(); _folderWatcher?.Dispose(); _folderWatcher = null; };
  if (!checkAtStart) return;
  // After the first page is shown, so the start stays fast even with large folders.
  Loaded += (_, _) =>
  {
   var start = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
   start.Tick += async (_, _) => { start.Stop(); RestartFolderWatcher(); await CheckLibraryFoldersAsync(); };
   start.Start();
  };
 }

 internal void RestartFolderWatcher()
 {
  _folderWatcher?.Dispose(); _folderWatcher = null;
  if (_closing) return;
  var folders = _repository.GetLibraryFolders();
  if (!folders.Any(f => f.Watch)) return;
  _folderWatcher = new LibraryFolderWatcher(folders, _settings.DataDirectory, FolderSettleTime);
  _folderWatcher.Changed += async () => await CheckLibraryFoldersAsync();
 }

 /// <summary>Checks all library folders in the background and shows the banner when there is something to take over.</summary>
 internal async Task CheckLibraryFoldersAsync()
 {
  if (_closing || _repository.GetLibraryFolders().Count == 0) { HideFolderBanner(); return; }
  _folderCheck?.Cancel();
  var cancellation = _folderCheck = new CancellationTokenSource();
  try
  {
   var plan = await Task.Run(() => _repository.PlanFolderSync(cancellationToken: cancellation.Token), cancellation.Token);
   if (_closing || cancellation.IsCancellationRequested) return;
   _folderPlan = plan;
   if (plan.Actionable.Count == 0) { HideFolderBanner(); return; }
   FolderBannerText.Text = T("Media folders: ") + LibraryFoldersWindow.Describe(plan with { Changes = plan.Actionable, Unreachable = [], Errors = [] });
   FolderBanner.Visibility = Visibility.Visible;
  }
  catch (OperationCanceledException) { }
  catch (ObjectDisposedException) { }
 }

 private void HideFolderBanner() { FolderBanner.Visibility = Visibility.Collapsed; }

 private void ShowLibraryFolders() => ShowLibraryFolders(null);
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
  var result = await Task.Run(() => _repository.ApplyFolderSync(plan, chosen, ignoreDeclined));
  Status.Text = T("Media folders · ") + LibraryFoldersWindow.Result(result);
  if (result.Imported > 0 && AfterImportWanted && _operation == null) await RunOperation(T("Prepare new media"), AfterImportAsync);
  else await RefreshAsync();
  return result;
 }

 private async void FolderBannerApply(object sender, RoutedEventArgs e)
 {
  if (_folderPlan is not { } plan) return;
  await ApplyFolderSyncAsync(plan, plan.Actionable, ignoreDeclined: false);
  await CheckLibraryFoldersAsync();
 }

 private void FolderBannerReview(object sender, RoutedEventArgs e) { HideFolderBanner(); ShowLibraryFolders(_folderPlan); }

 /// <summary>Hidden until the next change in a watched folder or the next check.</summary>
 private void FolderBannerDismiss(object sender, RoutedEventArgs e) => HideFolderBanner();

 /// <summary>A file removed from the catalog stays out even though it is still in a library folder.</summary>
 private void ForgetInLibraryFolders(string path)
 {
  try { _repository.IgnoreInLibraryFolders([path]); }
  catch (Exception error) when (error is IOException or ArgumentException) { Status.Text = T("Hiding in the media folder failed: ") + error.Message; }
 }
}
