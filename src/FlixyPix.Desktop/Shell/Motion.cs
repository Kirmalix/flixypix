using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace FlixyPix.Desktop;

/// <summary>Small motions of the interface: controls answer the pointer softly, tiles lift with a shadow, pictures fade in
/// when they arrive, pages settle into place and the side bars glide. Short (90–260 ms) and eased out, without overshoot;
/// off when Windows turns animations off ("Animationseffekte" in the settings). No work ever waits for a motion: the state
/// changes at once, only the picture follows.</summary>
public static class Motion
{
 internal static bool Enabled => SystemParameters.ClientAreaAnimation;
 /// <summary>Tests that check places and widths right after a change switch off the motions of whole areas.</summary>
 internal static bool Steady { get; set; }
 /// <summary>Whole pages and side bars move only with graphics hardware; software rendering (remote sessions) shows them at once.</summary>
 internal static bool Smooth => Enabled && !Steady && RenderCapability.Tier >> 16 >= 2;
 /// <summary>Tiles of the media grid lift under the pointer (library setting "Beim Überfahren anheben").</summary>
 internal static bool TilesLift { get; set; } = true;
 internal static readonly Duration Quick = TimeSpan.FromMilliseconds(140), Soft = TimeSpan.FromMilliseconds(200);
 private static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
 private static IEasingFunction Freeze(CubicEase ease) { ease.Freeze(); return ease; }

 // ----- Control states: triggers of a template set these instead of switching a colour or a shape abruptly. -----

 /// <summary>The element fades to this opacity. A template lays a second surface with the hover colour over the normal one
 /// and fades that, which also follows a change of the colour scheme.</summary>
 public static readonly DependencyProperty FadeToProperty = DependencyProperty.RegisterAttached("FadeTo", typeof(double), typeof(Motion),
  new PropertyMetadata(0.0, (d, e) => { if (d is FrameworkElement element) Settle(element, element, UIElement.OpacityProperty, (double)e.NewValue); }));
 public static double GetFadeTo(DependencyObject d) => (double)d.GetValue(FadeToProperty);
 public static void SetFadeTo(DependencyObject d, double value) => d.SetValue(FadeToProperty, value);

 /// <summary>The element shrinks or grows to this scale around its centre (a pressed button gives way a little).</summary>
 public static readonly DependencyProperty ScaleToProperty = DependencyProperty.RegisterAttached("ScaleTo", typeof(double), typeof(Motion),
  new PropertyMetadata(1.0, (d, e) =>
  {
   if (d is not FrameworkElement element) return;
   var scale = Scale(element); var value = (double)e.NewValue;
   Settle(element, scale, ScaleTransform.ScaleXProperty, value); Settle(element, scale, ScaleTransform.ScaleYProperty, value);
  }));
 public static double GetScaleTo(DependencyObject d) => (double)d.GetValue(ScaleToProperty);
 public static void SetScaleTo(DependencyObject d, double value) => d.SetValue(ScaleToProperty, value);

 /// <summary>The element grows to this height share from its centre (the mark of the current navigation entry).</summary>
 public static readonly DependencyProperty GrowToProperty = DependencyProperty.RegisterAttached("GrowTo", typeof(double), typeof(Motion),
  new PropertyMetadata(0.0, (d, e) => { if (d is FrameworkElement element) Settle(element, Scale(element), ScaleTransform.ScaleYProperty, (double)e.NewValue); }));
 public static double GetGrowTo(DependencyObject d) => (double)d.GetValue(GrowToProperty);
 public static void SetGrowTo(DependencyObject d, double value) => d.SetValue(GrowToProperty, value);

 /// <summary>The element turns to this angle around its centre (the arrow of something that folds).</summary>
 public static readonly DependencyProperty TurnToProperty = DependencyProperty.RegisterAttached("TurnTo", typeof(double), typeof(Motion),
  new PropertyMetadata(0.0, (d, e) =>
  {
   if (d is not FrameworkElement element) return;
   if (element.RenderTransform is not RotateTransform { IsFrozen: false } turn) element.RenderTransform = turn = new RotateTransform();
   Settle(element, turn, RotateTransform.AngleProperty, (double)e.NewValue);
  }));
 public static double GetTurnTo(DependencyObject d) => (double)d.GetValue(TurnToProperty);
 public static void SetTurnTo(DependencyObject d, double value) => d.SetValue(TurnToProperty, value);

 /// <summary>The element fades in whenever it becomes visible and goes at once (the actions over a tile).</summary>
 public static readonly DependencyProperty AppearProperty = DependencyProperty.RegisterAttached("Appear", typeof(bool), typeof(Motion),
  new PropertyMetadata(false, (d, e) =>
  {
   if (d is not UIElement element) return;
   element.IsVisibleChanged -= Appeared;
   if ((bool)e.NewValue) element.IsVisibleChanged += Appeared;
  }));
 public static bool GetAppear(DependencyObject d) => (bool)d.GetValue(AppearProperty);
 public static void SetAppear(DependencyObject d, bool value) => d.SetValue(AppearProperty, value);
 private static void Appeared(object sender, DependencyPropertyChangedEventArgs e)
 {
  if (sender is UIElement element && e.NewValue is true && Enabled) element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Quick) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
 }

 /// <summary>Moves a value from where it is now to the new one. Before the element is loaded it simply takes the value, so a
 /// window opens in its finished state.</summary>
 private static void Settle(FrameworkElement owner, IAnimatable target, DependencyProperty property, double value)
 {
  var holder = (DependencyObject)target;
  var from = (double)holder.GetValue(property);
  target.BeginAnimation(property, null);
  holder.SetValue(property, value);
  if (!Enabled || !owner.IsLoaded || from == value) return;
  target.BeginAnimation(property, new DoubleAnimation(from, value, Quick) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
 }

 /// <summary>The element's own scale; one given by a template is frozen and is replaced by a copy.</summary>
 private static ScaleTransform Scale(FrameworkElement element)
 {
  if (element.RenderTransform is ScaleTransform { IsFrozen: false } own) return own;
  var scale = element.RenderTransform is ScaleTransform given ? new ScaleTransform(given.ScaleX, given.ScaleY) : new ScaleTransform();
  element.RenderTransform = scale; element.RenderTransformOrigin = new Point(.5, .5);
  return scale;
 }

 /// <summary>A short answer to a click that changes something for good, such as marking a favorite.</summary>
 internal static void Pop(FrameworkElement element)
 {
  if (!Enabled) return;
  var scale = Scale(element);
  var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(280), FillBehavior = FillBehavior.Stop };
  pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.28, KeyTime.FromPercent(.4), EaseOut));
  pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), EaseOut));
  scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop); scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
 }

 // ----- Pages -----

 private const double PageDim = .3, PageRise = 8, ResultsRise = 6, PageTurn = 28;
 /// <summary>Practically invisible, but still drawn: at zero the costly first drawing would wait for the first visible frame.</summary>
 private const double Unseen = .01;
 private static readonly Duration ResultsTime = TimeSpan.FromMilliseconds(170);
 /// <summary>Counts the motions of an area; a later one replaces what an earlier one still had planned.</summary>
 private static readonly DependencyProperty MotionTurnProperty = DependencyProperty.RegisterAttached("MotionTurn", typeof(int), typeof(Motion));
 private static int NextTurn(FrameworkElement area) { var turn = (int)area.GetValue(MotionTurnProperty) + 1; area.SetValue(MotionTurnProperty, turn); return turn; }

 /// <summary>The page steps back as soon as another is asked for and stays back while that one loads, however long it takes.
 /// The new page ends this, or the return when none follows; the time limit is only the last resort, so it never stays dimmed.</summary>
 internal static void LeavePage(FrameworkElement page)
 {
  if (!Smooth) return;
  NextTurn(page); page.CacheMode = null;
  var shift = Transform(page); shift.BeginAnimation(TranslateTransform.YProperty, null); shift.Y = 0;
  var dim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(60.2), FillBehavior = FillBehavior.Stop };
  dim.KeyFrames.Add(new EasingDoubleKeyFrame(PageDim, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)), EaseOut));
  dim.KeyFrames.Add(new LinearDoubleKeyFrame(PageDim, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(60))));
  dim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(60.2)), EaseOut));
  page.BeginAnimation(UIElement.OpacityProperty, dim);
 }

 /// <summary>The new page is in place and is seen to arrive, however fast it loaded.</summary>
 internal static void EnterPage(FrameworkElement page) => Enter(page, PageRise, Soft);
 /// <summary>Other filters, a search or another order: only the tiles arrive anew, a little quicker.</summary>
 internal static void EnterResults(FrameworkElement results) => Enter(results, ResultsRise, ResultsTime);

 /// <summary>Turning to another page of tiles: the old ones go before the jump, so the new ones never flash up first.
 /// If nothing follows, they return by themselves.</summary>
 internal static void LeaveTiles(FrameworkElement results)
 {
  if (!Smooth) return;
  NextTurn(results);
  results.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(Unseen, Unseen, TimeSpan.FromSeconds(1)) { FillBehavior = FillBehavior.Stop });
 }
 /// <summary>The new page of tiles comes from the side one is heading to: forward from the right, back from the left.</summary>
 internal static void EnterTiles(FrameworkElement results, bool forward) => Enter(results, 0, ResultsTime, forward ? PageTurn : -PageTurn);

 /// <summary>Fades in from nothing and settles from below. New content first costs a long frame or two, and an animation runs
 /// by the clock: started at once, it would be over when the first picture appears. So the area is drawn unseen first and
 /// moves as a cached picture, which keeps the frames short on large screens. A hold that nothing follows ends by itself.</summary>
 private static void Enter(FrameworkElement area, double rise, Duration time, double side = 0)
 {
  if (!Smooth)
  {
   area.BeginAnimation(UIElement.OpacityProperty, null);
   if (area.RenderTransform is TranslateTransform still) { still.BeginAnimation(TranslateTransform.YProperty, null); still.Y = 0; still.BeginAnimation(TranslateTransform.XProperty, null); still.X = 0; }
   return;
  }
  var turn = NextTurn(area);
  var shift = Transform(area); shift.BeginAnimation(TranslateTransform.YProperty, null); shift.Y = rise; shift.BeginAnimation(TranslateTransform.XProperty, null); shift.X = side;
  area.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
  area.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(Unseen, Unseen, TimeSpan.FromSeconds(1)) { FillBehavior = FillBehavior.Stop });
  var clock = System.Diagnostics.Stopwatch.StartNew();
  area.Dispatcher.BeginInvoke(() =>
  {
   var frames = 0;
   void Waiting(object? sender, EventArgs e)
   {
    if ((int)area.GetValue(MotionTurnProperty) != turn) { CompositionTarget.Rendering -= Waiting; shift.Y = shift.X = 0; area.CacheMode = null; return; }
    if (++frames < 2 && clock.ElapsedMilliseconds < 250) return;
    CompositionTarget.Rendering -= Waiting;
    shift.Y = shift.X = 0;
    if (rise != 0) shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, time) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
    if (side != 0) shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(side, 0, time) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
    var show =new DoubleAnimation(Unseen, 1, time) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
    show.Completed += (_, _) => { if ((int)area.GetValue(MotionTurnProperty) == turn) area.CacheMode = null; };
    area.BeginAnimation(UIElement.OpacityProperty, show);
   }
   CompositionTarget.Rendering += Waiting;
  }, System.Windows.Threading.DispatcherPriority.Loaded);
 }


 // ----- Windows -----

 /// <summary>A dialog arrives softly the first time it has a size. Windows for watching and the screen lock stand at once.</summary>
 internal static void WindowOpens(Window window)
 {
  if (window is MainWindow or VideoPlayerWindow or ImageViewerWindow or QuickLookWindow or LockWindow) return;
  if (window.Content is FrameworkElement content) Enter(content, ResultsRise, ResultsTime);
 }

 // ----- Scrolling -----

 /// <summary>How far the wheel moves per notch, and how quickly a glide closes in: about 95 % of the way in 200 ms at any
 /// frame rate, without overshoot.</summary>
 private const double WheelLine = 32, GlideTime = 65;
 internal static double GlideShare(double milliseconds) => 1 - Math.Exp(-milliseconds / GlideTime);

 private sealed class ScrollGlide { public double Target; public double Last; public System.Diagnostics.Stopwatch Clock = new(); public EventHandler? Step; }
 private static readonly DependencyProperty ScrollGlideProperty = DependencyProperty.RegisterAttached("ScrollGlide", typeof(ScrollGlide), typeof(Motion));

 /// <summary>The wheel glides in every page that scrolls by pixels. Lists that scroll by whole items and the tile grid, which
 /// glides by itself, keep their way; where nothing can scroll, the wheel goes on as before.</summary>
 internal static void InstallWheel() => EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent, new System.Windows.Input.MouseWheelEventHandler((sender, e) =>
 {
  if (e.Handled || !Smooth || sender is not ScrollViewer view || view.ScrollableHeight <= 0) return;
  var byPixels = !view.CanContentScroll || view.TemplatedParent is ItemsControl list && VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel;
  if (!byPixels || VirtualizingTilePanel.FindPanel(view) != null) return;
  var lines = SystemParameters.WheelScrollLines;
  GlideBy(view, -e.Delta / 120d * (lines < 0 ? view.ViewportHeight : lines * WheelLine));
  e.Handled = true;
 }));

 /// <summary>Moves the view by a distance; further turns extend the way, and any other scrolling ends the glide.</summary>
 internal static void GlideBy(ScrollViewer view, double distance)
 {
  if (!Smooth) { view.ScrollToVerticalOffset(view.VerticalOffset + distance); return; }
  if (view.GetValue(ScrollGlideProperty) is ScrollGlide running) { running.Target = Math.Clamp(running.Target + distance, 0, view.ScrollableHeight); return; }
  var glide = new ScrollGlide { Target = Math.Clamp(view.VerticalOffset + distance, 0, view.ScrollableHeight), Last = view.VerticalOffset };
  glide.Clock.Start();
  void Stop() { CompositionTarget.Rendering -= glide.Step; view.ClearValue(ScrollGlideProperty); }
  glide.Step = (_, _) =>
  {
   if (!view.IsLoaded || Math.Abs(view.VerticalOffset - glide.Last) > 2) { Stop(); return; }
   var next = view.VerticalOffset + (glide.Target - view.VerticalOffset) * GlideShare(glide.Clock.Elapsed.TotalMilliseconds);
   glide.Clock.Restart();
   if (Math.Abs(glide.Target - next) < .5) { view.ScrollToVerticalOffset(glide.Target); Stop(); return; }
   glide.Last = next; view.ScrollToVerticalOffset(next);
  };
  view.SetValue(ScrollGlideProperty, glide);
  CompositionTarget.Rendering += glide.Step;
 }

 // ----- Side bars -----

 private sealed class Glide(double target) { public double Target { get; } = target; public List<Action> Settled { get; } = []; }
 private static readonly DependencyProperty GlideProperty = DependencyProperty.RegisterAttached("Glide", typeof(Glide), typeof(Motion));
 private static readonly DependencyProperty GlideWidthProperty = DependencyProperty.RegisterAttached("GlideWidth", typeof(double), typeof(Motion),
  new PropertyMetadata(0.0, (d, e) => ((ColumnDefinition)d).Width = new GridLength(Math.Max(0, (double)e.NewValue))));

 /// <summary>The width the column has or is gliding to; what gets saved, never a value from the middle of a motion.</summary>
 internal static double WidthOf(ColumnDefinition column) => column.GetValue(GlideProperty) is Glide glide ? glide.Target : column.Width.Value;
 internal static bool IsGlidingTo(ColumnDefinition column, double width) => column.GetValue(GlideProperty) is Glide glide && glide.Target == width;
 /// <summary>Runs once the column has its width: at once unless it is gliding. Dropped if the column gets another target.</summary>
 internal static void WhenSettled(ColumnDefinition column, Action action)
 {
  if (column.GetValue(GlideProperty) is Glide glide) glide.Settled.Add(action); else action();
 }

 /// <summary>Gives the column its width, gliding there from where it is if asked to.</summary>
 internal static void SetWidth(ColumnDefinition column, double width, bool glide)
 {
  if (IsGlidingTo(column, width)) return;
  var from = column.Width.Value;
  column.BeginAnimation(GlideWidthProperty, null); column.ClearValue(GlideProperty);
  column.SetValue(GlideWidthProperty, width); column.Width = new GridLength(width);
  if (!glide || !Smooth || from == width) return;
  var current = new Glide(width); column.SetValue(GlideProperty, current);
  var move = new DoubleAnimation(from, width, Soft) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
  move.Completed += (_, _) =>
  {
   if (!ReferenceEquals(column.GetValue(GlideProperty), current)) return;
   column.ClearValue(GlideProperty); column.Width = new GridLength(width);
   foreach (var action in current.Settled) action();
  };
  column.BeginAnimation(GlideWidthProperty, move);
  // The first tick comes later; a layout before it must not show the end.
  column.Width = new GridLength(from);
 }

 /// <summary>A side bar in its column: while the column glides, the bar keeps its full width and is uncovered or covered,
 /// so its content does not wrap anew in every frame. <paramref name="rail"/> is what takes its place when folded.</summary>
 internal static void SlidePane(FrameworkElement pane, ColumnDefinition column, double width, bool open, bool glide, FrameworkElement? rail)
 {
  if (IsGlidingTo(column, width)) return;
  void Rest() { pane.ClearValue(FrameworkElement.WidthProperty); pane.ClearValue(FrameworkElement.HorizontalAlignmentProperty); pane.Visibility = open ? Visibility.Visible : Visibility.Collapsed; }
  if (!glide || !Smooth || WidthOf(column) == width) { Rest(); SetWidth(column, width, false); return; }
  var full = open ? width : column.ActualWidth;
  pane.Visibility = Visibility.Visible; pane.Width = full; pane.HorizontalAlignment = HorizontalAlignment.Left;
  if (open) pane.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.4, 1, Quick) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
  rail?.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Quick) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
  SetWidth(column, width, true);
  WhenSettled(column, Rest);
 }

 // ----- Notes -----

 /// <summary>A notice rises into place.</summary>
 internal static void NoteIn(FrameworkElement note)
 {
  if (!Enabled) { note.Opacity = 1; return; }
  note.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = EaseOut });
  Transform(note).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
 }

 /// <summary>A notice leaves to the side, then is removed.</summary>
 internal static void NoteOut(FrameworkElement note, Action removed)
 {
  if (!Enabled) { removed(); return; }
  var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
  fade.Completed += (_, _) => removed();
  note.BeginAnimation(UIElement.OpacityProperty, fade);
  Transform(note).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 12, TimeSpan.FromMilliseconds(160)) { EasingFunction = EaseOut });
 }

 /// <summary>The element rises a few pixels while the pointer is over it, and casts a shadow if its template has a part
 /// named "shade". Rows of the list view stay flat.</summary>
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
 internal static bool IsLifted(FrameworkElement element) => element.RenderTransform is TranslateTransform { Y: < 0 } || Shade(element) is { Visibility: Visibility.Visible };

 /// <summary>How far this element lifts: <see cref="LiftHeight"/> rounded to whole screen pixels, so pictures and text
 /// stay as sharp lifted as they are at rest, whatever the display and interface scaling.</summary>
 internal static double LiftFor(Visual element)
 {
  var device = (PresentationSource.FromVisual(element)?.CompositionTarget?.TransformToDevice.M22 ?? 1) * InterfaceScaling.Current;
  return device > 0 ? Math.Max(1, Math.Round(LiftHeight * device)) / device : LiftHeight;
 }

 /// <summary>The part of the template that casts the shadow, lying under the content. The shadow is an effect, and an effect
 /// draws its element through a picture of it: on the whole tile that blurred pictures and text. An element without this
 /// part lifts without a shadow.</summary>
 internal static FrameworkElement? Shade(FrameworkElement element) => element is Control { Template: { } template } ? template.FindName("shade", element) as FrameworkElement : null;

 private static void Lift(object sender, System.Windows.Input.MouseEventArgs e)
 {
  var element = (FrameworkElement)sender;
  if (VisualTreeHelper.GetParent(element) is VirtualizingTilePanel panel && (panel.SingleColumn || !TilesLift)) return;
  var shift = Transform(element); var height = LiftFor(element); var shade = Shade(element);
  if (shade != null)
  {
   shade.Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Color = Colors.Black, Opacity = Theme.IsDark ? .55 : .22, RenderingBias = RenderingBias.Performance };
   shade.Visibility = Visibility.Visible;
  }
  if (!Enabled) { shift.Y = -height; if (shade != null) { shade.BeginAnimation(UIElement.OpacityProperty, null); shade.Opacity = 1; } return; }
  shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-height, Quick) { EasingFunction = EaseOut });
  shade?.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, Quick) { EasingFunction = EaseOut });
 }

 private static void Drop(object sender, System.Windows.Input.MouseEventArgs e)
 {
  var element = (FrameworkElement)sender;
  if (element.RenderTransform is not TranslateTransform shift) return;
  var shade = Shade(element);
  // An effect costs a render pass even when invisible; resting tiles carry none.
  void Rest() { if (shade == null) return; shade.BeginAnimation(UIElement.OpacityProperty, null); shade.Opacity = 0; shade.Effect = null; shade.Visibility = Visibility.Collapsed; }
  if (!Enabled) { shift.BeginAnimation(TranslateTransform.YProperty, null); shift.Y = 0; Rest(); return; }
  shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Quick) { EasingFunction = EaseOut });
  if (shade == null) return;
  var fade = new DoubleAnimation(0, Quick) { EasingFunction = EaseOut };
  fade.Completed += (_, _) => { if (!element.IsMouseOver) Rest(); };
  shade.BeginAnimation(UIElement.OpacityProperty, fade);
 }

 /// <summary>An element of a new page settles into place, one shortly after the other. The element is whole again afterwards.</summary>
 internal static void Arrive(FrameworkElement element, int index)
 {
  if (!Enabled) return;
  var delay = TimeSpan.FromMilliseconds(index * 28); var total = delay + TimeSpan.FromMilliseconds(260);
  DoubleAnimationUsingKeyFrames Move(double from, double to)
  {
   var frames = new DoubleAnimationUsingKeyFrames { Duration = total, FillBehavior = FillBehavior.Stop };
   frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
   frames.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
   frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(total), EaseOut));
   return frames;
  }
  element.BeginAnimation(UIElement.OpacityProperty, Move(0, 1));
  Transform(element).BeginAnimation(TranslateTransform.YProperty, Move(6, 0));
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
