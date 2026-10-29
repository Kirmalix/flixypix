using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace FlixyPix.Desktop;

/// <summary>Small motions of the interface: tiles lift with a shadow under the pointer, pictures fade in when they arrive.
/// Short (120–200 ms) and eased out; off when Windows turns animations off ("Animationseffekte" in the settings).</summary>
public static class Motion
{
 internal static bool Enabled => SystemParameters.ClientAreaAnimation;
 /// <summary>Tiles of the media grid lift under the pointer (library setting "Beim Überfahren anheben").</summary>
 internal static bool TilesLift { get; set; } = true;
 internal static readonly Duration Quick = TimeSpan.FromMilliseconds(140), Soft = TimeSpan.FromMilliseconds(200);
 private static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
 private static IEasingFunction Freeze(CubicEase ease) { ease.Freeze(); return ease; }

 /// <summary>The element rises a few pixels and casts a shadow while the pointer is over it. Rows of the list view stay flat.</summary>
 public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached("HoverLift", typeof(bool), typeof(Motion),
  new PropertyMetadata(false, (d, e) =>
  {
   if (d is not FrameworkElement element) return;
   element.MouseEnter -= Lift; element.MouseLeave -= Drop;
   if ((bool)e.NewValue) { element.MouseEnter += Lift; element.MouseLeave += Drop; }
  }));
 public static bool GetHoverLift(DependencyObject d) => (bool)d.GetValue(HoverLiftProperty);
 public static void SetHoverLift(DependencyObject d, bool value) => d.SetValue(HoverLiftProperty, value);

 /// <summary>The picture fades in when its source arrives for the same item (not when a recycled tile shows another item).
 /// Needs NotifyOnTargetUpdated on the Source binding.</summary>
 public static readonly DependencyProperty FadeInProperty = DependencyProperty.RegisterAttached("FadeIn", typeof(bool), typeof(Motion),
  new PropertyMetadata(false, (d, e) =>
  {
   if (d is not Image image) return;
   image.TargetUpdated -= SourceArrived;
   if ((bool)e.NewValue) image.TargetUpdated += SourceArrived;
  }));
 public static bool GetFadeIn(DependencyObject d) => (bool)d.GetValue(FadeInProperty);
 public static void SetFadeIn(DependencyObject d, bool value) => d.SetValue(FadeInProperty, value);
 private static readonly DependencyProperty ShownForProperty = DependencyProperty.RegisterAttached("ShownFor", typeof(object), typeof(Motion));

 /// <summary>Lifts in px; the shadow is stronger on the dark scheme, where a light shadow would vanish.</summary>
 internal const double LiftHeight = 3;
 internal static bool IsLifted(FrameworkElement element) => element.RenderTransform is TranslateTransform { Y: < 0 } || element.Effect is DropShadowEffect;

 private static void Lift(object sender, System.Windows.Input.MouseEventArgs e)
 {
  var element = (FrameworkElement)sender;
  if (VisualTreeHelper.GetParent(element) is VirtualizingTilePanel panel && (panel.SingleColumn || !TilesLift)) return;
  var shift = Transform(element);
  if (element.Effect is not DropShadowEffect shadow)
   element.Effect = shadow = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Color = Colors.Black, Opacity = 0, RenderingBias = RenderingBias.Performance };
  var strength = Theme.IsDark ? .55 : .22;
  if (!Enabled) { shift.Y = -LiftHeight; shadow.Opacity = strength; return; }
  shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-LiftHeight, Quick) { EasingFunction = EaseOut });
  shadow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(strength, Quick) { EasingFunction = EaseOut });
 }

 private static void Drop(object sender, System.Windows.Input.MouseEventArgs e)
 {
  var element = (FrameworkElement)sender;
  if (element.RenderTransform is not TranslateTransform shift) return;
  if (!Enabled || element.Effect is not DropShadowEffect shadow) { shift.BeginAnimation(TranslateTransform.YProperty, null); shift.Y = 0; element.Effect = null; return; }
  shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Quick) { EasingFunction = EaseOut });
  var fade = new DoubleAnimation(0, Quick) { EasingFunction = EaseOut };
  // An effect costs a render pass even when invisible; resting tiles carry none.
  fade.Completed += (_, _) => { if (!element.IsMouseOver && ReferenceEquals(element.Effect, shadow)) element.Effect = null; };
  shadow.BeginAnimation(DropShadowEffect.OpacityProperty, fade);
 }

 private static TranslateTransform Transform(FrameworkElement element)
 {
  if (element.RenderTransform is TranslateTransform existing && !existing.IsFrozen) return existing;
  var shift = new TranslateTransform(); element.RenderTransform = shift; return shift;
 }

 private static void SourceArrived(object? sender, DataTransferEventArgs e)
 {
  if (sender is not Image image || e.Property != Image.SourceProperty) return;
  var same = ReferenceEquals(image.GetValue(ShownForProperty), image.DataContext);
  image.SetValue(ShownForProperty, image.DataContext);
  if (!same || image.Source == null || !Enabled) { image.BeginAnimation(UIElement.OpacityProperty, null); image.Opacity = 1; return; }
  image.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Soft) { EasingFunction = EaseOut });
 }
}

/// <summary>Clips an element to a rectangle with round corners, which Border.ClipToBounds does not do.</summary>
public static class RoundedClip
{
 // Dependency-property identifiers are part of the XAML contract, not display text.
 public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached("Radius", typeof(double), typeof(RoundedClip),
  new PropertyMetadata(0.0, (d, e) =>
  {
   if (d is not FrameworkElement element) return;
   element.SizeChanged -= Resize;
   if ((double)e.NewValue > 0) { element.SizeChanged += Resize; Apply(element); }
   else element.Clip = null;
  }));
 public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
 public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

 private static void Resize(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);
 private static void Apply(FrameworkElement element)
 {
  var radius = GetRadius(element);
  var clip = new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius); clip.Freeze();
  element.Clip = clip;
 }
}
