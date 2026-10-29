using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

/// <summary>Area "Sicherung" of the database management (plan phase V4): automatic backups at fixed intervals with a chosen
/// number kept, the backups of the folder with checking and restoring, and checking any package without restoring it.</summary>
public partial class MainWindow
{
 private readonly DispatcherTimer _backupTimer = new() { Interval = TimeSpan.FromMinutes(30) };

 /// <summary>Checks after the start and then every half hour whether an automatic backup is due.</summary>
 private void InitializeBackupSchedule()
 {
  if (!_persistPreferences) return;
  _backupTimer.Tick += async (_, _) => await RunScheduledBackupAsync();
  _backupTimer.Start();
  Closing += (_, _) => _backupTimer.Stop();
  _ = Dispatcher.InvokeAsync(async () => { await Startup; await RunScheduledBackupAsync(); }, DispatcherPriority.ApplicationIdle);
 }

 /// <summary>Writes the automatic backup when it is due and nothing else runs; as an operation it shows its progress and
 /// can be cancelled. Returns whether a backup was written (also for tests).</summary>
 internal async Task<bool> RunScheduledBackupAsync(bool force = false)
 {
  if (_closing || _operation != null) return false;
  BackupSchedule schedule; CatalogMaintenanceLog log;
  try { schedule = _repository.GetBackupSchedule(); log = _repository.GetMaintenanceLog(); }
  catch (Exception error) when (error is ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException) { return false; }
  if (!force && !schedule.IsDue(log, DateTimeOffset.Now)) return false;
  var written = false; var repository = _repository; var directory = _settings.DataDirectory;
  await RunOperation(T("Automatic backup"), async token =>
  {
   var progress = new Progress<CatalogBackupProgress>(value => Status.Text = T("Automatic backup · files: {0}", value.CompletedFiles));
   var result = await Task.Run(() => repository.CreateAutomaticBackupAsync(directory, token, progress), token);
   Status.Text = T("Automatic backup created: {0} ({1})", Path.GetFileName(result.PackagePath), MediaCard.SizeText(new FileInfo(result.PackagePath).Length));
   written = true;
  }, refresh: false);
  return written;
 }

 /// <summary>Controls of the backup area, for tests.</summary>
 internal ComboBox? BackupIntervalChoice { get; private set; }
 internal ListBox? BackupList { get; private set; }

 private static readonly int[] BackupIntervals = [0, 1, 7, 30];
 private static readonly int[] BackupCounts = [3, 5, 10, 20];

 private FrameworkElement BackupContent(DatabaseWindow window)
 {
  var schedule = _repository.GetBackupSchedule();
  var interval = new ComboBox { MinWidth = 200, Items = { T("Off"), T("Daily"), T("Weekly"), T("Monthly") } };
  interval.SelectedIndex = Math.Max(0, Array.IndexOf(BackupIntervals, BackupIntervals.LastOrDefault(days => days <= schedule.IntervalDays)));
  var keep = new ComboBox { MinWidth = 200 };
  foreach (var count in BackupCounts) keep.Items.Add(Tn(count, "1 backup", "{0} backups"));
  keep.SelectedIndex = Math.Max(0, Array.IndexOf(BackupCounts, BackupCounts.LastOrDefault(count => count <= schedule.Keep, BackupCounts[0])));
  var media = new CheckBox { Content = T("Also back up the media files"), IsChecked = schedule.IncludeMedia, VerticalAlignment = VerticalAlignment.Center };
  var folder = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = SettingsUi.Muted };
  var change = SettingsUi.SmallButton(T("Change …")); var reset = SettingsUi.SmallButton(T("Default"));
  var now = SettingsUi.SmallButton(T("Back up now")); now.Margin = new Thickness(0);
  var list = new ListBox { MaxHeight = 260, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
  ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
  var empty = SettingsUi.Hint(""); empty.Margin = new Thickness(16, 4, 16, 12);
  BackupIntervalChoice = interval; BackupList = list;
  System.Windows.Automation.AutomationProperties.SetName(interval, T("Back up automatically")); System.Windows.Automation.AutomationProperties.SetName(keep, T("Keep"));

  string Folder() => _repository.GetBackupSchedule().FolderOf(_settings.DataDirectory);
  void Save(Func<BackupSchedule, BackupSchedule> change)
  {
   _repository.SaveBackupSchedule(change(_repository.GetBackupSchedule()));
   folder.Text = Folder(); reset.Visibility = _repository.GetBackupSchedule().Folder == null ? Visibility.Collapsed : Visibility.Visible;
  }
  void Fill()
  {
   list.Items.Clear();
   foreach (var backup in BackupRotation.List(Folder())) list.Items.Add(BackupRow(window, backup, Fill));
   empty.Text = list.Items.Count == 0 ? T("There are no FlixyPix backups in the backup folder yet.") : T("Before every repair FlixyPix saves a copy of the catalog here; older backups and copies beyond the chosen number are deleted.");
  }
  interval.SelectionChanged += (_, _) => Save(s => s with { IntervalDays = BackupIntervals[interval.SelectedIndex] });
  keep.SelectionChanged += (_, _) => { Save(s => s with { Keep = BackupCounts[keep.SelectedIndex] }); BackupRotation.Prune(Folder(), BackupCounts[keep.SelectedIndex]); Fill(); };
  media.Click += (_, _) => Save(s => s with { IncludeMedia = media.IsChecked == true });
  change.Click += (_, _) =>
  {
   var picker = new OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose folder for backups"), InitialDirectory = Directory.Exists(Folder()) ? Folder() : _settings.DataDirectory };
   if (picker.ShowDialog(window) != true) return;
   Save(s => s with { Folder = picker.FolderName }); Fill();
  };
  reset.Click += (_, _) => { Save(s => s with { Folder = null }); Fill(); };
  now.Click += async (_, _) => await window.RunHereAsync(async () =>
  {
   window.Report(T("Creating backup …"));
   var repository = _repository; var directory = _settings.DataDirectory;
   try
   {
    var progress = new Progress<CatalogBackupProgress>(value => window.Report(T("Automatic backup · files: {0}", value.CompletedFiles)));
    var result = await Task.Run(() => repository.CreateAutomaticBackupAsync(directory, CancellationToken.None, progress));
    window.Report(T("Automatic backup created: {0} ({1})", Path.GetFileName(result.PackagePath), MediaCard.SizeText(new FileInfo(result.PackagePath).Length)));
   }
   catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
   { window.Report(""); Message.Show(window, error.Message, T("Back up now"), MessageBoxButton.OK, MessageBoxImage.Warning); }
   Fill();
  });
  Save(s => s); Fill();
  // Backups made elsewhere (automatic, before a repair) appear when the area is shown again.
  list.IsVisibleChanged += (_, e) => { if (e.NewValue is true) Fill(); };

  Grid Rows(params (string Label, UIElement Value)[] rows)
  {
   var grid = new Grid { Margin = new Thickness(16, 10, 16, 12) };
   grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
   for (var index = 0; index < rows.Length; index++)
   {
    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    var label = new TextBlock { Text = rows[index].Label, Foreground = SettingsUi.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 12, 6), VerticalAlignment = VerticalAlignment.Center };
    var value = rows[index].Value; if (value is FrameworkElement element) { element.Margin = new Thickness(0, 4, 0, 4); element.HorizontalAlignment = HorizontalAlignment.Left; }
    Grid.SetRow(label, index); Grid.SetRow(value, index); Grid.SetColumn(value, 1); grid.Children.Add(label); grid.Children.Add(value);
   }
   return grid;
  }
  var place = new DockPanel(); var placeButtons = new StackPanel { Orientation = Orientation.Horizontal }; placeButtons.Children.Add(change); placeButtons.Children.Add(reset);
  DockPanel.SetDock(placeButtons, Dock.Right); place.Children.Add(placeButtons); place.Children.Add(folder);
  var schedulePanel = Rows((T("Back up automatically"), interval), (T("Keep"), keep), (T("Scope"), media), (T("Folder"), place));
  ((FrameworkElement)schedulePanel.Children[^1]).HorizontalAlignment = HorizontalAlignment.Stretch;
  var nowRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 10, 16, 12) };
  nowRow.Children.Add(now);
  var nowHint = SettingsUi.Hint(T("Creates a backup package in this folder right away.")); nowHint.Margin = new Thickness(12, 0, 0, 0); nowHint.VerticalAlignment = VerticalAlignment.Center; nowRow.Children.Add(nowHint);
  var panel = new StackPanel();
  panel.Children.Add(SettingsUi.Card(T("Automatic backup"), T("Backs up the catalog with graphics, scenes, collections and settings at fixed intervals while FlixyPix is running with this library."), schedulePanel, nowRow));
  var listPanel = new StackPanel(); listPanel.Children.Add(empty); listPanel.Children.Add(list);
  panel.Children.Add(SettingsUi.Card(T("Existing backups"), null, listPanel));
  return panel;
 }

 private FrameworkElement BackupRow(DatabaseWindow window, StoredBackup backup, Action changed)
 {
  var name = new TextBlock { Text = backup.Kind == StoredBackupKind.Automatic ? T("Backup package") : T("Copy of the catalog before a repair"), FontWeight = FontWeights.SemiBold };
  var details = SettingsUi.Hint(T("{0} · {1} · {2}", backup.WrittenAt.ToString("g", CultureInfo.CurrentCulture), MediaCard.SizeText(backup.Bytes), Path.GetFileName(backup.Path)));
  details.ToolTip = backup.Path;
  var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; text.Children.Add(name); text.Children.Add(details);
  var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
  if (backup.Kind == StoredBackupKind.Automatic)
  {
   var check = SettingsUi.SmallButton(T("Check"));
   check.Click += async (_, _) => await window.RunHereAsync(() => VerifyBackupHere(window, backup.Path));
   buttons.Children.Add(check);
  }
  var restore = SettingsUi.SmallButton(T("Restore …"));
  restore.Click += (_, _) =>
  {
   if (backup.Kind == StoredBackupKind.Automatic) window.Run(new(T("Restore backup package"), "", "", () => RestoreBackupPackage(backup.Path)));
   else RestoreCatalogCopy(window, backup.Path);
  };
  buttons.Children.Add(restore);
  var row = new DockPanel { Margin = new Thickness(12, 6, 4, 6) }; DockPanel.SetDock(buttons, Dock.Right); row.Children.Add(buttons); row.Children.Add(text);
  return new ListBoxItem { Content = row, Tag = backup, Focusable = false };
 }

 /// <summary>Checks a package completely without restoring it (also for tests: the text shown in the status line).</summary>
 internal async Task VerifyBackupHere(DatabaseWindow window, string? path = null)
 {
  if (path == null)
  {
   var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Check backup package"), Filter = T("FlixyPix backups|*.flixypixbackup;*.ezfpbackup|All files|*.*") };
   if (picker.ShowDialog(window) != true) return;
   path = picker.FileName;
  }
  if (!SecretDialogs.TryAskContainerSecret(window, path, T("Check backup package"), out var secret)) return;
  window.Report(T("Checking backup package …"));
  try
  {
   var progress = new Progress<CatalogBackupProgress>(value => window.Report(T("Checking backup package … files: {0:N0}", value.CompletedFiles)));
   var check = await Task.Run(() => CatalogRepository.VerifyBackupAsync(path, CancellationToken.None, progress, secret));
   window.Report(T("Backup package in order: {0}, {1}, {2} · {3}", Tn(check.Media, "1 item", "{0:N0} items"), Tn(check.Files, "1 file", "{0:N0} files"),
    MediaCard.SizeText(check.Bytes), Path.GetFileName(path)));
  }
  catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or CatalogLockedException or System.Security.Cryptography.CryptographicException or NotSupportedException)
  {
   window.Report(T("Backup package is faulty"));
   Message.Show(window, T("The backup package is not in order:\n\n{0}", error.Message), T("Check backup package"), MessageBoxButton.OK, MessageBoxImage.Warning);
  }
 }

 /// <summary>Makes a copy of the catalog made before a repair into a new library of its own and offers to open it; the open
 /// library stays as it is.</summary>
 internal void RestoreCatalogCopy(DatabaseWindow window, string copy, bool open = true)
 {
  try
  {
   var parent = _persistPreferences ? DatabaseWindow.DefaultParent : Path.Combine(Path.GetDirectoryName(_settings.DataDirectory)!, "libraries");
   var name = T("{0} (as of {1})", LibraryName, File.GetLastWriteTime(copy).ToString("g", CultureInfo.CurrentCulture));
   var directory = LibraryCatalogs.NewLibraryDirectory(parent, Path.GetFileNameWithoutExtension(copy));
   FileAccessPolicy.Current.EnsureWritable(directory); Directory.CreateDirectory(directory);
   var catalog = LibraryCatalogs.CatalogPath(directory);
   File.Copy(copy, catalog);
   if (File.Exists(CatalogEncryption.KeyFilePath(copy))) File.Copy(CatalogEncryption.KeyFilePath(copy), CatalogEncryption.KeyFilePath(catalog));
   window.AddLibrary(directory, name);
   if (open && Message.Show(window, T("The copy is now a library of its own, “{0}”. The open library stays unchanged.\n\nOpen it now?", name),
    T("Restore"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) window.Choose(directory);
   else window.Report(T("“{0}” is now listed under Libraries.", name));
  }
  catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
  { Message.Show(window, error.Message, T("Restore"), MessageBoxButton.OK, MessageBoxImage.Warning); }
 }
}
