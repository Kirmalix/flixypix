using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Hover preview on video tiles, ported from the original's "Hover preview" (frame mode): after a short
/// delay the tile cycles through evenly spaced frames. Frames are rendered once with FFmpeg into the library's
/// hover cache, so later hovers start at once; leaving the tile cancels rendering and restores the thumbnail.</summary>
public partial class MainWindow
{
 private MediaCard? _hoverCard;
 private CancellationTokenSource? _hoverRun;
 internal Task HoverPreviewTask { get; private set; } = Task.CompletedTask;
 internal int HoverFramesShown { get; private set; }
 /// <summary>Off while a test drives the preview, so the real mouse pointer over the test window cannot interfere.</summary>
 internal bool HoverFollowsMouse { get; set; } = true;

 private void InitializeHoverPreview()
 {
  MediaList.MouseMove += (_, e) => { if (HoverFollowsMouse) HoverOver(e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(MediaList, source) is ListBoxItem { DataContext: MediaCard card } ? card : null); };
  MediaList.MouseLeave += (_, _) => { if (HoverFollowsMouse) HoverOver(null); };
 }

 internal void HoverOver(MediaCard? card)
 {
  if (ReferenceEquals(card, _hoverCard)) return;
  StopHoverPreview(); _hoverCard = card;
  if (card == null || card.Item.Kind != MediaKind.Video || card.Item.DurationSeconds is not > 0 || ViewChoice.SelectedIndex != 0) return;
  var previews = _repository.GetPreferences().Previews;
  if (!previews.HoverEnabled) return;
  var run = new CancellationTokenSource(); _hoverRun = run;
  HoverPreviewTask = RunHoverPreviewAsync(card, previews, run.Token);
 }

 private void StopHoverPreview()
 {
  // The running preview still observes the token; it is not disposed while in use.
  _hoverRun?.Cancel(); _hoverRun = null;
  _hoverCard?.ShowPreviewFrame(null, "");
 }

 private async Task RunHoverPreviewAsync(MediaCard card, PreviewPreferences previews, CancellationToken token)
 {
  try
  {
   await Task.Delay(HoverPreview.StartDelay(previews.HoverDelay), token);
   if (previews.HoverMode == HoverMode.Clip) { await ShowHoverClipAsync(card, previews, token); return; }
   var item = card.Item; var times = HoverPreview.FrameTimes(item.DurationSeconds!.Value, previews.HoverFrames);
   var folder = Path.Combine(_settings.DataDirectory, "hover-cache");
   FileAccessPolicy.Current.EnsureWritable(folder); Directory.CreateDirectory(folder);
   var interval = HoverPreview.Interval(previews.HoverSpeed);
   var frames = new List<(ImageSource Image, string Time)>();
   // A frame decoded while the pointer left must not appear afterwards and stay on the tile.
   void Show(int index) { token.ThrowIfCancellationRequested(); card.ShowPreviewFrame(frames[index].Image, previews.HoverTimestamp ? frames[index].Time : ""); HoverFramesShown++; }
   for (var index = 0; index < times.Count; index++)
   {
    var started = DateTime.UtcNow;
    var path = Path.Combine(folder, HoverPreview.CacheFileName(item.Id, times.Count, index));
    if (!File.Exists(path))
    {
     if (!File.Exists(item.Path)) return;
     // Render beside the target and move it in place, so a cancelled render never leaves a broken frame.
     var part = path[..^4] + ".part.jpg";
     await _tools.CreateThumbnailAsync(item.Path, part, times[index], token);
     File.Move(part, path, true);
    }
    var image = await Task.Run(() => MediaCard.LoadBitmap(path, 480), token);
    if (image == null) continue;
    frames.Add((image, JumpMarks.FormatTime(times[index])));
    var wait = interval - (DateTime.UtcNow - started);
    if (frames.Count > 1 && wait > TimeSpan.Zero) await Task.Delay(wait, token);
    Show(frames.Count - 1);
   }
   for (var index = 0; frames.Count > 1; index = (index + 1) % frames.Count) { await Task.Delay(interval, token); Show(index); }
  }
  catch (OperationCanceledException) { }
  catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException or Win32Exception)
  {
   if (!token.IsCancellationRequested) Status.Text = T("Hover preview not possible · ") + error.Message;
  }
 }

 /// <summary>Clip mode: plays the preview clip, made now if it is missing.</summary>
 private async Task ShowHoverClipAsync(MediaCard card, PreviewPreferences previews, CancellationToken token)
 {
  var item = card.Item; var spec = ClipSpec(previews); var clip = PreviewClipPath(item, spec);
  if (!File.Exists(clip))
  {
   if (!File.Exists(item.Path)) return;
   FileAccessPolicy.Current.EnsureWritable(ClipFolder);
   await _tools.CreatePreviewClipAsync(item.Path, clip, item.DurationSeconds!.Value, spec, token);
  }
  token.ThrowIfCancellationRequested();
  card.ShowPreviewClip(clip); HoverClipsShown++;
 }
 internal int HoverClipsShown { get; private set; }
}
