using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>O counter and playback history of the selected medium in the details, and the end of a playback in the history.</summary>
public partial class MainWindow
{
 private long? _historyEntry;

 /// <summary>Notes how much of the closing playback was seen; the watched count follows the share in the settings.</summary>
 private void FinishPlayback(VideoPlayerWindow? window)
 {
  if (window == null || _playingId is not { } id) return;
  try
  {
   var item = _repository.FinishPlayback(id, _historyEntry, window.Coverage.WatchedSeconds, window.Coverage.Share);
   if (item != null && _detailsShown?.Id == id) ShowHistory(item);
  }
  catch (Exception error) when (error is InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException) { Status.Text = T("Playback history not saved: ") + error.Message; }
 }

 /// <summary>Counter button and the last entries of the history; hidden counter and switched-off history say so.</summary>
 private void ShowHistory(MediaItem? item)
 {
  var settings = _repository.GetPreferences().History;
  // The tiles keep the medium as listed; counters change during playback, so the details read it fresh.
  if (item != null) item = _repository.GetMedia(item.Id) ?? item;
  EditOCounter.Visibility = settings.ShowOCounter ? Visibility.Visible : Visibility.Collapsed;
  EditOCounter.Content = "O " + (item?.OCount ?? 0).ToString("N0", CultureInfo.CurrentCulture);
  var undo = new MenuItem { Header = T("Take back the last O"), IsEnabled = item?.OCount > 0 };
  undo.Click += (_, _) => ChangeO(add: false);
  EditOCounter.ContextMenu = new ContextMenu { Items = { undo } };
  if (item == null) { HistoryLabel.Text = ""; return; }
  var entries = _repository.GetHistory(item.Id, 6);
  var lines = new List<string>();
  var watched = item.WatchCount ?? item.PlayCount ?? 0;
  lines.Add(item.Kind == MediaKind.Image ? Tn((int)Math.Min(int.MaxValue, item.ViewCount ?? 0), "Viewed once", "Viewed {0:N0} times")
   : Tn((int)Math.Min(int.MaxValue, item.PlayCount ?? 0), "Played once", "Played {0:N0} times") + " · " + Tn((int)Math.Min(int.MaxValue, watched), "watched once", "watched {0:N0} times"));
  foreach (var entry in entries.Take(5))
  {
   var when = entry.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
   var seen = TimeSpan.FromSeconds(Math.Round(entry.Seconds)).ToString(entry.Seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
   lines.Add(entry.Kind == HistoryKind.O ? when + " · O" : when + " · " + T("{0} · {1:0} % seen", seen, entry.Share * 100));
  }
  if (entries.Count > 5) lines.Add("…");
  if (!settings.Enabled) lines.Add(T("The history is switched off (Settings › History)."));
  HistoryLabel.Text = string.Join("\n", lines);
  ClearHistoryButton.IsEnabled = entries.Count > 0;
 }

 private void OCounterClicked(object sender, RoutedEventArgs e) => ChangeO(add: true);

 private void ChangeO(bool add)
 {
  if (Selected is not { } selected || SelectedMedia.Count > 1) return;
  var item = add ? _repository.AddO(selected.Id, DateTimeOffset.Now) : _repository.UndoO(selected.Id);
  if (item == null) return;
  Status.Text = add ? T("O counter · {0:N0}", item.OCount ?? 0) + T(" · the right mouse button takes it back") : T("Last O taken back · {0:N0}", item.OCount ?? 0);
  Refresh(item.Id);
 }

 private void ClearMediumHistory(object sender, RoutedEventArgs e)
 {
  if (Selected is not { } item || SelectedMedia.Count > 1) return;
  if (Message.Show(this, T("Delete the playback history of “{0}”? Play count and O counter stay.", item.Title), T("Delete history"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
  var removed = _repository.ClearHistory(item.Id);
  Status.Text = Tn(removed, "1 history entry deleted", "{0:N0} history entries deleted");
  ShowHistory(_repository.GetMedia(item.Id));
 }
}
