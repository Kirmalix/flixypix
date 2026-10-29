using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private string? _category;
 private string? _tagGroup;
 private string? _folder;
 private string _folderFingerprint = "";
 private string _tagFingerprint = "";
 private DataTemplate? _tileTemplate;
 private ItemsPanelTemplate? _tilePanel;
 private IReadOnlyList<MediaItem> _currentImages = [];
 private readonly bool _persistPreferences;
 private MediaQueueWindow? _queueWindow;
 private bool _initializingBrowsing;
 private sealed record CategoryOption(string? Value, long? Count = null) { public string Label => (Value ?? T("All categories")) + CountText(Count); }
 /// <summary>A media count after a name in the sidebar; empty when counts are off.</summary>
 private static string CountText(long? count) => count is { } value ? "  " + value.ToString("N0", CultureInfo.CurrentCulture) : "";
 private sealed class BrowseNode(string label, string? path = null, string? category = null, string? tag = null) : INotifyPropertyChanged
 {
  public string Label { get; } = label;
  public string? Path { get; } = path;
  public string? Category { get; } = category;
  public string? Tag { get; } = tag;
  public List<BrowseNode> Children { get; } = [];
  private string _count = "";
  /// <summary>Changes in place, so a new count keeps the tree's expanded folders.</summary>
  public string Count { get => _count; set { if (_count == value) return; _count = value; PropertyChanged?.Invoke(this, new(nameof(Count))); } }
  public event PropertyChangedEventHandler? PropertyChanged;
  public IEnumerable<BrowseNode> Descendants() => Children.SelectMany(child => child.Descendants().Prepend(child));
 }
 private void InitializeBrowsing()
 {
  _tileTemplate = MediaList.ItemTemplate; _tilePanel = MediaList.ItemsPanel;
  _initializingBrowsing = true;
  const string treeTemplate = "<HierarchicalDataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' ItemsSource='{Binding Children}'><TextBlock Foreground='{DynamicResource TextSoft}' ToolTip='{Binding Path}' Margin='1,4'>"
   + "<Run Text='{Binding Label, Mode=OneWay}'/><Run Text='{Binding Count, Mode=OneWay}' Foreground='{DynamicResource Muted}' FontSize='11'/></TextBlock></HierarchicalDataTemplate>";
  Folders.ItemTemplate = (HierarchicalDataTemplate)XamlReader.Parse(treeTemplate); CategorizedTags.ItemTemplate = (HierarchicalDataTemplate)XamlReader.Parse(treeTemplate);
  CategoryFilter.DisplayMemberPath = "Label";
  ViewChoice.SelectedIndex = Math.Clamp(_settings.ViewMode, 0, 2); SortChoice.SelectedIndex = Math.Clamp(_settings.SortOrder, 0, SortChoice.Items.Count - 1);
  SetSidebar(!_settings.SidebarCollapsed); SetDetails(!_settings.DetailsCollapsed); ApplyDetailsLayout();
  ViewButtonText.Text = Tc("library", "View");
  _initializingBrowsing = false;
  ApplyViewMode();
 }
 private void ViewChanged(object sender, SelectionChangedEventArgs e) { if (_tileTemplate == null) return; ApplyViewMode(); SaveViewPreferences(); }
 private void ApplyViewMode()
 {
  TileSizePanel.Visibility = ViewChoice.SelectedIndex != 1 ? Visibility.Visible : Visibility.Collapsed;
  UpdateVideoWall();
  // The other view gets a new panel that starts at the top; keep the medium that was at the top.
  var anchor = _anchor;
  if (anchor > 0) Dispatcher.BeginInvoke(() => Tiles?.ScrollToIndex(anchor), System.Windows.Threading.DispatcherPriority.Loaded);
  // The video wall is the tile view with clips playing.
  if (ViewChoice.SelectedIndex != 1) { MediaList.ItemTemplate = _tileTemplate; MediaList.ItemsPanel = _tilePanel; return; }
  var rows = new FrameworkElementFactory(typeof(VirtualizingTilePanel)); rows.SetValue(VirtualizingTilePanel.SingleColumnProperty, true); MediaList.ItemsPanel = new ItemsPanelTemplate(rows);
  MediaList.ItemTemplate = (DataTemplate)XamlReader.Parse("""
  <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:local="clr-namespace:FlixyPix.Desktop;assembly=FlixyPix"><Border Background="{DynamicResource CardBackground}" Padding="9" CornerRadius="6"><Grid><Grid.ColumnDefinitions><ColumnDefinition Width="76"/><ColumnDefinition Width="*"/><ColumnDefinition Width="105"/></Grid.ColumnDefinitions><Grid Height="52" Width="68" Background="{DynamicResource MediaBackground}" ClipToBounds="True" local:Privacy.Blur="True"><TextBlock Text="{Binding Icon}" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" Foreground="{DynamicResource IconMuted}" VerticalAlignment="Center" HorizontalAlignment="Center" FontSize="20"/><Image Source="{Binding Thumbnail}" Stretch="UniformToFill"/></Grid><StackPanel Grid.Column="1" Margin="10,0"><TextBlock Text="{Binding Title}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/><TextBlock Text="{Binding Subtitle}" Foreground="{DynamicResource Muted}" FontSize="11" Margin="0,5,0,0"/><TextBlock Text="{Binding Path}" Foreground="{DynamicResource Subtle}" FontSize="10" TextTrimming="CharacterEllipsis"/></StackPanel><StackPanel Grid.Column="2"><TextBlock Text="{Binding KindLabel}" Foreground="{DynamicResource Muted}" FontSize="10"/><TextBlock Text="{Binding DurationLabel}" Foreground="{DynamicResource Muted}" FontSize="11" Margin="0,3,0,0"/><TextBlock Text="{Binding Stars}" Foreground="{DynamicResource Gold}" Margin="0,5,0,0" FontSize="11"/></StackPanel></Grid></Border></DataTemplate>
  """);
 }
 private void SortChanged(object sender, SelectionChangedEventArgs e) { if (_repository == null || _refreshing || _initializingBrowsing) return; _anchor = 0; SaveViewPreferences(); Refresh(); }
 private void SaveViewPreferences()
 {
  if (_settings == null || _initializingBrowsing) return;
  _settings = _settings with { ViewMode = ViewChoice.SelectedIndex, SortOrder = SortChoice.SelectedIndex, TileWidth = (int)Math.Round(TileSize.Value), LastView = _view,
   SidebarCollapsed = _sidebarAutoFolded ? _settings.SidebarCollapsed : !SidebarExpanded, SidebarWidth = PaneWidths().Sidebar, DetailsWidth = PaneWidths().Details,
   DetailsCollapsed = _detailsAutoFolded || _view == "home" ? _settings.DetailsCollapsed : !DetailsExpanded };
  if (_persistPreferences) (_settings with { DataDirectory = _pendingDataDirectory ?? _settings.DataDirectory }).Save();
 }
 private void CategoryChanged(object sender, SelectionChangedEventArgs e) { if (_refreshing || _repository == null) return; _category = (CategoryFilter.SelectedItem as CategoryOption)?.Value; _anchor = 0; UpdateFilterBar(); RenderTagFilterChips(); Refresh(); }
 private void FolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e) { if (_refreshing || e.NewValue is not BrowseNode node) return; _folder = node.Path; _playlist = null; _anchor = 0; ViewTitle.Text = _folder == null ? T("All items") : node.Label; Refresh(); }
 private void CategorizedTagSelected(object sender, RoutedPropertyChangedEventArgs<object> e) {
  if (_refreshing || e.NewValue is not BrowseNode node) return; _tagGroup = node.Category; _tag = node.Tag; _playlist = null; _anchor = 0;
  ViewTitle.Text = node.Tag ?? node.Category ?? T("All tags"); Refresh(); }
 private string _tagListFingerprint = "";
 /// <param name="FolderCounts">Media per folder path including subfolders ("" is all folders); empty when counts are off.</param>
 /// <param name="TagCategoryCounts">Media per tag category; empty when counts are off.</param>
 private sealed record BrowseSnapshot(IReadOnlyList<CategoryOption> Categories, TagListEntry[] Tags, string TagListFingerprint, string FolderFingerprint, Func<BrowseNode[]> FolderTree, string TagFingerprint, BrowseNode[] CategorizedTags,
  IReadOnlyDictionary<string, TagRole> CategoryRoles, RoleName[] RoleNames, IReadOnlyDictionary<string, long> FolderCounts, IReadOnlyDictionary<string, long> TagCategoryCounts);
 // Runs off the UI thread: large catalogs carry thousands of vocabulary tags and folders.
 private static BrowseSnapshot PrepareBrowsing(CatalogFacets facets, CatalogPreferences preferences, IReadOnlyList<TagUsage> usage)
 {
  var general = preferences.General;
  var categoryCounts = general.ShowCategoryCounts ? facets.CategoryCounts : null;
  var categories = facets.Categories.Select(c => new CategoryOption(c, categoryCounts?.GetValueOrDefault(c))).Prepend(new CategoryOption(null)).ToList();
  var tags = TagEntries(usage, preferences);
  var directories = facets.Folders.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
  var folderCounts = general.ShowFolderCounts && facets.FolderCounts is { } direct ? FolderTotals(direct) : new Dictionary<string, long>();
  // Tag categories are grouped without regard to case in the sidebar, so their counts are as well.
  var tagCategoryCounts = general.ShowTagCategoryCounts && facets.TagCategoryCounts is { } perCategory
   ? perCategory.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(p => p.Value), StringComparer.OrdinalIgnoreCase) : new Dictionary<string, long>();
  var vocabulary = facets.CategorizedTags.SelectMany(pair => pair.Value.Select(tag=>(Category:pair.Key,Tag:tag))).Concat(preferences.TagVocabulary.Select(t=>(Category:t.Category,Tag:t.Tag)))
   .GroupBy(t=>t.Category,StringComparer.OrdinalIgnoreCase).Select(g=>(Name:g.Key,Tags:g.Select(t=>t.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray())).OrderBy(g=>g.Name,StringComparer.OrdinalIgnoreCase).ToArray();
  var roles = vocabulary.ToDictionary(g => g.Name, g => TagRoles.Of(preferences, g.Name), StringComparer.OrdinalIgnoreCase);
  var categorized = vocabulary.Select(group => { var node = new BrowseNode(group.Name, category: group.Name); node.Children.AddRange(group.Tags.Select(tag => new BrowseNode(tag, category: group.Name, tag: tag))); return node; }).ToArray();
  return new(categories, tags, string.Join('\n', tags.Select(t => $"{t.Tag}\t{t.Count}\t{t.IsFavorite}")), string.Join('\n', directories), () => FolderTree(directories),
   string.Join("|", vocabulary.Select(g => g.Name + ":" + string.Join(",", g.Tags))), categorized, roles, RoleNames(vocabulary, roles, preferences, tags), folderCounts, tagCategoryCounts);
 }
 /// <summary>Media of each folder with all its subfolders, from the counts of media directly in a folder; "" holds all.</summary>
 private static Dictionary<string, long> FolderTotals(IReadOnlyDictionary<string, long> direct)
 {
  var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
  foreach (var (folder, count) in direct)
  {
   totals[""] = totals.GetValueOrDefault("") + count;
   for (var current = folder; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
    totals[current] = totals.GetValueOrDefault(current) + count;
  }
  return totals;
 }
 private static BrowseNode[] FolderTree(string[] directories)
 {
  var root = new BrowseNode(T("All folders"));
  var index = new Dictionary<string, BrowseNode>(StringComparer.OrdinalIgnoreCase);
  foreach (var directory in directories)
  {
   var path = directory; var drive = Path.GetPathRoot(path) ?? ""; var segments = path[drive.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
   var parent = root; var current = drive;
   foreach (var segment in new[] { drive }.Concat(segments))
   {
    if (string.IsNullOrEmpty(segment)) continue;
    current = segment == drive ? drive : Path.Combine(current, segment);
    if (!index.TryGetValue(current, out var child)) { child = new BrowseNode(segment.TrimEnd('\\'), current); parent.Children.Add(child); index[current] = child; }
    parent = child;
   }
  }
  // Compress common prefixes so the selected library folders stay reachable in a narrow sidebar.
  var roots = root.Children;
  while (roots.Count == 1 && roots[0].Children.Count == 1) roots = roots[0].Children;
  var all = new BrowseNode(T("All folders")); all.Children.AddRange(roots); return [all];
 }
 private void ApplyBrowsing(BrowseSnapshot browse)
 {
  CategoryFilter.ItemsSource = browse.Categories; CategoryFilter.SelectedItem = browse.Categories.FirstOrDefault(c => c.Value == _category) ?? browse.Categories[0];
  if (browse.TagListFingerprint != _tagListFingerprint) { _tagListFingerprint = browse.TagListFingerprint; _tagEntries = browse.Tags; RenderTagList(); }
  if (browse.FolderFingerprint != _folderFingerprint) { _folderFingerprint = browse.FolderFingerprint; Folders.ItemsSource = browse.FolderTree(); }
  if (browse.TagFingerprint != _tagFingerprint) { _tagFingerprint = browse.TagFingerprint; CategorizedTags.ItemsSource = browse.CategorizedTags; }
  // Counts change with every import or removal; they are set on the nodes shown, which keeps expanded folders open.
  foreach (var node in (Folders.ItemsSource as IEnumerable<BrowseNode> ?? []).SelectMany(root => root.Descendants().Prepend(root)))
   node.Count = browse.FolderCounts.TryGetValue(node.Path ?? "", out var count) ? CountText(count) : "";
  foreach (var node in CategorizedTags.ItemsSource as IEnumerable<BrowseNode> ?? [])
   node.Count = node.Category is { } name && browse.TagCategoryCounts.TryGetValue(name, out var count) ? CountText(count) : "";
  _categoryRoles = browse.CategoryRoles; _roleNames = browse.RoleNames;
 }
 private static bool IsWithinFolder(string path, string folder)
 {
  try { var full = Path.GetFullPath(path); var selected = Path.GetFullPath(folder); if (!selected.EndsWith(Path.DirectorySeparatorChar)) selected += Path.DirectorySeparatorChar; return full.StartsWith(selected, StringComparison.OrdinalIgnoreCase); } catch { return false; }
 }
 private void EditCategorizedTags(object sender, RoutedEventArgs e)
 {
  if (Selected is not { } item) return;
  var initial = string.Join("; ", item.TagCategories.Select(pair => pair.Key + ": " + string.Join(", ", pair.Value)));
  var values = Dialogs.Fields(this, T("Tags by category"), (T("Format: People: Anna, Tom; Places: Berlin (empty = remove assignments)"), initial));
  if (values == null) return;
  var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
  foreach (var group in values[0].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
  {
   var parts = group.Split(':', 2, StringSplitOptions.TrimEntries);
   if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0])) { Message.Show(this, T("Each category needs a name, a colon and tags separated by commas.")); return; }
   var tags = parts[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
   if (groups.ContainsKey(parts[0])) { Message.Show(this, T("A category may appear only once.")); return; }
   if (tags.Count > 0) groups[parts[0]] = tags;
  }
  _repository.Upsert(item with { TagCategories = groups }); Refresh(item.Id); Status.Text = T("Tag categories saved · previous flat tags are kept");
 }
 private async void SelectCustomThumbnail()
 {
  if (Selected is not { } item) return;
  var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose custom thumbnail"), Filter = MediaFormats.ImageDialogFilter };
  if (picker.ShowDialog(this) == true) { try { await SetCustomThumbnail(item, picker.FileName); Refresh(item.Id); } catch(Exception error) { Message.Show(this,error.Message,T("Thumbnail"),MessageBoxButton.OK,MessageBoxImage.Warning); } }
 }
 private async Task SetCustomThumbnail(MediaItem item, string source)
 {
  if (new FileInfo(source).Length > CatalogRepository.MaximumAssetBytes) throw new IOException(T("The image file exceeds 32 MiB."));
  if (MediaCard.LoadBitmap(source) == null) throw new InvalidOperationException(T("The chosen image cannot be read."));
  var asset = _repository.UpsertMediaAsset(new() { MediaId = item.Id, Data = File.ReadAllBytes(source), Kind = MediaAssetKind.Thumbnail, IsCustom = true, Label = Path.GetFileNameWithoutExtension(source) });
  var target = _repository.MaterializeMediaAsset(asset,Path.Combine(_settings.DataDirectory,"media-assets"));
  _repository.Upsert(item with { ThumbnailPath = target, Extra = new(item.Extra) { ["thumbnail.assetId"] = asset.Id.ToString() } });
 }
 private void FavoriteThresholds()
 {
  var preferences = _repository.GetPreferences();
  var input = Dialogs.Fields(this, T("Favorites by rating"), (T("Video · minimum rating 0–10"),
   preferences.VideoFavoriteMinimumRating.ToString()), (T("Images · minimum rating 0–10"), preferences.ImageFavoriteMinimumRating.ToString()),
   (T("Audio · minimum rating 0–10"), preferences.AudioFavoriteMinimumRating.ToString()));
  if (input == null) return;
  if (!int.TryParse(input[0], out var video) || !int.TryParse(input[1], out var image) || !int.TryParse(input[2], out var audio) || new[] {
   video, image, audio }.Any(v => v is < 0 or > 10)) { Message.Show(this,
   T("Please enter whole ratings from 0 to 10. 6 equals three stars.")); return; }
  _repository.SavePreferences(preferences with { VideoFavoriteMinimumRating = video, ImageFavoriteMinimumRating = image, AudioFavoriteMinimumRating = audio }); Refresh();
 }
 private void ToggleFavorite() { if (Selected is { } item) ToggleFavorite(item); }
 /// <summary>Switches the favorite state through the rating threshold; returns the new state.</summary>
 internal bool ToggleFavorite(MediaItem item)
 {
  item = _repository.GetMedia(item.Id) ?? item;
  var preferences = _repository.GetPreferences();
  var threshold = item.Kind switch { MediaKind.Image => preferences.ImageFavoriteMinimumRating, MediaKind.Video => preferences.VideoFavoriteMinimumRating, _ => preferences.AudioFavoriteMinimumRating };
  var favorite = !_repository.IsFavorite(item);
  _repository.Upsert(item with { Rating = favorite ? threshold : 0, IsFavorite = false }); Refresh(item.Id);
  return favorite;
 }
 private void QueueClicked(object sender, RoutedEventArgs e) => OpenQueue();
 private void OpenQueue()
 {
  if (_queueWindow != null) { _queueWindow.Activate(); return; }
  try
  {
   var inputs = _playlist?.MediaIds.Select(_repository.GetMedia).OfType<MediaItem>().Select(i => i.Path).ToList() ?? [];
   _queueWindow = new MediaQueueWindow(this, _settings.DataDirectory, _tools, Selected?.Path, inputs, ApplyScenePreviewResults);
   _queueWindow.Closed += (_, _) => _queueWindow = null; _queueWindow.Show();
  }
  catch (Exception ex) { Message.Show(this, ex.Message, T("Media jobs"), MessageBoxButton.OK, MessageBoxImage.Warning); }
 }
}
