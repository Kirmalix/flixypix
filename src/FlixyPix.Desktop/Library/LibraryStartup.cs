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
                RootContent.Visibility = Visibility.Visible;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                _startupReady = true;
                RootContent.IsEnabled = true;
                if (_view == "home") _home.Reveal();
                _startupSurface.FadeOut();
                _startup.Ready();
                RecordStartupPhase("Ready");
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
