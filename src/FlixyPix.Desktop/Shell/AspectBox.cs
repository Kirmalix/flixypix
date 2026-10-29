using System.Windows;
using System.Windows.Controls;

namespace FlixyPix.Desktop;

/// <summary>Gives its child the full available width and a height of <see cref="Ratio"/> times that width, so a tile that
/// stretches with the window keeps the 5:3 frame of its thumbnail.</summary>
public sealed class AspectBox : Decorator
{
 public static readonly DependencyProperty RatioProperty = DependencyProperty.Register(nameof(Ratio), typeof(double), typeof(AspectBox),
  new FrameworkPropertyMetadata(.6, FrameworkPropertyMetadataOptions.AffectsMeasure));
 public double Ratio { get => (double)GetValue(RatioProperty); set => SetValue(RatioProperty, value); }

 protected override Size MeasureOverride(Size constraint)
 {
  var width = double.IsInfinity(constraint.Width) ? 200 : constraint.Width;
  var size = new Size(width, Math.Round(width * Ratio));
  Child?.Measure(size);
  return size;
 }

 protected override Size ArrangeOverride(Size arrangeSize)
 {
  Child?.Arrange(new Rect(arrangeSize));
  return arrangeSize;
 }
}
