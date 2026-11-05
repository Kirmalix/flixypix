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
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Capture(window, Path.Combine(directory, "startup-loading.png"));
            if (cancel)
            {
                window.Close(); window.Close();
                try { await window.Startup; throw new InvalidOperationException("Cancelled startup succeeded."); }
                catch (OperationCanceledException) { }
            }
            else
            {
                release.SetResult(); await window.Startup;
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
