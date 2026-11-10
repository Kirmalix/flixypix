using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Main window chrome: header menus, navigation highlight, tile size, quick actions on tiles,
/// immediate rating/favourite/watch-later edits, saved searches in the sidebar and library shortcuts.</summary>
public partial class MainWindow
{
 private bool _showingDetails;

 private void InitializeMainView()
 {
  EditRating.Describe = value => value == 0 ? T("Unrated") : T("{0:0.#} out of 5 stars", value / 2d);
  EditRating.Changed += () => { if (!_showingDetails) QuickSave(item => item with { Rating = EditRating.Value }, EditRating.Value == 0 ? T("Rating removed") : T("Rating saved")); };
  SetTileWidth(_settings.TileWidth);
  ViewPopup.Opened += (_, _) => SyncTileSlider();
  ViewPopup.Closed += (_, _) => SaveViewPreferences();
 }

 /// <summary>Opens a menu below <paramref name="sender"/> instead of at the mouse pointer.</summary>
 private ContextMenu MenuBelow(object sender, params (string Label, Action Action)[] actions)
 {
  return OpenMenu(sender as UIElement, actions);
 }
 private ContextMenu MenuBelow(object sender, IEnumerable<MenuSpec> items) => Menus.Open(sender as UIElement, items, MenuFailed);
 private void AddMenu(object sender, RoutedEventArgs e) => MenuBelow(sender,
  (T("Files …"), () => ImportFiles(this, new RoutedEventArgs())), (T("Folder with subfolders …"), () => ImportFolder(this, new RoutedEventArgs())),
  (T("Image archive (ZIP/RAR) …"), ImportImageArchive), (T("Media folders …"), ShowLibraryFolders), (T("Import library from EazyFlixPix 4 …"), ImportOriginalDatabase));
 private void ClearSearch(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }

 /// <summary>Highlights the library entry that matches what the grid shows; collections, folders and tags clear it.</summary>
 private void UpdateNavigation()
 {
  var active = _view == "performers" || EntityRole == TagRole.Performer ? "gallery-performers" : _view == "studios" || EntityRole == TagRole.Studio ? "gallery-studios" : InFolderView ? FolderView : _playlist == null && _folder == null && _tagGroup == null && _tag == null && _advancedExpression == null ? _view : null;
  foreach (var entry in Logicals<RadioButton>(NavItems)) entry.IsChecked = (string)entry.Tag == active;
  // An entry that is hidden for lack of content stays while it is open, so the view never loses its place.
  if (_view == "inbox") InboxNav.Visibility = Visibility.Visible;
  if (_view == "Audio") AudioNav.Visibility = Visibility.Visible;
  UpdateBrowsePanel(); RenderFolderNavigation(); RenderEntityHead();
 }

 /// <summary>The review inbox shows once reviewing is in use (or as set), with its open items as a badge; audio only
 /// when the library holds audio.</summary>
 private void ApplyNavigationEntries(NavigationCounts counts, ReviewInboxMode mode)
 {
  var inbox = counts.ShowsReviewInbox(mode) || _view == "inbox";
  InboxNav.Visibility = inbox ? Visibility.Visible : Visibility.Collapsed;
  SidebarCard.SetBadge(InboxNav, counts.Inbox > 0 ? counts.Inbox.ToString("N0") : null);
  AudioNav.Visibility = counts.Audio > 0 || _view == "Audio" ? Visibility.Visible : Visibility.Collapsed;
 }

 /// <summary>A list inside the sidebar scrolls itself up to its end; from there the wheel moves the whole sidebar on,
 /// so the pointer never gets stuck in a nested list.</summary>
 private void NestedListWheel(object sender, MouseWheelEventArgs e)
 {
  if (e.Handled || Keyboard.Modifiers != ModifierKeys.None || sender is not DependencyObject list) return;
  var inner = Visuals<ScrollViewer>(list).FirstOrDefault();
  var atEdge = inner == null || inner.ScrollableHeight <= 0 || e.Delta > 0 && inner.VerticalOffset <= 0 || e.Delta < 0 && inner.VerticalOffset >= inner.ScrollableHeight;
  if (!atEdge) return;
  e.Handled = true;
  var lines = SystemParameters.WheelScrollLines;
  var distance = lines < 0 ? SidebarScroll.ViewportHeight : lines * 16;
  Motion.GlideBy(SidebarScroll, -e.Delta / 120d * distance);
 }

 /// <summary>Narrowest and widest tile the size control allows.</summary>
 private const int MinTileWidth = 140, MaxTileWidth = 480;
 private double _tileWidth = 200;
 private bool _syncingTileSlider;
 /// <summary>The chosen smallest tile width, as saved with the view; the tiles stretch to fill the row.</summary>
 private int TileWidthSetting => (int)Math.Round(_tileWidth);
 /// <summary>Width the tiles share: the grid as laid out, or before that the list itself.</summary>
 private double TileArea => Tiles is { ViewportWidth: > 0 } panel ? panel.ViewportWidth : MediaList is { ActualWidth: > 0 } list ? list.ActualWidth : 1200;
 /// <summary>Fewest and most columns that keep the tiles between <see cref="MinTileWidth"/> and <see cref="MaxTileWidth"/>.</summary>
 internal static (int Fewest, int Most) TileColumnRange(double area)
 {
  var fewest = Math.Max(1, (int)(area / MaxTileWidth));
  return (fewest, Math.Max(fewest, (int)(area / MinTileWidth)));
 }
 /// <summary>Smallest tile width that gives exactly <paramref name="columns"/> columns in <paramref name="area"/>.</summary>
 internal static double TileWidthForColumns(double area, int columns) => Math.Clamp(Math.Floor(area / Math.Max(1, columns)), MinTileWidth, MaxTileWidth);

 private void SetTileWidth(double width)
 {
  _tileWidth = Math.Clamp(width, MinTileWidth, MaxTileWidth);
  // The panel fits as many columns of at least this width as possible and stretches them to the full width.
  // Opening a side pane or resizing the window a little keeps the columns and only scales the tiles, while they stay
  // close to the chosen size; a larger change lays the grid out for the chosen size again.
  if (MediaList != null)
  {
   VirtualizingTilePanel.SetTileWidthRange(MediaList, new Size(Math.Max(MinTileWidth, _tileWidth * .75), Math.Min(MaxTileWidth, _tileWidth * 1.5)));
   VirtualizingTilePanel.SetTargetTileWidth(MediaList, TileWidthSetting);
  }
  SyncTileSlider(Math.Max(1, (int)(TileArea / Math.Max(40, _tileWidth))));
 }

 /// <summary>Changes the tiles by whole columns, so every step shows; more columns make smaller tiles.</summary>
 private void SetTileColumns(int columns)
 {
  var area = TileArea; var (fewest, most) = TileColumnRange(area);
  Tiles?.ChooseColumnsAgain();
  SetTileWidth(TileWidthForColumns(area, Math.Clamp(columns, fewest, most)));
 }
 private int CurrentTileColumns => Tiles is { Columns: > 0 } panel && !panel.SingleColumn ? panel.Columns : Math.Max(1, (int)(TileArea / Math.Max(40, _tileWidth)));

 /// <summary>The slider has one step per possible column count for the current width; right means larger tiles.</summary>
 /// <param name="columns">The columns about to be laid out; otherwise those of the grid as it is.</param>
 private void SyncTileSlider(int? columns = null)
 {
  if (TileSize == null) return;
  var (fewest, most) = TileColumnRange(TileArea);
  var shown = Math.Clamp(columns ?? CurrentTileColumns, fewest, most);
  _syncingTileSlider = true;
  try { TileSize.Minimum = 0; TileSize.Maximum = Math.Max(1, most - fewest); TileSize.Value = most - shown; TileSize.IsEnabled = most > fewest; }
  finally { _syncingTileSlider = false; }
  var label = Tn(shown, "1 column", "{0} columns");
  TileColumnsLabel.Text = label; System.Windows.Automation.AutomationProperties.SetHelpText(TileSize, label);
 }
 private void TileSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
 {
  // The slider reports its first value while the window is still being built, before the list exists.
  if (_syncingTileSlider || MediaList == null) return;
  SetTileColumns(TileColumnRange(TileArea).Most - (int)Math.Round(e.NewValue));
 }
 private void TileSizeCommitted(object sender, RoutedEventArgs e) => SaveViewPreferences();

 private static MediaCard? CardOf(object sender) => (sender as FrameworkElement)?.DataContext as MediaCard;
 private async void TilePlay(object sender, RoutedEventArgs e) { if (CardOf(sender) is { } card) { e.Handled = true; StopSlideshow(); await Play(card.Item); } }
 private void TileToggleWatchLater(object sender, RoutedEventArgs e)
 {
  if (CardOf(sender) is not { } card) return; e.Handled = true;
  var item = _repository.GetMedia(card.Item.Id) ?? card.Item; ApplyImmediate([item.Id], ImmediateEditFields.WatchLater, current => current with { WatchLater = !current.WatchLater },
   item.WatchLater ? T("“{0}” removed from Watch later", card.Title) : T("“{0}” marked for later", card.Title)); Refresh();
 }
 private void TileToggleFavorite(object sender, RoutedEventArgs e)
 {
  if (CardOf(sender) is not { } card) return; e.Handled = true;
  if (sender is FrameworkElement heart) Motion.Pop(heart);
  var item = _repository.GetMedia(card.Item.Id) ?? card.Item; ApplyImmediate([item.Id], ImmediateEditFields.Favorite, current => current with { IsFavorite = !current.IsFavorite },
   item.IsFavorite ? T("“{0}” no longer pinned as favorite", card.Title) : T("“{0}” pinned as favorite", card.Title)); Refresh();
 }

 /// <summary>Saves one field of the selected medium at once while unsaved title and description edits stay in the boxes.</summary>
 private void QuickSave(Func<MediaItem, MediaItem> change, string message)
 {
  if (Selected is not { } selected || SelectedMedia.Count > 1) return;
  var item = _repository.GetMedia(selected.Id) ?? selected;
  ApplyImmediate([item.Id], ImmediateEditFields.Rating | ImmediateEditFields.Favorite | ImmediateEditFields.WatchLater, change, message); Refresh(item.Id);
 }
 private void FavoriteClicked(object sender, RoutedEventArgs e) => QuickSave(item => item with { IsFavorite = EditFavorite.IsChecked == true }, EditFavorite.IsChecked == true ? T("Pinned as favorite") : T("Favorite unpinned"));
 private void WatchLaterClicked(object sender, RoutedEventArgs e) => QuickSave(item => item with { WatchLater = EditWatchLater.IsChecked == true }, EditWatchLater.IsChecked == true ? T("Marked for later") : T("No longer marked for later"));
 /// <summary>Digits 0–5 rate every selected medium in whole stars, as in common photo tools.</summary>
 private void RateSelection(int stars) => RateMedia(SelectedMedia, stars);

 private static string ViewName(string view) => view switch { "publication-groups" => T("Publication groups"), "image-sets" => T("Image sets"), "inbox" => T("Review inbox"), "performers" or "gallery-performers" => T("Performers"), "studios" or "gallery-studios" => T("Studios"), "home" => T("Home"), "wall" => T("Scene wall"), "order" => T("Organize"), "jobs" => T("Media jobs"), "folders" => T("All folders"), "Video" => T("Videos"), "Image" => T("Images"), "Audio" => T("Audio"), "favorites" => T("Favorites"), "watchlater" => T("Watch later"), _ => T("All items") };
 private void CopyPathClicked(object sender, RoutedEventArgs e) { if (Selected is { } item) { Clipboard.SetText(item.Path); Status.Text = T("Path copied"); } }
 private void RevealClicked(object sender, RoutedEventArgs e) => Reveal();
}
