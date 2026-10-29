using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

/// <summary>A tool of the database management: a card with title, explanation and a button. <paramref name="Run"/> runs after
/// the window closed, so longer work shows its progress in the main window; <paramref name="Here"/> instead runs in the window
/// itself, which stays open and busy meanwhile.</summary>
internal sealed record DatabaseTool(string Title, string Description, string Button, Action Run, Func<DatabaseWindow, Task>? Here = null)
{
 /// <summary>Heading the card is shown under; tools of one group follow each other.</summary>
 internal string? Group { get; init; }
}

/// <summary>One area of the database management. <paramref name="Name"/> is its German name (also used in code and tests);
/// <paramref name="Content"/> builds extra content above the tools when the area is first shown.</summary>
internal sealed record DatabaseSection(string Name, string Glyph, string Description, IReadOnlyList<DatabaseTool> Tools, Func<DatabaseWindow, FrameworkElement>? Content = null);

/// <summary>The database management (original: database manager): everything that concerns the library as a database in
/// one place, with the libraries of this computer as one area. One library is open at a time; opening another one rebuilds
/// the main window. On start, when there are several libraries and the user wants to choose, only the list is shown.</summary>
internal sealed class DatabaseWindow : Window
{
 /// <summary>German name of the area with the list of libraries.</summary>
 internal static readonly string LibrariesSection = TKey("Libraries");

 /// <summary>Folder of new libraries unless the user adds a folder of their own.</summary>
 internal static string DefaultParent => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlixyPix", "Libraries");

 private readonly string? _openDirectory;
 private readonly CatalogRepository? _openCatalog;
 private readonly Action<IReadOnlyList<KnownLibrary>, bool> _save;
 private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
 private readonly CheckBox _askAtStart = new() { Content = T("Ask at startup when there are several libraries"), Margin = new Thickness(0, 14, 0, 0) };
 private readonly Button _open, _openNew, _rename, _move, _compact, _forget, _delete;
 private readonly WindowFrame _frame;
 private readonly CancellationTokenSource _closing = new();
 // Counting opens catalogs read-only for a moment; moving or deleting a folder waits until it stopped.
 private CancellationTokenSource _inspection = new();
 private readonly Dictionary<string, TextBlock> _details = new(StringComparer.OrdinalIgnoreCase);
 private IReadOnlyList<KnownLibrary> _libraries;
 /// <summary>Where "Neue Bibliothek" creates its folder.</summary>
 internal string NewLibraryParent { get; init; } = DefaultParent;
 private bool _busy;

 /// <summary>The folder to open after the window closed; null keeps the open library.</summary>
 internal string? Chosen { get; private set; }
 /// <summary>The tool to run after the window closed.</summary>
 internal DatabaseTool? Pending { get; private set; }
 private readonly ListBox _navigation = new() { Margin = new Thickness(0, 0, 18, 0), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
 private readonly ContentControl _page = new();
 private readonly Dictionary<string, FrameworkElement> _pages = [];
 private readonly Dictionary<string, DatabaseSection> _sections = [];
 private UIElement? _librariesPage;
 private string _librariesHint = "";
 /// <summary>German name of the area shown.</summary>
 internal string? Section => (_navigation.SelectedItem as ListBoxItem)?.Tag as string;
 internal IReadOnlyList<string> SectionNames => _navigation.Items.OfType<ListBoxItem>().Select(item => (string)item.Tag).ToArray();
 internal IReadOnlyList<KnownLibrary> Libraries => _libraries;
 internal Task Inspected { get; private set; } = Task.CompletedTask;
 internal string StatusText => _frame.Status.Text;

 /// <param name="openDirectory">The library open in the main window, if any; it cannot be moved, forgotten or deleted.</param>
 /// <param name="save">Stores list and start choice for this computer at once.</param>
 /// <param name="sections">Areas of the database management; without them the window is the plain list of libraries (start choice).</param>
 /// <param name="section">German name of the area to show first.</param>
 internal DatabaseWindow(Window? owner, IReadOnlyList<KnownLibrary> libraries, bool askAtStart, string? openDirectory, CatalogRepository? openCatalog,
  Action<IReadOnlyList<KnownLibrary>, bool> save, bool chooser = false, IReadOnlyList<DatabaseSection>? sections = null, string? section = null)
 {
  if (owner != null) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; } else WindowStartupLocation = WindowStartupLocation.CenterScreen;
  Title = T("Libraries · FlixyPix"); Width = 760; Height = 620; MinWidth = 560; MinHeight = 420; FontSize = 14; ShowInTaskbar = owner == null;
  if (sections != null && !chooser) { Title = T("Database management · FlixyPix"); Width = 1040; Height = 700; MinWidth = 760; MinHeight = 480; }
  _libraries = libraries; _openDirectory = openDirectory; _openCatalog = openCatalog; _save = save;
  _askAtStart.IsChecked = askAtStart;
  _askAtStart.Click += (_, _) => _save(_libraries, _askAtStart.IsChecked == true);

  var create = SettingsUi.SmallButton(T("＋ New library …")); create.Margin = new Thickness(0, 0, 8, 0); create.Click += (_, _) => Create();
  var add = SettingsUi.SmallButton(T("Add existing folder …")); add.Margin = new Thickness(0, 0, 8, 0); add.Click += (_, _) => AddExisting();
  _open = ActionButton(T("Open"), () => Choose());
  _openNew = ActionButton(T("Open in new window"), () => OpenInNewWindow());
  if (chooser) _openNew.Visibility = Visibility.Collapsed;
  _rename = ActionButton(T("Rename …"), Rename);
  _move = ActionButton(T("Move …"), async () => await MoveAsync());
  _compact = ActionButton(T("Compact"), async () => await CompactAsync());
  _forget = ActionButton(T("Remove from list"), Forget);
  _delete = ActionButton(T("Delete …"), async () => await DeleteAsync());
  var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) }; top.Children.Add(create); top.Children.Add(add);
  var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
  foreach (var button in new[] { _open, _openNew, _rename, _move, _compact, _forget, _delete }) actions.Children.Add(button);
  var body = new DockPanel();
  foreach (var (element, dock) in new (UIElement, Dock)[] { (top, Dock.Top), (_askAtStart, Dock.Bottom), (actions, Dock.Bottom) }) { DockPanel.SetDock(element, dock); body.Children.Add(element); }
  body.Children.Add(_list);
  var librariesHint = T("Each library has its own catalog with thumbnails, collections and settings. One is always open; switching reloads the main window.");
  if (sections != null && !chooser)
  {
   _librariesHint = librariesHint; _librariesPage = body;
   var layout = new Grid();
   layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
   Grid.SetColumn(_page, 1); layout.Children.Add(_navigation); layout.Children.Add(_page);
   foreach (var area in sections) AddSection(area);
   _frame = new WindowFrame(T("Database management"), openDirectory, layout, "", scroll: false);
   _navigation.SelectionChanged += (_, _) => ShowSection();
   _navigation.SelectedItem = _navigation.Items.OfType<ListBoxItem>().FirstOrDefault(item => (string)item.Tag == section) ?? _navigation.Items[0];
  }
  else _frame = new WindowFrame(T("Libraries"), librariesHint, body, "", scroll: false);
  if (chooser) { _frame.Add(T("Open"), () => Choose(), primary: true); _frame.Add(T("Quit"), Close, cancel: true); }
  else _frame.Add(T("Close"), Close, primary: true, cancel: true);
  Content = _frame;
  _list.SelectionChanged += (_, _) => UpdateActions();
  _list.MouseDoubleClick += (_, e) => { if (e.OriginalSource is FrameworkElement { DataContext: KnownLibrary }) Choose(); };
  _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Choose(); } else if (e.Key == Key.F2) { e.Handled = true; Rename(); } };
  Closing += (_, e) => { if (_busy) { e.Cancel = true; return; } _closing.Cancel(); };
  Loaded += (_, _) => { _list.Focus(); (_list.ItemContainerGenerator.ContainerFromIndex(_list.SelectedIndex) as ListBoxItem)?.Focus(); };
  Fill(openDirectory);
 }

 private void AddSection(DatabaseSection section)
 {
  _sections[section.Name] = section;
  var icon = SettingsUi.Icon(section.Glyph, 15); icon.Margin = new Thickness(0, 0, 12, 0); icon.Foreground = SettingsUi.Muted;
  // Long names (Russian, Polish …) wrap instead of being cut off.
  var row = new DockPanel { Margin = new Thickness(8, 5, 6, 5) }; DockPanel.SetDock(icon, Dock.Left);
  row.Children.Add(icon); row.Children.Add(new TextBlock { Text = T(section.Name), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
  var item = new ListBoxItem { Content = row, Tag = section.Name, HorizontalContentAlignment = HorizontalAlignment.Stretch };
  ScrollViewer.SetHorizontalScrollBarVisibility(_navigation, ScrollBarVisibility.Disabled);
  System.Windows.Automation.AutomationProperties.SetName(item, T(section.Name));
  _navigation.Items.Add(item);
 }

 /// <summary>Shows an area by its German name (also for tests).</summary>
 internal void ShowSection(string name) => _navigation.SelectedItem = _navigation.Items.OfType<ListBoxItem>().First(item => (string)item.Tag == name);

 private void ShowSection()
 {
  if (Section is not { } name) return;
  if (!_pages.TryGetValue(name, out var page)) _pages[name] = page = BuildPage(_sections[name]);
  _page.Content = page;
 }

 private FrameworkElement BuildPage(DatabaseSection section)
 {
  var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
  heading.Children.Add(new TextBlock { Text = T(section.Name), FontSize = 22, FontWeight = FontWeights.SemiBold });
  heading.Children.Add(new TextBlock { Text = section.Name == LibrariesSection ? _librariesHint : section.Description, Foreground = SettingsUi.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
  if (section.Name == LibrariesSection && _librariesPage is { } libraries)
  {
   // The list fills the page and scrolls on its own; detach it from an earlier page first.
   var page = new DockPanel(); DockPanel.SetDock(heading, Dock.Top); page.Children.Add(heading);
   if (LogicalTreeHelper.GetParent(libraries) is Panel previous) previous.Children.Remove(libraries);
   page.Children.Add(libraries); return page;
  }
  var content = new StackPanel { Margin = new Thickness(0, 0, 8, 12) };
  content.Children.Add(heading);
  if (section.Content?.Invoke(this) is { } extra) content.Children.Add(extra);
  string? group = null;
  foreach (var tool in section.Tools)
  {
   if (tool.Group is { } title && title != group)
    content.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = SettingsUi.Muted, Margin = new Thickness(2, group == null ? 0 : 10, 0, 8) });
   group = tool.Group;
   content.Children.Add(ToolCard(tool));
  }
  return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
 }

 private Border ToolCard(DatabaseTool tool)
 {
  var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
  text.Children.Add(new TextBlock { Text = tool.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
  text.Children.Add(SettingsUi.Hint(tool.Description));
  var button = SettingsUi.SmallButton(tool.Button); button.Margin = new Thickness(16, 0, 0, 0); button.MinWidth = 120;
  button.Click += async (_, _) => { if (tool.Here is { } here) await RunHereAsync(() => here(this)); else Run(tool); };
  System.Windows.Automation.AutomationProperties.SetName(button, tool.Title);
  var row = new DockPanel { Margin = new Thickness(16, 12, 16, 12) }; DockPanel.SetDock(button, Dock.Right); row.Children.Add(button); row.Children.Add(text);
  return new Border { Background = SettingsUi.CardBackground, BorderBrush = SettingsUi.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 10), Child = row };
 }

 /// <summary>Closes the window and leaves the tool to the main window (also for tests).</summary>
 internal void Run(DatabaseTool tool)
 {
  if (_busy) return;
  Pending = tool; DialogResultIfModal(true); Close();
 }

 /// <summary>Runs work in the window: the window stays open, cannot close and its tools wait until the work is done.</summary>
 internal async Task RunHereAsync(Func<Task> work)
 {
  if (_busy) return;
  SetBusy(true);
  try { await work(); }
  finally { SetBusy(false); }
 }

 internal bool Busy => _busy;

 /// <summary>Shows a line in the status bar of the window.</summary>
 internal void Report(string text) => _frame.Status.Text = text;

 /// <summary>The tools of the area shown (for tests).</summary>
 internal IReadOnlyList<DatabaseTool> ToolsShown => Section is { } name ? _sections[name].Tools : [];

 private static Button ActionButton(string text, System.Action click)
 {
  var button = SettingsUi.SmallButton(text); button.Margin = new Thickness(0, 0, 8, 6); button.Click += (_, _) => click(); return button;
 }

 private KnownLibrary? Selected => _list.SelectedItem is ListBoxItem { DataContext: KnownLibrary library } ? library : null;
 private bool IsOpen(KnownLibrary library) => _openDirectory != null && LibraryCatalogs.SameDirectory(library.Directory, _openDirectory);

 private void Fill(string? select)
 {
  _list.Items.Clear(); _details.Clear();
  foreach (var library in LibraryCatalogs.Ordered(_libraries, _openDirectory))
  {
   var name = new TextBlock { Text = LibraryCatalogs.DisplayName(library), FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
   var heading = new DockPanel();
   if (IsOpen(library) || OpenElsewhere(library))
   {
    var badge = new Border { Background = SettingsUi.Chip, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
     Child = new TextBlock { Text = IsOpen(library) ? T("open") : T("open in another window"), FontSize = 11 } };
    DockPanel.SetDock(badge, Dock.Right); heading.Children.Add(badge);
   }
   heading.Children.Add(name);
   var details = new TextBlock { Text = T("counting …"), FontSize = 12, Foreground = SettingsUi.Muted, TextWrapping = TextWrapping.Wrap };
   var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
   text.Children.Add(heading);
   text.Children.Add(new TextBlock { Text = library.Directory, FontSize = 12, Foreground = SettingsUi.Muted, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = library.Directory });
   text.Children.Add(details);
   var row = new DockPanel { Margin = new Thickness(4, 8, 4, 8) };
   var icon = SettingsUi.Icon("", 26); icon.Foreground = IsOpen(library) ? SettingsUi.Accent : SettingsUi.Muted; DockPanel.SetDock(icon, Dock.Left);
   row.Children.Add(icon); row.Children.Add(text);
   var item = new ListBoxItem { Content = row, DataContext = library };
   System.Windows.Automation.AutomationProperties.SetName(item, LibraryCatalogs.DisplayName(library));
   _list.Items.Add(item);
   _details[library.Directory] = details;
   if (select != null && LibraryCatalogs.SameDirectory(library.Directory, select)) _list.SelectedItem = item;
  }
  if (_list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;
  UpdateActions();
  RestartInspection();
 }

 private void RestartInspection()
 {
  _inspection.Cancel();
  _inspection = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
  Inspected = InspectAllAsync(_inspection.Token);
 }

 private async Task StopInspectionAsync()
 {
  _inspection.Cancel();
  try { await Inspected; } catch (OperationCanceledException) { }
 }

 private async Task InspectAllAsync(CancellationToken token)
 {
  foreach (var library in _libraries.ToArray())
  {
   LibrarySummary summary;
   try { summary = await Task.Run(() => LibraryCatalogs.Inspect(library.Directory, token), token); }
   catch (OperationCanceledException) { return; }
   if (!_details.TryGetValue(library.Directory, out var details)) continue;
   var parts = new List<string>();
   if (summary.Problem != null) { details.Foreground = SettingsUi.Error; parts.Add(summary.Problem); }
   else if (!summary.HasCatalog) parts.Add(T("empty, created when opened"));
   else if (summary.Encrypted) parts.Add(T("encrypted"));
   else parts.Add(Tn(summary.MediaCount ?? 0, "1 item", "{0:N0} items"));
   if (summary.Exists) parts.Add(MediaCard.SizeText(summary.Bytes));
   if (library.LastOpened is { } opened) parts.Add(T("last opened {0}", opened.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)));
   details.Text = string.Join(" · ", parts);
  }
 }

 private void UpdateActions()
 {
  var library = Selected; var open = library != null && IsOpen(library);
  _open.IsEnabled = library != null && !_busy;
  _openNew.IsEnabled = library != null && !IsOpen(library) && !_busy;
  _rename.IsEnabled = library != null && !_busy;
  _compact.IsEnabled = open && _openCatalog != null && !_busy;
  _compact.ToolTip = open ? T("Rebuilds the catalog file without unused space; the data stays the same.") : T("Only the open library can be compacted.");
  foreach (var button in new[] { _move, _forget, _delete })
  {
   button.IsEnabled = library != null && !open && !_busy;
   button.ToolTip = open ? T("Open another library first.") : null;
  }
 }

 private void Store(IReadOnlyList<KnownLibrary> libraries, string? select)
 {
  _libraries = libraries; _save(_libraries, _askAtStart.IsChecked == true); Fill(select);
 }

 /// <summary>Opens the selected library (or <paramref name="directory"/>); the open one just closes the window.</summary>
 internal void Choose(string? directory = null)
 {
  directory ??= Selected?.Directory;
  if (directory == null || _busy) return;
  // Open in another instance: that window comes to the front; this one keeps its library.
  if (_openDirectory != null && !LibraryCatalogs.SameDirectory(directory, _openDirectory) && LibraryInstance.Activate(directory))
  { _frame.Status.Text = T("This library is already open in another window; it was brought to the front."); return; }
  Chosen = _openDirectory != null && LibraryCatalogs.SameDirectory(directory, _openDirectory) ? null : directory;
  DialogResultIfModal(true); Close();
 }

 private bool OpenElsewhere(KnownLibrary library) => !IsOpen(library) && LibraryInstance.IsOpen(library.Directory);

 /// <summary>Starts another instance of FlixyPix for the library (also for tests: <see cref="Launch"/> replaces the start).</summary>
 internal void OpenInNewWindow(string? directory = null)
 {
  directory ??= Selected?.Directory;
  if (directory == null || _busy || _openDirectory != null && LibraryCatalogs.SameDirectory(directory, _openDirectory)) return;
  if (LibraryInstance.Activate(directory)) { _frame.Status.Text = T("This library is already open in another window; it was brought to the front."); return; }
  if (Environment.ProcessPath is not { } program) return;
  var start = new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false };
  start.ArgumentList.Add("--library"); start.ArgumentList.Add(LibraryCatalogs.Normalize(directory));
  Launch(start);
  _frame.Status.Text = T("“{0}” opens in a new window.", LibraryCatalogs.DisplayName(_libraries.FirstOrDefault(l => LibraryCatalogs.SameDirectory(l.Directory, directory)) ?? new KnownLibrary(directory)));
 }

 /// <summary>Starts a process; tests replace it so no second instance starts.</summary>
 internal static Action<System.Diagnostics.ProcessStartInfo> Launch { get; set; } = start => System.Diagnostics.Process.Start(start);

 private void DialogResultIfModal(bool value) { try { DialogResult = value; } catch (InvalidOperationException) { } }

 /// <summary>Creates the folder of a new library and opens it; the catalog is made when it opens.</summary>
 internal void Create(string? name = null)
 {
  name ??= Dialogs.Fields(this, T("New library"), (T("Name of the new library"), ""))?[0];
  if (name == null) return;
  try
  {
   var directory = LibraryCatalogs.NewLibraryDirectory(NewLibraryParent, name);
   FileAccessPolicy.Current.EnsureWritable(directory);
   Directory.CreateDirectory(directory);
   Store(LibraryCatalogs.Rename(LibraryCatalogs.Remember(_libraries, directory, DateTimeOffset.Now), directory, name), directory);
   Choose(directory);
  }
  catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { Message.Show(this, ex.Message, T("New library"), MessageBoxButton.OK, MessageBoxImage.Warning); }
 }

 /// <summary>Adds a folder that holds a library, or an empty folder for a new one.</summary>
 internal void AddExisting(string? folder = null)
 {
  if (folder == null)
  {
   var picker = new OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose the folder of a library (or an empty folder for a new one)") };
   if (picker.ShowDialog(this) != true) return;
   folder = picker.FolderName;
  }
  var directory = LibraryCatalogs.Normalize(folder);
  if (!File.Exists(LibraryCatalogs.CatalogPath(directory)) && Directory.EnumerateFileSystemEntries(directory).Any())
  {
   Message.Show(this, T("This folder holds no library (no file “{0}”). Choose the folder of a library or an empty folder.", LibraryCatalogs.CatalogFileName),
    T("Add library"), MessageBoxButton.OK, MessageBoxImage.Warning);
   return;
  }
  var known = _libraries.Any(l => LibraryCatalogs.SameDirectory(l.Directory, directory));
  Store(known ? _libraries : [.. _libraries, new KnownLibrary(directory)], directory);
  _frame.Status.Text = known ? T("This library is already in the list.") : T("Library added · switch with “Open”");
 }

 internal void Rename(string? name)
 {
  if (Selected is not { } library) return;
  Store(LibraryCatalogs.Rename(_libraries, library.Directory, name), library.Directory);
 }
 private void Rename()
 {
  if (Selected is not { } library) return;
  if (Dialogs.Fields(this, T("Rename library"), (T("Name (empty: folder name)"), LibraryCatalogs.DisplayName(library))) is { } values) Rename(values[0]);
 }

 internal async Task MoveAsync(string? parent = null)
 {
  if (Selected is not { } library || IsOpen(library)) return;
  if (parent == null)
  {
   var picker = new OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose a new location for “{0}”", LibraryCatalogs.DisplayName(library)) };
   if (picker.ShowDialog(this) != true) return;
   parent = picker.FolderName;
  }
  var target = Path.Combine(parent, Path.GetFileName(LibraryCatalogs.Normalize(library.Directory)));
  SetBusy(true);
  try
  {
   await StopInspectionAsync();
   _frame.Status.Text = T("Moving …");
   var progress = new Progress<double>(p => _frame.Status.Text = T("Moving … {0:P0}", p));
   var moved = await LibraryCatalogs.MoveAsync(library.Directory, target, progress, _closing.Token);
   Store(LibraryCatalogs.Relocate(_libraries, library.Directory, moved), moved);
   _frame.Status.Text = T("Moved to {0}", moved);
  }
  catch (OperationCanceledException) { _frame.Status.Text = T("Move cancelled"); }
  catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message.Show(this, ex.Message, T("Move"), MessageBoxButton.OK, MessageBoxImage.Warning); _frame.Status.Text = ""; }
  finally { SetBusy(false); }
 }

 internal async Task CompactAsync()
 {
  if (_openCatalog is not { } catalog || Selected is not { } library || !IsOpen(library)) return;
  SetBusy(true);
  try
  {
   _frame.Status.Text = T("Compacting …");
   var (before, after) = await Task.Run(catalog.Compact);
   _frame.Status.Text = T("Catalog compacted: {0} → {1}", MediaCard.SizeText(before), MediaCard.SizeText(after));
   RestartInspection();
  }
  catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException) { Message.Show(this, ex.Message, T("Compact"), MessageBoxButton.OK, MessageBoxImage.Warning); _frame.Status.Text = ""; }
  finally { SetBusy(false); }
 }

 private void Forget()
 {
  if (Selected is not { } library || IsOpen(library)) return;
  Store(LibraryCatalogs.Forget(_libraries, library.Directory), _openDirectory);
  _frame.Status.Text = T("“{0}” removed from the list; the folder stays.", LibraryCatalogs.DisplayName(library));
 }

 /// <summary>Moves the whole library folder to the recycle bin after a question; media files elsewhere stay.</summary>
 internal async Task DeleteAsync(bool confirmed = false)
 {
  if (Selected is not { } library || IsOpen(library)) return;
  if (!confirmed && Message.Show(this, T("Move “{0}” and its catalog, thumbnails and caches to the Recycle Bin?\n\n{1}\n\nThe media files themselves stay where they are.", LibraryCatalogs.DisplayName(library), library.Directory),
   T("Delete library"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
  SetBusy(true);
  try
  {
   await StopInspectionAsync();
   if (Directory.Exists(library.Directory)) { FileAccessPolicy.Current.EnsureWritable(library.Directory); MainWindow.SendToRecycleBin(library.Directory); }
   Store(LibraryCatalogs.Forget(_libraries, library.Directory), _openDirectory);
   _frame.Status.Text = T("“{0}” is in the Recycle Bin.", LibraryCatalogs.DisplayName(library));
  }
  catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message.Show(this, ex.Message, T("Delete library"), MessageBoxButton.OK, MessageBoxImage.Warning); }
  finally { SetBusy(false); }
 }

 private void SetBusy(bool busy) { _busy = busy; Cursor = busy ? Cursors.AppStarting : null; UpdateActions(); BusyChanged?.Invoke(); }

 /// <summary>Raised when work in the window starts or ends, so pages can disable their own buttons.</summary>
 internal event Action? BusyChanged;

 /// <summary>Puts a library made elsewhere (restored copy) into the list under <paramref name="name"/>.</summary>
 internal void AddLibrary(string directory, string name) =>
  Store(LibraryCatalogs.Rename(LibraryCatalogs.Remember(_libraries, directory, DateTimeOffset.Now), directory, name), _openDirectory);

 internal void Select(string directory)
 {
  foreach (ListBoxItem item in _list.Items) if (item.DataContext is KnownLibrary library && LibraryCatalogs.SameDirectory(library.Directory, directory)) { _list.SelectedItem = item; return; }
 }
}
