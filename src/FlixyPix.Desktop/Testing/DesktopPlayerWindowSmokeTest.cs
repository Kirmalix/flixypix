using System.Windows;
using System.Windows.Threading;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>The real player window around the 16:9 test clip: the video area has the shape of the picture, opening the
 /// mark list keeps the picture as large and widens the window, and the window reports its place for the next video.</summary>
 private async Task RequirePlayerWindowFollowsPictureAsync(VideoPlayerWindow window)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  async Task Idle() { for (var round = 0; round < 3; round++) await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
  bool Fits((int Width, int Height) area) => Math.Abs(area.Width - area.Height * 16.0 / 9) <= 2;
  if (window.IsFullscreen || window.WindowState != WindowState.Normal) return;
  await Idle();
  Require(window.PictureAspect is { } aspect && Math.Abs(aspect - 16.0 / 9) < .01, $"The player did not know the shape of the picture ({window.PictureAspect}).");
  var reported = 0; void Count(PlayerWindowPlace _) => reported++; window.PlaceChanged += Count;
  try
  {
   var opened = window.VideoAreaPixels;
   Require(window.FollowsPicture && Fits(opened), $"The player did not open in the shape of the 16:9 picture: {opened}, following {window.FollowsPicture}.");
   window.FitWindowToVideo(); await Idle();
   var before = window.VideoAreaPixels;
   Require(window.FollowsPicture && Fits(before), $"The video area {before} does not have the shape of the 16:9 picture.");
   var marks = window.MarkPanelVisible; var width = window.ActualWidth;
   window.ToggleMarkPanel(); await Idle();
   var after = window.VideoAreaPixels;
   Require(window.MarkPanelVisible != marks && Math.Abs(after.Width - before.Width) <= 2 && Fits(after) && (marks ? window.ActualWidth < width : window.ActualWidth > width),
    $"The mark list did not keep the picture size {before} → {after} or did not change the window width ({width} → {window.ActualWidth}).");
   window.ToggleMarkPanel(); await Idle();
   Require(reported > 0 && window.Place is { LongSide: > 0, Fullscreen: false } place && Math.Abs(place.LongSide - before.Width) <= 2,
    $"The window did not report its place with the picture size ({window.Place}).");
  }
  finally { window.PlaceChanged -= Count; }
 }
}
