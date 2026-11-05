using System.IO;
using System.Windows;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e) { AppStorage.Release(); base.OnExit(e); }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string[] startupArguments = e.Args;
        try
        {
            StorageSwitchRecovery.Recover(AppContext.BaseDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlixyPix"));
            var portable = e.Args.Contains("--portable"); var profile = e.Args.Contains("--profile");
            if (portable && profile) throw new ArgumentException("Choose only one storage mode.");
            var args = e.Args.Where(a => a is not ("--portable" or "--profile")).ToArray();
            var test = Array.FindIndex(args, a => a is "--draft-crash-test" or "--folder-sync-test" or "--storage-crash-test" or "--storage-test" or "--smoke-test" or "--lifetime-test" or "--localization-test" or "--perf-probe" or "--player-input-test");
            var inputTest = Array.IndexOf(args, "--player-input-test");
            if (inputTest >= 0)
            {
                if (args.Length != inputTest + 3 || args[inputTest + 1] is not ("plain" or "encrypted" or "self-plain" or "self-encrypted")) throw new ArgumentException("Use --player-input-test plain|encrypted|self-plain|self-encrypted <new-output-folder>.");
                var inputTarget = Path.GetFullPath(args[^1]);
                if (File.Exists(inputTarget) || Directory.Exists(inputTarget) && Directory.EnumerateFileSystemEntries(inputTarget).Any()) throw new IOException("The input acceptance test requires a new, empty output folder.");
            }
            var paths = test >= 0 && args.Length > test + 1
             ? new AppStoragePaths(StorageMode.Portable, AppContext.BaseDirectory, Path.Combine(Path.GetFullPath(args[^1]), "app-data"))
             : AppStoragePaths.Resolve(AppContext.BaseDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlixyPix"), portable ? StorageMode.Portable : profile ? StorageMode.Profile : null);
            if (paths.Mode == StorageMode.Profile)
                SettingsTransition.EnsureMigrated(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductIdentity.PreviousApplicationDirectory), paths.Root);
            AppStorage.Initialize(paths);
            startupArguments = args;
        }
        catch (Exception error) { MessageBox.Show(error.Message, "FlixyPix"); Shutdown(1); return; }

        EventManager.RegisterClassHandler(typeof(Window), System.Windows.Input.Keyboard.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler((sender, key) =>
        {
            if (sender is Window window && window is not (FlixyPix.Desktop.MainWindow or VideoPlayerWindow or ImageViewerWindow or HelpWindow) && key.Key == System.Windows.Input.Key.F1)
            { key.Handled = true; new HelpWindow(window, T("Help"), HelpWindow.Guide()).ShowDialog(); }
        }));
        // "--language <code>" in front of the other arguments forces a language for test runs.
        var arguments = startupArguments.Length >= 2 && startupArguments[0] == "--language" ? startupArguments[2..] : startupArguments;
        string? forcedLanguage = arguments.Length < startupArguments.Length ? startupArguments[1] : null;
        // "--library <folder>" opens that library in this instance ("In neuem Fenster öffnen"); otherwise a normal start.
        string? requestedLibrary = null;
        if (arguments.Length == 2 && arguments[0] == "--library") { requestedLibrary = Path.GetFullPath(arguments[1]); arguments = []; }
        AppShell.PrepareTaskbar();
        // Implicit styles only match the exact type; derived windows would otherwise render white.
        var windowStyle = (Style)Resources[typeof(Window)];
        foreach (var type in typeof(App).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Window)))) Resources.Add(type, new Style(type, windowStyle));
        Theme.Install(this);
        // Test modes start dark and switch themselves; a normal start follows the saved choice (default: like Windows).
        DesktopSettings? saved = null;
        if (arguments.Length == 0) { try { saved = DesktopSettings.Load(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { } }
        if (arguments.Length == 0) { Theme.Apply(saved == null ? ColorScheme.System : Theme.ParseMode(saved.Theme)); }
        else Theme.Apply(ColorScheme.Dark);
        // Test modes run hidden: a dialog there would pump messages, repeat the failure and overflow the stack.
        string? testDirectory = arguments.Length >= 2 && arguments[0].StartsWith("--", StringComparison.Ordinal) ? Path.GetFullPath(arguments[^1]) : null;
        // Test runs keep their log beside their results instead of the user's profile.
        if (testDirectory != null) ErrorLog.Folder = Path.Combine(testDirectory, "logs");
        ErrorLog.Install();
        var localizationTest = arguments.Length == 2 && arguments[0] == "--localization-test";
        if (localizationTest) saved = DesktopSettings.LoadFrom(Path.Combine(testDirectory!, "appsettings.json"));
        // Test modes check German texts unless a language is forced; a normal start follows the setting of this computer.
        LanguageSetup.Apply(forcedLanguage ?? (arguments.Length == 0 || localizationTest ? saved?.Language : "de"), useCustomTranslations: arguments.Length == 0);
        // App.xaml is read before the language is known; texts of its templates come from these resources.
        Resources["SwitchOnText"] = T("On"); Resources["SwitchOffText"] = T("Off");
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            ErrorLog.Write("Oberfläche", args.Exception);
            if (testDirectory == null) { Message.Show(T("{0}\n\nDetails are in the error log:\n{1}", args.Exception.Message, ErrorLog.TodayFile), T("FlixyPix · Error"), MessageBoxButton.OK, MessageBoxImage.Error); return; }
            try { Directory.CreateDirectory(testDirectory); File.WriteAllText(Path.Combine(testDirectory, "result.txt"), "UNHANDLED: " + args.Exception); } catch (IOException) { }
            Shutdown(1);
        };
        try
        {
            SourceProtection.Configure([]);
            if (localizationTest)
            {
                FileAccessPolicy.Current.EnsureWritable(testDirectory!);
                Directory.CreateDirectory(testDirectory!);
                var window = new MainWindow(saved! with { DataDirectory = Path.Combine(testDirectory!, "catalog"), AutoGeneratePreviews = false }, persistPreferences: false);
                window.Loaded += async (_, _) =>
                {
                    try { await window.RunLocalizationSmokeTestAsync(testDirectory!); File.WriteAllText(Path.Combine(testDirectory!, "result.txt"), "PASS: saved language, WPF texts, settings round trip, fonts and layout captures."); window.Close(); }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(testDirectory!, "result.txt"), ex.ToString()); Shutdown(1); }
                };
                window.Show();
            }
            else if (arguments.Length == 2 && arguments[0] == "--draft-crash-test")
            {
                await MetadataCrashProbe.RunAsync(Path.GetFullPath(arguments[1])); Shutdown(0);
            }
            else if (arguments.Length == 2 && arguments[0] == "--storage-crash-test")
            {
                StorageCrashProbe.Run(Path.GetFullPath(arguments[1])); Shutdown(1);
            }
            else if (arguments.Length == 2 && arguments[0] == "--storage-test")
            {
                var directory = Path.GetFullPath(arguments[1]);
                var window = new MainWindow(new DesktopSettings { DataDirectory = Path.Combine(directory, "catalog"), AutoGeneratePreviews = false }, persistPreferences: false);
                window.Loaded += async (_, _) =>
                {
                    try { await window.RunStorageTransitionSmokeAsync(directory); File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: storage modes, encrypted copies, cancellation, destination preservation, shared files and persistent selection."); window.Close(); }
                    catch (Exception error) { File.WriteAllText(Path.Combine(directory, "result.txt"), error.ToString()); Shutdown(1); }
                };
                window.Show();
            }
            else if (arguments.Length == 2 && arguments[0] == "--folder-sync-test")
            {
                var directory = Path.GetFullPath(arguments[1]); Directory.CreateDirectory(directory);
                var window = new MainWindow(new DesktopSettings { DataDirectory = Path.Combine(directory, "catalog"), AutoGeneratePreviews = false }, persistPreferences: false);
                window.Loaded += async (_, _) => { try { await window.RunFolderSyncVisualTestAsync(directory); File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: watched folders, manual scope, selection, import, themes and folder layout"); window.Close(); } catch (Exception error) { File.WriteAllText(Path.Combine(directory, "result.txt"), error.ToString()); Shutdown(1); } };
                window.Show();
            }
            else if (arguments.Length == 3 && arguments[0] == "--player-input-test" && arguments[1] is "plain" or "encrypted" or "self-plain" or "self-encrypted")
            {
                var directory = Path.GetFullPath(arguments[2]); Directory.CreateDirectory(directory);
                var database = Path.Combine(directory, "catalog", "catalog.db"); Directory.CreateDirectory(Path.GetDirectoryName(database)!);
                var encrypted = arguments[1].EndsWith("encrypted", StringComparison.Ordinal); var selfTest = arguments[1].StartsWith("self-", StringComparison.Ordinal);
                using (var empty = new CatalogRepository(database)) { }
                if (encrypted) CatalogEncryption.EnableEncryption(database, "synthetic-input-acceptance-password");
                using var key = encrypted ? CatalogEncryption.Unlock(database, "synthetic-input-acceptance-password") : null;
                var window = new MainWindow(new DesktopSettings { DataDirectory = Path.GetDirectoryName(database)!, AutoGeneratePreviews = false }, persistPreferences: false, unlockedKey: key);
                window.Loaded += async (_, _) =>
                {
                    try { await window.RunPhysicalInputAcceptanceAsync(directory, selfTest); window.Close(); }
                    catch (Exception error) { File.WriteAllText(Path.Combine(directory, "result.txt"), error.ToString()); Shutdown(1); }
                };
                window.Show();
            }
            else if (arguments.Length == 2 && arguments[0] == "--lifetime-test")
            {
                var directory = Path.GetFullPath(arguments[1]);
                Directory.CreateDirectory(directory);
                var window = new MainWindow(new DesktopSettings { DataDirectory = Path.Combine(directory, "catalog") }, persistPreferences: false);
                window.Loaded += async (_, _) =>
                {
                    try { await window.Startup; await window.RunLibraryLifetimeProbeAsync(directory); File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: stale view and selection rejection; close during folder operation."); window.Close(); }
                    catch (Exception error) { File.WriteAllText(Path.Combine(directory, "result.txt"), error.ToString()); Shutdown(1); }
                };
                window.Show();
            }
            else if (arguments.Length == 2 && arguments[0] == "--smoke-test")
            {
                var directory = Path.GetFullPath(arguments[1]);
                FileAccessPolicy.Current.EnsureWritable(directory);
                Directory.CreateDirectory(directory);
                var window = new MainWindow(new DesktopSettings { DataDirectory = Path.Combine(directory, "catalog") }, persistPreferences: false);
                window.Loaded += async (_, _) =>
                {
                    try { await window.RunSmokeTestAsync(directory); await window.RunOpenWithSmokeTestAsync(directory); var last = await window.RunLibrarySwitchSmokeTestAsync(directory); File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: import, edit, half-star rating, search, tags, collections, scenes, backup, open with, libraries and WPF rendering."); last.Close(); }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "result.txt"), ex.ToString()); Shutdown(1); }
                };
                window.Show();
            }
            else if (arguments.Length is 3 or 4 && arguments[0] == "--import-original-check")
            {
                // Headless takeover check for the offline Sandbox scenario; writes counts only. The optional fourth argument is a
                // folder with SQL Server Compact 4.0 (the original's provider files).
                var code = await FlixyPix.Desktop.MainWindow.RunOriginalImportCheckAsync(Path.GetFullPath(arguments[1]), Path.GetFullPath(arguments[2]), arguments.Length == 4 ? Path.GetFullPath(arguments[3]) : null);
                Shutdown(code);
            }
            else if (arguments.Length == 3 && arguments[0] == "--sheet-benchmark")
            {
                // Headless speed check of the thumbnail generator with 1–8 processes; writes times and counts only.
                Shutdown(await FlixyPix.Desktop.MainWindow.RunSheetBenchmarkAsync(Path.GetFullPath(arguments[1]), Path.GetFullPath(arguments[2])));
            }
            else if (arguments.Length == 4 && arguments[0] == "--takeover-check")
            {
                // Headless takeover of cache pictures and settings into a migrated library copy; writes counts only.
                Shutdown(await FlixyPix.Desktop.MainWindow.RunTakeoverCheckAsync(Path.GetFullPath(arguments[1]), Path.GetFullPath(arguments[2]), Path.GetFullPath(arguments[3])));
            }
            else if (arguments.Length == 3 && arguments[0] == "--perf-probe")
            {
                var directory = Path.GetFullPath(arguments[2]);
                FileAccessPolicy.Current.EnsureWritable(directory);
                Directory.CreateDirectory(directory);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var window = new MainWindow(new DesktopSettings { DataDirectory = Path.GetFullPath(arguments[1]) }, persistPreferences: false);
                var construction = watch.Elapsed;
                window.Loaded += async (_, _) =>
                {
                    try { await window.RunPerfProbeAsync(directory, construction); window.Close(); }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "error.txt"), ex.GetType().Name + ": " + ex.Message); Shutdown(1); }
                };
                window.Show();
            }
            else if (arguments.Length == 3 && arguments[0] == "--library-probe")
            {
                var directory = Path.GetFullPath(arguments[2]);
                FileAccessPolicy.Current.EnsureWritable(directory);
                Directory.CreateDirectory(directory);
                var window = new MainWindow(new DesktopSettings { DataDirectory = Path.GetFullPath(arguments[1]) }, persistPreferences: false);
                window.Loaded += async (_, _) =>
                {
                    try { await window.RunLibraryProbeAsync(directory); window.Close(); }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "error.txt"), ex.GetType().Name + ": " + ex.Message); Shutdown(1); }
                };
                window.Show();
            }
            else OpenLibraryAtStart(requestedLibrary);
        }
        catch (OperationCanceledException) { Shutdown(0); }
        catch (Exception ex)
        {
            if (testDirectory != null) { File.WriteAllText(Path.Combine(testDirectory, "result.txt"), ex.ToString()); Shutdown(1); return; }
            MessageBox.Show(T("The library could not be opened.\n{0}", ex.Message), "FlixyPix", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1);
        }
    }

    /// <summary>Normal start: with several libraries and "ask at start" (original: database prompt unless bypassed) the
    /// user picks one; otherwise the last one opens, or the one given with "--library". A library that is already open in
    /// another instance comes to the front there instead.</summary>
    private void OpenLibraryAtStart(string? requested)
    {
        var firstPortableStart = AppStorage.Current.Mode == StorageMode.Portable && !File.Exists(DesktopSettings.SettingsPath);
        var settings = DesktopSettings.Load();
        if (requested != null) settings = settings with { DataDirectory = LibraryCatalogs.Normalize(requested) };
        else if (settings.AskForLibraryAtStart && settings.Libraries.Count > 1)
        {
            // The chooser is the only window; closing it must not end the application before the main window exists.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var chooser = new DatabaseWindow(null, settings.Libraries, true, null, null, (libraries, ask) =>
            {
                settings = settings with { Libraries = [.. libraries], AskForLibraryAtStart = ask }; settings.Save();
            }, chooser: true);
            chooser.Select(settings.DataDirectory);
            chooser.ShowDialog();
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            if (chooser.Chosen is not { } chosen) { Shutdown(0); return; }
            settings = settings with { DataDirectory = chosen };
            settings.Save();
        }
        if (LibraryInstance.IsOpen(settings.DataDirectory)) { LibraryInstance.Activate(settings.DataDirectory); Shutdown(0); return; }
        var main = new MainWindow(settings);
        if (firstPortableStart) main.ContentRendered += (_, _) => main.OfferProfileImport(initial: true);
        main.Show();
    }
}
