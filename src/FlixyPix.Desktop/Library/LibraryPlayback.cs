using System.IO;
using System.Windows;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

/// <summary>What the player asks of the library: previous and next video in the order of the current view (images are
/// skipped), a random one for shuffle, continuing at the end when repeat is on, and frames for the time line preview.</summary>
public partial class MainWindow
{
 private readonly Random _shuffleRandom = new();

 /// <summary>Wires a new player window to the view it was started from. Collections and scenes play their own sequence.</summary>
 private void ConnectPlayer(VideoPlayerWindow window, MediaItem item)
 {
  window.Repeat = _settings.PlayerRepeat; window.Shuffle = _settings.PlayerShuffle;
  window.MarksChanged = () => _ = EnsureMarkPreviewsAsync(item);
  window.MediaChanged = changed => { if (_detailsShown?.Id == changed.Id) Refresh(changed.Id); };
  window.PlaybackModeChanged += () =>
  {
   _settings = _settings with { PlayerRepeat = window.Repeat, PlayerShuffle = window.Shuffle };
   if (_persistPreferences) (_settings with { DataDirectory = _pendingDataDirectory ?? _settings.DataDirectory }).Save();
  };
  if (_collectionPlayback == null && _window != null)
  {
   window.StepRequested = direction => PlayNeighborAsync(item, direction, window.Shuffle, wrap: true);
   window.RandomRequested = () => PlayNeighborAsync(item, 1, shuffle: true, wrap: true);
  }
  window.FrameAt = (seconds, token) => FrameAtAsync(item, seconds, token);
 }

 /// <summary>Plays the previous or next video of the current view; with shuffle a random one.</summary>
 internal async Task PlayNeighborAsync(MediaItem current, int direction, bool shuffle, bool wrap)
 {
  var neighbor = await Task.Run(() => shuffle ? RandomPlayable(current) : Neighbor(current, direction, wrap));
  if (neighbor == null) { Status.Text = direction < 0 ? T("No previous video in this view") : T("No further video in this view"); return; }
  await Play(neighbor, 0);
 }

 /// <summary>Video or audio next to <paramref name="current"/> in the view's order, walking over images; wraps once if asked.</summary>
 internal MediaItem? Neighbor(MediaItem current, int direction, bool wrap)
 {
  if (_window is not { } window || direction == 0) return null;
  var (position, total) = PositionInView(window, current.Id);
  if (position is not { } start || total <= 1) return null;
  var found = direction > 0 ? Walk(window, start + 1, 1, total, current.Id) : Walk(window, start - 1, -1, -1, current.Id);
  if (found == null && wrap) found = direction > 0 ? Walk(window, 0, 1, start, current.Id) : Walk(window, total - 1, -1, start, current.Id);
  return found;
 }

 /// <summary>First playable medium from index <paramref name="from"/> in <paramref name="direction"/>, stopping before <paramref name="stop"/>.</summary>
 private MediaItem? Walk((MediaQuery Query, Playlist? Playlist, bool CollectionOrder) window, int from, int direction, int stop, Guid currentId)
 {
  const int batch = 50;
  while (direction > 0 ? from < stop : from > stop)
  {
   var first = direction > 0 ? from : Math.Max(stop + 1, from - batch + 1);
   var count = direction > 0 ? Math.Min(batch, stop - from) : from - first + 1;
   var items = Page(window, first, count);
   if (items.Count == 0) return null;
   if ((direction > 0 ? items : items.Reverse()).FirstOrDefault(i => i.Kind != MediaKind.Image && i.Id != currentId) is { } found) return found;
   from = direction > 0 ? from + count : first - 1;
  }
  return null;
 }

 private MediaItem? RandomPlayable(MediaItem current)
 {
  if (_window is not { } window) return null;
  var (_, total) = PositionInView(window, current.Id);
  for (var attempt = 0; attempt < 30 && total > 1; attempt++)
   if (Page(window, _shuffleRandom.Next(total), 1).FirstOrDefault() is { Kind: not MediaKind.Image } item && item.Id != current.Id) return item;
  return Neighbor(current, 1, wrap: true);
 }

 private (int? Position, int Total) PositionInView((MediaQuery Query, Playlist? Playlist, bool CollectionOrder) window, Guid id)
 {
  if (window.Playlist == null) return (_repository.GetPosition(window.Query, id), (int)_repository.QueryPage(window.Query with { Offset = 0, Limit = 1 }).TotalCount);
  var result = _repository.QueryPlaylistPage(window.Playlist, window.Query with { Offset = 0, Limit = 1 }, window.CollectionOrder, id);
  return (result.Page.Items.FirstOrDefault()?.Id == id ? result.Offset : null, (int)result.Page.TotalCount);
 }

 private IReadOnlyList<MediaItem> Page((MediaQuery Query, Playlist? Playlist, bool CollectionOrder) window, int offset, int limit)
 {
  var query = window.Query with { Offset = Math.Max(0, offset), Limit = limit };
  return window.Playlist == null ? _repository.QueryPage(query).Items : _repository.QueryPlaylistPage(window.Playlist, query, window.CollectionOrder).Page.Items;
 }

 /// <summary>With repeat on, the end of a video starts the next one of the view (or a random one with shuffle).</summary>
 private async Task ContinueAtEndAsync(MpvPlayer player, VideoPlayerWindow window, MediaItem item)
 {
  try
  {
   await player.WaitForEndAsync();
   if (!ReferenceEquals(_player, player) || _collectionPlayback != null || !window.Repeat || window.StepRequested == null) return;
   await PlayNeighborAsync(item, 1, window.Shuffle, wrap: true);
  }
  catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException) { }
 }

 /// <summary>A small frame of the video for the time line preview, cached in the library's hover cache in steps of about 1 % of the duration.</summary>
 private async Task<string?> FrameAtAsync(MediaItem item, double seconds, CancellationToken token)
 {
  if (!ToolExists(_settings.Ffmpeg, "ffmpeg") || item.DurationSeconds is not > 0) return null;
  var step = Math.Max(1, Math.Round(item.DurationSeconds.Value / 100));
  var rounded = Math.Min(Math.Round(seconds / step) * step, Math.Max(0, item.DurationSeconds.Value - .5));
  var folder = Path.Combine(_settings.DataDirectory, "hover-cache");
  var path = Path.Combine(folder, $"{item.Id:N}-seek-{rounded:0}.jpg");
  if (File.Exists(path)) return path;
  Directory.CreateDirectory(folder);
  try { return await _tools.CreateThumbnailAsync(item.Path, path, rounded, token); }
  catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException) { return null; }
 }
}
