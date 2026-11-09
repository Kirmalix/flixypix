using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
    private readonly LibraryStartupCoordinator _startup = new();
    private bool _startupReady;
    private bool _returnToSelectionOnCancel = true;
    private StartupSurface _startupSurface = null!;
    private readonly DispatcherTimer _startupTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    /// <summary>Time the start worked; a question that waits for an answer does not count.</summary>
    private readonly System.Diagnostics.Stopwatch _startupWatch = new();
    private bool _startupAsks, _startupQuestionSeen;
    private static bool _firstWindowLogged;

    private void InitializeStartup(CatalogKey? suppliedKey)
    {
        var key = suppliedKey?.Clone();
        Startup = _startup.Completion;
        RootContent.IsEnabled = false;
        RootContent.Visibility = Visibility.Hidden;
        _startupSurface = new StartupSurface(Privacy.Settings.NeutralTitle, Math.Clamp(_settings.SidebarWidth, SidebarMinimum, SidebarMaximum), _settings.SidebarCollapsed, RequestStartupCancellation);
        _startupSurface.SetPhase(T("Checking library"), .05);
        _startupTimer.Tick += (_, _) => WatchStartup();
        Content = null;
        var host = new Grid(); host.Children.Add(RootContent); host.Children.Add(_startupSurface); Content = host;
        ContentRendered += Begin;
        async void Begin(object? sender, EventArgs args)
        {
            ContentRendered -= Begin;
            LogFirstWindow();
            _startupWatch.Start();
            _startupTimer.Start();
            try
            {
                // An encrypted library asks first; the logo then belongs to the moment it really loads.
                _startupAsks = key == null && CatalogEncryption.IsEncrypted(Path.Combine(_settings.DataDirectory, "catalog.db"));
                if (!_startupAsks) _startupSurface.PlayIntro();
                await Dispatcher.Yield(DispatcherPriority.Background);
                await _startup.EnterAsync();
                await InitializeLibraryAsync(key);
                _startup.Token.ThrowIfCancellationRequested();
                StartupMark("library ready");
                RootContent.Visibility = Visibility.Visible;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                StartupMark("first picture");
                _startupReady = true;
                RootContent.IsEnabled = true;
                if (_view == "home") _home.Reveal();
                _startupSurface.FadeOut();
                _startup.Ready();
                RecordStartupPhase("Ready");
                FillBrowsingAfterStart();
                WarmSearchIndex();
            }
            catch (OperationCanceledException)
            {
                await ReleaseFailedStartAsync();
                key?.Dispose(); key = null;
                _startupTimer.Stop();
                _startup.Cancelled();
                RecordStartupPhase("Cancelled");
                _closeCompleted = true;
                if (_persistPreferences && _returnToSelectionOnCancel) ((App)Application.Current).ReturnToLibrarySelection(this);
                else Close();
            }
            catch (Exception error)
            {
                _closing = true;
                await ReleaseFailedStartAsync();
                ErrorLog.Write("Library startup", error);
                _startupSurface.ShowFailure(T("The library could not be opened.\n{0}", ErrorLog.RedactPaths(error.Message)), LibraryName, RequestStartupCancellation, () => { _closeCompleted = true; Close(); });
                _startup.Failed(error);
                RecordStartupPhase("Failed:" + error.GetType().Name);
                _ = Startup.Exception; // The visible failure panel observes the error; callers may still await it.
            }
            finally { _startupTimer.Stop(); key?.Dispose(); }
        }
    }

    /// <summary>While a question of the start is open (password, conversion), it waits for the person and nothing runs on.</summary>
    private void WatchStartup()
    {
        var open = OwnedWindows.Cast<Window>().Any(window => window.IsVisible);
        if (open) { _startupQuestionSeen = true; _startupWatch.Stop(); }
        else if (!_startupWatch.IsRunning) _startupWatch.Start();
        _startupSurface.SetWaiting(open);
        if (!open && _startupAsks && _startupQuestionSeen) _startupSurface.PlayIntro();
        _startupSurface.ShowTime(_startupWatch.Elapsed);
    }

    private void SetStartupPhase(string phase, double share, double seconds = .4)
    {
        _startupSurface.SetPhase(phase, share, seconds);
        RecordStartupPhase(phase);
    }

    /// <summary>How long Windows and .NET took until the first picture, which the time of the library does not contain.</summary>
    private void LogFirstWindow()
    {
        if (_firstWindowLogged) return;
        _firstWindowLogged = true;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        RecordStartupPhase($"First window {(DateTime.Now - process.StartTime).TotalMilliseconds:0}ms after process start");
    }

    /// <summary>Set when the first view left its lists for later, and finished once they are there.</summary>
    private TaskCompletionSource? _browsingFilled;

    /// <summary>Folders, filters, tag lists and the numbers of the sidebar after the first view. They are read beside the shared
    /// connection, so the window answers meanwhile, and they are put in place without touching the tiles.</summary>
    private void FillBrowsingAfterStart()
    {
        if (_searchCountsWaiting is { } rows) { _searchCountsWaiting = null; CountSmartCollections(rows); }
        if (_browsingFilled is not { } filled) return;
        _ = _libraryTasks.Run(async token =>
        {
            try
            {
                // Changes during the reading make it useless. A few tries, then the usual way, so the lists are right in the end.
                for (var attempt = 0; attempt < 3 && !await Task.Run(() => _repository.LoadBrowsingAside(token), token); attempt++) { }
                var browse = await Task.Run(() => PrepareBrowsing(_repository.GetFacets(token), _repository.GetPreferences(), _repository.GetTagUsage(), _repository.GetNavigationCounts()), token);
                if (_closing || token.IsCancellationRequested) return;
                var refreshing = _refreshing; _refreshing = true;
                try { ApplyBrowsing(browse); } finally { _refreshing = refreshing; }
                UpdateNavigation(); RenderTagFilterChips(); RecordStartupPhase("Lists filled");
                await Task.Run(_repository.SaveBrowsingLists, token); // The next start finds them.
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
            catch (Microsoft.Data.Sqlite.SqliteException error) { ErrorLog.Write("Keeping the lists", error); }
            finally { filled.TrySetResult(); }
        });
    }

    /// <summary>A step inside a phase, while the program starts: only its name and the time, also from other threads.</summary>
    private void StartupMark(string step) { if (!_startupReady) lock (_startupLog) RecordStartupPhase("· " + step); }
    private readonly object _startupLog = new();

    private void RecordStartupPhase(string phase)
    {
        try
        {
            Directory.CreateDirectory(ErrorLog.Folder);
            foreach (var old in new DirectoryInfo(ErrorLog.Folder).EnumerateFiles("startup-*.log").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-14))) old.Delete();
            File.AppendAllText(Path.Combine(ErrorLog.Folder, "startup-" + DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log"),
                $"{DateTime.UtcNow:O} {typeof(MainWindow).Assembly.GetName().Version} {_startupWatch.ElapsedMilliseconds}ms {phase}\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void RequestStartupCancellation()
    {
        if (_startupReady || _closeCompleted) return;
        _startupSurface.SetCancelling();
        _closing = true;
        _startup.RequestCancellation();
        _ = _libraryTasks.StopAsync();
        if (Startup.IsCompleted)
        {
            _closeCompleted = true;
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_persistPreferences && _returnToSelectionOnCancel) ((App)Application.Current).ReturnToLibrarySelection(this);
                else Close();
            });
        }
    }
}
