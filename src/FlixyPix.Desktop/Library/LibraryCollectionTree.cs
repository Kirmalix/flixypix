using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

internal enum CollectionNodeKind { Quick, Set, Collection, Smart }

/// <summary>A row of the collection tree: the quick collection, a collection set, a collection or slideshow, or a smart
/// collection (saved search).</summary>
internal sealed class CollectionNode : INotifyPropertyChanged
{
 public CollectionNodeKind Kind { get; init; }
 public string Name { get; init; } = "";
 public string Glyph { get; init; } = "";
 public string? Hint { get; init; }
 /// <summary>The collection; null for a quick collection not made yet, a set or a smart collection.</summary>
 public Playlist? Collection { get; init; }
 public SavedSearch? Search { get; init; }
 public CollectionSet? Set { get; init; }
 public Guid Id => Collection?.Id ?? Search?.Id ?? Set?.Id ?? Guid.Empty;
 public Guid? ParentId => Collection?.ParentId ?? Search?.ParentId ?? Set?.ParentId;
 public int Order => Collection?.Order ?? Search?.Order ?? Set?.Order ?? 0;
 public ObservableCollection<CollectionNode> Children { get; } = [];
 public bool IsTarget { get; init; }
 public Visibility TargetVisibility => IsTarget ? Visibility.Visible : Visibility.Collapsed;
 /// <summary>The collection shown in the library right now.</summary>
 public bool IsOpen { get; init; }
 public FontWeight Weight => IsOpen ? FontWeights.SemiBold : FontWeights.Normal;
 /// <summary>Depth in the tree; deeper rows get a shorter name, so count and target mark stay in the sidebar.</summary>
 public int Depth { get; set; }
 public double NameWidth => Math.Max(60, TreeWidth - 80 - Depth * 19);
 /// <summary>Width of the tree as laid out; the sidebar can be dragged wider.</summary>
 internal static double TreeWidth { get; set; } = 210;
 internal void WidthChanged() => Changed(nameof(NameWidth));
 private bool expanded = true;
 public bool IsExpanded { get => expanded; set { if (expanded == value) return; expanded = value; Changed(nameof(IsExpanded)); ExpandedChanged?.Invoke(this); } }
 internal Action<CollectionNode>? ExpandedChanged { get; init; }
 private string count = "", fresh = "";
 public string CountLabel { get => count; set { count = value; Changed(nameof(CountLabel)); } }
 public string NewLabel { get => fresh; set { fresh = value; Changed(nameof(NewLabel)); Changed(nameof(NewVisibility)); } }
 public Visibility NewVisibility => fresh.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
 public event PropertyChangedEventHandler? PropertyChanged;
 private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
 public IEnumerable<CollectionNode> Descendants() => Children.SelectMany(child => child.Descendants().Prepend(child));
}

/// <summary>The collection tree after Lightroom (UX plan U7): the quick collection on top, then collection sets, collections and
/// smart collections. Key B, dragging tiles onto a collection and dragging rows onto a set fill and arrange it.</summary>
public partial class MainWindow
{
 private const string CollectionDragFormat = "FlixyPix.CollectionNode";
 private readonly HashSet<Guid> _collapsedSets = [];
 private Point? _collectionDragStart;
 private CollectionNode? _collectionDragNode;
 private bool _collectionTreeSized;

 internal IEnumerable<CollectionNode> CollectionNodes() => (CollectionTree.ItemsSource as IEnumerable<CollectionNode> ?? []).SelectMany(n => n.Descendants().Prepend(n));

 /// <summary>The name shown for a collection; the quick collection has a translated one.</summary>
 internal static string CollectionName(Playlist playlist) => playlist.Kind == PlaylistKind.Quick ? T("Quick collection") : playlist.Name;
 private static int EntryCount(Playlist playlist) => playlist.Entries.Count > 0 ? playlist.Entries.Count : playlist.MediaIds.Count;

 private void ShowCollections(IReadOnlyList<Playlist> playlists, IReadOnlyList<CollectionSet> sets, IReadOnlyList<SavedSearch> searches, Playlist? target)
 {
  if (CollectionTree.ActualWidth > 0) CollectionNode.TreeWidth = CollectionTree.ActualWidth;
  var quick = playlists.FirstOrDefault(p => p.Kind == PlaylistKind.Quick);
  var setIds = sets.Select(s => s.Id).ToHashSet();
  Guid? Parent(Guid? id) => id is { } parent && setIds.Contains(parent) ? parent : null;
  bool IsTarget(Guid? id) => target == null ? id == null || id == quick?.Id : id == target.Id;
  void Expanded(CollectionNode node) { if (node.IsExpanded) _collapsedSets.Remove(node.Id); else _collapsedSets.Add(node.Id); }
  CollectionNode Collection(Playlist p) => new()
  {
   Kind = CollectionNodeKind.Collection, Name = p.Name, Collection = p, Glyph = p.Kind == PlaylistKind.Slideshow ? "" : "",
   IsTarget = IsTarget(p.Id), IsOpen = _playlist?.Id == p.Id, CountLabel = EntryCount(p).ToString("N0"),
  };
  CollectionNode Smart(SavedSearch s) => new() { Kind = CollectionNodeKind.Smart, Name = s.Name, Search = s, Glyph = SmartGlyph(s), Hint = T("Smart collection: stays current by itself") };
  var placed = new HashSet<Guid>();
  List<CollectionNode> ChildrenOf(Guid? parent) =>
  [
   .. sets.Where(s => Parent(s.ParentId) == parent && placed.Add(s.Id)).OrderBy(s => s.Order).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s =>
   {
    var node = new CollectionNode { Kind = CollectionNodeKind.Set, Name = s.Name, Set = s, Glyph = "", IsExpanded = !_collapsedSets.Contains(s.Id), ExpandedChanged = Expanded };
    foreach (var child in ChildrenOf(s.Id)) node.Children.Add(child);
    return node;
   }),
   .. playlists.Where(p => p.Kind != PlaylistKind.Quick && Parent(p.ParentId) == parent).OrderBy(p => p.Order).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(Collection),
   .. searches.Where(s => Parent(s.ParentId) == parent).OrderBy(s => s.Order).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(Smart),
  ];
  var nodes = new List<CollectionNode>
  {
   new() { Kind = CollectionNodeKind.Quick, Name = T("Quick collection"), Collection = quick, Glyph = "", IsTarget = IsTarget(quick?.Id),
    IsOpen = quick != null && _playlist?.Id == quick.Id, CountLabel = (quick == null ? 0 : EntryCount(quick)).ToString("N0"),
    Hint = T("Quick collection: B adds the selected items or takes them out again") },
  };
  nodes.AddRange(ChildrenOf(null));
  void Place(IEnumerable<CollectionNode> level, int depth) { foreach (var node in level) { node.Depth = depth; Place(node.Children, depth + 1); } }
  Place(nodes, 0);
  CollectionTree.ItemsSource = nodes;
  if (!_collectionTreeSized)
  {
   _collectionTreeSized = true;
   CollectionTree.SizeChanged += (_, e) =>
   {
    if (!e.WidthChanged) return;
    CollectionNode.TreeWidth = e.NewSize.Width;
    foreach (var node in CollectionNodes()) node.WidthChanged();
   };
  }
  SavedSearchHint.Visibility = searches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
  CountSmartCollections(nodes.SelectMany(n => n.Descendants().Prepend(n)));
 }

 // ----- Opening -----

 private void OpenCollectionNode(CollectionNode node)
 {
  switch (node.Kind)
  {
   case CollectionNodeKind.Set: node.IsExpanded = !node.IsExpanded; break;
   case CollectionNodeKind.Smart: ApplySavedSearch(node.Search!); break;
   default: OpenCollection(node.Collection ?? _repository.GetQuickCollection()); break;
  }
 }
 private void OpenCollection(Playlist playlist)
 {
  _playlist = playlist; _tag = null; ClearTagFilters(); _tagGroup = null; _advancedExpression = null;
  _category = null; _folder = null; _view = "all"; _anchor = 0; SortChoice.SelectedIndex = 5; ViewTitle.Text = CollectionName(playlist); Refresh();
 }

 private static CollectionNode? NodeAt(object source)
 {
  for (var element = source as DependencyObject; element != null; element = VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element))
  {
   if (element is ToggleButton) return null;
   if (element is TreeViewItem { DataContext: CollectionNode node }) return node;
  }
  return null;
 }

 private void CollectionTreeClicked(object sender, MouseButtonEventArgs e)
 {
  _collectionDragStart = null;
  if (NodeAt(e.OriginalSource) is { } node) OpenCollectionNode(node);
 }

 private void CollectionTreeKeyDown(object sender, KeyEventArgs e)
 {
  if (CollectionTree.SelectedItem is not CollectionNode node) return;
  if (e.Key == Key.Enter) { e.Handled = true; OpenCollectionNode(node); }
  else if (e.Key == Key.F2 && node.Kind != CollectionNodeKind.Quick) { e.Handled = true; RenameCollectionNode(node); }
  else if (e.Key == Key.Delete && node.Kind != CollectionNodeKind.Quick) { e.Handled = true; DeleteCollectionNode(node); }
 }

 private void CollectionTreeRightClicked(object sender, MouseButtonEventArgs e)
 {
  e.Handled = true;
  Menus.Open(null, NodeAt(e.OriginalSource) is { } node ? CollectionNodeMenu(node) : NewCollectionEntries(null), MenuFailed, PlacementMode.MousePoint);
 }

 // ----- Menus -----

 private void NewPlaylist(object sender, RoutedEventArgs e) => Menus.Open(sender as UIElement, NewCollectionEntries(null), MenuFailed);

 private IReadOnlyList<MenuSpec> NewCollectionEntries(Guid? parent) =>
 [
  MenuSpec.Command(T("Collection …"), () => CreatePlaylist(PlaylistKind.Playlist, parent), ""),
  MenuSpec.Command(T("Collection from the selection …"), () => CreatePlaylist(PlaylistKind.Playlist, parent, SelectedMedia), "", enabled: SelectedMedia.Count > 0),
  MenuSpec.Command(T("Smart collection from the current search …"), () => { SaveSearch(parent); Refresh(); }, ""),
  MenuSpec.Command(T("Slideshow …"), () => CreatePlaylist(PlaylistKind.Slideshow, parent), ""),
  MenuSpec.Separator,
  MenuSpec.Command(T("Collection set …"), () => CreateSet(parent), ""),
 ];

 internal IReadOnlyList<MenuSpec> CollectionNodeMenu(CollectionNode node)
 {
  if (node.Kind == CollectionNodeKind.Smart) return [.. SmartCollectionMenu(node)];
  if (node.Kind == CollectionNodeKind.Set)
   return
   [
    MenuSpec.Submenu(T("New in this set"), NewCollectionEntries(node.Id), ""),
    MenuSpec.Separator,
    MenuSpec.Command(T("Rename …"), () => RenameCollectionNode(node), "", "F2"),
    MenuSpec.Submenu(T("Move to set"), MoveEntries(node), ""),
    MenuSpec.Separator,
    MenuSpec.Command(T("Delete set …"), () => DeleteCollectionNode(node), "", T("Del")),
   ];
  var quick = node.Kind == CollectionNodeKind.Quick;
  var count = node.Collection is { } p ? EntryCount(p) : 0;
  var entries = new List<MenuSpec>
  {
   MenuSpec.Command(T("Open"), () => OpenCollectionNode(node), "", T("Enter")),
   MenuSpec.Command(T("Play collection"), () => { OpenCollectionNode(node); PlayCollection(); }, "", enabled: count > 0),
   new MenuCommand(T("Target collection for B"), () => SetTargetCollection(node.IsTarget || quick ? null : node.Collection)) { Icon = "", Checked = node.IsTarget },
   MenuSpec.Separator,
  };
  if (quick)
  {
   entries.Add(MenuSpec.Command(T("Save as collection …"), SaveQuickCollection, "", enabled: count > 0));
   entries.Add(MenuSpec.Command(T("Clear quick collection"), ClearQuickCollection, "", enabled: count > 0));
   return entries;
  }
  entries.Add(MenuSpec.Command(T("Rename …"), () => RenameCollectionNode(node), "", "F2"));
  if (node.Collection!.Kind == PlaylistKind.Slideshow)
   entries.Add(MenuSpec.Command(T("Change name / image duration …"), () => { OpenCollectionNode(node); EditCollection(); }, ""));
  entries.Add(MenuSpec.Submenu(T("Move to set"), MoveEntries(node), ""));
  entries.Add(MenuSpec.Separator);
  entries.Add(MenuSpec.Command(T("Delete collection …"), () => DeleteCollectionNode(node), "", T("Del")));
  return entries;
 }

 /// <summary>The top level and every set the row may move into (not itself or a set inside it), indented by depth.</summary>
 private IEnumerable<MenuSpec> MoveEntries(CollectionNode node)
 {
  var excluded = node.Kind == CollectionNodeKind.Set ? node.Descendants().Append(node).Where(n => n.Kind == CollectionNodeKind.Set).Select(n => n.Id).ToHashSet() : [];
  yield return new MenuCommand(T("Top level"), () => MoveCollectionNode(node, null)) { Checked = node.ParentId == null };
  IEnumerable<(CollectionNode Set, int Depth)> Sets(IEnumerable<CollectionNode> nodes, int depth) =>
   nodes.Where(n => n.Kind == CollectionNodeKind.Set && !excluded.Contains(n.Id)).SelectMany(n => Sets(n.Children, depth + 1).Prepend((n, depth)));
  foreach (var (set, depth) in Sets(CollectionTree.ItemsSource as IEnumerable<CollectionNode> ?? [], 0))
   yield return new MenuCommand(new string(' ', depth * 3) + set.Name, () => MoveCollectionNode(node, set.Id)) { Checked = node.ParentId == set.Id };
 }

 // ----- Changes -----

 private void CreatePlaylist(PlaylistKind kind) => CreatePlaylist(kind, null);
 private void CreatePlaylist(PlaylistKind kind, Guid? parent, IReadOnlyList<MediaItem>? items = null)
 {
  var input = Dialogs.Fields(this, kind == PlaylistKind.Slideshow ? T("New slideshow") : T("New collection"), ("Name", ""));
  if (input == null || string.IsNullOrWhiteSpace(input[0])) return;
  if (kind == PlaylistKind.Slideshow && items?.Any(m => m.Kind != MediaKind.Image) == true) { Message.Show(this, T("Slideshows contain images. For videos and audio, use a playlist.")); return; }
  var ids = items?.Select(m => m.Id).ToList() ?? [];
  OpenCollection(_repository.UpsertPlaylist(new Playlist { Name = input[0].Trim(), Kind = kind, ParentId = parent, MediaIds = ids, Entries = ids.Select(id => new PlaylistEntry(id)).ToList() }));
 }

 private void CreateSet(Guid? parent)
 {
  if (Dialogs.Fields(this, T("New collection set"), (T("Name (a set holds collections, smart collections and further sets)"), "")) is not [var name] || name.Length == 0) return;
  _repository.UpsertCollectionSet(new CollectionSet { Name = name, ParentId = parent });
  if (parent is { } outer) _collapsedSets.Remove(outer);
  Refresh();
 }

 private void RenameCollectionNode(CollectionNode node)
 {
  if (node.Kind == CollectionNodeKind.Quick) return;
  if (Dialogs.Fields(this, T("Rename"), ("Name", node.Name)) is not [var name] || name.Length == 0 || name == node.Name) return;
  switch (node.Kind)
  {
   case CollectionNodeKind.Set: _repository.UpsertCollectionSet(node.Set! with { Name = name }); break;
   case CollectionNodeKind.Smart: _repository.UpsertSavedSearch(node.Search! with { Name = name }); break;
   default:
    var saved = _repository.UpsertPlaylist(node.Collection! with { Name = name });
    if (_playlist?.Id == saved.Id) { _playlist = saved; ViewTitle.Text = saved.Name; }
    break;
  }
  Refresh();
 }

 private void DeleteCollectionNode(CollectionNode node)
 {
  switch (node.Kind)
  {
   case CollectionNodeKind.Set:
    if (Message.Show(this, T("Delete the set “{0}”? What it holds moves up one level.", node.Name), T("Delete set"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
    _repository.DeleteCollectionSet(node.Id); break;
   case CollectionNodeKind.Smart:
    if (Message.Show(this, T("Delete the smart collection “{0}”? The items stay in the catalog.", node.Name), T("Delete smart collection"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
    _repository.DeleteSavedSearch(node.Id); break;
   case CollectionNodeKind.Collection:
    if (Message.Show(this, T("Delete the collection “{0}”? The items stay in the catalog.", node.Name), T("Delete collection"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
    _repository.DeletePlaylist(node.Id);
    if (_playlist?.Id == node.Id) { _playlist = null; ViewTitle.Text = T("All items"); }
    break;
  }
  Refresh();
 }

 /// <summary>Puts the row into a set (or the top level); with <paramref name="before"/> it takes that row's place among its kind.</summary>
 private void MoveCollectionNode(CollectionNode node, Guid? parent, CollectionNode? before = null)
 {
  if (node.Kind == CollectionNodeKind.Quick) return;
  var siblings = CollectionNodes().Where(n => n.Kind == node.Kind && n.ParentId == parent && n.Id != node.Id).OrderBy(n => n.Order).ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
  var place = before != null && before.Kind == node.Kind && siblings.IndexOf(before) is >= 0 and var index ? index : siblings.Count;
  siblings.Insert(place, node);
  // Positions are written for the whole row of siblings, so the new place holds even where names decided the order before.
  for (var order = 0; order < siblings.Count; order++)
  {
   var sibling = siblings[order];
   if (sibling != node && sibling.Order == order && before == null) continue;
   var target = sibling == node ? parent : sibling.ParentId;
   switch (sibling.Kind)
   {
    case CollectionNodeKind.Set: _repository.UpsertCollectionSet(sibling.Set! with { ParentId = target, Order = order }); break;
    case CollectionNodeKind.Smart: _repository.UpsertSavedSearch(sibling.Search! with { ParentId = target, Order = order }); break;
    default: _repository.UpsertPlaylist(sibling.Collection! with { ParentId = target, Order = order }); break;
   }
  }
  if (parent is { } set) _collapsedSets.Remove(set);
  Refresh();
 }

 private void SetTargetCollection(Playlist? collection)
 {
  _repository.SetTargetCollection(collection?.Id);
  Status.Text = T("Target collection for B: {0}", collection == null ? T("Quick collection") : collection.Name);
  Refresh();
 }

 private void SaveQuickCollection()
 {
  var quick = _repository.GetQuickCollection();
  if (Dialogs.Fields(this, T("Save quick collection"), ("Name", "")) is not [var name] || name.Length == 0) return;
  var saved = _repository.UpsertPlaylist(quick with { Id = Guid.NewGuid(), Name = name, Kind = PlaylistKind.Playlist, ParentId = null, Order = 0 });
  if (Message.Show(this, T("Saved as “{0}”. Clear the quick collection now?", name), T("Save quick collection"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
   _repository.UpsertPlaylist(quick with { MediaIds = [], Entries = [] });
  OpenCollection(saved);
 }

 private void ClearQuickCollection()
 {
  var quick = _repository.GetQuickCollection();
  _repository.UpsertPlaylist(quick with { MediaIds = [], Entries = [] });
  Status.Text = T("Quick collection cleared");
  Refresh();
 }

 /// <summary>B puts the selection into the target collection or, when all of it is there already, takes it out again.</summary>
 internal void ToggleTargetCollection()
 {
  var items = SelectedMedia;
  if (items.Count == 0) return;
  var target = _repository.GetTargetCollection();
  if (target.Kind == PlaylistKind.Slideshow && items.Any(m => m.Kind != MediaKind.Image)) { ShowToast(T("Slideshows contain images. For videos and audio, use a playlist."), MessageBoxImage.Warning); return; }
  var (saved, added) = _repository.ToggleInCollection(target.Id, items.Select(m => m.Id).ToList());
  var name = CollectionName(saved);
  Status.Text = added ? Tn(items.Count, "1 item added to “{1}”", "{0:N0} items added to “{1}”", name)
   : Tn(items.Count, "1 item removed from “{1}”", "{0:N0} items removed from “{1}”", name);
  Refresh();
 }

 // ----- Dragging -----

 private void CollectionTreeMouseDown(object sender, MouseButtonEventArgs e)
 {
  _collectionDragNode = NodeAt(e.OriginalSource);
  _collectionDragStart = _collectionDragNode is { Kind: not CollectionNodeKind.Quick } ? e.GetPosition(CollectionTree) : null;
 }

 private void CollectionTreeMouseMove(object sender, MouseEventArgs e)
 {
  if (_collectionDragStart is not { } start || _collectionDragNode is not { } node || e.LeftButton != MouseButtonState.Pressed) return;
  var offset = e.GetPosition(CollectionTree) - start;
  if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance) return;
  _collectionDragStart = null;
  DragDrop.DoDragDrop(CollectionTree, new DataObject(CollectionDragFormat, node), DragDropEffects.Move);
  e.Handled = true;
 }

 private void CollectionTreeDragOver(object sender, DragEventArgs e)
 {
  var target = NodeAt(e.OriginalSource);
  e.Effects = e.Data.GetDataPresent(MediaDragFormat) ? target is { Kind: CollectionNodeKind.Quick or CollectionNodeKind.Collection } ? DragDropEffects.Copy : DragDropEffects.None
   : e.Data.GetData(CollectionDragFormat) is CollectionNode moved && moved != target ? DragDropEffects.Move : DragDropEffects.None;
  e.Handled = true;
 }

 private void CollectionTreeDrop(object sender, DragEventArgs e)
 {
  e.Handled = true;
  var target = NodeAt(e.OriginalSource);
  if (e.Data.GetData(MediaDragFormat) is string[] ids)
  {
   if (target is not { Kind: CollectionNodeKind.Quick or CollectionNodeKind.Collection }) return;
   var items = ids.Select(id => _repository.GetMedia(Guid.Parse(id))).OfType<MediaItem>().ToList();
   if (items.Count > 0) AddToCollection(target.Collection ?? _repository.GetQuickCollection(), items, null);
   return;
  }
  if (e.Data.GetData(CollectionDragFormat) is not CollectionNode moved || moved == target) return;
  // Onto a set: into it. Onto another row: beside it, at its place. Onto free space: to the top level.
  if (target == null) MoveCollectionNode(moved, null);
  else if (target.Kind == CollectionNodeKind.Set) MoveCollectionNode(moved, target.Id);
  else if (target.Kind != CollectionNodeKind.Quick) MoveCollectionNode(moved, target.ParentId, target);
 }

 // ----- Details -----

 /// <summary>"In collections" in the details: a chip per collection holding the medium, opening it.</summary>
 private void ShowMemberships(MediaItem? item)
 {
  MembershipChips.Children.Clear();
  var holding = item == null ? [] : _repository.GetPlaylists().Where(p => p.MediaIds.Contains(item.Id)).OrderBy(p => p.Kind != PlaylistKind.Quick).ThenBy(CollectionName, StringComparer.CurrentCultureIgnoreCase).ToList();
  MembershipSection.Visibility = holding.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
  foreach (var playlist in holding)
  {
   var chip = new Button { Content = CollectionName(playlist), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 5, 5), FontSize = 12, ToolTip = T("Open the collection") };
   chip.Click += (_, _) => OpenCollection(playlist);
   MembershipChips.Children.Add(chip);
  }
 }
}
