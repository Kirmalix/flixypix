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
    private Grid _startupSurface = null!;
    private TextBlock _startupPhase = null!;
    private Button _startupCancel = null!;
    private readonly DispatcherTimer _startupTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly System.Diagnostics.Stopwatch _startupWatch = new();

    private void InitializeStartup(CatalogKey? suppliedKey)
    {
        var key = suppliedKey?.Clone();
        Startup = _startup.Completion;
        RootContent.IsEnabled = false;
        RootContent.Visibility = Visibility.Hidden;
        _startupPhase = new TextBlock { Text = T("Checking library"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
        _startupCancel = new Button { Content = T("Cancel"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 0) };
        _startupCancel.Click += (_, _) => RequestStartupCancellation();
        var panel = new StackPanel { MaxWidth = 480, Margin = new Thickness(32), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = LibraryName, FontSize = 24, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_startupPhase);
        var progress = new ProgressBar { IsIndeterminate = true, Height = 6 };
        System.Windows.Automation.AutomationProperties.SetName(progress, T("Progress"));
        panel.Children.Add(progress);
        var elapsed = new TextBlock { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(elapsed);
        _startupTimer.Tick += (_, _) => { if (_startupWatch.Elapsed.TotalSeconds >= 10) elapsed.Text = T("Elapsed: {0}", _startupWatch.Elapsed.ToString(@"mm\:ss")); };
        panel.Children.Add(_startupCancel);
        var close = new Button { Content = T("Close"), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        close.Click += (_, _) => { _closeCompleted = true; Close(); };
        panel.Children.Add(close);
        _startupSurface = new Grid();
        _startupSurface.SetResourceReference(BackgroundProperty, "WindowBackground");
        _startupSurface.Children.Add(panel);
        Content = null;
        var host = new Grid(); host.Children.Add(RootContent); host.Children.Add(_startupSurface); Content = host;
        ContentRendered += Begin;
        async void Begin(object? sender, EventArgs args)
        {
            ContentRendered -= Begin;
            _startupWatch.Start();
            _startupTimer.Start();
            try
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                await _startup.EnterAsync();
                await InitializeLibraryAsync(key);
                _startup.Token.ThrowIfCancellationRequested();
                RootContent.Visibility = Visibility.Visible;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                _startupReady = true;
                RootContent.IsEnabled = true;
                _startupSurface.Visibility = Visibility.Collapsed;
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
                _startupPhase.Text = T("The library could not be opened.\n{0}", ErrorLog.RedactPaths(error.Message));
                progress.IsIndeterminate = false;
                close.Visibility = Visibility.Visible;
                _startupCancel.Content = T("Libraries");
                _startupCancel.IsEnabled = true;
                _startup.Failed(error);
                RecordStartupPhase("Failed:" + error.GetType().Name);
                _ = Startup.Exception; // The visible failure panel observes the error; callers may still await it.
            }
            finally { _startupTimer.Stop(); key?.Dispose(); }
        }
    }

    private void SetStartupPhase(string phase)
    {
        _startupPhase.Text = phase;
        RecordStartupPhase(phase);
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
        _startupCancel.IsEnabled = false;
        _startupPhase.Text = T("Cancelling …");
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
