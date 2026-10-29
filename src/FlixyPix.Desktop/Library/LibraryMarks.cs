using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;
using FlixyPix.MediaTools;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

/// <summary>Scenes and bookmarks in the details column.</summary>
public partial class MainWindow
{
 private void AddScene(object sender, RoutedEventArgs e)
 {
  if (Selected is not { } item || item.Kind == MediaKind.Image) return;
  var values = Dialogs.Fields(this, T("Mark scene"), ("Name", T("New scene")), (T("Start in seconds"), "0"), (T("End in seconds"), (item.DurationSeconds ?? 10).ToString(CultureInfo.InvariantCulture)));
  if (values == null) return;
  if (!TrySeconds(values[1], out var start) || !TrySeconds(values[2], out var end) || start < 0 || end <= start || (item.DurationSeconds.HasValue && end > item.DurationSeconds.Value)) { Message.Show(this, T("Start and end must form a valid range within the item.")); return; }
  _repository.UpsertScene(new Scene { MediaId = item.Id, Title = values[0], StartSeconds = start, EndSeconds = end }); ShowDetails();
 }
 private void DeleteScene(object sender, RoutedEventArgs e) { if (Scenes.SelectedItem is Scene scene) { _repository.DeleteScene(scene.Id); ShowDetails(); } }
 private void SceneDoubleClick(object sender, MouseButtonEventArgs e) { _renameDelay?.Stop(); PlaySelectedScene(); }
 private async void PlaySelectedScene()
 {
  if (Scenes.SelectedItem is not Scene scene || Selected is not { } item) return;
  StopSlideshow(); var cancellation = new CancellationTokenSource(); _collectionPlayback = cancellation;
  try { await Play(item, scene.StartSeconds); var player = _player; if (player != null) { await WaitForEntryEnd(player, scene.EndSeconds, cancellation.Token); if (ReferenceEquals(player, _player)) await StopPlayer(); } }
  catch (OperationCanceledException) { }
  catch (Exception ex) { Status.Text = T("Scene interrupted: ") + ex.Message; }
  finally { if (ReferenceEquals(_collectionPlayback, cancellation)) _collectionPlayback = null; cancellation.Dispose(); }
 }
 private async void AddBookmark(object sender, RoutedEventArgs e)
 {
  if (Selected is not { } item || item.Kind == MediaKind.Image) return;
  var current = _playingId == item.Id && _player != null ? await _player.GetPositionAsync() : item.PlaybackPositionSeconds;
  var input = Dialogs.Fields(this, T("Set bookmark"), ("Name", T("Bookmarks")), (T("Position in seconds"), (current ?? 0).ToString(CultureInfo.InvariantCulture)));
  if (input == null) return;
  if (!TrySeconds(input[1], out var position) || position < 0) { Message.Show(this, T("Please enter a valid position of 0 or more.")); return; }
  _repository.UpsertBookmark(new Bookmark { MediaId = item.Id, Title = input[0], PositionSeconds = position }); ShowDetails();
 }
 private void DeleteBookmark(object sender, RoutedEventArgs e) { if (Bookmarks.SelectedItem is Bookmark bookmark) { _repository.DeleteBookmark(bookmark.Id); ShowDetails(); } }
 private void BookmarkDoubleClick(object sender, MouseButtonEventArgs e) { _renameDelay?.Stop(); PlaySelectedBookmark(); }
 private async void PlaySelectedBookmark() { if (Bookmarks.SelectedItem is Bookmark bookmark && Selected is { } item) { StopSlideshow(); await Play(item, bookmark.PositionSeconds); } }
}
