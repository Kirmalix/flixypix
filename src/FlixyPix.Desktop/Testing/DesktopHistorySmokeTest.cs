using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>O counter and history: the details button counts and its menu takes back; a playback in the player is noted
 /// with the share seen and counts as watched; the player's O counts; the details list both; deleting empties the history.</summary>
 private async Task RunHistorySmokeTestAsync(string directory, Guid videoId)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  var before = _repository.GetMedia(videoId)!;
  await RefreshAsync(videoId);
  Require(Selected?.Id == videoId && EditOCounter.IsVisible, "The O counter is missing in the details.");
  EditOCounter.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await _lastRefresh;
  Require(_repository.GetMedia(videoId)!.OCount == (before.OCount ?? 0) + 1 && (string)EditOCounter.Content == "O " + ((before.OCount ?? 0) + 1),
   "A click on the O counter in the details did not count.");
  ChangeO(add: false); await _lastRefresh;
  Require(_repository.GetMedia(videoId)!.OCount == (before.OCount ?? 0), "Taking back the O did not count down.");
  if (ToolExists(_settings.Mpv, "mpv"))
  {
   await Play(_repository.GetMedia(videoId)!, 0);
   var window = _videoWindow ?? throw new InvalidOperationException("The player did not open.");
   await window.TogglePauseAsync();await window.SeekToAsync(0);await window.TogglePauseAsync();await window.SamplePlaybackAsync();
   for (var wait = 0; window.Coverage.Share < .9 && wait < 100; wait++) await Task.Delay(50);
   Require(window.Coverage.Share >= .9, $"The player did not note what was seen ({window.Coverage.Share:P0}).");
   window.ChangeO(add: true);
   Require(window.OCount == (before.OCount ?? 0) + 1, "The O counter in the player did not count.");
   await StopPlayer(); await _lastRefresh;
   var history = _repository.GetHistory(videoId);
   Require(history.Count(e => e.Kind == HistoryKind.Play && e.Share >= .9) == 1 && history.Count(e => e.Kind == HistoryKind.O) == 1,
    "The playback or the O were not noted in the history: " + string.Join(", ", history.Select(e => $"{e.Kind} {e.Share:P0}")));
   var after = _repository.GetMedia(videoId)!;
   Require(after.WatchCount == (before.WatchCount ?? Math.Max(0, before.PlayCount ?? 0)) + 1, "The playback did not count as watched.");
   Require(HistoryLabel.Text.Contains('%') && HistoryLabel.Text.Contains(" · O", StringComparison.Ordinal), "The details do not list the history: " + HistoryLabel.Text);
   HistorySection.BringIntoView(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Capture(RootContent, Path.Combine(directory, "history-details.png"));
  }
  await RunStatisticsSmokeTestAsync(directory, videoId);
  _repository.ClearHistory(videoId); ShowHistory(_repository.GetMedia(videoId));
  Require(_repository.GetHistory(videoId).Count == 0 && !ClearHistoryButton.IsEnabled, "Deleting the history of the item did not empty it.");
  _repository.Upsert(_repository.GetMedia(videoId)! with { OCount = before.OCount, WatchCount = before.WatchCount });
 }

 /// <summary>Statistics: the window counts the library and the playbacks, and a click on the videos bar shows the videos.</summary>
 private async Task RunStatisticsSmokeTestAsync(string directory, Guid videoId)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  var window = ShowStatistics();
  try
  {
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await window.Loading;
   var statistics = window.Statistics ?? throw new InvalidOperationException("The statistics were not counted.");
   Require(statistics.Media == _repository.QueryPage(new MediaQuery { Limit = 1 }).TotalCount && statistics.Kinds.Any(k => k.View == "Video"), "The statistics did not count the library.");
   Require(statistics.MostWatched.Any(m => m.MediaId == videoId) || _repository.GetHistory(videoId).Count == 0, "The watched video is missing among the most watched.");
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Capture((FrameworkElement)window.Content, Path.Combine(directory, "statistics.png"));
   var videos = window.Bars.First(b => b.Bar.View == "Video");
   videos.Button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await _lastRefresh;
   Require(_view == "Video", "A click on the videos bar did not show the videos.");
  }
  finally { window.Close(); ApplyViewState(new ViewState()); await _lastRefresh; }
 }
}
