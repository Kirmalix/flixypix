using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Compare: C in the loupe keeps the image as the reference and shows the next one; zooming one side zooms
 /// the other, the next candidate starts fitted on both sides, and C ends the comparison. C on two chosen tiles compares
 /// them. Survey: N shows the chosen images side by side, the selection follows, a digit rates the selected image, X
 /// takes it out, Enter shows it in the loupe and G leads back to the survey, then to the tiles.</summary>
 private async Task RunCompareAndSurveySmokeAsync(string directory, MediaItem first, Func<Task> idle, Func<Task> settled)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  async Task Press(FrameworkElement target, Key key)
  {
   target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
   await idle(); if (target is ImageViewer viewer) await viewer.ReadyTask; await settled();
  }
  async Task<ImageViewer> OpenLoupeOn(MediaItem item)
  {
   OpenFromDoubleClick(_repository.GetMedia(item.Id)!);
   for (var wait = 0; Loupe == null && wait < 60; wait++) await Task.Delay(50);
   var opened = Loupe ?? throw new InvalidOperationException("The loupe did not open.");
   await idle(); await opened.ReadyTask; return opened;
  }

  // C in the loupe: the image stays on the left, the next one is shown beside it.
  var loupe = await OpenLoupeOn(first);
  if (loupe.CurrentIndex == loupe.Count - 1) { await Press(loupe, Key.Left); }
  var referenceItem = loupe.CurrentItem!;
  await Press(loupe, Key.C);
  var reference = CompareReference ?? throw new InvalidOperationException("C did not start a comparison.");
  await reference.ReadyTask; await idle();
  Require(ReferenceEquals(Loupe, loupe) && reference.IsReference && reference.CurrentItem?.Id == referenceItem.Id && reference.HasImage, "The reference does not show the image the comparison started from.");
  Require(loupe.CurrentItem?.Id != referenceItem.Id && loupe.IsVisible && reference.IsVisible && reference.ActualWidth > 100 && loupe.ActualWidth > 100, "The comparison does not show the next image beside the reference.");
  // Zooming one side zooms the other to the same scale of the whole image.
  await Press(loupe, Key.Z);
  for (var wait = 0; reference.Fitted && wait < 40; wait++) await Task.Delay(50);
  await reference.ReadyTask; await idle();
  Require(!loupe.Fitted && !reference.Fitted && loupe.Place is { } one && reference.Place is { } other && Math.Abs(one.Scale - other.Scale) < .02,
   $"The reference did not follow the zoom ({loupe.Place?.Scale:0.###} / {reference.Place?.Scale:0.###}).");
  await Task.Delay(200); await idle();
  Capture(RootContent, Path.Combine(directory, "loupe-compare.png"));
  // The next candidate starts fitted, and so does the reference.
  await Press(loupe, Key.Left);
  Require(loupe.Fitted && reference.Fitted && ReferenceEquals(CompareReference, reference), "The next candidate did not start fitted on both sides.");
  await Press(loupe, Key.C);
  Require(CompareReference == null && ReferenceEquals(Loupe, loupe) && loupe.IsVisible && loupe.ActualWidth > 300, "C did not end the comparison, or the loupe did not take the whole place again.");
  Require(!Status.Text.Contains(referenceItem.Title, StringComparison.Ordinal), "The hint of the comparison stayed after it ended.");
  await Press(loupe, Key.G);
  Require(Loupe == null && MediaPane.Visibility == Visibility.Visible, "G did not lead back to the tiles.");

  // Two chosen tiles: C compares them, G ends the comparison first and then the loupe.
  var cards = MediaList.Items.OfType<MediaCard>().Where(card => card.Item.Kind == MediaKind.Image).Take(3).ToArray();
  Require(cards.Length == 3, "The compare test needs three image tiles.");
  MediaList.SelectedItems.Clear(); MediaList.SelectedItems.Add(cards[0]); MediaList.SelectedItems.Add(cards[1]); await idle();
  CompareSelection();
  for (var wait = 0; (Loupe == null || CompareReference == null) && wait < 60; wait++) await Task.Delay(50);
  loupe = Loupe ?? throw new InvalidOperationException("C on two tiles did not open the loupe.");
  await idle(); await loupe.ReadyTask; if (CompareReference is { } pair) await pair.ReadyTask; await idle();
  Require(CompareReference?.CurrentItem?.Id == cards[0].Item.Id && loupe.CurrentItem?.Id == cards[1].Item.Id,
   $"C on two tiles did not compare them (reference {CompareReference?.CurrentItem?.Title ?? "none"}, shown {loupe.CurrentItem?.Title ?? "none"}).");
  await Press(loupe, Key.G);
  Require(CompareReference == null && ReferenceEquals(Loupe, loupe), "G did not end the comparison first.");
  await Press(loupe, Key.G);
  Require(Loupe == null, "G did not lead back to the tiles after the comparison.");

  // N: the chosen images side by side.
  var ratings = cards.ToDictionary(card => card.Item.Id, card => _repository.GetMedia(card.Item.Id)!.Rating);
  try
  {
   MediaList.SelectedItems.Clear(); foreach (var card in cards) MediaList.SelectedItems.Add(card); await idle();
   Require(OpenSurvey(), "N did not open the survey.");
   var survey = Survey ?? throw new InvalidOperationException("The survey is missing.");
   await idle(); await survey.ReadyTask; await idle();
   Require(MediaPane.Visibility == Visibility.Hidden && survey.IsVisible && survey.Items.Count == 3 && survey.ShownPictures == 3, $"The survey does not show the three images ({survey.ShownPictures}).");
   Require(SelectedMedia.Count == 1 && Selected?.Id == survey.SelectedItem?.Id && survey.SelectedItem?.Id == cards[0].Item.Id, "The selection does not follow the survey.");
   await Task.Delay(300); await idle();
   Capture(RootContent, Path.Combine(directory, "survey.png"));
   await Press(survey, Key.Right);
   var chosen = survey.SelectedItem!;
   Require(chosen.Id == cards[1].Item.Id && Selected?.Id == chosen.Id, "The arrow key did not select the next image of the survey.");
   await Press(survey, Key.D3);
   Require(_repository.GetMedia(chosen.Id)!.Rating == 6 && cards.Where(card => card.Item.Id != chosen.Id).All(card => _repository.GetMedia(card.Item.Id)!.Rating == ratings[card.Item.Id]), "The digit did not rate the selected image only.");
   await Press(survey, Key.X);
   Require(ReferenceEquals(Survey, survey) && survey.Items.Count == 2 && survey.Items.All(item => item.Id != chosen.Id) && _repository.GetMedia(chosen.Id) != null, "X did not take the image out of the survey, or took it out of the library.");
   var shown = survey.SelectedItem!;
   await Press(survey, Key.Enter);
   for (var wait = 0; Loupe == null && wait < 60; wait++) await Task.Delay(50);
   loupe = Loupe ?? throw new InvalidOperationException("Enter in the survey did not open the loupe."); await loupe.ReadyTask;
   Require(Survey == null && loupe.CurrentItem?.Id == shown.Id, "The loupe does not show the image chosen in the survey.");
   await Press(loupe, Key.G);
   Require(Loupe == null && ReferenceEquals(Survey, survey) && survey.IsVisible && MediaPane.Visibility == Visibility.Hidden, "G in the loupe did not lead back to the survey.");
   await Press(survey, Key.G);
   for (var wait = 0; MediaPane.Visibility != Visibility.Visible && wait < 60; wait++) await Task.Delay(50);
   Require(Survey == null && MediaPane.Visibility == Visibility.Visible && MediaList.IsVisible, "G did not lead back from the survey to the tiles.");
  }
  finally
  {
   CloseSurvey(reselect: false);
   foreach (var (id, rating) in ratings) if (_repository.GetMedia(id) is { } item && item.Rating != rating) _repository.Upsert(item with { Rating = rating });
   await settled();
  }
 }
}
