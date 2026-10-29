using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Remembering the view: it is saved on close and every half minute while it changes, and restored on start as
/// far as the library wants ("Beim Start wiederherstellen": off, view only, everything). Back and forward walk through the
/// views of this session (Alt+←/→, mouse buttons 4/5). The window's place is kept on this computer.</summary>
public partial class MainWindow
{
 private readonly ViewHistory _history = new();
 private readonly DispatcherTimer _viewSaveTimer = new() { Interval = TimeSpan.FromSeconds(30) };
 private bool _walkingHistory;
 private Guid? _lastPlayedId;
 private string? _savedViewJson;
 internal ViewHistory History => _history;

 private void InitializeViewHistory()
 {
  _history.Capacity = _repository.GetPreferences().General.HistoryLength;
  _viewSaveTimer.Tick += (_, _) => SaveViewNow();
  _viewSaveTimer.Start();
  MouseUp += (_, e) =>
  {
   if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2) { e.Handled = true; _ = WalkHistoryAsync(e.ChangedButton == MouseButton.XButton1 ? -1 : 1); }
  };
 }

 /// <summary>Called after each refresh: a new view becomes a history entry, scrolling only updates the current one.</summary>
 private void RecordView()
 {
  if (_walkingHistory || _closing) return;
  _history.Record(CaptureViewState() with { LastPlayedId = _lastPlayedId });
  BackButton.IsEnabled = _history.CanGoBack; ForwardButton.IsEnabled = _history.CanGoForward;
 }

 internal async Task WalkHistoryAsync(int direction)
 {
  if (CaptureViewState() is var now) _history.Record(now); // keep the current scroll place in the entry we leave
  var target = direction < 0 ? _history.Back() : _history.Forward();
  if (target is null) return;
  _walkingHistory = true;
  try
  {
   ApplyViewState(_repository.Sanitize(target, out _));
   await _lastRefresh;
   await RestorePlaceAsync(target);
  }
  finally { _walkingHistory = false; BackButton.IsEnabled = _history.CanGoBack; ForwardButton.IsEnabled = _history.CanGoForward; }
 }

 private void BackClicked(object sender, RoutedEventArgs e) => _ = WalkHistoryAsync(-1);
 private void ForwardClicked(object sender, RoutedEventArgs e) => _ = WalkHistoryAsync(1);

 /// <summary>Stores the view in the catalog when it changed since the last save; cheap, it keeps the browsing caches.</summary>
 internal void SaveViewNow()
 {
  if (_repository == null) return;
  try
  {
   var state = CaptureViewState() with { LastPlayedId = _lastPlayedId };
   var json = System.Text.Json.JsonSerializer.Serialize(state with { SavedAt = default });
   if (json == _savedViewJson) return;
   _repository.SaveViewState(state); _savedViewJson = json;
  }
  catch (Exception error) when (error is ObjectDisposedException or ArgumentException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { ErrorLog.Write("Ansicht merken", error); }
 }

 /// <summary>Restores the last view as the library's setting asks; without a stored state the original's last view counts.</summary>
 internal async Task RestoreLastViewAsync()
 {
  var general = _repository.GetPreferences().General;
  var stored = general.RestoreLastView ? _repository.GetViewState() : null;
  if (!general.RestoreLastView) { Refresh(); RecordView(); return; }
  if (stored is null)
  {
   ApplyViewState(new ViewState { View = ViewState.Views.Contains(_settings.LastView) ? _settings.LastView : "all", SortOrder = _settings.SortOrder, ViewMode = _settings.ViewMode, TileWidth = _settings.TileWidth });
   await _lastRefresh; RecordView(); return;
  }
  var state = _repository.Sanitize(stored, out var dropped).Restricted(general.RestoreScope);
  ApplyViewState(state, state.SavedSearchId is { } search ? _repository.GetSavedSearches().FirstOrDefault(s => s.Id == search)?.Name : null);
  await _lastRefresh;
  if (general.RestoreScope == RestoreScope.All)
  {
   await RestorePlaceAsync(state);
   if (dropped.Count > 0) ShowToast(T("No longer present: ") + string.Join(", ", dropped), MessageBoxImage.Warning);
   OfferContinueWatching(stored.LastPlayedId);
  }
  _lastPlayedId = stored.LastPlayedId;
  RecordView();
 }

 /// <summary>Scrolls to where the state was (the anchor medium if it is still in the result, else the saved index) and
 /// selects what was selected and is loaded.</summary>
 private async Task RestorePlaceAsync(ViewState state)
 {
  if (_total == 0 || _window is not { } window) return;
  var index = state.Position;
  if (state.AnchorId is { } anchor && window.Playlist == null && await Task.Run(() => _repository.GetPosition(window.Query, anchor)) is { } found) index = found;
  index = Math.Clamp(index, 0, _total - 1);
  if (index > 0) await ScrollToAsync(index);
  if (state.Selection.Count == 0) return;
  var wanted = state.Selection.ToHashSet();
  MediaList.SelectedItems.Clear();
  foreach (var card in MediaList.Items.Cast<MediaCard>().Where(c => wanted.Contains(c.Item.Id))) MediaList.SelectedItems.Add(card);
 }

 private void OfferContinueWatching(Guid? id)
 {
  if (id is not { } played || _repository.GetMedia(played) is not { Kind: not MediaKind.Image, DurationSeconds: > 0 } item) return;
  var card = new MediaCard(item, false);
  if (card.ProgressVisibility != Visibility.Visible) return;
  ShowToast(T("Continue watching: {0} (at {1})", item.Title, MediaCard.DurationText(item.PlaybackPositionSeconds)), MessageBoxImage.Information, T("Play"),
   async () => await Play(item, item.PlaybackPositionSeconds), seconds: 12);
 }

 /// <summary>The window comes back where it was; a place on a screen that is gone falls back to the default.</summary>
 private void RestoreWindowPlace()
 {
  if (PlaceToRestore() is not { } place) return;
  var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
  var bounds = new Rect(place.Left, place.Top, place.Width, place.Height);
  // At least the title bar must be reachable on some screen.
  if (!screen.IntersectsWith(new Rect(bounds.Left + 40, bounds.Top, Math.Max(1, bounds.Width - 80), 40))) return;
  WindowStartupLocation = WindowStartupLocation.Manual;
  Left = place.Left; Top = place.Top; Width = place.Width; Height = place.Height;
  if (place.Maximized) WindowState = WindowState.Maximized;
 }

 /// <summary>The stored place of the main window when the library wants it back ("Fensterplatz merken") and it is usable.</summary>
 internal WindowPlace? PlaceToRestore() =>
  _repository.GetPreferences().General.RememberWindow && _settings.MainWindowPlace is { } place && place.Width >= MinWidth && place.Height >= MinHeight ? place : null;

 private void SaveWindowPlace()
 {
  var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
  if (bounds.IsEmpty || bounds.Width <= 0) return;
  _settings = _settings with { MainWindowPlace = new WindowPlace(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized) };
  try { (_settings with { DataDirectory = _pendingDataDirectory ?? _settings.DataDirectory }).Save(); }
  catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { ErrorLog.Write("Fensterposition merken", error); }
 }
}
