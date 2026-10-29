using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>The database management: one window for everything that concerns the library as a database (plan phase V).
/// The tools that were spread over the catalog menu, "Altdaten / Migration" and the cache menu are gathered here.</summary>
public partial class MainWindow
{
 internal static readonly string OverviewSection = TKey("Overview"), RepairSection = TKey("Check and repair"), BackupSection = TKey("Backup"),
  TransferSection = TKey("Import and export"), SecuritySection = TKey("Security"), LegacySection = TKey("Legacy data");

 internal DatabaseWindow CreateDatabaseWindow(string? section = null) =>
  new(this, _settings.Libraries, _settings.AskForLibraryAtStart, _settings.DataDirectory, _repository, SaveLibraries, sections: DatabaseSections(), section: section)
  {
   // Test runs keep new libraries beside their catalog instead of the user's profile.
   NewLibraryParent = _persistPreferences ? DatabaseWindow.DefaultParent : Path.Combine(Path.GetDirectoryName(_settings.DataDirectory)!, "libraries"),
  };

 private void ShowDatabase(object sender, RoutedEventArgs e) => ShowDatabase();
 /// <summary>Opens the database management; a chosen library is opened, a chosen tool runs afterwards in this window.</summary>
 internal async void ShowDatabase(string? section = null)
 {
  var window = CreateDatabaseWindow(section);
  window.ShowDialog();
  if (window.Chosen is { } chosen) { await SwitchLibraryAsync(chosen); return; }
  if (window.Pending is { } tool)
  {
   try { tool.Run(); }
   catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
   { Message.Show(this, error.Message, tool.Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
  }
 }

 private IReadOnlyList<DatabaseSection> DatabaseSections()
 {
  static DatabaseTool Tool(string title, string description, string button, Action run) => new(title, description, button, run);
  static IEnumerable<DatabaseTool> Group(string heading, params DatabaseTool[] tools) => tools.Select(tool => tool with { Group = heading });
  var open = T("Open …"); var start = T("Start");
  return
  [
   new(OverviewSection, "", T("The open library at a glance."),
    [Tool(T("Open library folder"), T("Shows the folder with catalog, thumbnails and caches in Explorer."), open,
      () => Process.Start(new ProcessStartInfo("explorer.exe", _settings.DataDirectory))),
     Tool(T("Open error log"), T("Shows the folder with the error logs, for example for a bug report."), open, ErrorLog.OpenFolder)],
    OverviewContent),
   new(DatabaseWindow.LibrariesSection, "", "", []),
   new(RepairSection, "", T("Check the catalog and media files and restore what is missing."),
    [new(T("Rebuild search index"), T("Rebuilds the tables for sorting, filters, tags and quick search from the item entries. The catalog is copied first."), start, () => { },
      RebuildSearchIndexHere),
     new(T("Compact catalog"), T("Rebuilds the catalog file without unused space; the data stays the same."), start, () => { }, CompactHere),
     Tool(T("Check availability of media files"), T("Checks page by page which files are missing or on a drive that is not connected right now."), open,
      () => new MediaAvailabilityWindow(this, _repository).ShowDialog()),
     Tool(T("Reassign media paths"), T("Assigns items to a new folder or drive after a move, with a preview before the change."), open, MapMigrationPaths),
     Tool(T("Find identical files"), T("Compares the files by SHA-256 and shows duplicate items."), start, FindDuplicates),
     Tool(T("Find similar items"), T("Finds videos and images that look alike, also in another resolution or quality, and helps to keep the best version."), open, () => ShowSimilarMedia()),
     Tool(T("Create missing previews and metadata"), T("Adds thumbnails, dimensions and duration where they are missing."), start,
      async () => await RunOperation(T("Prepare media"), PrepareMediaAsync)),
     Tool(T("Adjust video thumbnails to the settings"), T("Recreates the thumbnails of all videos with the current settings."), start, RegenerateAllThumbnails),
     Tool(T("Create preview clips"), T("Makes the missing preview clips of all videos with the current settings."), start, CreateAllPreviewClips),
     Tool(T("Restore missing archive images"), T("Extracts images from ZIP and RAR archives again whose cache is missing."), start, RestoreArchiveCache),
     Tool(T("Thumbnails of single folders"), T("Recreates the thumbnails of one media folder or switches automatic previews on or off."), T("Choose …"), CacheMenu)],
    CatalogCheckContent),
   new(BackupSection, "", T("Back up the library and restore it from a backup."),
    [Tool(T("Create backup package"), T("Backs up the catalog with graphics, scenes, collections and settings in one verified file, and the media files too if you want."), open, CreateBackupPackage),
     Tool(T("Restore backup package"), T("Checks a backup package and restores it as a new library of its own; the open one stays unchanged."), open, () => RestoreBackupPackage()),
     new(T("Check backup package"), T("Checks the checksums, manifest and catalog of a backup package without restoring it."), open, () => { }, window => VerifyBackupHere(window)),
     Tool(T("Catalog backup as JSON"), T("Writes only the catalog as a readable JSON file, without graphics."), open, BackupCatalog)],
    BackupContent),
   new(TransferSection, "", T("Bring data into the library or pass it on. The import from EazyFlixPix 4 is under Legacy data."),
    [.. Group(T("Catalog"),
      Tool(T("Export catalog as JSON"), T("The complete catalog in the portable format, for example for another FlixyPix installation."), open, ExportCatalog),
      Tool(T("Import catalog or backup"), T("Imports a JSON catalog or a backup; existing data is kept."), open, ImportCatalog),
      Tool(T("Export CSV"), T("Items as a table, for example for a spreadsheet."), open, ExportCsv),
      Tool(T("Import CSV"), T("Imports items from a CSV table into the catalog."), open, ImportCsv)),
     .. Group(T("Sidecar files next to the media"),
      Tool(T("Read metadata from files"), T("Reads keywords, title, rating and date taken from the files and XMP sidecars, with a preview."), open, ImportFileMetadata),
      Tool(T("Write to XMP sidecars"), T("Writes tags, title, description and rating beside the files; the media files stay unchanged."), open, ExportSidecarFiles),
      Tool(T("Read NFO files"), T("Imports details from NFO files beside the videos, with a preview."), open, ImportNfoFiles),
      Tool(T("Write NFO files"), T("Writes title, description, rating and tags of the selected or all videos into NFO files next to them; existing ones stay unchanged."), start, ExportNfoFiles)),
     .. Group(T("Tags, image archives and playlists"),
      Tool(T("Import tag package"), T("Imports tags, performers and studios with categories and aliases from a tag package or a CSV file, with a preview."), open,
       () => ShowDirectory(start: directory => directory.ImportPackage())),
      Tool(T("Export tag package"), T("Saves the directory of tags, performers and studios as a tag package, for example for another library."), open,
       () => ShowDirectory(start: directory => directory.ExportPackage())),
      Tool(T("Import image archive (ZIP/RAR)"), T("Adds the images of an archive without extracting it."), open, ImportImageArchive),
      Tool(T("Save playlist"), T("Saves the items of the current view in the main window, in their order, as an M3U playlist."), open,
       async () => await SaveM3uAsync(selectionOnly: false)),
      Tool(T("Import playlist as collection"), T("Creates a collection from an M3U playlist with the items that are in the catalog."), open, () => ImportM3uPlaylist()))]),
   new(SecuritySection, "", T("Protect the library with a password."),
    _repository.IsEncrypted
     ? [Tool(T("Change password"), T("Sets a new password; the recovery key stays valid."), open, ChangeLibraryPassword),
        Tool(T("Remove encryption"), T("Saves the library unencrypted again."), open, DisableLibraryEncryption),
        _repository.EncryptsPictures
         ? Tool(T("Encrypt pictures"), T("Scene images, artwork and tag images are stored encrypted. Turned off, they are saved as plain image files; video thumbnails are never encrypted."), T("Turn off"), TogglePictureEncryption)
         : Tool(T("Encrypt pictures"), T("Scene images, artwork and tag images are stored as plain image files. Turned on, they are encrypted as well; video thumbnails are never encrypted."), T("Turn on"), TogglePictureEncryption)]
     : [Tool(T("Encrypt library with a password"), T("Encrypts the catalog; FlixyPix asks for the password when opening it."), open, EnableLibraryEncryption)]),
   new(LegacySection, "", T("Import from EazyFlixPix 4 and earlier legacy data."),
    [Tool(T("Import from EazyFlixPix 4"), T("Imports a library of the original into a new library of its own; the original stays unchanged."), open, ImportOriginalDatabase),
     Tool(T("Import old catalog (JSONL) into a new library"), T("For an export of the original that already exists."), open, ImportLegacyNewLibrary),
     Tool(T("Reconcile another snapshot"), T("Reconciles a later export of the original with this library; your own changes are kept."), open, ImportLegacySnapshot),
     Tool(T("View migration reports"), T("Shows what was imported, what was kept raw and why."), open, ShowMigrationReports),
     Tool(T("Historical templates and inactive drafts"), T("Searches, jobs and templates of the original that are not activated yet."), open, ShowHistoricalLibrary),
     Tool(T("Import historical functions"), T("Activates functions of the original that can be translated, after a review."), open, ShowHistoricalFunctions),
     Tool(T("Prepare offline export"), T("Instructions for exporting the original in an isolated environment."), open, ShowOfflineExportHelp)]),
  ];
 }

/// <summary>Folders of a library whose size the overview shows, with their German names.</summary>
 private static readonly (string Folder, string Label)[] OverviewFolders =
 [
  ("thumbnails", TKey("Video thumbnails")), ("media-assets", TKey("Custom artwork")), ("tag-assets", TKey("Tag images")),
  ("image-cache", TKey("Image cache")), ("archive-cache", TKey("Archive cache")), ("hover-cache", TKey("Hover preview")),
  ("clip-cache", TKey("Preview clips")),
 ];

 /// <summary>Values the overview shows once they are counted (also for tests).</summary>
 internal Dictionary<string, TextBlock> OverviewValues { get; } = [];
 internal Task OverviewReady { get; private set; } = Task.CompletedTask;

 /// <summary>The open library at a glance: place and protection, content, storage and maintenance. Counting runs in the
 /// background, so the window opens at once even for large libraries.</summary>
 private FrameworkElement OverviewContent(DatabaseWindow window)
 {
  OverviewValues.Clear();
  TextBlock Value(string key, string text = "…") => OverviewValues[key] = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
  var panel = new StackPanel();
  panel.Children.Add(Facts(T("Library"), (T("Name"), Value("name", LibraryName)), (T("Location"), Value("place", _settings.DataDirectory)),
   (T("Encryption"), Value("encryption", _repository.IsEncrypted ? T("encrypted") : T("not encrypted")))));
  panel.Children.Add(Facts(T("Content"), (T("Items"), Value("media")), (T("Tags"), Value("tags")), (T("Collections"), Value("collections")),
   (T("Saved searches"), Value("searches")), (T("Scenes and bookmarks"), Value("marks"))));
  panel.Children.Add(Facts(T("Storage"), [(T("Catalog"), Value("catalog")), (T("Graphics in the catalog"), Value("graphics")), (T("Imported legacy data"), Value("legacy")),
   .. OverviewFolders.Select(folder => (T(folder.Label), Value(folder.Folder)))]));
  panel.Children.Add(Facts(T("Maintenance"), (T("Last backup"), Value("backup")), (T("Last check"), Value("check"))));
  OverviewReady = FillOverviewAsync();
  return panel;
 }

 private static Border Facts(string title, params (string Label, TextBlock Value)[] rows)
 {
  var grid = new Grid { Margin = new Thickness(16, 10, 16, 12) };
  grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
  for (var index = 0; index < rows.Length; index++)
  {
   grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
   var label = new TextBlock { Text = rows[index].Label, Foreground = SettingsUi.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 12, 3) };
   Grid.SetRow(label, index); Grid.SetRow(rows[index].Value, index); Grid.SetColumn(rows[index].Value, 1); grid.Children.Add(label); grid.Children.Add(rows[index].Value);
  }
  return SettingsUi.Card(title, null, grid);
 }

 private async Task FillOverviewAsync()
 {
  var repository = _repository; var directory = _settings.DataDirectory;
  string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
  void Set(string key, string text, Brush? color = null) { if (OverviewValues.TryGetValue(key, out var block)) { block.Text = text; if (color != null) block.Foreground = color; } }
  try
  {
   var (statistics, log) = await Task.Run(() => (repository.GetStatistics(), repository.GetMaintenanceLog()));
   Set("media", T("{0} · {1} videos, {2} images, {3} audio", Tn(statistics.Media, "1 item", "{0:N0} items"), Count(statistics.Videos), Count(statistics.Images), Count(statistics.Audio)));
   Set("tags", Count(statistics.Tags)); Set("collections", Count(statistics.Collections)); Set("searches", Count(statistics.SavedSearches));
   Set("marks", T("{0} scenes, {1} bookmarks", Count(statistics.Scenes), Count(statistics.Bookmarks)));
   Set("catalog", MediaCard.SizeText(statistics.DatabaseBytes));
   Set("graphics", T("{0} media artwork ({1}), {2} tag images ({3})", Count(statistics.MediaGraphics), MediaCard.SizeText(statistics.MediaGraphicBytes),
    Count(statistics.TagPictures), MediaCard.SizeText(statistics.TagPictureBytes)));
   Set("legacy", statistics.LegacyRecords == 0 ? T("none") : T("{0} original records, {1} binary data", Count(statistics.LegacyRecords), MediaCard.SizeText(statistics.LegacyBlobBytes)));
   if (log.LastBackupAt is { } backup)
   {
    var old = DateTimeOffset.Now - backup > TimeSpan.FromDays(30);
    Set("backup", T("{0} · {1}", backup.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), MediaCard.SizeText(log.LastBackupBytes ?? 0))
     + (old ? T(" · older than 30 days") : ""), old ? SettingsUi.Gold : null);
    OverviewValues["backup"].ToolTip = log.LastBackupPath;
   }
   else Set("backup", T("no backup yet · create one under “Backup”"), SettingsUi.Gold);
   Set("check", log.LastCheckAt is { } check
    ? T("{0} · {1}", check.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), log.LastCheckIssues is 0 or null ? T("no problems") : Tn(log.LastCheckIssues.Value, "1 problem", "{0:N0} problems"))
    : T("not checked yet"));
   foreach (var (folder, _) in OverviewFolders)
   {
    var path = Path.Combine(directory, folder);
    var (files, bytes) = await Task.Run(() => FolderSize(path));
    Set(folder, files == 0 ? T("empty") : T("{0} · {1}", Tn(files, "1 file", "{0:N0} files"), MediaCard.SizeText(bytes)));
   }
  }
  catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
  { ErrorLog.Write("Datenbankverwaltung", error); }
 }

 private static (long Files, long Bytes) FolderSize(string path)
 {
  if (!Directory.Exists(path)) return (0, 0);
  long files = 0, bytes = 0;
  foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
  { files++; bytes += file.Length; }
  return (files, bytes);
 }
}
