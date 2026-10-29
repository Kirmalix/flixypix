using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Phase 7 library settings take effect: tiles show file names, wrap and hide progress, a click plays, tiles stay
 /// flat, the page bar hides, back/forward keeps fewer steps, the window place is not restored, tag filters start with
 /// "at least one", the viewer cross-fades, files go to the recycle bin, and the start page shows "Zuletzt gesehen".</summary>
 private async Task RunLibrarySettingsSmokeTestAsync(string directory, string fixtureDirectory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  async Task Settled() { await _lastRefresh; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
  var before = _repository.GetPreferences(); var placeBefore = _settings.MainWindowPlace; var recycleBefore = SendToRecycleBin;
  try
  {
   var video = _repository.Search().FirstOrDefault(i => i.Kind == MediaKind.Video && i.DurationSeconds > 10);
   if (video != null) _repository.Upsert(video with { PlaybackPositionSeconds = video.DurationSeconds!.Value / 2, Title = "Ein ganz besonders langer Titel, der umbrechen soll" });
   var settings = new SettingsWindow(this, _repository, _settings, _settings.DataDirectory, () => { }, () => { }); settings.Show();
   try
   {
    var library = settings.Library;
    library.ShowTitles.IsChecked = false; library.FullTitles.IsChecked = true; library.ShowProgress.IsChecked = false; library.ClickPlays.IsChecked = true;
    library.HoverLift.IsChecked = false; library.ShowPager.IsChecked = false; library.HistoryLength.Value = 3; library.RememberWindow.IsChecked = false;
    library.TagsMatchAny.IsChecked = true; library.RecycleBin.IsChecked = true; library.ImageFade.IsChecked = true; library.RecentDays.Value = 3;
    library.RemoveCharacters.Text = "#"; library.SpaceCharacters.Text = "_"; library.SkipImages.Text = "skipme";
    Require(settings.TrySave(), "The library options could not be saved.");
    settings.SelectPage("General"); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    Capture((FrameworkElement)settings.Content, Path.Combine(directory, "settings-general.png"));
   }
   finally { settings.Close(); }

   ApplyViewState(new ViewState { View = "all" }); await Settled();
   var cards = MediaList.Items.Cast<MediaCard>().ToList();
   Require(cards.Count > 0 && cards.All(c => c.Title == Path.GetFileNameWithoutExtension(c.Item.Path)), "Tiles did not show the file name instead of the title.");
   Require(cards.All(c => c.TitleWrapping == TextWrapping.Wrap && c.TitleHeight == 38), "Long titles did not wrap.");
   if (video != null) Require(cards.Where(c => c.Item.Id == video.Id).All(c => c.ProgressVisibility == Visibility.Visible && c.TileProgressVisibility == Visibility.Collapsed),
    "The progress bar showed although it is switched off.");
   Require(!Motion.TilesLift && PagerBar.Visibility != Visibility.Visible, "Tiles still lift or the page bar still shows.");
   Require(History.Capacity == 3, "Back and forward did not take the number of steps.");
   _settings = _settings with { MainWindowPlace = new WindowPlace(Left, Top, Math.Max(MinWidth, ActualWidth), Math.Max(MinHeight, ActualHeight), false) };
   Require(PlaceToRestore() == null, "The window place would be restored although it should not be remembered.");
   Require(_tagsMatchAny, "New tag filters did not start with \"at least one\".");

   // Import: titles lose "#", "_" becomes a space; images named with "skipme" stay out of folder imports.
   var importFolder = Path.Combine(fixtureDirectory, "phase7"); Directory.CreateDirectory(importFolder);
   var source = Directory.GetFiles(fixtureDirectory, "*.png").First();
   File.Copy(source, Path.Combine(importFolder, "Am_See#2.png"), true); File.Copy(source, Path.Combine(importFolder, "skipme-cover.png"), true);
   await ImportPaths([importFolder]);
   var cleaned = _repository.Search().SingleOrDefault(i => i.Path.EndsWith("Am_See#2.png", StringComparison.OrdinalIgnoreCase));
   Require(cleaned?.Title == "Am See2" && !_repository.Search().Any(i => i.Path.Contains("skipme", StringComparison.OrdinalIgnoreCase)),
    $"Import did not clean the title or skip the image ({cleaned?.Title ?? "fehlt"}).");

   // A plain click on a tile's picture plays (an image opens in the viewer); the viewer then cross-fades to the next image.
   await RefreshAsync(cleaned!.Id); await Settled();
   var item = (ListBoxItem)MediaList.ItemContainerGenerator.ContainerFromItem(MediaList.SelectedItem)!;
   var picture = Descendants<AspectBox>(item).First();
   var target = Descendants<Image>(picture).First();
   target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
   var armed = $"armed {_clickToPlay != null}, drag {_dragStart != null}, display {MediaCard.Display.ClickPlays}, on picture {OnPicture(target, item)}";
   target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
   for (var wait = 0; _viewer is not ImageViewerWindow && wait < 40; wait++) await Task.Delay(50);
   Require(_viewer is ImageViewerWindow, "A click on the picture did not open the medium (" + armed + ").");
   var viewer = (ImageViewerWindow)_viewer!; await viewer.ReadyTask;
   viewer.Navigate(1); await viewer.ReadyTask;
   Require(viewer.FadeOnChange && viewer.LastChangeFaded, "The viewer did not cross-fade to the next image.");
   StopSlideshow();

   // Deleting moves the file to the recycle bin (recorded here, so no test file lands in the real bin).
   var recycled = new List<string>();
   SendToRecycleBin = path => { recycled.Add(path); File.Delete(path); };
   DeleteConfirmed(cleaned); await Settled();
   Require(recycled.SequenceEqual([cleaned.Path]) && _repository.GetMedia(cleaned.Id) == null, "Deleting did not go to the recycle bin.");

   // The start page has "Zuletzt gesehen" with what was just played.
   var feed = _repository.BuildHomeFeed(DateTimeOffset.Now);
   Require(feed.Rows.Any(r => r.Kind == HomeRowKind.RecentlyWatched && r.Cards.Count > 0), "The start page has no \"Zuletzt gesehen\" row.");
  }
  finally
  {
   SendToRecycleBin = recycleBefore; _settings = _settings with { MainWindowPlace = placeBefore };
   _repository.SavePreferences(_repository.GetPreferences() with { General = before.General, Home = before.Home });
   History.Capacity = before.General.HistoryLength; ApplyViewState(new ViewState()); await Settled();
  }
 }
}
