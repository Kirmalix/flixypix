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
  TileSize.Value = Math.Clamp(_settings.TileWidth, (int)TileSize.Minimum, (int)TileSize.Maximum);
  ApplyTileSize(TileSize.Value);
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
  var active = _playlist == null && _folder == null && _tagGroup == null && _tag == null && _advancedExpression == null ? _view : null;
  foreach (var entry in NavItems.Children.OfType<RadioButton>()) entry.IsChecked = (string)entry.Tag == active;
 }

 private void TileSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyTileSize(e.NewValue);
 private void TileSizeCommitted(object sender, MouseEventArgs e) => SaveViewPreferences();
 private void ApplyTileSize(double width)
 {
  // The slider sets the smallest tile; the panel fits as many columns as possible and stretches them to the full width.
  // The slider reports its first value while the window is still being built, before the list exists.
  if (MediaList != null) VirtualizingTilePanel.SetTargetTileWidth(MediaList, Math.Round(width));
 }

 private static MediaCard? CardOf(object sender) => (sender as FrameworkElement)?.DataContext as MediaCard;
 private async void TilePlay(object sender, RoutedEventArgs e) { if (CardOf(sender) is { } card) { e.Handled = true; StopSlideshow(); await Play(card.Item); } }
 private void TileToggleWatchLater(object sender, RoutedEventArgs e)
 {
  if (CardOf(sender) is not { } card) return; e.Handled = true;
  var item = _repository.GetMedia(card.Item.Id) ?? card.Item; _repository.Upsert(item with { WatchLater = !item.WatchLater });
  Status.Text = item.WatchLater ? T("“{0}” removed from Watch later", card.Title) : T("“{0}” marked for later", card.Title); Refresh();
 }
 private void TileToggleFavorite(object sender, RoutedEventArgs e)
 {
  if (CardOf(sender) is not { } card) return; e.Handled = true;
  var item = _repository.GetMedia(card.Item.Id) ?? card.Item; _repository.Upsert(item with { IsFavorite = !item.IsFavorite });
  Status.Text = item.IsFavorite ? T("“{0}” no longer pinned as favorite", card.Title) : T("“{0}” pinned as favorite", card.Title); Refresh();
 }

 /// <summary>Saves one field of the selected medium at once while unsaved title and description edits stay in the boxes.</summary>
 private void QuickSave(Func<MediaItem, MediaItem> change, string message)
 {
  if (Selected is not { } selected || SelectedMedia.Count > 1) return;
  var item = _repository.GetMedia(selected.Id) ?? selected;
  _repository.Upsert(change(item)); Status.Text = message; Refresh(item.Id);
 }
 private void FavoriteClicked(object sender, RoutedEventArgs e) => QuickSave(item => item with { IsFavorite = EditFavorite.IsChecked == true }, EditFavorite.IsChecked == true ? T("Pinned as favorite") : T("Favorite unpinned"));
 private void WatchLaterClicked(object sender, RoutedEventArgs e) => QuickSave(item => item with { WatchLater = EditWatchLater.IsChecked == true }, EditWatchLater.IsChecked == true ? T("Marked for later") : T("No longer marked for later"));
 /// <summary>Digits 0–5 rate every selected medium in whole stars, as in common photo tools.</summary>
 private void RateSelection(int stars) => RateMedia(SelectedMedia, stars);

 private static string ViewName(string view) => view switch { "home" => T("Home"), "Video" => T("Videos"), "Image" => T("Images"), "Audio" => T("Audio"), "favorites" => T("Favorites"), "watchlater" => T("Watch later"), _ => T("All items") };
 private void CopyPathClicked(object sender, RoutedEventArgs e) { if (Selected is { } item) { Clipboard.SetText(item.Path); Status.Text = T("Path copied"); } }
 private void RevealClicked(object sender, RoutedEventArgs e) => Reveal();
}
