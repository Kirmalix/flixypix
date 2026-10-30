using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>The start page as the library view "home": it lies over the media grid, keeps the sidebar and the search, and
/// gives way to the grid as soon as a search, tag or filter asks for media. "Alle anzeigen" opens the matching view.</summary>
public partial class MainWindow
{
 private HomePage _home = null!;
 private long _homeVersion;
 private Task _lastHome = Task.CompletedTask;
 internal HomePage Home => _home;
 internal Task HomeTask => _lastHome;

 private void InitializeHome()
 {
  _home = new HomePage { Visibility = Visibility.Collapsed };
  _home.SetResourceReference(BackgroundProperty, "WindowBackground");
  Grid.SetRow(_home, 1); Grid.SetRowSpan(_home, 4); Panel.SetZIndex(_home, 10);
  MediaPane.Children.Add(_home);
  _home.ToggleQuick = ToggleQuickCollection;
  _home.FeaturedVideoArtwork = LoadFeaturedVideoArtworkAsync;
  _home.Open = async item => { StopSlideshow(); await Play(item); };
  _home.ToggleWatchLater = item =>
  {
   if (_repository.GetMedia(item.Id) is { } current) { _repository.Upsert(current with { WatchLater = !current.WatchLater }); Status.Text = current.WatchLater ? T("Removed from the watch list") : T("Added to the watch list"); }
  };
  _home.ShowAll = row => ApplyViewState(row.Kind switch
  {
   HomeRowKind.WatchLater => new ViewState { View = "watchlater", SortOrder = 2 },
   HomeRowKind.Popular => new ViewState { View = "all", MinimumStars = 3, SortOrder = 4 },
   HomeRowKind.MoreWithTag when row.Tag is { } tag => new ViewState { View = "all", RequiredTags = [tag], SortOrder = 4 },
   HomeRowKind.ForYou => new ViewState { View = "all", Filter = new MediaFilter { Watched = false }, SortOrder = 4 },
   _ => new ViewState { View = "all", SortOrder = 2 },
  }, row.Title);
  _home.ShowEntity = entry => ApplyViewState(new ViewState { View = "all", Tag = entry.Tag, TagCategory = entry.Category, SortOrder = 4 }, entry.Tag);
  _home.ContentPack = ShowAdultContentPack;
  _home.ContentPackAvailable = () => OriginalPresets.FindProgram() != null;
  _home.Import = () => ImportFolder(this, new RoutedEventArgs());
  _home.MenuRequested = item => OpenMediaMenu(item, MediaMenuPlace.Home);
 }

 /// <summary>Opens the start page (sidebar "Start", Strg+Pos1).</summary>
 internal void GoHome() => ApplyViewState(new ViewState { View = "home", SortOrder = Math.Max(SortChoice.SelectedIndex, 0), ViewMode = ViewChoice.SelectedIndex, TileWidth = (int)Math.Round(TileSize.Value) });

 /// <summary>Asking for media leaves the start page for "Alle Medien": typing a search, picking a tag, folder, filter …</summary>
 private void LeaveHomeWhenFiltered()
 {
  if (_view != "home") return;
  var asks = SearchBox.Text.Length > 0 || _tag != null || _tagGroup != null || _category != null || _folder != null || _playlist != null || _advancedExpression != null ||
   _requiredTags.Count > 0 || _excludedTags.Count > 0 || !_filter.IsEmpty || RatingFilter.SelectedIndex > 0;
  if (!asks) return;
  _view = "all";
  if (ViewTitle.Text == ViewName("home")) ViewTitle.Text = ViewName("all");
 }

 /// <summary>After each refresh: shows or hides the start page and rebuilds its rows off the UI thread.</summary>
 private void UpdateHome(bool catalogEmpty, long total)
 {
  // The grid behind stays loaded for a quick way back, but does not realise tiles or load pictures while hidden.
  MediaList.Visibility = _view == "home" ? Visibility.Collapsed : Visibility.Visible;
  AdaptToWidth();
  if (_view != "home") { _homeArtworkCancellation.Cancel(); _home.Visibility = Visibility.Collapsed; return; }
  var first = _home.Visibility != Visibility.Visible || _home.RowCount == 0 && _home.HeroCount == 0;
  if(_homeArtworkCancellation.IsCancellationRequested) { _homeArtworkCancellation.Dispose();_homeArtworkCancellation=new(); }
  _home.Visibility = Visibility.Visible;
  var hour = DateTime.Now.Hour;
  CountLabel.Text = (hour < 11 ? T("Good morning") : hour < 18 ? T("Good afternoon") : T("Good evening")) + (total > 0 ? Tn(total, " · 1 item in your library", " · {0:N0} items in your library") : "");
  if (first && !catalogEmpty) _home.ShowLoading();
  _lastHome = BuildHomeAsync(catalogEmpty);
 }

 private async Task BuildHomeAsync(bool catalogEmpty)
 {
  var version = ++_homeVersion;
  try
  {
   var feed = catalogEmpty ? new HomeFeed([], []) : await Task.Run(() => _repository.BuildHomeFeed(DateTimeOffset.Now));
   if (version != _homeVersion || _closing || _view != "home") return;
   _home.Show(feed, catalogEmpty);
  }
  catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
  {
   if (!_closing) { ErrorLog.Write("Startseite", error); Status.Text = T("The home page could not be loaded: ") + error.Message; }
  }
 }

 /// <summary>Start: the last view as the library asks, then the start page if it is switched on. "Zurück" leads to the
 /// restored view.</summary>
 internal async Task StartAsync(bool mayOpenHome)
 {
  await RestoreLastViewAsync();
  if (mayOpenHome && _view != "home" && _repository.GetPreferences().Home.StartOnHome) { GoHome(); await _lastRefresh; }
  OfferCompaction();
 }

 /// <summary>After the library was converted while opening, its catalog file holds space the old format used; compacting
 /// gives it back. Offered once, as a notification, so the start does not wait for it.</summary>
 private void OfferCompaction()
 {
  if (!_repository.UpgradedOnOpen) return;
  var unused = _repository.UnusedCatalogBytes;
  if (unused < 64L * 1024 * 1024 || unused < _repository.CatalogBytes / 5) return;
  ShowToast(T("The library was converted. Its catalog file can give back {0}.", MediaCard.SizeText(unused)), MessageBoxImage.Information,
   T("Compact catalog"), CompactCatalogNow, seconds: 30);
 }

 private async void CompactCatalogNow()
 {
  var repository = _repository; (long Before, long After) sizes = default;
  await RunOperation(T("Compact catalog"), async _ => sizes = await Task.Run(repository.Compact), refresh: false);
  if (sizes != default) ShowToast(T("Catalog compacted: {0} → {1}", MediaCard.SizeText(sizes.Before), MediaCard.SizeText(sizes.After)));
 }
}
