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

/// <summary>Collections and slideshows: create, edit, order and play.</summary>
public partial class MainWindow
{
 private static List<PlaylistEntry> CollectionEntries(Playlist playlist) => playlist.Entries.Count > 0 ? playlist.Entries.ToList() : playlist.MediaIds.Select(id => new PlaylistEntry(id)).ToList();
 /// <summary>Offers the existing collections and a new one; with no collection yet, creating one is the only choice.</summary>
 private void AddToPlaylist(bool asScene)
 {
  if (Selected is not { } item) return;
  var scene = asScene ? Scenes.SelectedItem as Scene : null;
  if (asScene && scene == null) { Message.Show(this, T("Choose a scene on the right first.")); return; }
  IReadOnlyList<MediaItem> items = scene != null ? [item] : SelectedMedia;
  var imagesOnly = items.All(m => m.Kind == MediaKind.Image);
  var choices = _repository.GetPlaylists().Select(p => (CollectionName(p), (Action)(() => AddToCollection(p, items, scene)))).ToList();
  choices.Add((T("New playlist …"), () => { if (NewCollectionFor(PlaylistKind.Playlist) is { } p) AddToCollection(p, items, scene); }));
  if (imagesOnly) choices.Add((T("New slideshow …"), () => { if (NewCollectionFor(PlaylistKind.Slideshow) is { } p) AddToCollection(p, items, scene); }));
  Menu(choices.ToArray());
 }
 private Playlist? NewCollectionFor(PlaylistKind kind)
 {
  var input = Dialogs.Fields(this, kind == PlaylistKind.Slideshow ? T("New slideshow") : T("New playlist"), ("Name", ""));
  return input == null || string.IsNullOrWhiteSpace(input[0]) ? null : _repository.UpsertPlaylist(new Playlist { Name = input[0].Trim(), Kind = kind });
 }
 private void AddToCollection(Playlist playlist, IReadOnlyList<MediaItem> items, Scene? scene)
 {
  if (playlist.Kind == PlaylistKind.Slideshow && items.Any(m => m.Kind != MediaKind.Image)) { Message.Show(this, T("Slideshows contain images. For videos and audio, use a playlist.")); return; }
  var entries = CollectionEntries(playlist);
  foreach (var entry in items.Select(m => new PlaylistEntry(m.Id, scene?.Id))) if (!entries.Contains(entry)) entries.Add(entry);
  _repository.UpsertPlaylist(playlist with { MediaIds = entries.Select(x => x.MediaId).ToList(), Entries = entries });
  Status.Text = items.Count == 1 ? T("Added to “{0}”", CollectionName(playlist)) : Tn(items.Count, "1 item added to “{1}”", "{0:N0} items added to “{1}”", CollectionName(playlist));
  Refresh(items[0].Id);
 }
 private void CollectionMenu(object sender, RoutedEventArgs e) => MenuBelow(sender, (T("Play collection"), PlayCollection),
  (T("Add selected scene to collection …"), () => AddToPlaylist(true)), (T("Change name / image duration …"), EditCollection),
  (T("Move selected item up"), () => MoveInCollection(-1)), (T("Move selected item down"), () => MoveInCollection(1)),
  (T("Delete collection …"), DeleteCollection), (T("Stop collection"), async () => { StopSlideshow(); await StopPlayer(); }));
 private void EditCollection()
 {
  if (_playlist == null) return;
  var values = Dialogs.Fields(this, T("Edit collection"), ("Name", _playlist.Name), (T("Image duration in seconds (slideshow)"), _playlist.SlideSeconds.ToString(CultureInfo.InvariantCulture)));
  if (values == null || string.IsNullOrWhiteSpace(values[0])) return;
  if (!TrySeconds(values[1], out var seconds) || seconds < 1) throw new InvalidOperationException(T("The image duration must be at least 1 second."));
  _playlist = _repository.UpsertPlaylist(_playlist with { Name = values[0], SlideSeconds = seconds }); ViewTitle.Text = _playlist.Name; Refresh();
 }
 private void MoveInCollection(int delta)
 {
  if (_playlist == null || Selected is not { } selected) return;
  var entries = CollectionEntries(_playlist); var index = entries.FindIndex(x => x.MediaId == selected.Id); var next = index + delta;
  if (index < 0 || next < 0 || next >= entries.Count) return;
  (entries[index], entries[next]) = (entries[next], entries[index]); _playlist = _repository.UpsertPlaylist(_playlist with { MediaIds = entries.Select(x => x.MediaId).ToList(), Entries = entries }); Refresh(selected.Id);
 }
 private void DeleteCollection() { if (_playlist == null) return; if (Message.Show(this, T("Delete the collection “{0}”? The items stay in the catalog.",
  _playlist.Name), T("Delete collection"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) {
  _repository.DeletePlaylist(_playlist.Id); _playlist = null; ViewTitle.Text = T("All items"); Refresh(); } }
 private async void PlayCollection()
 {
  if (_playlist == null) return;
  StopSlideshow();
  var entries = CollectionEntries(_playlist);
  var available = entries.Select(entry => entry.MediaId).Distinct().Select(_repository.GetMedia).OfType<MediaItem>().ToDictionary(i => i.Id);
  var scenes = _repository.GetScenes().ToDictionary(s => s.Id);
  var items = entries.Where(entry => available.ContainsKey(entry.MediaId)).Select(entry => (Item: available[entry.MediaId], Scene: entry.SceneId.HasValue ? scenes.GetValueOrDefault(entry.SceneId.Value) : null)).Where(i => File.Exists(i.Item.Path)).ToList();
  if (items.Count == 0) { Message.Show(this, T("This collection contains no available items.")); return; }
  var cancellation = new CancellationTokenSource();
  _collectionPlayback = cancellation;
  var slideSeconds = _playlist.SlideSeconds;
  using var awake = _repository.GetPreferences().Playback.PreventSleep ? SleepGuard.Acquire() : null;
  try
  {
   for (var index = 0; index < items.Count; index++)
   {
    cancellation.Token.ThrowIfCancellationRequested();
    var item = items[index].Item; var scene = items[index].Scene;
    await Play(item, scene?.StartSeconds ?? 0);
    cancellation.Token.ThrowIfCancellationRequested();
    Status.Text = T("Collection · {0}/{1} · {2}", index + 1, items.Count, scene?.Title ?? item.Title);
    if (item.Kind == MediaKind.Image) await Task.Delay(TimeSpan.FromSeconds(slideSeconds), cancellation.Token);
    else if (_player != null) { await WaitForEntryEnd(_player, scene?.EndSeconds, cancellation.Token); if (scene != null) await StopPlayer(); }
   }
   Status.Text = T("Collection finished");
  }
  catch (OperationCanceledException) { }
  catch (Exception ex) { Status.Text = T("Collection interrupted: ") + ex.Message; }
  finally { if (ReferenceEquals(_collectionPlayback, cancellation)) { _collectionPlayback = null; _viewer?.Close(); _viewer = null; } cancellation.Dispose(); }
 }
 private static async Task WaitForEntryEnd(MpvPlayer player, double? end, CancellationToken token)
 {
  using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
  var eof = player.WaitForEndAsync(wait.Token);
  try
  {
   if (!end.HasValue) { await eof; return; }
   while (!eof.IsCompleted)
   {
    await Task.WhenAny(eof, Task.Delay(250, token)); token.ThrowIfCancellationRequested();
    if (!eof.IsCompleted && await player.GetPositionAsync() is double position && position >= end.Value) return;
   }
   await eof;
  }
  finally { wait.Cancel(); try { await eof; } catch (OperationCanceledException) { } }
 }
 private void StopSlideshow() { _collectionPlayback?.Cancel(); _collectionPlayback = null; var viewer = _viewer; _viewer = null; viewer?.Close(); }
 internal static bool TrySeconds(string text, out double value) => (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value));
}
