using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

/// <summary>Library folders and the changes the last check found. Folder options are saved at once; changes are taken
/// over only for the rows left ticked.</summary>
internal sealed class LibraryFoldersWindow : Window
{
 private readonly CatalogRepository _repository;
 private readonly Action _foldersChanged;
 private readonly Func<FolderSyncPlan, IReadOnlyList<FolderChange>, bool, Task<FolderSyncResult>> _apply;
 private readonly StackPanel _folders = new();
 private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
 private readonly ListView _changes = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
 private readonly CheckBox _ignoreDeclined = new() { Content = T("Don't offer deselected new files again"), VerticalAlignment = VerticalAlignment.Center };
 private readonly Button _applyButton = new() { Content = T("Apply selected"), IsEnabled = false };
 private readonly Button _check = new() { Content = T("Check now") };
 private readonly Button _adopt = new() { Visibility = Visibility.Collapsed };
 private FolderSyncPlan? _plan;
 private CancellationTokenSource? _checking;
 internal FolderSyncPlan? Plan => _plan;
 internal IReadOnlyList<ChangeRow> Rows => _changes.ItemsSource as IReadOnlyList<ChangeRow> ?? [];

 internal sealed class ChangeRow(FolderChange change)
 {
  public FolderChange Change { get; } = change;
  public bool Chosen { get; set; } = change.Kind != FolderChangeKind.Missing;
  public bool CanChoose => Change.Kind != FolderChangeKind.Missing;
  public string Type => Change.Kind switch { FolderChangeKind.Moved => T("Moved"), FolderChangeKind.Renamed => T("Renamed"), FolderChangeKind.New => T("New"), _ => T("Missing") };
  public string Name => Path.GetFileName(Change.Path);
  public string Where => Change.Kind is FolderChangeKind.Moved or FolderChangeKind.Renamed ? T("before ") + Change.PreviousPath + "\njetzt " + Change.Path : Change.Path;
 }

 internal LibraryFoldersWindow(Window owner, CatalogRepository repository, FolderSyncPlan? plan, Action foldersChanged,
  Func<FolderSyncPlan, IReadOnlyList<FolderChange>, bool, Task<FolderSyncResult>> apply)
 {
  Owner = owner; Title = T("Media folders · FlixyPix"); Width = 980; Height = 720; MinWidth = 760; MinHeight = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner; FontSize = 14;
  _repository = repository; _foldersChanged = foldersChanged; _apply = apply;
  var layout = new Grid { Margin = new Thickness(22) };
  foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) layout.RowDefinitions.Add(new RowDefinition { Height = height });

  var header = new StackPanel();
  header.Children.Add(new TextBlock { Text = T("Media folders"), FontSize = 25, FontWeight = FontWeights.SemiBold });
  header.Children.Add(SettingsUi.Hint(T("FlixyPix reconciles these folders with the catalog: new files are offered, moved and renamed files reassigned, missing ones reported. The files themselves stay unchanged.")));
  var add = new Button { Content = T("＋ Add folder …"), Style = (Style)Application.Current.Resources["Primary"] };
  add.Click += (_, _) => AddFolders();
  _check.Click += async (_, _) => await CheckAsync();
  _adopt.Click += (_, _) => AdoptFromOriginal();
  var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
  foreach (var button in new[] { add, _check, _adopt }) { button.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(button); }
  header.Children.Add(actions);
  layout.Children.Add(header);

  var folderScroll = new ScrollViewer { Content = _folders, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 10) };
  Grid.SetRow(folderScroll, 1); layout.Children.Add(folderScroll);

  var changesPanel = new DockPanel();
  var changesHeader = new StackPanel();
  changesHeader.Children.Add(new TextBlock { Text = T("Changes found"), FontSize = 17, FontWeight = FontWeights.SemiBold });
  changesHeader.Children.Add(_summary);
  DockPanel.SetDock(changesHeader, Dock.Top); changesPanel.Children.Add(changesHeader);
  var view = new GridView();
  var check = new FrameworkElementFactory(typeof(CheckBox));
  check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ChangeRow.Chosen)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
  check.SetBinding(IsEnabledProperty, new Binding(nameof(ChangeRow.CanChoose)));
  check.SetValue(AutomationProperties.NameProperty, T("Apply"));
  view.Columns.Add(new GridViewColumn { Header = "", Width = 40, CellTemplate = new DataTemplate { VisualTree = check } });
  foreach (var (title, member, width) in new[] { (Tc("library folder", "Type"), nameof(ChangeRow.Type), 110d), (T("File"), nameof(ChangeRow.Name), 240d), (T("Place"), nameof(ChangeRow.Where), 520d) })
   view.Columns.Add(new GridViewColumn { Header = title, DisplayMemberBinding = new Binding(member), Width = width });
  _changes.View = view;
  changesPanel.Children.Add(_changes);
  Grid.SetRow(changesPanel, 2); layout.Children.Add(changesPanel);

  var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
  var buttons = new StackPanel { Orientation = Orientation.Horizontal };
  var close = new Button { Content = T("Close"), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
  close.Click += (_, _) => Close();
  _applyButton.Style = (Style)Application.Current.Resources["Primary"];
  _applyButton.Click += async (_, _) => await ApplyAsync();
  buttons.Children.Add(_applyButton); buttons.Children.Add(close);
  DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons); footer.Children.Add(_ignoreDeclined);
  Grid.SetRow(footer, 3); layout.Children.Add(footer);
  Content = layout;

  Closed += (_, _) => _checking?.Cancel();
  ShowFolders();
  if (plan != null) ShowPlan(plan); else ShowPlan(null);
 }

 private void ShowFolders()
 {
  _folders.Children.Clear();
  var folders = _repository.GetLibraryFolders();
  if (folders.Count == 0) _folders.Children.Add(SettingsUi.Hint(T("No media folders yet. Add the folders that hold your videos, images and music.")));
  foreach (var folder in folders) _folders.Children.Add(FolderCard(folder));
  var suggestions = _repository.SuggestLibraryFoldersFromOriginal();
  _adopt.Content = T("Import content folders from EazyFlixPix 4 ({0})", suggestions.Count);
  _adopt.Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
  _check.IsEnabled = folders.Count > 0;
 }

 private Border FolderCard(LibraryFolder folder)
 {
  var watch = SettingsUi.Switch(); watch.IsChecked = folder.Watch; AutomationProperties.SetName(watch, T("Monitor"));
  var subfolders = SettingsUi.Switch(); subfolders.IsChecked = folder.IncludeSubfolders; AutomationProperties.SetName(subfolders, T("Subfolders"));
  var kind = new ComboBox { Width = 120, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = T("Media type") };
  foreach (var label in new[] { T("All items"), T("Images"), "Videos", "Audio" }) kind.Items.Add(label);
  kind.SelectedIndex = folder.Kind is { } value ? (int)value + 1 : 0;
  var category = new TextBox { Text = folder.Category ?? "", Width = 150, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = T("Category for newly imported files") };
  AutomationProperties.SetName(category, T("Category"));
  var remove = new Button { Content = "", Style = (Style)Application.Current.Resources["IconButton"], ToolTip = T("Remove folder (items stay in the catalog)"), Margin = new Thickness(8, 0, 0, 0) };
  AutomationProperties.SetName(remove, T("Remove folder"));
  void Save(LibraryFolder changed) => Update(folders => folders.Select(f => f.Id == folder.Id ? changed : f).ToList());
  LibraryFolder Current() => _repository.GetLibraryFolders().FirstOrDefault(f => f.Id == folder.Id) ?? folder;
  watch.Click += (_, _) => Save(Current() with { Watch = watch.IsChecked == true });
  subfolders.Click += (_, _) => Save(Current() with { IncludeSubfolders = subfolders.IsChecked == true });
  kind.SelectionChanged += (_, _) => Save(Current() with { Kind = kind.SelectedIndex == 0 ? null : (MediaKind)(kind.SelectedIndex - 1) });
  category.LostKeyboardFocus += (_, _) => { if ((Current().Category ?? "") != category.Text.Trim()) Save(Current() with { Category = category.Text }); };
  remove.Click += (_, _) => Update(folders => folders.Where(f => f.Id != folder.Id).ToList());

  var reachable = Directory.Exists(folder.Path);
  var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
  text.Children.Add(new TextBlock { Text = folder.Path, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = folder.Path });
  text.Children.Add(new TextBlock { Text = reachable ? (folder.IgnoredPaths.Count > 0 ? Tn(folder.IgnoredPaths.Count, "Available · 1 file hidden", "Available · {0} files hidden") : T("Available")) : T("Unavailable · missing files are not reported here"),
   FontSize = 12, Foreground = reachable ? SettingsUi.Muted : SettingsUi.Error });
  var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
  controls.Children.Add(Labeled(T("Monitor"), watch)); controls.Children.Add(Labeled(T("Subfolders"), subfolders));
  controls.Children.Add(kind); controls.Children.Add(category); controls.Children.Add(remove);
  var row = new DockPanel { Margin = new Thickness(14, 10, 10, 10) };
  DockPanel.SetDock(controls, Dock.Right); row.Children.Add(controls); row.Children.Add(text);
  return new Border { Background = SettingsUi.CardBackground, BorderBrush = SettingsUi.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 8), Child = row };
 }

 private static StackPanel Labeled(string label, Control control)
 {
  var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
  panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = SettingsUi.Muted, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
  panel.Children.Add(control);
  return panel;
 }

 private void Update(Func<List<LibraryFolder>, List<LibraryFolder>> change)
 {
  try { _repository.SaveLibraryFolders(change(_repository.GetLibraryFolders().ToList())); }
  catch (ArgumentException error) { Message.Show(this, error.Message, T("Media folders"), MessageBoxButton.OK, MessageBoxImage.Warning); }
  ShowFolders();
  _foldersChanged();
 }

 private async void AddFolders()
 {
  var picker = new OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title = T("Add media folder"), Multiselect = true };
  if (picker.ShowDialog(this) != true) return;
  await AddFoldersAsync(picker.FolderNames);
 }

 internal async Task AddFoldersAsync(IEnumerable<string> paths)
 {
  var known = _repository.GetLibraryFolders().Select(f => Path.GetFullPath(f.Path).TrimEnd(Path.DirectorySeparatorChar)).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var added = paths.Select(Path.GetFullPath).Where(p => !known.Contains(p.TrimEnd(Path.DirectorySeparatorChar))).Select(p => new LibraryFolder { Path = p }).ToList();
  if (added.Count == 0) return;
  Update(folders => [.. folders, .. added]);
  await CheckAsync();
 }

 private async void AdoptFromOriginal()
 {
  var suggestions = _repository.SuggestLibraryFoldersFromOriginal();
  if (suggestions.Count == 0) return;
  var watched = suggestions.Count(f => f.Watch);
  if (Message.Show(this, Tn(suggestions.Count, "Import 1 content folder from EazyFlixPix 4?\n\n{1}{2}\n\n", "Import {0} content folders from EazyFlixPix 4?\n\n{1}{2}\n\n", string.Join("\n", suggestions.Select(f => f.Path).Take(12)), (suggestions.Count > 12 ? "\n…" : "")) +
   Tn(watched, "EazyFlixPix 4 updated 1 of them automatically; it is monitored. FlixyPix checks the others only on request.",
    "EazyFlixPix 4 updated {0} of them automatically; they are monitored. FlixyPix checks the others only on request."),
    T("Import content folders"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
  Update(folders => [.. folders, .. suggestions]);
  await CheckAsync();
 }

 internal async Task CheckAsync()
 {
  _checking?.Cancel();
  var cancellation = _checking = new CancellationTokenSource();
  _check.IsEnabled = _applyButton.IsEnabled = false;
  _summary.Text = T("Checking folders …");
  try { ShowPlan(await Task.Run(() => _repository.PlanFolderSync(cancellationToken: cancellation.Token), cancellation.Token)); }
  catch (OperationCanceledException) { }
  finally { if (_checking == cancellation) _check.IsEnabled = _repository.GetLibraryFolders().Count > 0; }
 }

 private void ShowPlan(FolderSyncPlan? plan)
 {
  _plan = plan;
  _changes.ItemsSource = plan?.Changes.Select(c => new ChangeRow(c)).ToList();
  _applyButton.IsEnabled = plan?.Actionable.Count > 0;
  _summary.Text = plan == null ? T("Not checked yet.") : Describe(plan);
 }

 internal static string Describe(FolderSyncPlan plan)
 {
  var parts = new List<string>();
  void Part(FolderChangeKind kind, Func<int, string> text) { var count = plan.Count(kind); if (count > 0) parts.Add(text(count)); }
  Part(FolderChangeKind.New, n => Tn(n, "1 new file", "{0:N0} new files")); Part(FolderChangeKind.Moved, n => Tn(n, "1 moved file", "{0:N0} moved files"));
  Part(FolderChangeKind.Renamed, n => Tn(n, "1 renamed file", "{0:N0} renamed files")); Part(FolderChangeKind.Missing, n => Tn(n, "1 missing file", "{0:N0} missing files"));
  var text = parts.Count == 0 ? T("Everything is up to date.") : string.Join(" · ", parts) + ".";
  if (plan.Count(FolderChangeKind.Missing) > 0) text += T(" Missing files stay in the catalog with all their details and are marked as “Missing”.");
  if (plan.Unreachable.Count > 0) text += T(" Unavailable: {0}.", string.Join(", ", plan.Unreachable.Select(f => f.Path)));
  if (plan.Errors.Count > 0) text += Tn(plan.Errors.Count, " 1 folder could not be read.", " {0} folders could not be read.");
  return text;
 }

 internal async Task ApplyAsync()
 {
  if (_plan is not { } plan) return;
  var chosen = Rows.Where(r => r.CanChoose && r.Chosen).Select(r => r.Change).ToList();
  _applyButton.IsEnabled = _check.IsEnabled = false;
  try
  {
   var result = await _apply(plan, chosen, _ignoreDeclined.IsChecked == true);
   ShowFolders();
   await CheckAsync();
   _summary.Text = Result(result) + " " + _summary.Text;
  }
  catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
  {
   Message.Show(this, error.Message, T("Media folders"), MessageBoxButton.OK, MessageBoxImage.Warning);
   _check.IsEnabled = true;
  }
 }

 internal static string Result(FolderSyncResult result)
 {
  var text = T("{0:N0} imported, {1:N0} reassigned", result.Imported, result.Relinked);
  if (result.Ignored > 0) text += T(", {0:N0} hidden", result.Ignored);
  if (result.Skipped.Count > 0) text += T(", {0:N0} skipped because they changed since the check", result.Skipped.Count);
  return text + ".";
 }
}
