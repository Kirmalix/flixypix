using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

// One-step takeover of an original EazyFlixPix 4.8.4 catalog (.sdf): the bundled exporter reads a
// private copy with SQL Server Compact 4.0, then the verified migration builds a new, separate library.
// FlixyPix does not ship SQL Server Compact: the exporter uses the copy in the original's program folder
// or a system-wide installation. The original database is never modified; rows without a modern
// equivalent are kept raw.
public partial class MainWindow
{
 internal sealed record OriginalImportResult(string LibraryPath, string ReportPath, ImportResult Import);
 internal static string LegacyExporterPath => Path.Combine(AppContext.BaseDirectory, "legacy-import", "FlixyPix.LegacyExporter.exe");

 private void ImportOriginalDatabaseClicked(object sender, RoutedEventArgs e) => ImportOriginalDatabase();

 /// <summary>Opens the takeover assistant: it finds the installation, builds a new library from its catalog (or uses the
 /// open one) and copies the chosen picture and settings groups.</summary>
 private async void ImportOriginalDatabase()
 {
  var parent = Path.GetDirectoryName(Path.GetFullPath(_settings.DataDirectory)) ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
  var assistant = new TakeoverAssistantWindow(this, new LegacyInstallationSearch(), _repository, ThumbnailFolder, parent, CreateLibraryFromInstallationAsync);
  assistant.ShowDialog();
  if (assistant.ChangedCurrentLibrary) await RefreshAsync();
  // A new library restarts anyway and then starts with the chosen language too.
  if (assistant.ChosenLanguage is { } language) SaveLanguage(language);
  if (assistant.CreatedLibrary is { } created) ShowNewLibraryMigration(created);
  else if (assistant.ChosenLanguage != null) OfferLanguageRestart();
 }

 /// <summary>New, separate library from the installation's catalog; program settings stay raw so the assistant can offer them by group.</summary>
 private static async Task<NewLibraryMigration> CreateLibraryFromInstallationAsync(LegacyInstallation installation, string target, IProgress<string> progress, CancellationToken token)
 {
  if (!File.Exists(LegacyExporterPath)) throw new FileNotFoundException(T("The import tool is missing in this installation (folder “legacy-import”)."));
  var result = await ImportOriginalCatalogAsync(installation.CatalogPath, target, progress, token, adoptAppSettings: false, runtimeFolder: installation.ProgramFolder);
  return new(result.LibraryPath, result.ReportPath, result.Import);
 }

 /// <summary><paramref name="runtimeFolder"/>: a folder with SQL Server Compact 4.0, preferably the original's program folder;
 /// without one, any installation found on this computer, then a system-wide SQL Server Compact.</summary>
 internal static async Task<OriginalImportResult> ImportOriginalCatalogAsync(string database, string target, IProgress<string> progress, CancellationToken token, bool adoptAppSettings = true, string? runtimeFolder = null)
 {
  var parent = Path.GetDirectoryName(Path.GetFullPath(target)) ?? throw new ArgumentException(T("A parent target folder is missing."));
  Directory.CreateDirectory(parent);
  var work = Path.Combine(parent, ".flixypix-export-" + Guid.NewGuid().ToString("N"));
  FileAccessPolicy.Current.EnsureWritable(work); Directory.CreateDirectory(work);
  try
  {
   progress.Report(T("Reading the original database …"));
   var runtime = await Task.Run(() => LegacyInstallations.SqlCeRuntimeFolder(runtimeFolder), token);
   var export = await ExportOriginalCatalogAsync(database, Path.Combine(work, "catalog.jsonl"), runtime, progress, token);
   progress.Report(T("Building and checking the new library …"));
   var migrated = await MigrateLegacyToNewLibraryAsync(export, target, Guid.NewGuid().ToString("N"), token, adoptAppSettings);
   return new(migrated.DirectoryPath, migrated.ReportPath, migrated.Result);
  }
  finally { DeleteOwnedExportWork(work); }
 }

 private static async Task<string> ExportOriginalCatalogAsync(string database, string output, string? runtime, IProgress<string> progress, CancellationToken token)
 {
  var start = new ProcessStartInfo(LegacyExporterPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(LegacyExporterPath)! };
  start.ArgumentList.Add("--import");
  if (runtime != null) { start.ArgumentList.Add("--runtime"); start.ArgumentList.Add(runtime); }
  start.ArgumentList.Add(Path.GetFullPath(database)); start.ArgumentList.Add(output);
  using var process = Process.Start(start) ?? throw new IOException(T("The import tool could not be started."));
  using var stop = token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
  var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
  while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
   if (line.StartsWith("progress ", StringComparison.Ordinal) && line.Split(' ') is [_, var tables, var rows])
    progress.Report(T("Table {0} · {1:N0} rows read", tables, long.Parse(rows)));
  await process.WaitForExitAsync(CancellationToken.None);
  token.ThrowIfCancellationRequested();
  if (process.ExitCode != 0) throw new InvalidDataException(ExplainExportFailure(await errors));
  return output;
 }

 private static string ExplainExportFailure(string errors)
 {
  var reason = errors.Contains("category=password") ? T("The database is protected with an unknown password.")
   : errors.Contains("category=version") ? T("The file is not an EazyFlixPix SQL Server Compact 4.0 database.")
   : errors.Contains("category=integrity") ? T("The database is damaged. Please use a backup.")
   : errors.Contains("category=access") ? T("The file cannot be accessed. Is EazyFlixPix still open?")
   : errors.Contains("category=runtime-missing") ? T("SQL Server Compact 4.0 was not found. FlixyPix uses the copy in the EazyFlixPix program folder or a Microsoft SQL Server Compact 4.0 SP1 (x86) installed on this computer. Microsoft offers it at https://www.microsoft.com/download/details.aspx?id=30709.")
   : T("The original database could not be read.");
  return reason + T("\n\nThe original file was not changed.\n\nTechnical detail: ") + errors.Trim();
 }

 private static void DeleteOwnedExportWork(string path)
 {
  // Only the freshly created export folder: catalog.jsonl and its binary sidecar folder.
  var directory = new DirectoryInfo(path);
  if (!directory.Exists || !directory.Name.StartsWith(".flixypix-export-", StringComparison.Ordinal) || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return;
  FileAccessPolicy.Current.EnsureWritable(path);
  foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
   if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return;
  directory.Delete(true);
 }

 /// <summary>Headless check used by the offline Sandbox scenario: writes counts only, never catalog content.</summary>
 internal static async Task<int> RunOriginalImportCheckAsync(string database, string outputDirectory, string? runtimeFolder)
 {
  Directory.CreateDirectory(outputDirectory);
  var messages = new List<string>(); var watch = Stopwatch.StartNew();
  try
  {
   var result = await ImportOriginalCatalogAsync(database, Path.Combine(outputDirectory, "library"), new Progress<string>(messages.Add), CancellationToken.None, runtimeFolder: runtimeFolder);
   var migration = result.Import.Migration!;
   using var catalog = new CatalogRepository(Path.Combine(result.LibraryPath, "catalog.db"));
   File.WriteAllText(Path.Combine(outputDirectory, "import-result.json"), JsonSerializer.Serialize(new
   {
    passed = result.Import.Errors.Count == 0, seconds = Math.Round(watch.Elapsed.TotalSeconds, 1), media = catalog.QueryPage(new() { Limit = 1 }).TotalCount,
    sourceRows = migration.Rows, blobs = migration.Blobs, migration.BlobBytes, projectionErrors = result.Import.Errors.Count, progressMessages = messages.Count,
    runtimeGiven = runtimeFolder != null
   }, new JsonSerializerOptions { WriteIndented = true }));
   return 0;
  }
  catch (Exception error)
  {
   File.WriteAllText(Path.Combine(outputDirectory, "import-result.json"), JsonSerializer.Serialize(new { passed = false, error = error.GetType().Name, message = error.Message.Split('\n')[0], seconds = Math.Round(watch.Elapsed.TotalSeconds, 1), runtimeGiven = runtimeFolder != null }));
   return 1;
  }
 }
}

public partial class MainWindow
{
 /// <summary>Headless takeover of an installation's pictures and settings into an already migrated library (use a copy).
 /// Writes counts and timings only, never names or paths from the catalog; a second plan proves nothing is copied twice.</summary>
 internal static async Task<int> RunTakeoverCheckAsync(string programFolder, string libraryDirectory, string outputDirectory)
 {
  Directory.CreateDirectory(outputDirectory);
  var watch = Stopwatch.StartNew(); var output = Path.Combine(outputDirectory, "takeover-result.json");
  try
  {
   var installation = LegacyInstallations.Inspect(programFolder) ?? throw new FileNotFoundException(T("No installation in the given folder."));
   using var catalog = new CatalogRepository(Path.Combine(libraryDirectory, "catalog.db"));
   var batch = catalog.GetMigrationReports().LastOrDefault()?.Batch ?? throw new InvalidDataException(T("The library contains no imported legacy data."));
   var plan = await Task.Run(() => catalog.PlanLegacyTakeover(installation, batch));
   var planned = watch.Elapsed;
   var report = await Task.Run(() => catalog.ApplyLegacyTakeover(plan, new(Enum.GetValues<LegacyDataGroup>(), plan.Settings.Groups), Path.Combine(libraryDirectory, "thumbnails")));
   var again = await Task.Run(() => catalog.PlanLegacyTakeover(installation, batch));
   File.WriteAllText(output, JsonSerializer.Serialize(new
   {
    passed = again.Groups.Where(g => g.Group != LegacyDataGroup.TagImages).All(g => g.Adopt == 0), planSeconds = Math.Round(planned.TotalSeconds, 1),
    totalSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1), peakWorkingSetMiB = Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024,
    plan = plan.Groups, settings = plan.Settings.Groups.Select(g => new { group = g.ToString(), differences = plan.Settings.Differences(g), conflicts = plan.Settings.Conflicts(g) }),
    result = report.Data.Select(r => new { group = r.Group.ToString(), r.Adopted, r.AlreadyAdopted, r.Kept, r.Orphans, r.Missing, r.Invalid }), report.SettingsChanged,
    problems = report.Problems.Count, secondPlan = again.Groups.Select(g => new { group = g.Group.ToString(), g.Adopt, g.AlreadyAdopted }),
   }, new JsonSerializerOptions { WriteIndented = true }));
   return 0;
  }
  catch (Exception error)
  {
   File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, error = error.GetType().Name, message = error.Message.Split('\n')[0], seconds = Math.Round(watch.Elapsed.TotalSeconds, 1) }));
   return 1;
  }
 }
}
