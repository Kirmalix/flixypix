using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Scene wall: lists a scene and a bookmark of the video, filters by text and kind, makes and plays the clip of
 /// a hovered tile in the clip cache, ends it on leave and plays the video from the mark on a click.</summary>
 private async Task RunSceneWallSmokeTestAsync(string directory, Guid videoId)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  var scene = _repository.UpsertScene(new Scene { MediaId = videoId, Title = "Wand-Szene", StartSeconds = .1, EndSeconds = .6 });
  var bookmark = _repository.UpsertBookmark(new Bookmark { MediaId = videoId, Title = "Wand-Marke", PositionSeconds = .3 });
  var window = ShowSceneWall();
  try
  {
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await window.Loading;
   Require(window.Tiles.Any(t => t.Mark.Id == scene.Id) && window.Tiles.Any(t => t.Mark.Id == bookmark.Id), "The scene wall did not list the scene and the bookmark.");
   window.Filter("wand-szene", "", null); await window.Loading;
   Require(window.Tiles.Count == 1 && window.Tiles[0].Mark.Id == scene.Id, "The text filter did not narrow the wall to the scene.");
   window.Filter("", "", WallMarkKind.Bookmark); await window.Loading;
   Require(window.Tiles.Any(t => t.Mark.Id == bookmark.Id) && window.Tiles.All(t => t.Mark.Kind == WallMarkKind.Bookmark), "The kind filter did not keep only bookmarks.");
   window.Filter("Wand", "", null); await window.Loading;
   var tile = window.Tiles.Single(t => t.Mark.Id == scene.Id);
   if (ToolExists(_settings.Ffmpeg, "ffmpeg"))
   {
    window.HoverOver(tile); await window.Hovering;
    Require(window.ClipsShown == 1 && tile.Clip is { } clip && File.Exists(clip.LocalPath) && clip.LocalPath.StartsWith(ClipFolder, StringComparison.OrdinalIgnoreCase),
     "Hovering a tile did not make and show its clip in the clip cache.");
    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    Capture((FrameworkElement)window.Content, Path.Combine(directory, "scene-wall.png"));
    window.HoverOver(null);
    Require(tile.Clip == null, "Leaving the tile did not end its clip.");
    // "Play by themselves" plays the tiles in view without hovering and is remembered; switching it off ends them.
    window.SetAutoplay(true);
    for (var wait = 0; window.PlayingCount < 2 && wait < 200; wait++) { await Task.Delay(100); await window.Autoplaying; }
    Require(window.PlayingCount == 2 && window.Tiles.All(t => t.Clip != null) && _repository.GetPreferences().Previews.SceneWallAutoplay,
     "The scene wall did not play the tiles in view by themselves or did not remember the choice.");
    window.SetAutoplay(false);
    Require(window.PlayingCount == 0 && window.Tiles.All(t => t.Clip == null) && !_repository.GetPreferences().Previews.SceneWallAutoplay,
     "Switching off did not end the clips or was not remembered.");
   }
   if (ToolExists(_settings.Mpv, "mpv"))
   {
    await window.Open(tile);
    Require(_playingId == videoId, "A click on a tile did not play its video.");
    await StopPlayer();
   }
  }
  finally
  {
   window.Close();
   _repository.DeleteScene(scene.Id); _repository.DeleteBookmark(bookmark.Id);
  }
 }

 /// <summary>Video wall: switching to it plays the clip of the video in view without hovering, within the limit;
 /// switching back to tiles ends every clip.</summary>
 private async Task RunVideoWallSmokeTestAsync(string directory, Guid videoId)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  if (!ToolExists(_settings.Ffmpeg, "ffmpeg")) return;
  var before = _repository.GetPreferences().Previews; var mode = ViewChoice.SelectedIndex;
  _repository.SavePreviewPreferences(before with { WallClips = 1, ClipSegments = 3, ClipSegmentMilliseconds = 250 });
  HoverFollowsMouse = false;
  try
  {
   await RefreshAsync(videoId);
   ViewChoice.SelectedIndex = 2;
   var card = MediaList.Items.Cast<MediaCard>().Single(c => c.Item.Id == videoId);
   for (var wait = 0; card.PreviewClip == null && wait < 200; wait++) { await Task.Delay(100); await VideoWallTask; }
   Require(card.PreviewClip != null && File.Exists(card.PreviewClip.LocalPath) && WallPlaying.Count == 1, "The video wall did not play the clip of the video in view.");
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Capture(RootContent, Path.Combine(directory, "video-wall.png"));
   ViewChoice.SelectedIndex = 0; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Require(card.PreviewClip == null && WallPlaying.Count == 0, "Leaving the video wall did not end its clips.");
  }
  finally { ViewChoice.SelectedIndex = mode; _repository.SavePreviewPreferences(before); HoverFollowsMouse = true; }
 }
}
