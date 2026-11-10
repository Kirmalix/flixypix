using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace FlixyPix.Desktop;

/// <summary>A click on the stars of a tile asked for this rating in whole stars; 0 removes it.</summary>
public sealed class TileRatedEventArgs(RoutedEvent routedEvent, object source, int stars) : RoutedEventArgs(routedEvent, source)
{
 public int Stars { get; } = stars;
}

/// <summary>The rating on a tile: five small stars that show it (half stars included) and take a click, on unrated media
/// too. A click sets whole stars, as the digit keys do; a click on the rating it already has removes it. Half stars are set
/// in the details. The element draws itself, so a grid full of tiles stays as light as with a line of text, and it does
/// not select or drag its tile.</summary>
public sealed class TileRating : FrameworkElement
{
 private const int Count = 5;

 /// <summary>The rating on the catalog's scale of 0–10.</summary>
 public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(int), typeof(TileRating),
  new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TileRating)d).Describe()));
 public static readonly DependencyProperty StarSizeProperty = DependencyProperty.Register(nameof(StarSize), typeof(double), typeof(TileRating),
  new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
 public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(TileRating),
  new FrameworkPropertyMetadata(Brushes.Goldenrod, FrameworkPropertyMetadataOptions.AffectsRender));
 public static readonly DependencyProperty EmptyProperty = DependencyProperty.Register(nameof(Empty), typeof(Brush), typeof(TileRating),
  new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
 public static readonly RoutedEvent RatedEvent = EventManager.RegisterRoutedEvent(nameof(Rated), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TileRating));

 public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
 public double StarSize { get => (double)GetValue(StarSizeProperty); set => SetValue(StarSizeProperty, value); }
 public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
 public Brush Empty { get => (Brush)GetValue(EmptyProperty); set => SetValue(EmptyProperty, value); }
 public event RoutedEventHandler Rated { add => AddHandler(RatedEvent, value); remove => RemoveHandler(RatedEvent, value); }

 /// <summary>Stars up to the one under the pointer; 0 while it is elsewhere.</summary>
 private int hover;
 private bool pressed;

 public TileRating()
 {
  Cursor = Cursors.Hand; VerticalAlignment = VerticalAlignment.Center; SnapsToDevicePixels = true;
  Describe();
 }

 private void Describe() => AutomationProperties.SetItemStatus(this, (Value / 2d).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " / " + Count);

 /// <summary>A five-pointed star in a square of side 1, its points reaching the edge.</summary>
 private static readonly Geometry Star = MakeStar();
 private static Geometry MakeStar()
 {
  var geometry = new StreamGeometry();
  using (var context = geometry.Open())
  {
   for (var point = 0; point < 10; point++)
   {
    var angle = -Math.PI / 2 + point * Math.PI / 5; var radius = point % 2 == 0 ? .5 : .21;
    var at = new Point(.5 + radius * Math.Cos(angle), .525 + radius * Math.Sin(angle));
    if (point == 0) context.BeginFigure(at, true, true); else context.LineTo(at, false, true);
   }
  }
  geometry.Freeze();
  return geometry;
 }

 protected override Size MeasureOverride(Size availableSize) => new(Count * StarSize, StarSize);

 protected override void OnRender(DrawingContext drawing)
 {
  var size = StarSize;
  // The gaps between the stars take the click as well.
  drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Count * size, size));
  var value = Math.Clamp(Value, 0, Count * 2);
  for (var index = 0; index < Count; index++)
  {
   drawing.PushTransform(new MatrixTransform(size, 0, 0, size, index * size, 0));
   var filled = Math.Clamp(value - index * 2, 0, 2);
   if (filled < 2) drawing.DrawGeometry(Empty, null, Star);
   if (filled > 0)
   {
    if (filled == 1) drawing.PushClip(new RectangleGeometry(new Rect(0, 0, .5, 1)));
    drawing.DrawGeometry(Fill, null, Star);
    if (filled == 1) drawing.Pop();
   }
   // What a click would add shows faintly; what the medium has stays as it is.
   if (filled < 2 && index < hover) { drawing.PushOpacity(.5); drawing.DrawGeometry(Fill, null, Star); drawing.Pop(); }
   drawing.Pop();
  }
 }

 private int StarAt(Point point) => point.Y < -4 || point.Y > StarSize + 4 || point.X < 0 || point.X > Count * StarSize ? 0 : Math.Clamp((int)(point.X / StarSize) + 1, 1, Count);
 private void Hover(int stars) { if (hover == stars) return; hover = stars; InvalidateVisual(); }

 protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); Hover(StarAt(e.GetPosition(this))); }
 protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); if (!pressed) Hover(0); }

 protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
 {
  base.OnMouseLeftButtonDown(e);
  // The tile neither changes the selection nor starts a drag for this click.
  e.Handled = true; pressed = CaptureMouse();
 }

 protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
 {
  base.OnMouseLeftButtonUp(e);
  if (!pressed) return;
  pressed = false; e.Handled = true; ReleaseMouseCapture();
  var stars = StarAt(e.GetPosition(this));
  if (!IsMouseOver) Hover(0);
  if (stars > 0) Rate(stars);
 }

 protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); pressed = false; if (!IsMouseOver) Hover(0); }

 /// <summary>Asks for a rating as a click on that star would.</summary>
 internal void Rate(int stars) => RaiseEvent(new TileRatedEventArgs(RatedEvent, this, Value == stars * 2 ? 0 : stars));
}
