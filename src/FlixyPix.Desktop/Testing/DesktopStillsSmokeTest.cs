using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Stills mode with a real player on the ten frames of the test clip: steps land on the neighbouring frames, a
 /// saved picture goes into the folder of its video and joins the library with its origin, a marked range becomes
 /// exactly its frames, and those frames are the ones the player showed.</summary>
 private async Task RunStillsSmokeTestAsync(string directory, MediaItem video)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  var window = _videoWindow!; var shots = Path.Combine(directory, "stills"); window.ScreenshotFolder = shots;
  bool At(double seconds) => Math.Abs(window.LastPositionSeconds - seconds) < .011;
  async Task Prepared() { for (var wait = 0; wait < 400 && (MediaPreparation.Running || _operation != null); wait++) await Task.Delay(50); await _lastRefresh; }
  var added = new List<Guid>();
  try
  {
   await window.SeekToAsync(.3);
   await window.ToggleStillsModeAsync();
   Require(window.StillsMode && window.IsPaused && Math.Abs(window.FrameRate - 10) < .01 && window.FrameText.Contains("10", StringComparison.Ordinal), "Stills mode did not start paused with the frame rate: " + window.FrameText);
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Capture((FrameworkElement)window.Content, Path.Combine(directory, "player-stills.png"));
   await window.StepFramesAsync(1); Require(At(.4), $"One frame forward landed at {window.LastPositionSeconds}.");
   await window.StepFramesAsync(-1); Require(At(.3), $"One frame back landed at {window.LastPositionSeconds}.");
   await window.StepFramesAsync(3); Require(At(.6), $"Three frames forward landed at {window.LastPositionSeconds}.");
   await window.StepFramesAsync(-4); Require(At(.2), $"Four frames back landed at {window.LastPositionSeconds}.");
   // The arrow key steps, although it is the shortcut of the small jump otherwise.
   var right = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
   window.RaiseEvent(right); for (var wait = 0; wait < 40 && !At(.3); wait++) await Task.Delay(25);
   Require(right.Handled && At(.3), $"The arrow key did not step one frame ({window.LastPositionSeconds}).");

   var still = await window.SaveStillAsync();
   Require(File.Exists(still) && Path.GetDirectoryName(still) == Path.Combine(shots, ScreenshotPreferences.FolderName(video.Title)) && Path.GetFileName(still).Contains("00-00-00-300", StringComparison.Ordinal),
    "The picture did not go into the folder of its video with its time: " + still);
   await Prepared();
   var image = _repository.GetMediaByPath(still);
   Require(image is { Kind: MediaKind.Image } && Stills.SourceOf(image) is { } origin && origin.VideoId == video.Id && Math.Abs(origin.Seconds - .3) < .011, "The picture did not join the library with its video and time.");
   added.Add(image!.Id);
   Require(MediaMenu([image]).OfType<MenuCommand>().Any(command => command.Label == T("Show this moment in the video")), "The picture does not lead back to its video.");

   // A range from the frame at 0.2 s to the frame at 0.5 s holds four frames; every second one makes two.
   await window.SeekToAsync(.2); window.Act(PlayerActions.ClipStart); await window.SeekToAsync(.5); window.Act(PlayerActions.ClipEnd);
   StillExportWindow? asked = null;
   window.ExportChoice = dialog => { asked = dialog; return dialog.Choose(StillSpacing.EveryFrame, 1); };
   await window.ExportRangeAsync(); await Prepared();
   var rangeFolder = Directory.GetDirectories(window.StillsFolder).Single();
   var frames = Directory.GetFiles(rangeFolder).Order(StringComparer.Ordinal).ToArray();
   Require(frames.Length == 4 && asked!.EstimateText == Tn(4, "About 1 picture in full resolution", "About {0:N0} pictures in full resolution")
    && frames.Select(Path.GetFileName).SequenceEqual(new[] { 200, 300, 400, 500 }.Select(ms => ScreenshotPreferences.FolderName(video.Title) + $" 00-00-00-{ms}.png")),
    "The range did not become its four frames: " + string.Join(", ", frames.Select(Path.GetFileName)) + " · " + window.MessageText);
   var known = frames.Select(_repository.GetMediaByPath).OfType<MediaItem>().ToList(); added.AddRange(known.Select(item => item.Id));
   Require(known.Count == 4 && known.All(item => Stills.SourceOf(item)?.VideoId == video.Id), "The pictures of the range did not join the library.");
   // Frame exact: the picture the player saved at 0.3 s is the range's second frame, not one of its neighbours. Player and
   // FFmpeg convert colours a little differently, so only the pixels that move from frame to frame are compared.
   var moving = Moving(frames[0], frames[2]);
   long same = Difference(still, frames[1], moving), before = Difference(still, frames[0], moving), after = Difference(still, frames[2], moving);
   Require(moving.Count > 200 && same * 2 < before && same * 2 < after, $"The frames of the range are not the ones the player showed ({before}, {same}, {after} on {moving.Count} pixels).");
   window.ExportChoice = dialog => dialog.Choose(StillSpacing.EveryNthFrame, 2);
   await window.ExportRangeAsync(); await Prepared();
   var all = Directory.GetFiles(rangeFolder);
   Require(all.Length == 6 && Difference(frames[2], all.Single(file => Path.GetFileName(file).Contains("-400 (2)", StringComparison.Ordinal)), moving) == 0, "Every second frame did not make two pictures: " + window.MessageText);
   added.AddRange(all.Select(_repository.GetMediaByPath).OfType<MediaItem>().Select(item => item.Id));
   window.ExportChoice = null; await window.ClearRangeAsync();

   // J plays backward, K stops. In a clip that is one group of pictures the player does not get going by itself, so
   // this is the way through single steps back.
   await window.SeekToAsync(.8);
   window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.J) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
   for (var wait = 0; wait < 240 && window.LastPositionSeconds > .7; wait++) await Task.Delay(25);
   var reached = window.LastPositionSeconds;
   window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.K) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
   for (var wait = 0; wait < 40 && !window.IsPaused; wait++) await Task.Delay(25);
   await Task.Delay(200);
   Require(reached <= .7 && window.IsPaused, $"J did not play backward or K did not stop ({reached}, paused {window.IsPaused}) · " + window.MessageText);

   await window.ToggleStillsModeAsync();
   Require(!window.StillsMode, "Stills mode did not end.");
  }
  finally
  {
   window.ExportChoice = null; window.StillSaved = null;
   foreach (var id in added.Distinct()) _repository.Delete(id);
   await Prepared(); await RefreshAsync();
  }
 }

 private static byte[] StillPixels(string path)
 {
  var frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
  var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
  var pixels = new byte[converted.PixelWidth * 4 * converted.PixelHeight];
  converted.CopyPixels(pixels, converted.PixelWidth * 4, 0); return pixels;
 }

 /// <summary>The pixels that clearly differ between two pictures of the same size.</summary>
 private static HashSet<int> Moving(string first, string second)
 {
  var a = StillPixels(first); var b = StillPixels(second); var moving = new HashSet<int>();
  for (var pixel = 0; pixel < Math.Min(a.Length, b.Length) / 4; pixel++)
   if (Math.Abs(a[pixel * 4] - b[pixel * 4]) > 32 || Math.Abs(a[pixel * 4 + 1] - b[pixel * 4 + 1]) > 32 || Math.Abs(a[pixel * 4 + 2] - b[pixel * 4 + 2]) > 32) moving.Add(pixel);
  return moving;
 }

 /// <summary>Sum of the differences of two pictures of the same size over the given pixels.</summary>
 private static long Difference(string first, string second, HashSet<int> pixels)
 {
  var a = StillPixels(first); var b = StillPixels(second);
  if (a.Length != b.Length) return long.MaxValue / 8;
  long sum = 0;
  foreach (var pixel in pixels) for (var channel = 0; channel < 3; channel++) sum += Math.Abs(a[pixel * 4 + channel] - b[pixel * 4 + channel]);
  return sum;
 }
}
