using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Libraries: a second library is created and opened in place of this one, then this one again; the manager
 /// renames, adds, moves, compacts and deletes. Returns the main window that is open at the end.</summary>
 internal async Task<MainWindow> RunLibrarySwitchSmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  static async Task Ready(MainWindow window) { await window.Startup; await window._lastRefresh; await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
  var first = _settings.DataDirectory;
  var mediaCount = _repository.Search().Count;
  Require(mediaCount > 0 && _settings.Libraries.Count == 1 && Title == "FlixyPix", "The open library was not remembered on its own.");

  // The database management: every area builds, the tools are there, and a tool waits for the window to close.
  var database = CreateDatabaseWindow(); database.Show(); await database.Inspected; await OverviewReady;
  Require(OverviewValues["media"].Text.Contains(Tn(mediaCount, "1 item", "{0:N0} items"), StringComparison.Ordinal) &&OverviewValues.Values.All(value => value.Text != "…"),
   "The overview did not count the library: " + string.Join(" | ", OverviewValues.Select(pair => pair.Key + "=" + pair.Value.Text)));
  Require(database.Section == OverviewSection && database.SectionNames.SequenceEqual([OverviewSection, DatabaseWindow.LibrariesSection, RepairSection, BackupSection, TransferSection, SecuritySection, LegacySection]),
   "The database management does not show its areas in order.");
  foreach (var name in database.SectionNames)
  {
   database.ShowSection(name); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Require(database.Section == name && (name == DatabaseWindow.LibrariesSection || database.ToolsShown.Count > 0), $"The area {name} has no tools.");
   Capture((FrameworkElement)database.Content, Path.Combine(directory, "database-" + database.SectionNames.ToList().IndexOf(name) + ".png"));
  }
  // Check and repair: an unused, older file in a cache is found, removed after a copy of the catalog, and the check is noted.
  database.ShowSection(RepairSection); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  var unused = Path.Combine(_settings.DataDirectory, "hover-cache", Guid.NewGuid().ToString("N") + "-1-0.jpg");
  Directory.CreateDirectory(Path.GetDirectoryName(unused)!); File.WriteAllBytes(unused, [1, 2, 3]); File.SetLastWriteTimeUtc(unused, DateTime.UtcNow.AddHours(-1));
  await StartCatalogCheck();
  Require(LastCatalogCheck is { } check && check.Media == mediaCount && check[CatalogFindingKind.Integrity].Count == 0 && check[CatalogFindingKind.OrphanFiles].Examples.Any(e => e.EndsWith(Path.GetFileName(unused), StringComparison.Ordinal))
   && RepairChoice.Contains(CatalogFindingKind.OrphanFiles) && !RepairChoice.Contains(CatalogFindingKind.MissingFiles), "The catalog check did not find the unused file: " + string.Join(" | ", LastCatalogCheck?.Findings.Select(f => f.Kind + "=" + f.Count) ?? []));
  Capture((FrameworkElement)database.Content, Path.Combine(directory, "database-check.png"));
  RepairChoice.IntersectWith([CatalogFindingKind.OrphanFiles]);
  await RepairCatalogChoice(confirmed: true);
  Require(!File.Exists(unused) && LastRepair is { } repaired && File.Exists(repaired.BackupPath) && LastCatalogCheck is { } after && after[CatalogFindingKind.OrphanFiles].Count == 0
   && _repository.GetMaintenanceLog().LastCheckAt is not null, "The repair did not remove the unused file after a copy of the catalog.");
  var reindex = database.ToolsShown.First(tool => tool.Here != null);
  await database.RunHereAsync(() => reindex.Here!(database));
  Require(_repository.PendingSearchIndexRows() == 0 && _repository.Search().Count == mediaCount && database.StatusText.Length > 0, "Rebuilding the search index lost media: " + database.StatusText);
  // Backup: a daily schedule makes an automatic package at once, it is listed beside the copy from the repair and checks out.
  database.ShowSection(BackupSection); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  BackupIntervalChoice!.SelectedIndex = 1;
  Require(_repository.GetBackupSchedule().IntervalDays == 1 && await RunScheduledBackupAsync() && !await RunScheduledBackupAsync(), "The daily backup was not written once.");
  var stored = BackupRotation.List(_repository.GetBackupSchedule().FolderOf(_settings.DataDirectory));
  Require(stored.Any(b => b.Kind == StoredBackupKind.Automatic) && stored.Any(b => b.Kind == StoredBackupKind.SafetyCopy), "The backup folder does not hold the package and the repair copy.");
  await database.RunHereAsync(() => VerifyBackupHere(database, stored.First(b => b.Kind == StoredBackupKind.Automatic).Path));
  Require(database.StatusText.Contains(Tn(mediaCount, "1 item", "{0:N0} items"), StringComparison.Ordinal), "The package did not check out: " + database.StatusText);
  database.ShowSection(OverviewSection); database.ShowSection(BackupSection); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Capture((FrameworkElement)database.Content, Path.Combine(directory, "database-backup.png"));
  BackupIntervalChoice.SelectedIndex = 0;
  database.ShowSection(OverviewSection); var errorLog = database.ToolsShown[1]; database.Run(errorLog);
  Require(database.Pending == errorLog && !database.IsVisible && database.Chosen == null, "A chosen tool did not close the window and wait.");

  var manager = CreateDatabaseWindow(DatabaseWindow.LibrariesSection); manager.Show(); await manager.Inspected;
  Require(manager.Section == DatabaseWindow.LibrariesSection && manager.Libraries.Count == 1, "The library list does not show the open library.");
  manager.Create("Zweite Bibliothek");
  Require(manager.Chosen is { } created && Directory.Exists(created) && !manager.IsVisible, "A new library was not created and chosen.");
  var second = manager.Chosen!;
  var next = await SwitchLibraryAsync(second) ?? throw new InvalidOperationException("The second library did not open.");
  await Ready(next);
  Require(!IsVisible && next.IsVisible && LibraryCatalogs.SameDirectory(next._settings.DataDirectory, second), "The main window did not switch to the second library.");
  Require(next._repository.Search().Count == 0 && File.Exists(LibraryCatalogs.CatalogPath(second)), "The new library is not an empty catalog of its own.");
  Require(next._settings.Libraries.Count == 2 && next.Title == "Zweite Bibliothek · FlixyPix" && next.LibraryHeader.Text == "ZWEITE BIBLIOTHEK",
   "The window does not name the open library once there are two.");
  Capture(next, Path.Combine(directory, "library-switched.png"));

  var back = await next.SwitchLibraryAsync(first) ?? throw new InvalidOperationException("The first library did not open again.");
  await Ready(back);
  Require(!next.IsVisible && back.IsVisible && back._repository.Search().Count == mediaCount, "Switching back did not bring the first library.");
  Require(Application.Current.Windows.OfType<MainWindow>().Count(w => w.IsVisible) == 1, "More than one main window is left.");

  manager = back.CreateDatabaseWindow(DatabaseWindow.LibrariesSection); manager.Show(); await manager.Inspected;
  Require(!manager.ShowInTaskbar && back.ShowInTaskbar, "A secondary window has a taskbar button of its own.");
  // One instance per library: the open one is taken, another one starts a second instance with "--library".
  Require(LibraryInstance.IsOpen(first) && LibraryInstance.Claim(first, () => { }) == null, "The open library is not held by this instance.");
  var launchBefore = DatabaseWindow.Launch; System.Diagnostics.ProcessStartInfo? launched = null;
  try { DatabaseWindow.Launch = start => launched = start; manager.OpenInNewWindow(second); }
  finally { DatabaseWindow.Launch = launchBefore; }
  Require(launched is { ArgumentList: ["--library", var target] } && LibraryCatalogs.SameDirectory(target, second), "Opening in a new window did not start an instance for the library.");
  // A library held by another instance comes to the front there instead of opening twice.
  var activated = new TaskCompletionSource();
  using (var otherInstance = LibraryInstance.Claim(second, () => activated.TrySetResult()) ?? throw new InvalidOperationException("The second library could not be held."))
  {
   manager.Choose(second);
   Require(manager.IsVisible && manager.Chosen == null, "Choosing a library open elsewhere switched to it.");
   Require(await Task.WhenAny(activated.Task, Task.Delay(5000)) == activated.Task, "The instance with the library was not asked to come to the front.");
  }
  Require(!LibraryInstance.IsOpen(second), "Closing an instance did not free its library.");
  manager.Select(second); manager.Rename("Archiv");
  Require(LibraryCatalogs.DisplayName(manager.Libraries.Single(l => LibraryCatalogs.SameDirectory(l.Directory, second))) == "Archiv", "Renaming did not change the name.");
  var elsewhere = Path.Combine(directory, "moved-libraries"); Directory.CreateDirectory(elsewhere);
  await manager.MoveAsync(elsewhere);
  var moved = Path.Combine(elsewhere, Path.GetFileName(second));
  Require(!Directory.Exists(second) && File.Exists(LibraryCatalogs.CatalogPath(moved)) && manager.Libraries.Any(l => LibraryCatalogs.SameDirectory(l.Directory, moved)),
   "Moving did not take the folder and the list entry along.");
  var empty = Path.Combine(directory, "empty-library"); Directory.CreateDirectory(empty);
  manager.AddExisting(empty);
  Require(manager.Libraries.Count == 3, "An empty folder was not added as a library.");
  var recycled = new List<string>(); var recycleBefore = SendToRecycleBin;
  try
  {
   SendToRecycleBin = path => { recycled.Add(path); Directory.Delete(path, true); };
   manager.Select(moved); await manager.DeleteAsync(confirmed: true);
  }
  finally { SendToRecycleBin = recycleBefore; }
  Require(recycled.Count == 1 && !Directory.Exists(moved) && manager.Libraries.Count == 2, "Deleting did not send the library folder to the recycle bin.");
  manager.Select(first); await manager.CompactAsync();
  Require(manager.StatusText.Contains('→'), "Compacting the open library reported nothing: " + manager.StatusText);
  Require(back._repository.Search().Count == mediaCount, "Compacting changed the catalog.");
  manager.Close();
  return back;
 }
}
