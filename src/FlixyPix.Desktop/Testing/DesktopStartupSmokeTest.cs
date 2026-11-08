using System.IO;
using System.Diagnostics;
using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using System.Windows;
using System.Windows.Threading;
namespace FlixyPix.Desktop;
public partial class MainWindow
{
    private async Task RunStartupLoadingSmokeAsync(string directory)
    {
        await RunStartupChooserSmokeAsync(directory);
        await RunFailedStartupSmokeAsync(directory);
        await RunNeutralStartupSmokeAsync(directory);
        await RunEncryptedStartupSmokeAsync(directory);
        await RunFirstImportSmokeAsync(directory);
        if (WindowPlacement.Fit(3000, -50, 2000, 1500, 0, 0, 1920, 1040) != (0, 0, 1920, 1040) || WindowPlacement.Fit(100, 900, 800, 600, 0, 0, 1920, 1040) != (100, 440, 800, 600))
            throw new InvalidOperationException("A window place was not fitted to the screen.");
        var placed = new Window { Width = 400, Height = 300, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        new System.Windows.Interop.WindowInteropHelper(placed).EnsureHandle();
        WindowPlacement.Apply(placed, new WindowPixels(40, 30, 700, 500, false), null);
        if (WindowPlacement.Read(placed) is not { Left: 40, Top: 30, Width: 700, Height: 500, Maximized: false }) throw new InvalidOperationException($"A window place did not come back: {WindowPlacement.Read(placed)}.");
        WindowPlacement.Apply(placed, new WindowPixels(90000, -90000, 90000, 90000, false), null);
        if (WindowPlacement.Read(placed) is not { Width: < 90000, Height: < 90000, Left: < 90000 }) throw new InvalidOperationException($"A place off the screens was kept: {WindowPlacement.Read(placed)}.");
        placed.Close();
        if (File.Exists(Path.Combine(directory, "startup-scale.flag"))) await RunStartupScaleAsync(directory);
        foreach (var afterOpen in new[] { false, true })
        foreach (var cancel in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = new MainWindow(new DesktopSettings { DataDirectory = Path.Combine(directory, (cancel ? "startup-cancel" : "startup-ready") + (afterOpen ? "-opened" : "-before")) }, persistPreferences: false);
            async Task Hold(CancellationToken token) { entered.SetResult(); await release.Task.WaitAsync(token); }
            if (afterOpen) window._startup.AfterOpen = Hold; else window._startup.BeforeOpen = Hold;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.SetResult();
            window.Show();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (!window.IsVisible || !window._startupSurface.IsVisible || window.RootContent.IsEnabled || window.Startup.IsCompleted)
                throw new InvalidOperationException("Loading surface was not rendered before library work.");
            if (!Descendants<System.Windows.Controls.Image>(window._startupSurface).Any(image => image.IsVisible && image.Source is System.Windows.Media.Imaging.BitmapSource { PixelWidth: > 0 }))
                throw new InvalidOperationException("Startup splash did not show its packaged logo.");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (!afterOpen && !cancel)
            {
                await Task.Delay(620); Capture(window, Path.Combine(directory, "startup-logo.png"));
                await Task.Delay(1100);
                if (window._startupSurface.IntroVisible) throw new InvalidOperationException("The logo stayed over the loading library.");
                Capture(window, Path.Combine(directory, "startup-loading.png"));
            }
            if (window._startupSurface.SlowNoticeVisible) throw new InvalidOperationException("A quick start already offered to cancel.");
            if (cancel)
            {
                window._startupSurface.ShowTime(TimeSpan.FromSeconds(6));
                if (!window._startupSurface.SlowNoticeVisible) throw new InvalidOperationException("A slow start did not say so.");
                Capture(window, Path.Combine(directory, "startup-slow.png"));
                if (afterOpen) { window.Close(); window.Close(); }
                else window._startupSurface.CancelButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                try { await window.Startup; throw new InvalidOperationException("Cancelled startup succeeded."); }
                catch (OperationCanceledException) { }
            }
            else
            {
                release.SetResult(); await window.Startup;
                await window._startupSurface.Gone.WaitAsync(TimeSpan.FromSeconds(5));
                if (!window._startupReady || !window.RootContent.IsEnabled || window._startupSurface.IsVisible)
                    throw new InvalidOperationException("Ready startup did not publish its usable view.");
                window.Close();
            }
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await window.CloseTask;
            if (LibraryInstance.IsOpen(window._settings.DataDirectory)) throw new InvalidOperationException("Startup retained the library reservation.");
        }
    }
    private async Task RunStartupChooserSmokeAsync(string directory)
    {
        if (!Path.GetFullPath(AppStorage.Current.Root).StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Chooser fixture requires isolated app storage.");
        var target = Path.Combine(directory, "startup-chosen");
        using (var empty = new CatalogRepository(Path.Combine(target, "catalog.db"))) { }
        var window = new MainWindow(new DesktopSettings { DataDirectory = Path.Combine(directory, "startup-return") });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window._startup.BeforeOpen = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        var chosen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var chooser = Application.Current.Windows.OfType<DatabaseWindow>().FirstOrDefault(w => w.IsVisible);
            if (chooser == null) return;
            timer.Stop(); chooser.Choose(target); chosen.SetResult();
        };
        timer.Start(); window.Show();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            window.Close();
            await chosen.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var next = Application.Current.Windows.OfType<MainWindow>().Single(w => LibraryCatalogs.SameDirectory(w._settings.DataDirectory, target));
            await next.Startup;
            if (window.IsVisible || LibraryInstance.IsOpen(window._settings.DataDirectory)) throw new InvalidOperationException("Returning to chooser retained cancelled startup.");
            next.Close(); await next.CloseTask;
        }
        finally { timer.Stop(); AppShell.Install(this); Application.Current.MainWindow = this; }
    }

    private async Task RunFailedStartupSmokeAsync(string directory)
    {
        var library = Path.Combine(directory, "startup-broken"); Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "catalog.db"), "synthetic damaged catalog");
        var window = new MainWindow(new DesktopSettings { DataDirectory = library }, persistPreferences: false);
        window.Show();
        try { await window.Startup; throw new InvalidOperationException("Damaged startup succeeded."); }
        catch (SqliteException) { }
        if (!window.IsVisible || !window._startupSurface.IsVisible || window._startupReady || LibraryInstance.IsOpen(library))
            throw new InvalidOperationException("Failed startup hid its error or retained resources.");
        window.Close();
    }

    /// <summary>A neutral title means no logo and no name while the library opens.</summary>
    private async Task RunNeutralStartupSmokeAsync(string directory)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MainWindow(new DesktopSettings { DataDirectory = Path.Combine(directory, "startup-neutral"), Privacy = new PrivacySettings { NeutralTitle = true } }, persistPreferences: false);
        window._startup.BeforeOpen = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        try
        {
            window.Show();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (window._startupSurface.HasLogo || window._startupSurface.IntroStarted || Descendants<System.Windows.Controls.Image>(window._startupSurface).Any())
                throw new InvalidOperationException("The neutral start showed the logo.");
            if (Descendants<System.Windows.Controls.TextBlock>(window._startupSurface).Any(text => text.Text.Contains(ProductIdentity.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The neutral start showed the name.");
            Capture(window, Path.Combine(directory, "startup-neutral.png"));
        }
        finally { Privacy.Prepare(_settings.Privacy); }
        window.Close(); window.Close();
        await window.CloseTask;
    }

    /// <summary>The password comes first: no logo behind the question, no time passing and no hint while it waits; the logo follows the answer.</summary>
    private async Task RunEncryptedStartupSmokeAsync(string directory)
    {
        var library = Path.Combine(directory, "startup-encrypted"); Directory.CreateDirectory(library);
        var database = Path.Combine(library, "catalog.db");
        using (var empty = new CatalogRepository(database)) { }
        CatalogEncryption.EnableEncryption(database, "synthetic-startup-password");
        var window = new MainWindow(new DesktopSettings { DataDirectory = library, AutoGeneratePreviews = false }, persistPreferences: false);
        window.Show();
        System.Windows.Controls.PasswordBox? input = null; Window? question = null;
        for (var wait = 0; input == null && wait < 200; wait++)
        {
            await Task.Delay(50);
            question = window.OwnedWindows.Cast<Window>().FirstOrDefault(w => w.IsVisible);
            if (question != null) input = Descendants<System.Windows.Controls.PasswordBox>(question).FirstOrDefault();
        }
        if (input == null) throw new InvalidOperationException("The encrypted start did not ask for the password.");
        await Task.Delay(5300);
        if (window._startupSurface.IntroStarted || !window._startupSurface.IsWaiting || window._startupSurface.SlowNoticeVisible || window._startupWatch.Elapsed.TotalSeconds > 4)
            throw new InvalidOperationException($"The start ran on behind the question (logo {window._startupSurface.IntroStarted}, waiting {window._startupSurface.IsWaiting}, hint {window._startupSurface.SlowNoticeVisible}, {window._startupWatch.Elapsed}).");
        Capture(window, Path.Combine(directory, "startup-encrypted-question.png"));
        Capture((FrameworkElement)question!.Content, Path.Combine(directory, "startup-encrypted-dialog.png"));
        input.Password = "synthetic-startup-password";
        question.DialogResult = true;
        await window.Startup.WaitAsync(TimeSpan.FromSeconds(60));
        if (window._startupSurface.IsWaiting || !window._startupSurface.IntroStarted)
            throw new InvalidOperationException($"The logo did not follow the password (waiting {window._startupSurface.IsWaiting}, logo {window._startupSurface.IntroStarted}).");
        window.Close(); await window.CloseTask;
    }

    /// <summary>The welcome page of an empty library gives way to the items of its first import.</summary>
    private async Task RunFirstImportSmokeAsync(string directory)
    {
        var library = Path.Combine(directory, "startup-first-import"); var source = Path.Combine(directory, "startup-first-files"); Directory.CreateDirectory(source);
        var picture = new System.Windows.Media.Imaging.RenderTargetBitmap(64, 64, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var drawing = new System.Windows.Media.DrawingVisual(); using (var context = drawing.RenderOpen()) context.DrawRectangle(System.Windows.Media.Brushes.SteelBlue, null, new Rect(0, 0, 64, 64));
        picture.Render(drawing);
        foreach (var name in new[] { "eins.png", "zwei.png" })
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
            using var file = File.Create(Path.Combine(source, name)); encoder.Save(file);
        }
        var window = new MainWindow(new DesktopSettings { DataDirectory = library, AutoGeneratePreviews = false }, persistPreferences: false);
        window.Show();
        await window.Startup.WaitAsync(TimeSpan.FromSeconds(60));
        window.GoHome(); await window._lastRefresh; await window.HomeTask;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!window.Home.IsVisible || window.Home.RowCount != 0) throw new InvalidOperationException("An empty library did not welcome.");
        Capture(window.RootContent, Path.Combine(directory, "startup-welcome.png"));
        await window.ImportPaths([source]); await window._lastRefresh;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (window._view != "all" || window.Home.IsVisible || window.MediaList.Items.Count != 2) throw new InvalidOperationException($"The first import did not lead to its items ({window._view}, {window.MediaList.Items.Count}).");
        // The smallest window, as on a laptop with a large scale: everything stays reachable.
        window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(window.RootContent, Path.Combine(directory, "startup-smallest-window.png"));
        window.Close(); await window.CloseTask;
    }

    private async Task RunStartupScaleAsync(string directory)
    {
        var library = Path.Combine(directory, "startup-100k");
        var path = Path.Combine(library, "catalog.db");
        await Task.Run(() =>
        {
            using (var empty = new CatalogRepository(path)) { }
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open(); connection.CreateCollation("EFP_ORDINAL", StringComparer.OrdinalIgnoreCase.Compare);
            using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<100000)
                INSERT INTO media(id,path_key,document)
                SELECT printf('00000000-0000-0000-0000-%012d',n),printf('C:/SYNTHETIC/CLIP%06d.MP4',n),
                json_object('id',printf('00000000-0000-0000-0000-%012d',n),'path',printf('C:/synthetic/clip%06d.mp4',n),
                'title',printf('Synthetic clip %06d',n),'kind','Video','addedAt','2026-01-01T00:00:00+00:00') FROM numbers;
                """;
            command.ExecuteNonQuery();
        });
        var watch = Stopwatch.StartNew(); double rendered = -1; double lastTick = 0; double maxStall = 0;
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        heartbeat.Tick += (_, _) => { var now = watch.Elapsed.TotalMilliseconds; maxStall = Math.Max(maxStall, now-lastTick); lastTick=now; };
        var window = new MainWindow(new DesktopSettings { DataDirectory = library }, persistPreferences: false);
        window.ContentRendered += (_, _) => { if (rendered < 0) rendered = watch.Elapsed.TotalMilliseconds; };
        heartbeat.Start(); window.Show();
        try
        {
            await window.Startup.WaitAsync(TimeSpan.FromMinutes(2));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (window._total != 100000 || rendered < 0) throw new InvalidOperationException("Large startup did not show its full view.");
            File.WriteAllText(Path.Combine(directory, "startup-100k.json"), JsonSerializer.Serialize(new { Media = window._total, FirstRenderMs = rendered, ReadyMs = watch.Elapsed.TotalMilliseconds, MaxDispatcherGapMs = maxStall }));
            Capture(window, Path.Combine(directory, "startup-100k-ready.png"));
        }
        finally { heartbeat.Stop(); window.Close(); await window.CloseTask; }
    }

}
