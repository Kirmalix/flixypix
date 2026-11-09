using System.Windows;
using System.Windows.Controls;
using System.IO;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
    private async Task CloseLibraryAsync()
    {
        try
        {
            _backupTimer.Stop(); StopFolderSync();
            _draftTimer.Stop();
            _operation?.Cancel(); _positionTimer.Stop(); StopSlideshow(); _searchIndexStop.Cancel();
            SaveFolderViewportState(); SaveSuggestionFeedback();
            SaveViewNow(); if (_persistPreferences) SaveWindowPlace();
            _closing = true;
            _thumbnailCheck?.Cancel(); _wallRun?.Cancel(); _folderCheck?.Cancel(); StopHoverPreview();
            var draining = _libraryTasks.StopAsync();
            var queueWindow = _queueWindow; if (queueWindow != null) { queueWindow.Close(); await queueWindow.ClosedTask; }
            await StopPlayer();
            await draining;
            await _keyboardNavigationGate.WaitAsync(); _keyboardNavigationGate.Release();
            try { _repository.SaveBrowsingLists(); }
            catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { ErrorLog.Write("Keeping the lists", error); } // Costs only the next start some time.
            _repository.Dispose(); _instance.Dispose();
            // Nothing of this window may run on once it is gone; switching libraries keeps the process.
            _viewSaveTimer.Stop(); _searchTimer.Stop(); AppShell.Hiding -= PauseOnHide; AppShell.Muting -= MuteOnHide;
            if (_storageChange is { } change)
            {
                _storageTransferring = true;
                _operation = new CancellationTokenSource();
                var transfer = new Window { Owner = this, Title = T("Data and storage"), Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var cancel = new Button { Content = T("Cancel"), Margin = new Thickness(24), Padding = new Thickness(12) };
                cancel.Click += (_, _) => _operation?.Cancel();
                transfer.Content = new StackPanel { Children = { new TextBlock { Text = T("Copy current libraries; keep the originals"), Margin = new Thickness(24) }, cancel } };
                transfer.Closing += (_, closing) => { if (_storageTransferring) { closing.Cancel = true; _operation?.Cancel(); } };
                IsEnabled = false; transfer.Show();
                try { await ApplyStorageChangeAsync(change.Mode, change.Copy); _restartAfterClose = true; }
                catch (Exception failure)
                {
                    if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, StorageSwitchRecovery.JournalName)))
                    {
                        try { StorageSwitchRecovery.Recover(AppContext.BaseDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlixyPix"), useCurrentSession: true); _restartAfterClose = true; }
                        catch (Exception recovery) { Message.Show(recovery.Message, T("Data and storage")); Application.Current.Shutdown(1); }
                    }
                    else
                    {
                        Message.Show(failure.Message, T("Data and storage"));
                        new MainWindow(DesktopSettings.Load()).Show(); _restartAfterClose = false;
                    }
                }
                finally { _storageTransferring = false; _operation.Dispose(); _operation = null; IsEnabled = true; transfer.Close(); }
            }
            _closeCompleted = true;
            // This window let go of its catalog; it closes even when opening the next library fails unexpectedly.
            if (_switchTo is { } target) _ = Dispatcher.BeginInvoke(() => { try { OpenSwitchedLibrary(target); } finally { Close(); } });
            else _ = Dispatcher.BeginInvoke(Close);
            // A restart (new language) starts the next instance once this one let go of the catalog.
            if (_restartAfterClose && Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { _storageChange is { } completed && _restartAfterClose ? (completed.Mode == StorageMode.Portable ? "--portable" : "--profile") : AppStorage.Current.Mode == StorageMode.Portable ? "--portable" : "--profile" } });
        }
        catch (Exception error)
        {
            ErrorLog.Write("Closing library", error);
            CancelLibrarySwitch();
            _closing = true;
            // Even failed cleanup must drain catalog users before releasing the library.
            await _libraryTasks.StopAsync();
            try { await StopPlayer(); }
            catch (Exception playerError) { ErrorLog.Write("Closing player", playerError); }
            try { _repository.Dispose(); }
            finally { _instance.Dispose(); }
            _closeCompleted = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
    }
}
