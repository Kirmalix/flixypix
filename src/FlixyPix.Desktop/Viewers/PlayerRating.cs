using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Star rating in the player bar: half steps like in the details, saved at once and passed on to the library.</summary>
internal sealed partial class VideoPlayerWindow
{
 private readonly RatingPicker ratingPicker = new(18) { Compact = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
 internal int Rating => ratingPicker.Value;

 private FrameworkElement BuildRating()
 {
  // Arrow keys belong to seeking; the stars take clicks only.
  ratingPicker.Focusable = false;
  ratingPicker.Describe = value => value == 0 ? T("Unrated") : T("{0:0.#} out of 5 stars", value / 2d);
  ratingPicker.Value = repository.GetMedia(media.Id)?.Rating ?? media.Rating;
  ratingPicker.Changed += () => Rate(ratingPicker.Value);
  System.Windows.Automation.AutomationProperties.SetName(ratingPicker, T("Rating"));
  return ratingPicker;
 }

 /// <summary>The rating as one star and its value, for a narrow bar; the click offers the same half steps.</summary>
 private Button RatingChip()
 {
  var chip = new Button { Style = (Style)Application.Current.Resources["Quiet"], Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = T("Rating") };
  System.Windows.Automation.AutomationProperties.SetName(chip, T("Rating"));
  void Show() => chip.Content = ratingPicker.Value == 0 ? "☆" : "★ " + (ratingPicker.Value / 2d).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
  chip.Click += (_, _) => Menus.Open(chip, RatingEntries(), (_, error) => ShowMessage(error.Message), System.Windows.Controls.Primitives.PlacementMode.Top);
  ratingPicker.Changed += Show; Show();
  return chip;
 }

 /// <summary>Saves the rating (0–10, half stars) of the playing medium; the library undo covers it like other quick edits.</summary>
 internal void Rate(int value)
 {
  if (ratingPicker.Value != value) { ratingPicker.Value = value; return; }
  repository.ApplyImmediateEdit([media.Id], ImmediateEditFields.Rating, current => current with { Rating = value });
  if (repository.GetMedia(media.Id) is { } item) MediaChanged?.Invoke(item);
  Notify(value == 0 ? T("Rating removed") : T("Rating saved") + " · " + T("{0:0.#} out of 5 stars", value / 2d));
 }
}
