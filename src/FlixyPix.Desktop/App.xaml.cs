using System.IO;
using System.Windows;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class App : Application
{
 protected override async void OnStartup(StartupEventArgs e)
 {
  base.OnStartup(e);
  // "--language <code>" in front of the other arguments forces a language for test runs.
  var arguments = e.Args.Length >= 2 && e.Args[0] == "--language" ? e.Args[2..] : e.Args;
  string? forcedLanguage = arguments.Length < e.Args.Length ? e.Args[1] : null;
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
   else OpenLibraryAtStart(requestedLibrary);
  }
  catch (OperationCanceledException) { Shutdown(0); }
  catch (Exception ex) { MessageBox.Show(T("The library could not be opened.\n{0}", ex.Message), "FlixyPix", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
 }

 /// <summary>Normal start: with several libraries and "ask at start" (original: database prompt unless bypassed) the
 /// user picks one; otherwise the last one opens, or the one given with "--library". A library that is already open in
 /// another instance comes to the front there instead.</summary>
 private void OpenLibraryAtStart(string? requested)
 {
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
  new MainWindow(settings).Show();
 }
}
