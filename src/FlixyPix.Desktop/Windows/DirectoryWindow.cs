using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using Shape = System.Windows.Shapes.Shape;
using FlixyPix.Core;
using FlixyPix.MediaTools;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Opens the directory of tags, performers and studios; "Medien zeigen" there filters the main window by a tag.</summary>
 /// <param name="start">Runs once the window is shown, such as importing or exporting a tag package from the database management.</param>
 internal void ShowDirectory(TagRole? role = null, Action<DirectoryWindow>? start = null)
 {
  var window = new DirectoryWindow(this, _repository, role, CurrentTag) { SearchProviders = SearchProviders, PictureCache = Path.Combine(_settings.DataDirectory, "tag-assets"),
   Tools = ToolExists(_settings.Ffmpeg, "ffmpeg") ? _tools : null };
  if (start != null) window.ContentRendered += (_, _) => start(window);
  window.ShowDialog();
  if (window.Changed) Refresh();
  if (window.ShowMediaOf is { } tag) { ClearTagFilters(); _requiredTags.Add(tag); RenderTagFilterChips(); _anchor = 0; Refresh(); }
 }
 private void DirectoryClicked(object sender, RoutedEventArgs e) => ShowDirectory();
}

/// <summary>A row of the directory list; its picture (portrait or logo) is loaded when the row is first shown.</summary>
internal sealed class DirectoryRow(DirectoryEntry entry, Func<DirectoryRow, Task>? loadPicture = null) : INotifyPropertyChanged
{
 private Brush? picture; private bool requested;
 public DirectoryEntry Entry { get; } = entry;
 /// <summary>Portraits of performers are round, logos and tag pictures have rounded corners.</summary>
 public double Radius => Entry.Role == TagRole.Performer ? 15 : 5;
 public string Initials => string.Concat(Entry.Tag.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpper(w[0], CultureInfo.CurrentCulture)));
 public Brush? Picture
 {
  get { if (!requested && loadPicture != null) { requested = true; _ = loadPicture(this); } return picture; }
  set { picture = value; PropertyChanged?.Invoke(this, new(nameof(Picture))); }
 }
 public event PropertyChangedEventHandler? PropertyChanged;
 public string Tag => Entry.Tag;
 public string Category => Entry.Category.Length == 0 ? "–" : Entry.Category;
 public string Role => TagPackages.RoleLabel(Entry.Role);
 public string Count => Entry.MediaCount.ToString("N0", CultureInfo.CurrentCulture);
 public string Aliases => string.Join(", ", Entry.AliasList);
 public string Favorite => Entry.IsFavorite ? "★" : "";
}

/// <summary>"Verzeichnis": every tag with category, role and media count. One entry is edited on the right (name, category,
/// role of the category, aliases, favourite, description); several are merged or deleted together with a preview of what
/// changes. Tag packages (CSV) are imported with a preview and exported from the list.</summary>
internal sealed partial class DirectoryWindow : Window
{
 private readonly CatalogRepository repository;
 private readonly ListView list = MigrationViews.List();
 private readonly TextBox search = new() { MinWidth = 220, VerticalContentAlignment = VerticalAlignment.Center };
 private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal };
 private readonly TextBox name = new(), aliases = new(), description = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
 private readonly ComboBox category = new() { IsEditable = true };
 private readonly RadioButton[] roles = [new() { Content = T("Tag") }, new() { Content = T("Performers") }, new() { Content = T("Studio") }];
 private readonly CheckBox favorite = SettingsUi.Switch();
 private readonly StackPanel single = new(), several = new() { Visibility = Visibility.Collapsed };
 private readonly TextBlock severalText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
 private readonly WindowFrame frame;
 private IReadOnlyList<DirectoryEntry> entries = [];
 private TagRole? filter;
 // Profile: picture, media of the entry, and the undo of the last merge or deletion.
 private readonly Rectangle portrait = new() { Width = 88, Height = 88 }, portraitBack = new() { Width = 88, Height = 88 };
 private readonly TextBlock portraitInitials = new() { FontSize = 28, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
 private readonly WrapPanel mediaTiles = new() { Margin = new Thickness(0, 0, 0, 4) };
 private readonly TextBlock mediaCount = new() { Foreground = SettingsUi.Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
 private readonly Border undoBar;
 private readonly TextBlock undoText = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
 private TagChangeUndo? undo;
 private IReadOnlyDictionary<TagKey, Guid> pictures = new Dictionary<TagKey, Guid>();
 private readonly Dictionary<Guid, Task<string?>> unpacked = [];
 private int profileVersion;
 private Task profileReady = Task.CompletedTask;

 /// <summary>Where tag pictures are unpacked for showing; without it the directory shows initials only.</summary>
 internal string? PictureCache { get; init; }
 internal bool CanUndo => undo != null;
 internal bool HasPortrait => portrait.Fill != null;
 internal int MediaTileCount => mediaTiles.Children.Count;
 internal Task ProfileReady => profileReady;
 internal string CategoryText => category.Text;
 internal bool RowHasPicture(string tag) => list.Items.Cast<DirectoryRow>().FirstOrDefault(r => r.Tag == tag)?.Picture != null;
 internal bool Changed { get; private set; }
 internal string? ShowMediaOf { get; private set; }
 internal int RowCount => list.Items.Count;
 internal DirectoryEntry? Current => (list.SelectedItem as DirectoryRow)?.Entry;
 /// <summary>Providers of "Im Browser suchen" for the selected entry.</summary>
 internal IReadOnlyList<WebSearchProvider> SearchProviders { get; init; } = WebSearch.Defaults;
 /// <summary>Makes stills of videos for profile pictures; without it the media tiles offer no still.</summary>
 internal MediaToolService? Tools { get; init; }
 /// <summary>The clipboard goes through this, so the smoke test leaves the user's clipboard alone.</summary>
 internal static Func<BitmapSource?> ClipboardImage { get; set; } = () => Clipboard.ContainsImage() ? Clipboard.GetImage() : null;

 internal DirectoryWindow(Window owner, CatalogRepository repository, TagRole? role = null, string? select = null)
 {
  Owner = owner; this.repository = repository; filter = role;
  Title = T("Directory · FlixyPix"); Width = 1120; Height = 740; MinWidth = 820; MinHeight = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
  var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
  search.ToolTip = T("Name, alias or category (Ctrl+F)"); search.Padding = new Thickness(6, 5, 6, 5); System.Windows.Automation.AutomationProperties.SetName(search, T("Search the directory"));
  var placeholder = new TextBlock { Text = T("Search …"), Foreground = SettingsUi.Muted, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
  search.TextChanged += (_, _) => { placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Fill(); };
  var searchBox = new Grid { VerticalAlignment = VerticalAlignment.Center }; searchBox.Children.Add(search); searchBox.Children.Add(placeholder);
  DockPanel.SetDock(searchBox, Dock.Right); top.Children.Add(searchBox); top.Children.Add(tabs);
  list.SelectionMode = SelectionMode.Extended; list.Margin = new Thickness(0, 8, 16, 0); list.ItemContainerStyle = TagAssetsWindow.ArtworkRowStyle();
  MigrationViews.Columns(list, (T("Name"), "Tag", 220), (T("Category"), "Category", 140), (T("Role"), "Role", 120), (T("Items"), "Count", 90), ("", "Favorite", 28), (T("Aliases"), "Aliases", 200));
  ((GridView)list.View).Columns.Insert(0, new GridViewColumn { Header = "", Width = 46, CellTemplate = PictureCell() });
  var undoButton = new Button { Content = T("Undo"), Margin = new Thickness(12, 0, 0, 0) }; undoButton.Click += (_, _) => Undo();
  var undoRow = new DockPanel(); DockPanel.SetDock(undoButton, Dock.Right); undoRow.Children.Add(undoButton); undoRow.Children.Add(undoText);
  undoBar = new Border { Background = SettingsUi.CardBackground, BorderBrush = SettingsUi.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
   Padding = new Thickness(12, 6, 8, 6), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, Child = undoRow };
  var header = new StackPanel(); header.Children.Add(top); header.Children.Add(undoBar);
  list.SelectionChanged += (_, _) => ShowSelection();
  list.MouseDoubleClick += (_, _) => { if (Current is { } entry) { ShowMediaOf = entry.Tag; Close(); } };
  var editor = Editor();
  var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(380) });
  body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
  Grid.SetColumnSpan(header, 2); body.Children.Add(header); Grid.SetRow(list, 1); body.Children.Add(list); Grid.SetRow(editor, 1); Grid.SetColumn(editor, 1); body.Children.Add(editor);
  frame = new WindowFrame(T("Directory"), T("Tags, performers and studios of the library. Renaming, merging and deleting affect all items, saved searches and rules."), body, "", scroll: false);
  frame.Add(T("Adult Content Pack"), () => { var pack = new AdultContentPackWindow(this, repository); pack.ShowDialog(); if (pack.Changed) { Changed = true; Reload(); } });
  frame.Add(T("New …"), AddEntry); frame.Add(T("Import package …"), ImportPackage); frame.Add(T("Export …"), ExportPackage); frame.Add(T("Close"), Close, primary: true, cancel: true);
  Content = frame;
  PreviewKeyDown += (_, e) =>
  {
   if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); search.SelectAll(); e.Handled = true; }
   if (e.Key == Key.Delete && list.IsKeyboardFocusWithin) { Delete(); e.Handled = true; }
   if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && undo != null && e.OriginalSource is not TextBox) { Undo(); e.Handled = true; }
  };
  Reload(select);
  // The picture cache is set after construction; the first profile is shown again with it.
  Loaded += (_, _) => ShowSelection();
 }

 /// <summary>Picture column: initials on a muted shape, covered by the portrait or logo once it is loaded.</summary>
 private static DataTemplate PictureCell()
 {
  var grid = new FrameworkElementFactory(typeof(Grid));
  grid.SetValue(WidthProperty, 30d); grid.SetValue(HeightProperty, 30d); grid.SetValue(MarginProperty, new Thickness(0, 2, 0, 2));
  var back = new FrameworkElementFactory(typeof(Rectangle));
  back.SetValue(Shape.FillProperty, SettingsUi.CardBorder);
  var initials = new FrameworkElementFactory(typeof(TextBlock));
  initials.SetBinding(TextBlock.TextProperty, new Binding(nameof(DirectoryRow.Initials)));
  initials.SetValue(TextBlock.FontSizeProperty, 11d); initials.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); initials.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
  var image = new FrameworkElementFactory(typeof(Rectangle));
  image.SetBinding(Shape.FillProperty, new Binding(nameof(DirectoryRow.Picture)));
  foreach (var shape in new[] { back, image }) { shape.SetBinding(Rectangle.RadiusXProperty, new Binding(nameof(DirectoryRow.Radius))); shape.SetBinding(Rectangle.RadiusYProperty, new Binding(nameof(DirectoryRow.Radius))); }
  image.SetValue(Privacy.BlurProperty, true);
  grid.AppendChild(back); grid.AppendChild(initials); grid.AppendChild(image);
  return new DataTemplate { VisualTree = grid };
 }

 private bool TryPicture(DirectoryEntry entry, out Guid id)
 {
  if (pictures.TryGetValue(new TagKey(entry.Category, entry.Tag), out id)) return true;
  // A tag without category, or one that moved, still shows a picture stored under its name.
  var match = pictures.FirstOrDefault(p => string.Equals(p.Key.Value, entry.Tag, StringComparison.OrdinalIgnoreCase));
  id = match.Value; return match.Key != null;
 }

 /// <summary>The picture of an entry as a brush filling its shape; null without picture, cache or a readable image.</summary>
 private async Task<Brush?> PictureAsync(DirectoryEntry entry, int size)
 {
  if (PictureCache is not { } cache || !TryPicture(entry, out var id)) return null;
  try
  {
   // Row and profile ask for the same picture at once; both wait for one unpacking into the cache file.
   if (!unpacked.TryGetValue(id, out var unpacking))
    unpacked[id] = unpacking = Task.Run(() => repository.GetTagAsset(id) is { IsOpaque: false } asset ? repository.MaterializeTagAsset(asset, cache) : null);
   string? path;
   try { path = await unpacking; }
   catch { unpacked.Remove(id); throw; }
   if (path == null) return null;
   var brush = new ImageBrush(await DesktopImaging.LoadBitmapAsync(path, size)) { Stretch = Stretch.UniformToFill }; brush.Freeze();
   return brush;
  }
  catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return null; }
 }

 private async Task LoadRowPicture(DirectoryRow row) => row.Picture = await PictureAsync(row.Entry, 64);

 private FrameworkElement Editor()
 {
  foreach (var radio in roles) { radio.Style = (Style)Application.Current.Resources["Segment"]; radio.GroupName = "role" + GetHashCode(); }
  var roleBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) }; foreach (var radio in roles) roleBar.Children.Add(radio);
  Label(T("Name"), name); Label(T("Category"), category);
  portraitBack.Fill = SettingsUi.CardBorder;
  var picture = new Grid { Width = 88, Height = 88, Margin = new Thickness(0, 0, 14, 0) };
  Privacy.SetBlur(portrait, true);
  picture.Children.Add(portraitBack); picture.Children.Add(portraitInitials); picture.Children.Add(portrait);
  var choose = new Button { Content = T("Choose image …"), HorizontalAlignment = HorizontalAlignment.Left }; choose.Click += (_, _) => ChoosePicture();
  var paste = new Button { Content = T("From the clipboard"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) }; paste.Click += (_, _) => PastePicture();
  var release = new Button { Content = T("Unlink image"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) }; release.Click += (_, _) => ReleasePicture();
  var pictureButtons = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; pictureButtons.Children.Add(choose); pictureButtons.Children.Add(paste); pictureButtons.Children.Add(release);
  pictureButtons.Children.Add(new TextBlock { Text = T("Portrait, logo or tag image; for a still, use the context menu of a video below"), Foreground = SettingsUi.Muted, FontSize = 11,
   TextWrapping = TextWrapping.Wrap, MaxWidth = 220, Margin = new Thickness(0, 6, 0, 0) });
  var profile = new DockPanel { Margin = new Thickness(0, 0, 0, 14) }; profile.Children.Add(picture); profile.Children.Add(pictureButtons);
  single.Children.Add(profile);
  single.Children.Add(Field(T("Name"), name));
  single.Children.Add(Field(T("Category"), category, T("A new category is created when saving.")));
  single.Children.Add(Field(T("Role of the category"), roleBar, T("Applies to all tags of this category.")));
  single.Children.Add(Field(T("Aliases"), aliases, T("Alternative spellings, separated by {0}; these can also be used to find the tag.", "|")));
  var favoriteRow = new DockPanel { Margin = new Thickness(0, 4, 0, 12) }; DockPanel.SetDock(favorite, Dock.Right); favoriteRow.Children.Add(favorite);
  favoriteRow.Children.Add(new TextBlock { Text = T("Favorite (quick tags, home)"), VerticalAlignment = VerticalAlignment.Center });
  single.Children.Add(favoriteRow);
  single.Children.Add(Field(T("Description"), description));
  single.Children.Add(ProfileSection());
  var save = new Button { Content = T("Save"), Style = (Style)Application.Current.Resources["Primary"] }; save.Click += (_, _) => Save();
  var show = new Button { Content = T("Show items") }; show.Click += (_, _) => { if (Current is { } entry) { ShowMediaOf = entry.Tag; Close(); } };
  var merge = new Button { Content = T("Merge …") }; merge.Click += (_, _) => Merge();
  var delete = new Button { Content = T("Delete …") }; delete.Click += (_, _) => Delete();
  var web = new Button { Content = T("Search in browser ▾") };
  web.Click += (_, _) =>
  {
   if (Current is not { } entry || SearchProviders.Count == 0) return;
   var menu = new ContextMenu { PlacementTarget = web, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
   foreach (var provider in SearchProviders) { var item = new MenuItem { Header = provider.Name }; item.Click += (_, _) => MainWindow.OpenWebSearch(provider, entry.Tag, this); menu.Items.Add(item); }
   menu.IsOpen = true;
  };
  single.Children.Add(SettingsUi.Buttons(save, show, web));
  single.Children.Add(new TextBlock { Text = T("Items"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
  single.Children.Add(mediaCount); single.Children.Add(mediaTiles);
  var mergeMany = new Button { Content = T("Merge …"), Style = (Style)Application.Current.Resources["Primary"] }; mergeMany.Click += (_, _) => Merge();
  var deleteMany = new Button { Content = T("Delete …") }; deleteMany.Click += (_, _) => Delete();
  several.Children.Add(severalText); several.Children.Add(SettingsUi.Buttons(mergeMany, deleteMany));
  var stack = new StackPanel(); stack.Children.Add(single); stack.Children.Add(several);
  stack.Children.Add(new Border { Height = 1, Background = SettingsUi.CardBorder, Margin = new Thickness(0, 14, 0, 12) });
  stack.Children.Add(SettingsUi.Buttons(merge, delete));
  return new Border { Background = SettingsUi.CardBackground, BorderBrush = SettingsUi.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Margin = new Thickness(0, 8, 0, 0),
   Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
 }

 private static void Label(string text, Control control) => System.Windows.Automation.AutomationProperties.SetName(control, text);
 private static StackPanel Field(string label, FrameworkElement input, string? hint = null)
 {
  var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
  panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
  if (input is TextBox box) box.Padding = new Thickness(6, 5, 6, 5);
  panel.Children.Add(input); if (hint != null) panel.Children.Add(SettingsUi.Hint(hint));
  return panel;
 }

 private void Reload(string? select = null)
 {
  var keep = select ?? Current?.Tag;
  entries = repository.GetDirectory(); pictures = repository.GetTagPictureIds();
  category.ItemsSource = entries.Select(e => e.Category).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
  BuildTabs(); Fill(keep);
 }

 private void BuildTabs()
 {
  tabs.Children.Clear();
  foreach (var (label, role) in new (string, TagRole?)[] { (T("All"), null), (T("Tags"), TagRole.Tag), (T("Performers"), TagRole.Performer), (T("Studios"), TagRole.Studio) })
  {
   var count = entries.Count(e => role is null || e.Role == role);
   var tab = new RadioButton { Content = $"{label} · {count:N0}", Style = (Style)Application.Current.Resources["Segment"], GroupName = "tabs" + GetHashCode(), IsChecked = filter == role, Padding = new Thickness(12, 6, 12, 6) };
   tab.Checked += (_, _) => { filter = role; Fill(); };
   tabs.Children.Add(tab);
  }
 }

 private void Fill(string? select = null)
 {
  var keep = select ?? Current?.Tag;
  var text = search.Text.Trim();
  var shown = entries.Where(e => filter is null || e.Role == filter)
   .Where(e => text.Length == 0 || e.Tag.Contains(text, StringComparison.CurrentCultureIgnoreCase) || e.Category.Contains(text, StringComparison.CurrentCultureIgnoreCase)
    || e.AliasList.Any(a => a.Contains(text, StringComparison.CurrentCultureIgnoreCase))).Select(e => new DirectoryRow(e, LoadRowPicture)).ToList();
  list.ItemsSource = shown;
  list.SelectedItem = shown.FirstOrDefault(r => string.Equals(r.Tag, keep, StringComparison.OrdinalIgnoreCase)) ?? shown.FirstOrDefault();
  if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
  frame.Status.Text = T("{0:N0} of {1:N0} entries", shown.Count, entries.Count);
  ShowSelection();
 }

 private List<DirectoryEntry> Selection => list.SelectedItems.Cast<DirectoryRow>().Select(r => r.Entry).ToList();

 private void ShowSelection()
 {
  var selected = Selection;
  single.Visibility = selected.Count <= 1 ? Visibility.Visible : Visibility.Collapsed;
  several.Visibility = selected.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
  severalText.Text = Tn(selected.Count, "1 entry selected · {1:N0} assignments in total", "{0:N0} entries selected · {1:N0} assignments in total", selected.Sum(e => e.MediaCount));
  var entry = selected.FirstOrDefault();
  single.IsEnabled = entry != null;
  name.Text = entry?.Tag ?? ""; category.Text = entry?.Category ?? ""; aliases.Text = entry is null ? "" : string.Join(" | ", entry.AliasList);
  favorite.IsChecked = entry?.IsFavorite == true; description.Text = entry?.Description ?? "";
  for (var i = 0; i < roles.Length; i++) { roles[i].IsChecked = entry != null && (int)entry.Role == i; roles[i].IsEnabled = entry?.Category.Length > 0; }
  ShowProperties(entry);
  profileReady = LoadProfileAsync(entry);
 }

 private void Save()
 {
  if (Current is not { } entry) return;
  var target = category.Text.Trim();
  if (!TryReadProperties(entry.Profile, out var profile, out var problem)) { frame.Status.Text = problem; return; }
  var edited = entry with
  {
   Tag = name.Text.Trim(), Category = target, IsFavorite = favorite.IsChecked == true, Description = description.Text,
   Aliases = aliases.Text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(), Profile = profile,
  };
  if (profile != null && target.Length == 0) { frame.Status.Text = T("Properties need a category; give the entry a category first."); return; }
  if (edited.Tag.Length == 0) { frame.Status.Text = T("The name must not be empty."); name.Focus(); return; }
  var merging = !string.Equals(entry.Tag, edited.Tag, StringComparison.OrdinalIgnoreCase) && entries.Any(e => string.Equals(e.Tag, edited.Tag, StringComparison.OrdinalIgnoreCase));
  if (merging && Message.Show(this, T("“{0}” already exists. Merge “{1}” into it? The old name is kept as an alias.", edited.Tag, entry.Tag), T("Merge"),
   MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
  try
  {
   var result = repository.SaveDirectoryEntry(entry.Tag, entry.Category, edited);
   var role = (TagRole)Array.FindIndex(roles, r => r.IsChecked == true);
   if (target.Length > 0 && Enum.IsDefined(role) && repository.GetTagRole(target) != role) repository.SetTagRole(target, role);
   Changed = true; HideUndo();
   frame.Status.Text = result.MediaChanged > 0 ? Tn(result.MediaChanged, "Saved · 1 item changed", "Saved · {0:N0} items changed") : T("Saved");
   Reload(edited.Tag);
  }
  catch (ArgumentException error) { Message.Show(this, error.Message, "Verzeichnis", MessageBoxButton.OK, MessageBoxImage.Warning); }
 }

 private void Merge()
 {
  var selected = Selection; if (selected.Count == 0) return;
  var target = selected.MaxBy(e => e.MediaCount)!.Tag;
  var input = Dialogs.Fields(this, T("Merge"), (T("Common name"), target));
  if (input == null || input[0].Trim().Length == 0) return;
  target = input[0].Trim();
  var sources = selected.Select(e => e.Tag).Where(t => !string.Equals(t, target, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
  if (sources.Count == 0) return;
  if (Message.Show(this, T("Merge {0} into “{1}”? The old names are kept as aliases.", string.Join(", ", sources.Select(s => T("“{0}”", s))), target), T("Merge"),
   MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
  var result = repository.MergeTags(sources, target);
  Changed = true; frame.Status.Text = Tn(result.MediaChanged, "Merged · 1 item changed", "Merged · {0:N0} items changed"); Reload(target);
  ShowUndo(result.Undo, T("“{0}” merged into “{1}”", string.Join(T("”, “"), sources), target));
 }

 private void Delete()
 {
  var tags = Selection.Select(e => e.Tag).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); if (tags.Count == 0) return;
  var (media, searches, rules) = (0, 0, 0);
  foreach (var tag in tags) { var preview = repository.PreviewTagChange(tag, null); media += preview.MediaChanged; searches += preview.SearchesChanged; rules += preview.RulesChanged; }
  var text = (tags.Count == 1 ? T("Delete “{0}”?", tags[0]) : Tn(tags.Count, "Delete 1 tag?", "Delete {0:N0} tags?"))
   + "\n\n" + (tags.Count == 1 ? Tn(media, "Removed from 1 item", "Removed from {0:N0} items") : Tn(media, "Removed from 1 item (assignments)", "Removed from {0:N0} items (assignments)")) +
   (searches > 0 ? Tn(searches, ", changes 1 saved search", ", changes {0:N0} saved searches") : "") + (rules > 0 ? Tn(rules, ", turns off 1 rule", ", turns off {0:N0} rules") : "") + T(". Afterwards it can be undone here.");
  if (Message.Show(this, text, T("Delete"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
  DeleteWithoutAsking(tags);
 }

 /// <summary>Deletes after the question (or directly in tests) and offers the undo.</summary>
 internal void DeleteWithoutAsking(IReadOnlyList<string> tags)
 {
  var result = repository.DeleteTags(tags);
  Changed = true; frame.Status.Text = Tn(result.MediaChanged, "Deleted · 1 assignment removed", "Deleted · {0:N0} assignments removed"); Reload();
  ShowUndo(result.Undo, tags.Count == 1 ? T("“{0}” deleted", tags[0]) : Tn(tags.Count, "1 tag deleted", "{0:N0} tags deleted"));
 }

 private void ShowUndo(TagChangeUndo? change, string text)
 {
  undo = change is { IsEmpty: false } ? change : null;
  undoText.Text = text; undoBar.Visibility = undo != null ? Visibility.Visible : Visibility.Collapsed;
 }

 private void HideUndo() { undo = null; undoBar.Visibility = Visibility.Collapsed; }

 /// <summary>Takes the last merge or deletion back (also Ctrl+Z), unless something it changed was changed since.</summary>
 internal void Undo()
 {
  if (undo is not { } pending) return;
  HideUndo();
  if (repository.UndoTagChange(pending)) { Changed = true; frame.Status.Text = T("Undone"); Reload(); }
  else Message.Show(this, T("This can no longer be undone: affected items, searches or entries have been changed since."), T("Undo"),
   MessageBoxButton.OK, MessageBoxImage.Information);
 }

 /// <summary>Shows picture and media of the entry; only the newest request writes into the profile.</summary>
 private async Task LoadProfileAsync(DirectoryEntry? entry)
 {
  var version = ++profileVersion;
  portrait.Fill = null; mediaTiles.Children.Clear(); mediaCount.Text = "";
  var radius = entry?.Role == TagRole.Performer ? 44 : 10;
  foreach (var shape in new[] { portrait, portraitBack }) { shape.RadiusX = radius; shape.RadiusY = radius; }
  portraitInitials.Text = entry == null ? "" : new DirectoryRow(entry).Initials;
  if (entry == null) return;
  var picture = await PictureAsync(entry, 176);
  if (version != profileVersion) return;
  portrait.Fill = picture;
  MediaPage page;
  try { page = await Task.Run(() => repository.QueryPage(new MediaQuery { Tag = entry.Tag, Limit = 8, Sort = MediaSort.AddedAt, Descending = true })); }
  catch (ObjectDisposedException) { return; }
  if (version != profileVersion) return;
  mediaCount.Text = page.TotalCount == 0 ? T("Not assigned to any item yet.") : page.TotalCount > page.Items.Count
   ? Tn(page.TotalCount, "1 item · the newest {1:N0}; “Show items” shows all", "{0:N0} items · the newest {1:N0}; “Show items” shows all", page.Items.Count)
   : Tn(page.TotalCount, "1 item", "{0:N0} items");
  foreach (var item in page.Items)
  {
   var image = new Image { Stretch = Stretch.UniformToFill };
   var tile = new Border { Width = 80, Height = 45, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(4), Background = SettingsUi.CardBorder, ClipToBounds = true,
    ToolTip = string.IsNullOrWhiteSpace(item.Title) ? Path.GetFileName(item.Path) : item.Title, Cursor = Cursors.Hand, Child = image };
   Privacy.SetBlur(image, true);
   tile.MouseLeftButtonUp += (_, _) => { ShowMediaOf = entry.Tag; Close(); };
   if (item.Kind == MediaKind.Video && Tools != null)
   {
    var still = new MenuItem { Header = T("Use still as image …") }; var video = item;
    still.Click += async (_, _) => await AskStillAsync(entry, video);
    tile.ContextMenu = new ContextMenu { Items = { still } };
   }
   mediaTiles.Children.Add(tile);
   if (item.ThumbnailPath is { } thumbnail && File.Exists(thumbnail))
    try { image.Source = await DesktopImaging.LoadBitmapAsync(thumbnail, 160); }
    catch (Exception error) when (error is IOException or NotSupportedException or InvalidOperationException or ArgumentException or UnauthorizedAccessException) { }
   if (version != profileVersion) return;
  }
 }

 private void ChoosePicture()
 {
  if (Current is not { } entry) return;
  if (entry.Category.Length == 0) { frame.Status.Text = T("A picture needs a category; give the entry a category and save first."); return; }
  var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose image"), Filter = T("Images|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp;*.tif;*.tiff;*.avif;*.heic|All files|*.*") };
  if (picker.ShowDialog(this) != true) return;
  try { SetPicture(entry, picker.FileName); }
  catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
  { Message.Show(this, T("The image could not be imported:\n{0}", error.Message), T("Choose image"), MessageBoxButton.OK, MessageBoxImage.Warning); }
 }

 /// <summary>Stores the picture for the entry (also for tests); an earlier picture of it is released first.</summary>
 internal void SetPicture(DirectoryEntry entry, string path) => StorePicture(entry, () => repository.ImportTagAssetFile(path, new TagKey(entry.Category, entry.Tag)));

 private void StorePicture(DirectoryEntry entry, Func<TagAsset> import)
 {
  repository.ReplaceTagPicture(new TagKey(entry.Category, entry.Tag), import);
  HideUndo(); frame.Status.Text = T("Image saved"); Reload(entry.Tag);
 }

 /// <summary>Takes the picture on the clipboard as PNG; false when there is none.</summary>
 internal bool PastePicture()
 {
  if (Current is not { } entry) return false;
  if (entry.Category.Length == 0) { frame.Status.Text = T("A picture needs a category; give the entry a category and save first."); return false; }
  if (ClipboardImage() is not { } image) { frame.Status.Text = T("The clipboard contains no image."); return false; }
  var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
  using var stream = new MemoryStream(); encoder.Save(stream);
  StorePicture(entry, () => repository.ImportTagAssetData(stream.ToArray(), new TagKey(entry.Category, entry.Tag), T("From the clipboard"), "clipboard"));
  return true;
 }

 private async Task AskStillAsync(DirectoryEntry entry, MediaItem video)
 {
  if (entry.Category.Length == 0) { frame.Status.Text = T("A picture needs a category; give the entry a category and save first."); return; }
  var suggested = video.DurationSeconds is > 0 and var duration ? Math.Round(duration / 2) : 0;
  var values = Dialogs.Fields(this, T("Still as image"), (Tc("directory", "Position in seconds"), suggested.ToString(System.Globalization.CultureInfo.CurrentCulture)));
  if (values == null) return;
  if (!MainWindow.TrySeconds(values[0], out var seconds) || seconds < 0) { frame.Status.Text = T("Please enter a position of 0 seconds or more."); return; }
  try { await SetStillAsync(entry, video, seconds); }
  catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
  { Message.Show(this, T("The image could not be imported:\n{0}", error.Message), T("Still as image"), MessageBoxButton.OK, MessageBoxImage.Warning); }
 }

 /// <summary>A still of the video at the given second becomes the entry's picture (also for tests).</summary>
 internal async Task SetStillAsync(DirectoryEntry entry, MediaItem video, double seconds)
 {
  if (Tools is not { } tools) throw new InvalidOperationException(T("FFmpeg is missing."));
  var still = Path.Combine(Path.GetTempPath(), "flixypix-still-" + Guid.NewGuid().ToString("N") + ".jpg");
  try
  {
   frame.Status.Text = T("Creating still …");
   await tools.CreateThumbnailAsync(video.Path, still, seconds);
   var data = await File.ReadAllBytesAsync(still);
   var label = (string.IsNullOrWhiteSpace(video.Title) ? Path.GetFileNameWithoutExtension(video.Path) : video.Title) + " · " + MediaCard.DurationText(seconds);
   StorePicture(entry, () => repository.ImportTagAssetData(data, new TagKey(entry.Category, entry.Tag), label, "video-still"));
  }
  finally { try { File.Delete(still); } catch (IOException) { } }
 }

 private void ReleasePicture()
 {
  if (Current is not { } entry || !ReleasePictureOf(entry)) return;
  HideUndo(); frame.Status.Text = T("Image unlinked; it remains under “Tag images”."); Reload(entry.Tag);
 }

 private bool ReleasePictureOf(DirectoryEntry entry)
 {
  if (!TryPicture(entry, out var id) || repository.GetTagAsset(id) is not { Tag: { } key } asset) return false;
  repository.UpsertTagAsset(asset with { Tag = null, PreviousTags = [.. asset.PreviousTags, key] });
  return true;
 }

 private void AddEntry()
 {
  var suggested = filter switch { TagRole.Performer => T("Performers"), TagRole.Studio => T("Studios"), _ => "" };
  var input = Dialogs.Fields(this, T("New entry"), ("Name", ""), (T("Category"), suggested));
  if (input == null || input[0].Trim().Length == 0 || input[1].Trim().Length == 0) return;
  repository.UpsertVocabularyTag(new TagVocabularyEntry(input[0].Trim(), input[1].Trim())); HideUndo();
  if (filter is { } role && repository.GetTagRole(input[1]) != role) repository.SetTagRole(input[1], role);
  Changed = true; Reload(input[0].Trim());
 }

 internal void ImportPackage()
 {
  var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose tag package"), Filter = "FlixyPix-Tagpaket|*" + TagPackages.Extension + T("|CSV and text files|*.csv;*.txt|All files|*.*") };
  if (picker.ShowDialog(this) != true) return;
  try { ImportPackage(picker.FileName, null, review: true); }
  catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
  { Message.Show(this, T("The package could not be read:\n{0}", error.Message), T("Tag package"), MessageBoxButton.OK, MessageBoxImage.Warning); }
 }

 /// <summary>Reads, plans and (after the review, or directly for tests) applies a package; returns the rows applied.</summary>
 internal int ImportPackage(string path, string? defaultCategory, bool review)
 {
  var package = TagPackages.Read(path);
  if (defaultCategory == null && review && package.Rows.Any(r => r.Category == null))
  {
   var suggested = filter switch { TagRole.Performer => T("Performers"), TagRole.Studio => T("Studios"), _ => Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)) };
   var input = Dialogs.Fields(this, T("Category for entries without a category"), (T("Category"), suggested));
   if (input == null) return 0;
   defaultCategory = input[0].Trim();
  }
  var plan = repository.PlanTagPackage(package, defaultCategory);
  IReadOnlyCollection<int> lines = plan.Items.Where(i => i.CanApply).Select(i => i.Row.Line).ToList();
  if (review)
  {
   var preview = new TagPackageWindow(this, Path.GetFileName(path), plan, package.Warnings);
   if (preview.ShowDialog() != true) return 0;
   lines = preview.ChosenLines;
  }
  var applied = repository.ApplyTagPackage(plan, lines);
  if (applied > 0) { Changed = true; HideUndo(); }
  frame.Status.Text = Tn(applied, "1 entry imported from “{1}”", "{0:N0} entries imported from “{1}”", Path.GetFileName(path));
  Reload();
  return applied;
 }

 internal void ExportPackage()
 {
  var rows = Selection.Count > 1 ? Selection : list.Items.Cast<DirectoryRow>().Select(r => r.Entry).ToList();
  var picker = new SaveFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Save tag package"), FileName = "FlixyPix-Tags" + TagPackages.Extension, Filter = "FlixyPix-Tagpaket|*" + TagPackages.Extension, AddExtension = false };
  if (picker.ShowDialog(this) != true) return;
  var path = picker.FileName.EndsWith(TagPackages.Extension, StringComparison.OrdinalIgnoreCase) ? picker.FileName : Path.ChangeExtension(picker.FileName, null) + TagPackages.Extension;
  TagPackages.Save(path, rows);
  frame.Status.Text = Tn(rows.Count, "1 entry exported to “{1}”", "{0:N0} entries exported to “{1}”", Path.GetFileName(path));
 }
}

/// <summary>One row of the package review with its checkbox.</summary>
internal sealed class PackageRowView(TagPackageItem item) : INotifyPropertyChanged
{
 private bool apply = item.CanApply;
 public TagPackageItem Item { get; } = item;
 public bool Apply { get => apply; set { if (apply == value || !Item.CanApply) return; apply = value; PropertyChanged?.Invoke(this, new(nameof(Apply))); } }
 public bool Enabled => Item.CanApply;
 public int Line => Item.Row.Line;
 public string Name => Item.Row.Name;
 public string Category => Item.Category;
 public string Role => TagPackages.RoleLabel(Item.Role);
 public string Status => Item.Status switch { TagPackageStatus.New => T("New"), TagPackageStatus.Exists => T("Present"), TagPackageStatus.Alias => T("Alias"), TagPackageStatus.Conflict => T("Conflict"), _ => T("Invalid") };
 public string Note => Item.Note + (Item.Row.Aliases.Count > 0 ? T(" · Aliases: ") + string.Join(", ", Item.Row.Aliases) : "");
 public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Review of a tag package before anything is stored: each row with what it would do; conflicts and invalid rows
/// cannot be chosen.</summary>
internal sealed class TagPackageWindow : Window
{
 private readonly List<PackageRowView> rows;
 internal IReadOnlyCollection<int> ChosenLines => rows.Where(r => r.Apply).Select(r => r.Line).ToList();

 internal TagPackageWindow(Window owner, string file, TagPackagePlan plan, IReadOnlyList<string> warnings)
 {
  Owner = owner; Title = T("Check tag package · FlixyPix"); Width = 980; Height = 640; MinWidth = 700; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
  rows = plan.Items.Select(i => new PackageRowView(i)).ToList();
  var list = MigrationViews.List(); list.ItemContainerStyle = TagAssetsWindow.ArtworkRowStyle(); list.ItemsSource = rows;
  var view = new GridView();
  var check = new FrameworkElementFactory(typeof(CheckBox));
  check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(PackageRowView.Apply)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
  check.SetBinding(IsEnabledProperty, new Binding(nameof(PackageRowView.Enabled)));
  view.Columns.Add(new GridViewColumn { Header = "", Width = 34, CellTemplate = new DataTemplate { VisualTree = check } });
  foreach (var (label, property, width) in new[] { (T("Row"), nameof(PackageRowView.Line), 56.0), (T("Name"), nameof(PackageRowView.Name), 200.0), (T("Category"), nameof(PackageRowView.Category), 130.0),
   (T("Role"), nameof(PackageRowView.Role), 120.0), (T("Result"), nameof(PackageRowView.Status), 100.0), (T("Note"), nameof(PackageRowView.Note), 320.0) })
   view.Columns.Add(new GridViewColumn { Header = label, DisplayMemberBinding = new Binding(property), Width = width });
  list.View = view;
  var body = new DockPanel();
  if (warnings.Count > 0)
  {
   var notes = SettingsUi.Hint(string.Join("\n", warnings.Take(8)) + (warnings.Count > 8 ? Tn(warnings.Count - 8, "\n… and 1 more note", "\n… and {0:N0} more notes") : ""));
   notes.Foreground = Theme.Brush("Error"); DockPanel.SetDock(notes, Dock.Top); body.Children.Add(notes);
  }
  body.Children.Add(list);
  string Count(TagPackageStatus status) => rows.Count(r => r.Item.Status == status).ToString("N0", CultureInfo.CurrentCulture);
  var frame = new WindowFrame(T("Check tag package"), T("“{0}”: {1} new, {2} present, {3} recognized by alias, ", file, Count(TagPackageStatus.New), Count(TagPackageStatus.Exists), Count(TagPackageStatus.Alias)) +
   Tn(rows.Count(r => r.Item.Status == TagPackageStatus.Conflict),
    "1 conflict, {1} invalid. Only directory entries are imported; media items remain unchanged.",
    "{0} conflicts, {1} invalid. Only directory entries are imported; media items remain unchanged.", Count(TagPackageStatus.Invalid)), body, "",
    scroll: false);
  frame.Add(T("Cancel"), () => DialogResult = false, cancel: true);
  frame.Add(T("Apply"), () => DialogResult = true, primary: true);
  Content = frame;
 }
}
