using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

/// <summary>Takes over an installation of the original in three steps: choose the installation and where its catalog goes,
/// choose data and settings groups, read the report. The installation is only read; nothing in it changes.</summary>
internal sealed class TakeoverAssistantWindow : Window
{
 internal delegate Task<NewLibraryMigration> CreateLibrary(LegacyInstallation installation, string target, IProgress<string> progress, CancellationToken token);
 private sealed record Destination(CatalogRepository Repository, string Batch, string ThumbnailDirectory, NewLibraryMigration? Created);

 private static readonly string Original = CatalogRepository.LegacyProductName;
 private readonly LegacyInstallationSearch _search;
 private readonly CatalogRepository _current;
 private readonly string _currentThumbnails;
 private readonly CreateLibrary _createLibrary;
 private readonly ContentControl _page = new();
 private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal };
 private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = SettingsUi.Muted };
 private readonly ProgressBar _progress = new() { Width = 140, Height = 6, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
 private readonly TextBox _parent = new() { MinWidth = 380 };
 private readonly TextBox _name = new() { MinWidth = 260 };
 private readonly RadioButton _toNew = new() { Content = T("Create a new, separate library from the catalog"), GroupName = "target", IsChecked = true };
 private readonly RadioButton _toCurrent = new() { Content = T("Import into the open library (its catalog was already imported)"), GroupName = "target" };
 private readonly ComboBox _batches = new() { MinWidth = 380, Margin = new Thickness(26, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
 private readonly CheckBox _replace = new() { Content = T("Replace existing FlixyPix thumbnails with the old ones"), Margin = new Thickness(26, 6, 0, 0) };
 private CancellationTokenSource? _running;
 private Destination? _destination;

 internal IReadOnlyList<LegacyInstallation> Installations { get; private set; } = [];
 internal LegacyInstallation? Installation { get; private set; }
 internal LegacyTakeoverPlan? Plan { get; private set; }
 internal LegacyTakeoverReport? Report { get; private set; }
 internal Dictionary<LegacyDataGroup, CheckBox> DataChoices { get; } = [];
 internal Dictionary<LegacySettingGroup, CheckBox> SettingChoices { get; } = [];
 /// <summary>Offered when the original used another language FlixyPix speaks; the main window saves it for this computer.</summary>
 internal CheckBox? LanguageChoice { get; private set; }
 internal string? ChosenLanguage => Report != null && LanguageChoice is { IsChecked: true } ? Installation?.Language : null;
 /// <summary>The new library, also when the user stopped after the catalog step: it exists and should be offered.</summary>
 internal NewLibraryMigration? CreatedLibrary => _destination?.Created;
 internal bool ChangedCurrentLibrary => Report != null && _destination?.Created == null;
 internal string Step { get; private set; } = "";

 internal TakeoverAssistantWindow(Window owner, LegacyInstallationSearch search, CatalogRepository current, string currentThumbnails, string libraryParent, CreateLibrary createLibrary)
 {
  Owner = owner; Title = T("Import from {0} · FlixyPix", Original); Width = 940; Height = 760; MinWidth = 720; MinHeight = 560; FontSize = 14;
  WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Theme.Brush("WindowBackground");
  _search = search; _current = current; _currentThumbnails = currentThumbnails; _createLibrary = createLibrary;
  _parent.Text = libraryParent; _name.Text = T("Import-{0:yyyyMMdd-HHmm}", DateTime.Now);
  var layout = new DockPanel { Margin = new Thickness(22) };
  var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
  DockPanel.SetDock(_buttons, Dock.Right); footer.Children.Add(_buttons); footer.Children.Add(_progress); footer.Children.Add(_status);
  DockPanel.SetDock(_progress, Dock.Left);
  DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
  layout.Children.Add(new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
  Content = layout;
  Closing += (_, e) => { if (_running != null) { _running.Cancel(); e.Cancel = true; _status.Text = T("Cancelling …"); } };
  Closed += (_, _) => { if (_destination?.Created != null) _destination.Repository.Dispose(); };
  var batches = _current.GetMigrationReports().Reverse().ToList();
  _batches.ItemsSource = batches.Select((r, i) => new ComboBoxItem { Content = T("{0} · {1:N0} rows · source {2}", (i == 0 ? T("Last import") : T("Import {0}", batches.Count - i)), r.Rows, Short(r.SourceId)), Tag = r.Batch }).ToList();
  _batches.SelectedIndex = 0;
  _toCurrent.IsEnabled = batches.Count > 0; _batches.Visibility = batches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
  foreach (var control in new Control[] { _toNew, _toCurrent, _replace }) control.Foreground = Theme.Brush("Text");
  Installations = LegacyInstallations.Find(_search);
  Installation = Installations.FirstOrDefault(i => i.HasCatalog) ?? Installations.FirstOrDefault();
  ShowStart();
 }

 private static string Short(string value) => value.Length > 12 ? value[..12] + "…" : value;
 private static TextBlock Heading(string text) => new() { Text = text, FontSize = 25, FontWeight = FontWeights.SemiBold };
 private static TextBlock Line(string text, bool muted = false) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = muted ? SettingsUi.Muted : Theme.Brush("Text"), Margin = new Thickness(0, 2, 0, 0) };
 private static Border Row(UIElement content) => new() { Padding = new Thickness(16, 10, 16, 10), Child = content };
 private static string GroupName(LegacyDataGroup group) => group switch
 {
  LegacyDataGroup.VideoThumbnails => T("Video thumbnails"), LegacyDataGroup.BookmarkImages => T("Bookmark images"),
  LegacyDataGroup.ClipPreviews => T("Clip previews"), _ => T("Tag images"),
 };

 private Button Primary(string text, Action click)
 {
  var button = new Button { Content = text, Style = (Style)Application.Current.Resources["Primary"], Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
  button.Click += (_, _) => click(); return button;
 }
 private Button Secondary(string text, Action click)
 {
  var button = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0) };
  button.Click += (_, _) => click(); return button;
 }
 private void SetButtons(params Button[] buttons) { _buttons.Children.Clear(); foreach (var button in buttons) _buttons.Children.Add(button); }
 /// <summary>The fixed controls move from page to page when a page is rebuilt.</summary>
 private static T Detach<T>(T element) where T : FrameworkElement
 {
  switch (element.Parent) { case Panel panel: panel.Children.Remove(element); break; case Decorator decorator: decorator.Child = null; break; }
  return element;
 }

 // Step 1: which installation, and where its catalog goes.
 internal void ShowStart()
 {
  Step = "start";
  var panel = new StackPanel();
  panel.Children.Add(Heading(T("Import from {0}", Original)));
  panel.Children.Add(SettingsUi.Hint(T("FlixyPix only reads the old installation. Catalog, thumbnails and settings are copied; nothing changes in the original. Further catalogs of the installation are not offered.")));
  panel.Children.Add(new Border { Height = 14 });
  if (Installations.Count == 0) panel.Children.Add(SettingsUi.Card(T("No installation found"), T("Please choose the program folder or the database file.")));
  foreach (var installation in Installations.Prepend(Installation).OfType<LegacyInstallation>().DistinctBy(i => i.CatalogPath, StringComparer.OrdinalIgnoreCase).OrderBy(i => Installations.Contains(i) ? 0 : 1))
   panel.Children.Add(InstallationCard(installation));
  var folder = SettingsUi.SmallButton(T("Choose another program folder …")); folder.Margin = new Thickness(0, 0, 8, 0);
  folder.Click += (_, _) => PickFolder();
  var database = SettingsUi.SmallButton(T("Choose a single database file …")); database.Margin = new Thickness(0);
  database.Click += (_, _) => PickDatabase();
  panel.Children.Add(new WrapPanel { Children = { folder, database }, Margin = new Thickness(0, 0, 0, 14) });

  var parentRow = new DockPanel { Margin = new Thickness(26, 8, 0, 0) };
  var browse = SettingsUi.SmallButton("…", T("Choose folder")); browse.Click += (_, _) => { var dialog = new OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title = T("Folder for the new library"), InitialDirectory = _parent.Text }; if (dialog.ShowDialog(this) == true) _parent.Text = dialog.FolderName; };
  DockPanel.SetDock(browse, Dock.Right); parentRow.Children.Add(browse); parentRow.Children.Add(Detach(_parent));
  AutomationProperties.SetName(_parent, T("Folder of the new library")); AutomationProperties.SetName(_name, T("Name of the new library"));
  var nameRow = new DockPanel { Margin = new Thickness(26, 6, 0, 0) };
  var nameLabel = new TextBlock { Text = T("Name"), Width = 60, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(nameLabel, Dock.Left);
  nameRow.Children.Add(nameLabel); nameRow.Children.Add(Detach(_name));
  var target = new StackPanel { Margin = new Thickness(16, 4, 16, 14) };
  foreach (var element in new FrameworkElement[] { Detach(_toNew), parentRow, nameRow, new Border { Height = 12 }, Detach(_toCurrent), Detach(_batches) }) target.Children.Add(element);
  if (!_toCurrent.IsEnabled) target.Children.Add(new TextBlock { Text = T("The open library does not contain any imported legacy data yet."), Margin = new Thickness(26, 2, 0, 0), FontSize = 12, Foreground = SettingsUi.Muted });
  panel.Children.Add(SettingsUi.Card(T("Where to?"), T("A new library leaves the open one unchanged; it can be chosen afterwards."), target));
  _page.Content = panel;
  SetButtons(Primary(T("Next"), async () => await ContinueAsync()), Secondary(T("Cancel"), Close));
  _status.Text = Installation == null ? T("Please choose an installation.") : "";
 }

 private Border InstallationCard(LegacyInstallation installation)
 {
  var choose = new RadioButton { GroupName = "installation", IsChecked = string.Equals(installation.CatalogPath, Installation?.CatalogPath, StringComparison.OrdinalIgnoreCase), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 10, 0) };
  AutomationProperties.SetName(choose, installation.ProgramFolder);
  choose.Checked += (_, _) => Installation = installation;
  var text = new StackPanel();
  text.Children.Add(new TextBlock { Text = installation.ProgramFolder, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = installation.ProgramFolder });
  text.Children.Add(Line((installation.Version is { } v ? "Version " + v + " · " : "") + (installation.HasCatalog
   ? T("Catalog {0} · {1}", Path.GetFileName(installation.CatalogPath), MediaCard.SizeText(installation.CatalogBytes)) : T("No catalog at {0}", installation.CatalogPath)), true));
  foreach (var folder in installation.Folders)
   text.Children.Add(Line($"{GroupName(folder.Group)}: " + (folder.Files == 0 ? T("none") : Tn(folder.Files, "1 file · {1}", "{0:N0} files · {1}", MediaCard.SizeText(folder.Bytes))), true));
  text.Children.Add(Line(installation.UserConfigPath != null ? T("User settings found") : T("No user settings; program defaults"), true));
  var row = new DockPanel(); DockPanel.SetDock(choose, Dock.Left); row.Children.Add(choose); row.Children.Add(text);
  var card = new Border { Background = SettingsUi.CardBackground, BorderBrush = SettingsUi.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(14, 12, 14, 12), Child = row };
  card.MouseLeftButtonUp += (_, _) => choose.IsChecked = true;
  return card;
 }

 private void PickFolder()
 {
  var dialog = new OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose the program folder of the old installation (not changed)") };
  if (dialog.ShowDialog(this) != true) return;
  if (LegacyInstallations.Inspect(dialog.FolderName, _search) is not { } found) { _status.Text = T("There is no {0} in this folder.", LegacyInstallations.ProgramFile); return; }
  Installation = found; ShowStart();
 }

 private void PickDatabase()
 {
  var dialog = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose database (not changed)"), Filter = T("Catalog (*.sdf)|*.sdf|All files|*.*"), FileName = "mmvdb.sdf" };
  if (dialog.ShowDialog(this) != true) return;
  Installation = LegacyInstallations.FromCatalog(dialog.FileName); ShowStart();
 }

 internal void ChooseCurrentLibrary() => _toCurrent.IsChecked = true;

 internal async Task ContinueAsync()
 {
  if (Installation is not { } installation) { _status.Text = T("Please choose an installation."); return; }
  if (_toCurrent.IsChecked == true)
  {
   _destination = new(_current, (string)((ComboBoxItem)_batches.SelectedItem).Tag, _currentThumbnails, null);
   await PlanAsync(false); return;
  }
  if (!installation.HasCatalog) { _status.Text = T("This installation has no catalog. Please choose the open library as the target."); return; }
  var name = _name.Text.Trim();
  if (!Path.IsPathFullyQualified(_parent.Text) || name.Length == 0 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
  { _status.Text = T("Please enter a full folder path and a valid name."); return; }
  var target = Path.Combine(_parent.Text, name);
  if (Directory.Exists(target) || File.Exists(target)) { _status.Text = T("That folder already exists. Please choose a new name."); return; }
  var created = await RunAsync(T("Importing the catalog …"), token => _createLibrary(installation, target, new Progress<string>(m => _status.Text = m), token));
  if (created == null) return;
  if (created.Result.Migration is not { } migration) { _status.Text = T("The catalog was not imported."); return; }
  _destination = new(new CatalogRepository(Path.Combine(created.DirectoryPath, "catalog.db")), migration.Batch, Path.Combine(created.DirectoryPath, "thumbnails"), created);
  await PlanAsync(false);
 }

 private async Task<TResult?> RunAsync<TResult>(string message, Func<CancellationToken, Task<TResult>> work) where TResult : class
 {
  _running = new CancellationTokenSource(); _progress.Visibility = Visibility.Visible; _status.Text = message;
  var enabled = _buttons.Children.OfType<UIElement>().ToDictionary(c => c, c => c.IsEnabled);
  foreach (var child in enabled.Keys) child.IsEnabled = false;
  _page.IsEnabled = false;
  var stop = Secondary(T("Cancel"), () => { _running?.Cancel(); _status.Text = T("Cancelling …"); }); stop.IsEnabled = true; _buttons.Children.Add(stop);
  try { return await work(_running.Token); }
  catch (OperationCanceledException) { _status.Text = T("Cancelled. Nothing was imported."); return null; }
  catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
  { _status.Text = ""; Dialogs.Text(this, T("Import not possible"), error.Message + T("\n\nNothing was imported.")); return null; }
  finally
  {
   _running.Dispose(); _running = null; _progress.Visibility = Visibility.Collapsed;
   _buttons.Children.Remove(stop); foreach (var (child, wasEnabled) in enabled) child.IsEnabled = wasEnabled;
   _page.IsEnabled = true;
  }
 }

 private async Task PlanAsync(bool replace)
 {
  var (destination, installation) = (_destination!, Installation!);
  var plan = await RunAsync(T("Assigning files …"), token => Task.Run(() => destination.Repository.PlanLegacyTakeover(installation, destination.Batch, replace, token), token));
  if (plan == null) return;
  Plan = plan; ShowChoice();
 }

 // Step 2: data and settings groups.
 internal void ShowChoice()
 {
  Step = "choice";
  var plan = Plan!; var panel = new StackPanel();
  panel.Children.Add(Heading(T("What should be imported?")));
  panel.Children.Add(SettingsUi.Hint(_destination!.Created != null
   ? T("The catalog is in the new library. Next come the images {0} already created, and the program settings.", Original)
   : T("The target is the open library. Things already imported are recognized and not copied twice.")));
  panel.Children.Add(new Border { Height = 14 });

  DataChoices.Clear(); var dataRows = new List<UIElement>();
  foreach (var group in plan.Groups.Where(g => g.Files > 0 || g.Missing > 0))
  {
   var box = new CheckBox { Content = T("{0} · {1:N0} new", GroupName(group.Group), group.Adopt), IsChecked = group.Adopt > 0, IsEnabled = group.Adopt > 0, FontWeight = FontWeights.SemiBold };
   DataChoices[group.Group] = box;
   var details = new StackPanel(); details.Children.Add(box);
   var facts = new List<string> { Tn(group.Files, "1 file · {1}", "{0:N0} files · {1}", MediaCard.SizeText(group.Bytes)) };
   if (group.AlreadyAdopted > 0) facts.Add(T("{0:N0} already imported", group.AlreadyAdopted));
   if (group.Kept > 0) facts.Add(T("{0:N0} kept because FlixyPix already has its own image", group.Kept));
   if (group.Orphans > 0) facts.Add(group.Group == LegacyDataGroup.TagImages ? T("{0:N0} without an unambiguous tag", group.Orphans) : Tn(group.Orphans, "1 belongs to a deleted entry (orphan)", "{0:N0} belong to deleted entries (orphans)"));
   if (group.Missing > 0) facts.Add(Tn(group.Missing, "1 entry without an image", "{0:N0} entries without an image"));
   details.Children.Add(new TextBlock { Text = string.Join(" · ", facts), Margin = new Thickness(26, 2, 0, 0), FontSize = 12, Foreground = SettingsUi.Muted, TextWrapping = TextWrapping.Wrap });
   if (group.Group == LegacyDataGroup.VideoThumbnails && (group.Kept > 0 || plan.ReplaceThumbnails))
   {
    _replace.IsChecked = plan.ReplaceThumbnails; _replace.Click -= ReplaceChanged; _replace.Click += ReplaceChanged;
    details.Children.Add(Detach(_replace));
   }
   dataRows.Add(Row(details));
  }
  panel.Children.Add(SettingsUi.Card(T("Data"), T("Copied into the library; the old files stay where they are. FlixyPix can later recreate imported thumbnails in its own style via “Adjust thumbnails”."), dataRows.ToArray()));

  SettingChoices.Clear(); var settingRows = new List<UIElement>();
  foreach (var group in plan.Settings.Groups)
  {
   var differences = plan.Settings.Differences(group); var conflicts = plan.Settings.Conflicts(group);
   var box = new CheckBox { Content = LegacySettingsPlan.Name(group) + (differences == 0 ? T(" · already matches") : Tn(differences, " · 1 value changes", " · {0:N0} values change")),
    IsChecked = differences > 0 && conflicts == 0, IsEnabled = differences > 0, FontWeight = FontWeights.SemiBold };
   SettingChoices[group] = box;
   var details = new StackPanel(); details.Children.Add(box);
   if (conflicts > 0) details.Children.Add(new TextBlock { Text = Tn(conflicts,
    "⚠ You already changed 1 value in FlixyPix yourself; the import would overwrite it.",
    "⚠ You already changed {0:N0} values in FlixyPix yourself; the import would overwrite them."), Margin = new Thickness(26, 2, 0, 0),
    FontSize = 12, Foreground = SettingsUi.Error, TextWrapping = TextWrapping.Wrap });
   var changes = plan.Settings.Changes.Where(c => c.Group == group && c.Differs).ToList();
   if (changes.Count > 0) details.Children.Add(new Expander { Header = T("View values"), Margin = new Thickness(22, 4, 0, 0), Content = ChangeTable(changes), Foreground = Theme.Brush("Text") });
   settingRows.Add(Row(details));
  }
  // The original's language belongs to this computer, not to the catalog; it applies after a restart.
  LanguageChoice = null;
  if (Installation?.Language is { } language && language != Texts.Current.Language && Texts.Find(language) is { } spoken)
  {
   LanguageChoice = new CheckBox { Content = T("Interface language: {0}", spoken.NativeName), IsChecked = true, FontWeight = FontWeights.SemiBold };
   var details = new StackPanel(); details.Children.Add(LanguageChoice);
   details.Children.Add(new TextBlock { Text = T("This is how EazyFlixPix 4 was set up. Applies on this computer after a restart."), Margin = new Thickness(26, 2, 0, 0), FontSize = 12, Foreground = SettingsUi.Muted, TextWrapping = TextWrapping.Wrap });
   settingRows.Add(Row(details));
  }
  panel.Children.Add(settingRows.Count == 0
   ? SettingsUi.Card(T("Settings"), T("The catalog contains no program settings that FlixyPix can use."))
   : SettingsUi.Card(T("Settings"), T("Settings are always applied as a complete group. Groups containing values you changed yourself are not preselected."), settingRows.ToArray()));
  if (plan.Problems.Count > 0) panel.Children.Add(SettingsUi.Card(T("Notes"), string.Join("\n", plan.Problems.Take(10))));
  _page.Content = panel;
  var back = Secondary(T("Back"), ShowStart); back.IsEnabled = _destination.Created == null;
  SetButtons(Primary(T("Apply"), async () => await ApplyAsync()), back, Secondary(T("Cancel"), Close));
  _status.Text = "";
 }

 internal void ExpandValues()
 {
  static IEnumerable<Expander> All(DependencyObject parent) => LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>().SelectMany(c => c is Expander e ? Enumerable.Repeat(e, 1) : All(c));
  foreach (var expander in All(this)) expander.IsExpanded = true;
 }

 private async void ReplaceChanged(object sender, RoutedEventArgs e) => await PlanAsync(_replace.IsChecked == true);

 private static Grid ChangeTable(IReadOnlyList<LegacySettingChange> changes)
 {
  var grid = new Grid { Margin = new Thickness(0, 6, 0, 4) };
  foreach (var width in new[] { 2.2, 1.6, 1.4, 1.4 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
  void Cell(int row, int column, string text, Brush? brush = null, bool bold = false)
  {
   var cell = new TextBlock { Text = text, Margin = new Thickness(0, 2, 10, 2), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text, FontSize = 12.5, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = brush ?? Theme.Brush("Text") };
   Grid.SetRow(cell, row); Grid.SetColumn(cell, column); grid.Children.Add(cell);
  }
  grid.RowDefinitions.Add(new RowDefinition());
  string[] headers = [T("Setting"), T("In the original"), T("New in FlixyPix"), T("So far in FlixyPix")];
  for (var c = 0; c < headers.Length; c++) Cell(0, c, headers[c], SettingsUi.Muted, true);
  for (var i = 0; i < changes.Count; i++)
  {
   grid.RowDefinitions.Add(new RowDefinition());
   var change = changes[i];
   Cell(i + 1, 0, change.Label); Cell(i + 1, 1, change.Original.Length == 0 ? "(leer)" : change.Original, SettingsUi.Muted); Cell(i + 1, 2, change.Proposed);
   Cell(i + 1, 3, change.Conflict ? "⚠ " + change.Current : change.Current, change.Conflict ? SettingsUi.Error : null);
  }
  return grid;
 }

 internal async Task ApplyAsync()
 {
  var (destination, plan) = (_destination!, Plan!);
  var choice = new LegacyTakeoverChoice(DataChoices.Where(p => p.Value.IsChecked == true && p.Value.IsEnabled).Select(p => p.Key).ToList(),
   SettingChoices.Where(p => p.Value.IsChecked == true && p.Value.IsEnabled).Select(p => p.Key).ToList());
  if (choice.Data.Count == 0 && choice.Settings.Count == 0 && destination.Created == null && LanguageChoice is not { IsChecked: true }) { _status.Text = T("Please choose at least one group."); return; }
  var progress = new Progress<string>(m => _status.Text = m);
  var report = await RunAsync(T("Import running …"), token => Task.Run(() => destination.Repository.ApplyLegacyTakeover(plan, choice, destination.ThumbnailDirectory, progress, token), token));
  if (report == null) return;
  Report = report; ShowReport();
 }

 // Step 3: what happened.
 internal void ShowReport()
 {
  Step = "report";
  var report = Report!; var panel = new StackPanel();
  panel.Children.Add(Heading(T("Import completed")));
  panel.Children.Add(SettingsUi.Hint(T("Took {0:0.#} s. The installation of {1} is unchanged.", report.Duration.TotalSeconds, Original)));
  panel.Children.Add(new Border { Height = 14 });
  var rows = report.Data.Select(r =>
  {
   var facts = new List<string> { T("{0:N0} imported", r.Adopted) };
   if (r.AlreadyAdopted > 0) facts.Add(Tn(r.AlreadyAdopted, "1 was already there", "{0:N0} were already there"));
   if (r.Kept > 0) facts.Add(T("{0:N0} kept", r.Kept));
   if (r.Orphans > 0) facts.Add(T("{0:N0} without an assignment", r.Orphans));
   if (r.Missing > 0) facts.Add(Tn(r.Missing, "1 entry without an image", "{0:N0} entries without an image"));
   if (r.Invalid > 0) facts.Add(T("{0:N0} unreadable", r.Invalid));
   var line = new StackPanel();
   line.Children.Add(new TextBlock { Text = GroupName(r.Group), FontWeight = FontWeights.SemiBold });
   line.Children.Add(Line(string.Join(" · ", facts), true));
   return (UIElement)Row(line);
  }).ToList();
  if (report.Settings.Count > 0)
  {
   var line = new StackPanel();
   line.Children.Add(new TextBlock { Text = T("Settings"), FontWeight = FontWeights.SemiBold });
   line.Children.Add(Line(string.Join(", ", report.Settings.Select(LegacySettingsPlan.Name)) + Tn(report.SettingsChanged, " · 1 value changed", " · {0:N0} values changed"), true));
   rows.Add(Row(line));
  }
  panel.Children.Add(rows.Count == 0 ? SettingsUi.Card(T("Nothing chosen"), T("No images or settings were imported.")) : SettingsUi.Card(T("Result"), null, rows.ToArray()));
  if (report.Problems.Count > 0)
   panel.Children.Add(SettingsUi.Card(T("Notes"), string.Join("\n", report.Problems.Take(20)) + (report.Problems.Count > 20 ? T("\n… and {0} more", report.Problems.Count - 20) : "")));
  _page.Content = panel;
  SetButtons(Primary(_destination!.Created != null ? T("Continue to the new library") : T("Close"), Close));
  _status.Text = "";
 }
}
