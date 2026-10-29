using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>"Prüfen und reparieren" of the database management (plan phase V3): the check runs in the window, shows each
/// finding with its entries, and repairs only what the user leaves ticked, after a copy of the catalog.</summary>
public partial class MainWindow
{
 /// <summary>Copies of the catalog made before repairs.</summary>
 private string BackupFolder => _repository.GetBackupSchedule().FolderOf(_settings.DataDirectory);

 /// <summary>Result of the last check in the window and the run in progress (also for tests).</summary>
 internal CatalogCheckReport? LastCatalogCheck { get; private set; }
 internal Task CatalogCheckRun { get; private set; } = Task.CompletedTask;
 /// <summary>Kinds ticked for repair; missing files only when "remove" is chosen.</summary>
 internal HashSet<CatalogFindingKind> RepairChoice { get; } = [];
 internal CatalogRepairResult? LastRepair { get; private set; }

 private Func<Task>? _startCheck;
 private Func<bool, Task>? _repairChecked;

 /// <summary>Starts the check as the button would (for tests).</summary>
 internal Task StartCatalogCheck() => _startCheck?.Invoke() ?? Task.CompletedTask;
 /// <summary>Repairs the ticked findings; <paramref name="confirmed"/> skips the question (for tests).</summary>
 internal Task RepairCatalogChoice(bool confirmed) => _repairChecked?.Invoke(confirmed) ?? Task.CompletedTask;

 private static string FindingTitle(CatalogFindingKind kind) => kind switch
 {
  CatalogFindingKind.Integrity => T("Catalog file"),
  CatalogFindingKind.OrphanRows => T("Orphaned rows"),
  CatalogFindingKind.UnreadableEntries => T("Unreadable item entries"),
  CatalogFindingKind.QueryTable => T("Table for sorting, filters and tags"),
  CatalogFindingKind.SearchIndex => T("Quick search index"),
  CatalogFindingKind.PathKeys => T("Path keys"),
  CatalogFindingKind.SamePath => T("Duplicate entries of the same file"),
  CatalogFindingKind.MissingFiles => T("Missing media files"),
  CatalogFindingKind.OfflineFiles => T("Items on disconnected drives"),
  CatalogFindingKind.OrphanFiles => T("Unused files in the library folder"),
  CatalogFindingKind.MissingPictures => T("Missing or damaged pictures"),
  _ => T("Videos without thumbnail"),
 };

 private static string FindingExplanation(CatalogFindingKind kind) => kind switch
 {
  CatalogFindingKind.Integrity => T("SQLite checks the catalog file for damaged pages and indexes. Such damage can only be fixed with a backup."),
  CatalogFindingKind.OrphanRows => T("Scenes, bookmarks, graphics or tag rows of items that are no longer in the catalog. Fixing removes them."),
  CatalogFindingKind.UnreadableEntries => T("Entries whose stored details cannot be read. They stay unchanged; a backup restores them."),
  CatalogFindingKind.QueryTable => T("Differs from the item entries, so sorting, filters or tags can give wrong results. Fixing rebuilds it."),
  CatalogFindingKind.SearchIndex => T("Differs from the items, so quick search can miss results. Fixing rebuilds it."),
  CatalogFindingKind.PathKeys => T("The stored comparison value does not match the path, so the same file could be added twice. Fixing recalculates it."),
  CatalogFindingKind.SamePath => T("The same file is in the catalog more than once, with its path written differently. Fixing merges the entries; tags, rating, scenes, bookmarks and collections are kept."),
  CatalogFindingKind.MissingFiles => T("The file is missing although its drive is connected. Reassign, remove from the catalog or leave as offline."),
  CatalogFindingKind.OfflineFiles => T("Not a problem: these items stay unchanged in the catalog until the drive is connected again."),
  CatalogFindingKind.OrphanFiles => T("Thumbnails, graphics and cache files that no entry uses any more. Fixing deletes them."),
  CatalogFindingKind.MissingPictures => T("Scene images, artwork or tag images whose file beside the catalog is missing or damaged. Only a backup brings them back."),
  _ => T("The video file is there, but its thumbnail is missing. “Create” adds missing previews and metadata in the main window."),
 };

 private static string CheckStepText(CatalogCheckProgress step) => step.Step switch
 {
  CatalogFindingKind.Integrity => T("Checking the catalog file …"),
  CatalogFindingKind.UnreadableEntries => T("Reading item entries … {0:N0} of {1:N0}", step.Done, step.Total),
  CatalogFindingKind.QueryTable => T("Comparing the table for sorting, filters and tags …"),
  CatalogFindingKind.SearchIndex => T("Comparing the quick search index …"),
  CatalogFindingKind.MissingFiles => T("Looking for media files … {0:N0} of {1:N0}", step.Done, step.Total),
  CatalogFindingKind.MissingPictures => T("Checking pictures … {0:N0} of {1:N0}", step.Done, step.Total),
  _ => T("Looking for unused files …"),
 };

 private FrameworkElement CatalogCheckContent(DatabaseWindow window)
 {
  var start = SettingsUi.SmallButton(T("Start check")); start.Margin = new Thickness(0); start.MinWidth = 140;
  var cancel = SettingsUi.SmallButton(T("Cancel")); cancel.Visibility = Visibility.Collapsed;
  var repair = SettingsUi.SmallButton(T("Fix selected problems …")); repair.Margin = new Thickness(0); repair.IsEnabled = false;
  var state = new TextBlock { Text = CheckSummary(_repository.GetMaintenanceLog()), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
  var results = new StackPanel();
  var repairRow = new DockPanel { Margin = new Thickness(16, 10, 16, 12), Visibility = Visibility.Collapsed };
  var repairHint = SettingsUi.Hint(T("FlixyPix first saves a copy of the catalog in the backup folder.")); repairHint.Margin = new Thickness(12, 0, 0, 0); repairHint.VerticalAlignment = VerticalAlignment.Center;
  DockPanel.SetDock(repair, Dock.Left); repairRow.Children.Add(repair); repairRow.Children.Add(repairHint);
  var top = new DockPanel { Margin = new Thickness(16, 10, 16, 12) };
  var buttons = new StackPanel { Orientation = Orientation.Horizontal }; buttons.Children.Add(start); buttons.Children.Add(cancel);
  DockPanel.SetDock(buttons, Dock.Left); top.Children.Add(buttons); top.Children.Add(state);
  CancellationTokenSource? running = null;
  System.Windows.Automation.AutomationProperties.SetName(start, T("Start check"));
  void UpdateRepair() => repair.IsEnabled = !window.Busy && LastCatalogCheck != null && RepairChoice.Any(kind => LastCatalogCheck[kind].Count > 0);
  window.BusyChanged += () => { start.IsEnabled = !window.Busy; UpdateRepair(); };

  async Task Check()
  {
   if (window.Busy) return;
   var source = running = new CancellationTokenSource(); var token = source.Token;
   start.Visibility = Visibility.Collapsed; cancel.Visibility = Visibility.Visible; repairRow.Visibility = Visibility.Collapsed; results.Children.Clear();
   var repository = _repository; var directory = _settings.DataDirectory;
   var progress = new Progress<CatalogCheckProgress>(step => state.Text = CheckStepText(step));
   await window.RunHereAsync(async () =>
   {
    try
    {
     var report = await Task.Run(() => repository.CheckCatalog(directory, progress, token), token);
     LastCatalogCheck = report;
     state.Text = report.Issues == 0 ? T("{0} · no problems found", Tn(report.Media, "1 item checked", "{0:N0} items checked"))
      : T("{0} · {1}", Tn(report.Media, "1 item checked", "{0:N0} items checked"), Tn(report.Issues, "1 problem found", "{0:N0} problems found"));
     state.Foreground = report.Issues == 0 ? SettingsUi.Good : SettingsUi.Gold;
     ShowFindings(report);
    }
    catch (OperationCanceledException) { state.Text = T("Check cancelled"); state.Foreground = SettingsUi.Muted; }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
    { ErrorLog.Write("Datenbankverwaltung", error); state.Text = T("Check failed: {0}", error.Message); state.Foreground = SettingsUi.Error; }
    finally { source.Dispose(); running = null; start.Visibility = Visibility.Visible; cancel.Visibility = Visibility.Collapsed; start.Content = T("Check again"); }
   });
   UpdateRepair();
  }

  void ShowFindings(CatalogCheckReport report)
  {
   results.Children.Clear(); RepairChoice.Clear();
   foreach (var finding in report.Findings) results.Children.Add(FindingRow(window, report, finding, UpdateRepair));
   repairRow.Visibility = report.Findings.Any(f => f.Repairable && f.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
  }

  async Task Repair(bool confirmed)
  {
   if (LastCatalogCheck is not { } report || window.Busy) return;
   var kinds = RepairChoice.Where(kind => report[kind].Count > 0).ToHashSet();
   if (kinds.Count == 0) return;
   var lines = string.Join("\n", report.Findings.Where(f => kinds.Contains(f.Kind)).Select(f => "• " + FindingTitle(f.Kind) + " (" + f.Count.ToString("N0") + ")"));
   if (!confirmed && Message.Show(window, T("The following will be changed:\n\n{0}\n\nFlixyPix first saves a copy of the catalog in the backup folder.", lines),
    T("Fix problems"), MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
   var repository = _repository; var backups = BackupFolder; CatalogRepairResult? result = null;
   await window.RunHereAsync(async () =>
   {
    state.Text = T("Fixing problems …"); state.Foreground = SettingsUi.Muted;
    try { result = await Task.Run(() => repository.RepairCatalog(report, kinds, backups)); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ArgumentException)
    { ErrorLog.Write("Datenbankverwaltung", error); state.Text = T("Fixing failed: {0}", error.Message); state.Foreground = SettingsUi.Error; }
   });
   if (result is null) return;
   LastRepair = result;
   Refresh();
   await Check();
   window.Report(T("{0} fixed · copy of the catalog before: {1}", Tn(result.Repaired.Values.Sum(), "1 problem", "{0:N0} problems"), result.BackupPath));
  }

  start.Click += async (_, _) => await Check();
  cancel.Click += (_, _) => running?.Cancel();
  repair.Click += async (_, _) => await Repair(false);
  _startCheck = Check; _repairChecked = Repair; LastCatalogCheck = null;
  return SettingsUi.Card(T("Check catalog"), T("Checks the catalog file, the tables for search and sorting, duplicate entries, missing media files and unused files in the library folder. The check changes nothing; fixes happen only after you choose them and confirm."),
   top, results, repairRow);
 }

 private static string CheckSummary(CatalogMaintenanceLog log) => log.LastCheckAt is { } at
  ? T("Last checked {0} · {1}", at.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
   log.LastCheckIssues is 0 or null ? T("no problems") : Tn(log.LastCheckIssues.Value, "1 problem", "{0:N0} problems"))
  : T("not checked yet");

 /// <summary>One line of the result: state, title, count and what can be done; the entries open below.</summary>
 private FrameworkElement FindingRow(DatabaseWindow window, CatalogCheckReport report, CatalogFinding finding, Action changed)
 {
  var problem = finding.IsProblem;
  var icon = SettingsUi.Icon(finding.Count == 0 ? "" : problem ? "" : "", 15);
  icon.Foreground = finding.Count == 0 ? SettingsUi.Good : problem ? SettingsUi.Gold : SettingsUi.Muted; icon.Margin = new Thickness(0, 2, 12, 0); icon.VerticalAlignment = VerticalAlignment.Top;
  var count = finding.Count == 0 ? T("no problems")
   : finding.Kind == CatalogFindingKind.OrphanFiles ? T("{0} · {1}", Tn(finding.Count, "1 file", "{0:N0} files"), MediaCard.SizeText(report.OrphanFileBytes))
   : finding.Kind is CatalogFindingKind.Integrity or CatalogFindingKind.SearchIndex ? Tn(finding.Count, "1 error", "{0:N0} errors")
   : Tn(finding.Count, "1 entry", "{0:N0} entries");
  var heading = new TextBlock { TextWrapping = TextWrapping.Wrap };
  heading.Inlines.Add(new System.Windows.Documents.Run(FindingTitle(finding.Kind)) { FontWeight = FontWeights.SemiBold });
  heading.Inlines.Add(new System.Windows.Documents.Run(" · " + count) { Foreground = problem ? SettingsUi.Gold : SettingsUi.Muted });
  var text = new StackPanel(); text.Children.Add(heading);
  if (finding.Count > 0)
  {
   text.Children.Add(SettingsUi.Hint(FindingExplanation(finding.Kind)));
   var more = finding.Count - finding.Examples.Count;
   var list = new TextBox { Text = string.Join("\n", finding.Examples) + (more > 0 ? "\n" + T("… and {0:N0} more", more) : ""), IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
    MaxHeight = 160, FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) };
   text.Children.Add(new Expander { Header = T("Show affected entries"), Content = list, Margin = new Thickness(0, 4, 0, 0), FontSize = 12 });
  }
  var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
  if (finding.Count > 0)
   switch (finding.Kind)
   {
    case CatalogFindingKind.MissingFiles:
     var choice = new ComboBox { Items = { T("Leave as offline"), Tc("catalog check", "Remove from catalog") }, SelectedIndex = 0, MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
     choice.SelectionChanged += (_, _) => { if (choice.SelectedIndex == 1) RepairChoice.Add(finding.Kind); else RepairChoice.Remove(finding.Kind); changed(); };
     System.Windows.Automation.AutomationProperties.SetName(choice, FindingTitle(finding.Kind));
     var map = SettingsUi.SmallButton(T("Reassign …"));
     map.Click += (_, _) => window.Run(new(T("Reassign media paths"), "", "", MapMigrationPaths));
     actions.Children.Add(choice); actions.Children.Add(map);
     break;
    case CatalogFindingKind.MissingThumbnails:
     var make = SettingsUi.SmallButton(T("Create"));
     make.Click += (_, _) => window.Run(new(T("Create missing previews and metadata"), "", "", async () => await RunOperation(T("Prepare media"), PrepareMediaAsync)));
     actions.Children.Add(make);
     break;
    case CatalogFindingKind.Integrity or CatalogFindingKind.UnreadableEntries or CatalogFindingKind.MissingPictures:
     var restore = SettingsUi.SmallButton(T("Go to backup …"));
     restore.Click += (_, _) => window.ShowSection(BackupSection);
     actions.Children.Add(restore);
     break;
    default:
     if (!finding.Repairable) break;
     var fix = new CheckBox { Content = T("Fix"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
     RepairChoice.Add(finding.Kind);
     fix.Click += (_, _) => { if (fix.IsChecked == true) RepairChoice.Add(finding.Kind); else RepairChoice.Remove(finding.Kind); changed(); };
     System.Windows.Automation.AutomationProperties.SetName(fix, T("Fix") + ": " + FindingTitle(finding.Kind));
     actions.Children.Add(fix);
     break;
   }
  var row = new DockPanel { Margin = new Thickness(16, 8, 16, 8) };
  DockPanel.SetDock(icon, Dock.Left); DockPanel.SetDock(actions, Dock.Right);
  row.Children.Add(icon); row.Children.Add(actions); row.Children.Add(text);
  return row;
 }

 private async Task RebuildSearchIndexHere(DatabaseWindow window)
 {
  var repository = _repository; var backups = BackupFolder;
  window.Report(T("Rebuilding search index …"));
  try
  {
   var count = await Task.Run(() => { repository.CreateSafetyCopy(backups, "reindex"); return repository.RebuildSearchIndex(); });
   window.Report(Tn(count, "Search index rebuilt · 1 item", "Search index rebuilt · {0:N0} items"));
   Refresh();
  }
  catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
  { window.Report(""); Message.Show(window, error.Message, T("Rebuild search index"), MessageBoxButton.OK, MessageBoxImage.Warning); }
 }

 private async Task CompactHere(DatabaseWindow window)
 {
  var repository = _repository;
  window.Report(T("Compacting …"));
  try
  {
   var (before, after) = await Task.Run(repository.Compact);
   window.Report(T("Catalog compacted: {0} → {1}", MediaCard.SizeText(before), MediaCard.SizeText(after)));
  }
  catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException)
  { window.Report(""); Message.Show(window, error.Message, T("Compact"), MessageBoxButton.OK, MessageBoxImage.Warning); }
 }
}
