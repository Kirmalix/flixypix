using System.ComponentModel;
using System.IO;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Video wall (feature plan P3): the tile view in which the preview clips of the videos in view play by
/// themselves, at most as many at once as the library allows. Missing clips are made one after another, top first;
/// scrolling cancels what is no longer in view.</summary>
public partial class MainWindow
{
 private CancellationTokenSource? _wallRun;
 private readonly List<MediaCard> _wallPlaying = [];
 internal Task VideoWallTask { get; private set; } = Task.CompletedTask;
 internal int WallClipsShown { get; private set; }
 internal IReadOnlyList<MediaCard> WallPlaying => _wallPlaying;
 private bool VideoWall => ViewChoice.SelectedIndex == 2;

 private void UpdateVideoWall()
 {
  _wallRun?.Cancel(); _wallRun = null;
  var wanted = new List<MediaCard>();
  if (VideoWall && _visibleFirst >= 0)
  {
   var limit = _repository.GetPreferences().Previews.WallClips;
   for (var index = Math.Max(_visibleFirst, _windowStart); index <= _visibleLast && wanted.Count < limit; index++)
   {
    var local = index - _windowStart;
    if (local >= MediaList.Items.Count) break;
    if (MediaList.Items[local] is MediaCard { Item: { Kind: MediaKind.Video, DurationSeconds: > 0 } } card) wanted.Add(card);
   }
  }
  foreach (var card in _wallPlaying.Except(wanted).ToList()) { card.ShowPreviewFrame(null, ""); _wallPlaying.Remove(card); }
  if (wanted.All(_wallPlaying.Contains)) return;
  var run = _wallRun = new CancellationTokenSource();
  VideoWallTask = PlayWallAsync(wanted, run.Token);
 }

 private async Task PlayWallAsync(IReadOnlyList<MediaCard> cards, CancellationToken token)
 {
  try
  {
   // Let scrolling settle before clips are made for tiles that may pass by.
   await Task.Delay(150, token);
   var spec = ClipSpec(_repository.GetPreferences().Previews); var canRender = ToolExists(_settings.Ffmpeg, "ffmpeg");
   foreach (var card in cards)
   {
    if (_wallPlaying.Contains(card)) continue;
    var clip = PreviewClipPath(card.Item, spec);
    try
    {
     if (!File.Exists(clip))
     {
      if (!canRender || !File.Exists(card.Item.Path)) continue;
      FileAccessPolicy.Current.EnsureWritable(ClipFolder);
      await _tools.CreatePreviewClipAsync(card.Item.Path, clip, card.Item.DurationSeconds!.Value, spec, token);
     }
    }
    // One video without a clip keeps its thumbnail; the others still play.
    catch (Exception error) when (!token.IsCancellationRequested && error is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException or ArgumentException or Win32Exception)
    { Status.Text = T("Video wall · ") + error.Message; continue; }
    token.ThrowIfCancellationRequested();
    card.ShowPreviewClip(clip); _wallPlaying.Add(card); WallClipsShown++;
   }
  }
  catch (OperationCanceledException) { }
 }
}
