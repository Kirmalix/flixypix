using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>The loupe: a double click on an image shows it in place of the tiles; the arrow keys leaf through the list,
 /// the selection and the details follow, a digit rates the image on show, Z changes between the fitted picture and its
 /// pixels, and G leads back to the tile of the image looked at last. An image that is not among the tiles on show
 /// opens in a window as before.</summary>
 private async Task RunLoupeSmokeTestAsync(string directory, MediaItem first)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  async Task Idle() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  async Task Settled() { await _lastRefresh; await Idle(); }
  async Task Key(ImageViewer target, Key key)
  {
   target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
   await Idle(); await target.ReadyTask; await Settled();
  }
  var ratingBefore = _repository.GetMedia(first.Id)!.Rating;
  try
  {
   await RefreshAsync(first.Id); await Settled();
   var images = (int)_repository.CountMedia(_imageQuery);
   Require(images >= 3, $"The loupe test needs three images among the tiles, found {images}.");

   // A double click on an image: the loupe takes the place of the tiles.
   OpenFromDoubleClick(_repository.GetMedia(first.Id)!);
   for (var wait = 0; Loupe == null && wait < 60; wait++) await Task.Delay(50);
   var loupe = Loupe ?? throw new InvalidOperationException("A double click on an image did not open the loupe.");
   await Idle(); await loupe.ReadyTask; await loupe.StripTask; await Idle();
   Require(loupe.Embedded && loupe.HasImage && !loupe.ShowsPlaceholder && loupe.Fitted, "The loupe does not show the image.");
   Require(_viewer == null && MediaPane.Visibility == Visibility.Hidden && loupe.IsVisible && loupe.ActualWidth > 300, "The loupe did not take the place of the tiles.");
   Require(loupe.CurrentItem?.Id == first.Id && Selected?.Id == first.Id && loupe.Count == images, "The loupe does not stand on the image that was opened.");
   Require(loupe.StripCount > 1, "The loupe shows no film strip.");
   await Task.Delay(300); await Idle();
   Capture(RootContent, Path.Combine(directory, "loupe.png"));

   // Leafing through: the selection follows at once, the details a moment later, and the next images are read ahead.
   var start = loupe.CurrentIndex;
   await Key(loupe, System.Windows.Input.Key.Right);
   var second = loupe.CurrentItem ?? throw new InvalidOperationException("The loupe lost its image.");
   Require(loupe.CurrentIndex == start + 1 && second.Id != first.Id && loupe.HasImage && !loupe.ShowsPlaceholder, "The right arrow did not show the next image.");
   Require(Selected?.Id == second.Id && SelectedMedia.Count == 1, "The selection did not follow the loupe.");
   await Task.Delay(400); await Idle();
   Require(_detailsShown?.Id == second.Id, "The details did not follow the loupe.");
   Require(loupe.PrefetchedCount >= 1, "The loupe did not read ahead.");

   // A digit rates the image on show; the loupe stays on it.
   await Key(loupe, System.Windows.Input.Key.D4);
   Require(_repository.GetMedia(second.Id)!.Rating == 8 && _repository.GetMedia(first.Id)!.Rating == ratingBefore, "The digit did not rate the image on show.");
   Require(ReferenceEquals(Loupe, loupe) && loupe.CurrentItem?.Id == second.Id && loupe.CurrentItem.Rating == 8 && loupe.HasImage, "Rating moved the loupe or left it with old data.");
   await Key(loupe, System.Windows.Input.Key.D0);
   Require(_repository.GetMedia(second.Id)!.Rating == 0, "The zero did not remove the rating.");

   // Z: the pixels of the image, and back to the fitted picture.
   var fitted = loupe.ZoomFactor;
   await Key(loupe, System.Windows.Input.Key.Z);
   Require(!loupe.Fitted && loupe.ZoomFactor == 1 && loupe.ShownSize.Width == second.Width, $"Z did not show the pixels of the image ({loupe.ZoomFactor:0.###}, {loupe.ShownSize.Width} of {second.Width}).");
   await Key(loupe, System.Windows.Input.Key.Z);
   Require(loupe.Fitted && Math.Abs(loupe.ZoomFactor - fitted) < .02, "Z did not lead back to the fitted picture.");

   // The ends of the list are those of the tiles: no way round.
   await Key(loupe, System.Windows.Input.Key.Home);
   Require(loupe.CurrentIndex == 0, "Home did not show the first image.");
   await Key(loupe, System.Windows.Input.Key.Left);
   Require(loupe.CurrentIndex == 0, "The loupe went round from the first image to the last.");
   await Key(loupe, System.Windows.Input.Key.End);
   Require(loupe.CurrentIndex == images - 1, "End did not show the last image.");
   var last = loupe.CurrentItem!;

   // G: back to the tiles, at the image looked at last.
   async Task BackToTiles(ImageViewer target, Key key)
   {
    await Key(target, key);
    for (var wait = 0; (Selected?.Id != last.Id || MediaPane.Visibility != Visibility.Visible) && wait < 60; wait++) await Task.Delay(50);
    await Idle();
    Require(Loupe == null && MediaPane.Visibility == Visibility.Visible && MediaList.IsVisible, $"{key} did not lead back to the tiles.");
    Require(Selected?.Id == last.Id && MediaList.ItemContainerGenerator.ContainerFromItem(MediaList.SelectedItem) is ListBoxItem { IsVisible: true }, "Back at the tiles the image looked at last is not selected and in view.");
   }
   await BackToTiles(loupe, System.Windows.Input.Key.G);
   // Esc as well, unless it is the boss key (the default), which goes first in every window.
   var bossKey = AppShell.BossKey; AppShell.BossKey = "F12";
   try
   {
    OpenFromDoubleClick(last);
    for (var wait = 0; Loupe == null && wait < 60; wait++) await Task.Delay(50);
    var again = Loupe ?? throw new InvalidOperationException("The loupe did not open again.");
    await Idle(); await again.ReadyTask;
    await BackToTiles(again, System.Windows.Input.Key.Escape);
   }
   finally { AppShell.BossKey = bossKey; }

   // Not among the tiles on show: a window, as before.
   ApplyViewState(new ViewState { View = "home" }); await Settled();
   var apart = ShowImage(_repository.GetMedia(first.Id)!); await Idle(); await apart.ReadyTask;
   Require(Loupe == null && _viewer is ImageViewerWindow && !apart.Embedded && apart.HasImage, "From the home page the image did not open in a window.");
   CloseImageViewers();
   ApplyViewState(new ViewState()); await Settled();
  }
  finally
  {
   CloseImageViewers();
   await Settled();
  }
 }
}
