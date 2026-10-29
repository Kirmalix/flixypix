using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>UX plan U6 and U7: tags of bookmarks and scenes with chips, rename in place, the jump mark filter and @tag;
 /// the collection tree with quick collection, key B, target collection, sets, smart collections and "In collections".</summary>
 private async Task RunCollectionSmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  async Task Settled() { await _lastRefresh; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
  KeyEventArgs Press(Visual source, Key key, RoutedEvent routed) => new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source)!, 0, key) { RoutedEvent = routed };

  // ----- U6: bookmarks and scenes -----
  var video = _repository.Upsert(new MediaItem { Path = Path.Combine(directory, "marken-test.mp4"), Title = "Marken-Test", Kind = MediaKind.Video, DurationSeconds = 300 });
  _repository.UpsertBookmark(new Bookmark { MediaId = video.Id, Title = "Anfang", PositionSeconds = 5, Tags = ["Smoke-Kuss"] });
  _repository.UpsertBookmark(new Bookmark { MediaId = video.Id, Title = "Mitte", PositionSeconds = 150, Tags = ["Smoke-Tanz"] });
  _repository.UpsertBookmark(new Bookmark { MediaId = video.Id, Title = "Ohne Tag", PositionSeconds = 200 });
  _repository.UpsertScene(new Scene { MediaId = video.Id, Title = "Szene", StartSeconds = 10, EndSeconds = 40, Tags = ["Smoke-Kuss"] });
  ApplyViewState(new ViewState { View = "Video" }); await Settled();
  await RefreshAsync(video.Id); await Settled();
  Require(Selected?.Id == video.Id, "The test video was not selected.");
  Require(BookmarkTagFilter.Visibility == Visibility.Visible && BookmarkTagFilter.Children.Count == 3 && SceneTagFilter.Children.Count == 2,
   "The tag chips above bookmarks and scenes are missing.");
  ((Button)BookmarkTagFilter.Children[2]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
  Require(Bookmarks.Items.Count == 1 && ((Bookmark)Bookmarks.Items[0]).Title == "Mitte", "The tag chip did not filter the bookmarks.");
  ((Button)BookmarkTagFilter.Children[0]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
  Require(Bookmarks.Items.Count == 3, "“All” did not show every bookmark again.");

  // Rename in place: F2 opens a field over the title, Enter keeps the name.
  Bookmarks.SelectedItem = Bookmarks.Items.Cast<Bookmark>().Single(b => b.Title == "Anfang"); Bookmarks.Focus(); await Settled();
  Bookmarks.RaiseEvent(Press(Bookmarks, Key.F2, Keyboard.KeyDownEvent)); await Settled();
  Require(InlineEdit.Current is { } field && field.Text == "Anfang", "F2 did not open the name field over the bookmark.");
  InlineEdit.Current!.Text = "Erster Kuss";
  InlineEdit.Current.RaiseEvent(Press(InlineEdit.Current, Key.Enter, Keyboard.PreviewKeyDownEvent)); await Settled();
  Require(_repository.GetBookmarks(video.Id).Any(b => b.Title == "Erster Kuss") && InlineEdit.Current == null, "Renaming in place did not save the name.");
  var renamed = (Bookmark)Bookmarks.SelectedItem!;
  var labels = Menus.Labels(Menus.Create(MarkMenu(Bookmarks, renamed))).ToList();
  Require(labels.Contains(T("Tags") + " ▸ Smoke-Kuss") && labels.Contains(T("Tags") + " ▸ Smoke-Tanz") && labels.Contains(T("Rename")),
   "The bookmark menu does not suggest the tags in use.");
  SetMarkTags(renamed, [.. renamed.Tags, "Smoke-Tanz"]); await Settled();
  Require(_repository.GetBookmarks(video.Id).Single(b => b.Title == "Erster Kuss").Tags.SequenceEqual(["Smoke-Kuss", "Smoke-Tanz"]), "Ticking a suggested tag did not add it.");
  OpenDetailsAt("scenes"); Bookmarks.BringIntoView(); await Settled();
  Capture(RootContent, Path.Combine(directory, "bookmark-tags.png"));

  // The filter bar and the short form @tag find media by the tags of their jump marks.
  ApplyFilter(new MediaFilter { MarkTags = ["Smoke-Kuss"] }); await Settled();
  Require(_total == 1, "The jump mark filter did not find the video.");
  OpenFilterPopup(FilterPills[T("Jump marks")].Button, MarkTagPanel(FilterPills[T("Jump marks")].Button)); await Settled();
  Capture((FrameworkElement)_filterPopup!.Child, Path.Combine(directory, "filter-jump-marks.png")); _filterPopup.IsOpen = false;
  ApplyViewState(new ViewState { View = "Video" }); await Settled();
  SearchBox.Text = "@smoke-tanz"; SearchKeyDown(SearchBox, Press(SearchBox, Key.Enter, Keyboard.KeyDownEvent)); await Settled();
  Require(_filter.MarkTags.SequenceEqual(["smoke-tanz"]) && SearchBox.Text.Length == 0 && _total == 1, "The short form @tag did not become a jump mark filter.");
  ApplyViewState(new ViewState()); await Settled();

  // ----- U7: collections -----
  ApplyViewState(new ViewState { View = "Image" }); await Settled();
  var cards = MediaList.Items.Cast<MediaCard>().Take(2).ToList();
  Require(cards.Count == 2, "The test needs two images.");
  MediaList.SelectedItems.Clear(); foreach (var card in cards) MediaList.SelectedItems.Add(card);
  var ids = cards.Select(c => c.Item.Id).ToHashSet();
  ToggleTargetCollection(); await Settled();
  var quick = _repository.GetQuickCollection();
  Require(quick.MediaIds.ToHashSet().SetEquals(ids), "B did not put the selection into the quick collection.");
  var quickNode = CollectionNodes().First();
  Require(quickNode.Kind == CollectionNodeKind.Quick && quickNode.IsTarget && quickNode.CountLabel == "2", "The quick collection is not on top as target with its count.");
  Require(MediaList.Items.Cast<MediaCard>().Where(c => ids.Contains(c.Item.Id)).All(c => c.TargetVisibility == Visibility.Visible), "Tiles in the quick collection have no mark.");
  Require(Menus.Labels(Menus.Create(CollectionNodeMenu(quickNode))).Contains(T("Clear quick collection")), "The quick collection menu is incomplete.");

  var set = _repository.UpsertCollectionSet(new CollectionSet { Name = "Smoke-Satz" });
  var collection = _repository.UpsertPlaylist(new Playlist { Name = "Smoke-Sammlung" });
  var smart = _repository.UpsertSavedSearch(new SavedSearch { Name = "Smoke-Smart", State = new ViewState { View = "Image" } });
  await RefreshAsync(); await Settled();
  MoveCollectionNode(CollectionNodes().Single(n => n.Id == collection.Id), set.Id); await Settled();
  MoveCollectionNode(CollectionNodes().Single(n => n.Id == smart.Id), set.Id); await Settled();
  var setNode = CollectionNodes().Single(n => n.Id == set.Id);
  Require(setNode.Children.Select(c => c.Id).SequenceEqual([collection.Id, smart.Id]), "Moving into a set did not nest the collection and the smart collection.");
  await Task.Delay(300); await Settled();
  Require(CollectionNodes().Single(n => n.Id == smart.Id).CountLabel.Length > 0, "The smart collection in the set was not counted.");

  // Another collection as target: B fills it, the quick collection stays as it was.
  SetTargetCollection(_repository.GetPlaylists().Single(p => p.Id == collection.Id)); await Settled();
  MediaList.SelectedItems.Clear(); foreach (var card in MediaList.Items.Cast<MediaCard>().Where(c => ids.Contains(c.Item.Id))) MediaList.SelectedItems.Add(card);
  ToggleTargetCollection(); await Settled();
  Require(_repository.GetPlaylists().Single(p => p.Id == collection.Id).MediaIds.Count == 2 && _repository.GetQuickCollection().MediaIds.Count == 2
   && CollectionNodes().Single(n => n.Id == collection.Id).IsTarget, "B did not fill the chosen target collection.");
  MediaList.SelectedItems.Clear(); MediaList.SelectedItem = MediaList.Items.Cast<MediaCard>().First(c => ids.Contains(c.Item.Id)); await Settled();
  var chips = MembershipChips.Children.OfType<Button>().Select(b => (string)b.Content).ToList();
  Require(MembershipSection.Visibility == Visibility.Visible && chips.Contains(T("Quick collection")) && chips.Contains("Smoke-Sammlung"), "“In collections” does not list both collections.");
  OpenSidebarAt("collections"); await Settled();
  Capture(RootContent, Path.Combine(directory, "collection-tree.png"));

  // Cleanup: a deleted set keeps what it held.
  SetTargetCollection(null); _repository.DeleteCollectionSet(set.Id);
  Require(_repository.GetPlaylists().Single(p => p.Id == collection.Id).ParentId == null, "Deleting the set lost its collection.");
  _repository.DeletePlaylist(collection.Id); _repository.DeleteSavedSearch(smart.Id); ClearQuickCollection();
  _repository.Delete(video.Id);
  ApplyViewState(new ViewState()); await Settled();
 }
}
