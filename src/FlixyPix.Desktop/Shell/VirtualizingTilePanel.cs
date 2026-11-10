using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FlixyPix.Desktop;

/// <summary>Arguments of <see cref="VirtualizingTilePanel.VisibleRangeChangedEvent"/>: absolute indexes of the first and last tile in view.</summary>
public sealed class VisibleRangeEventArgs(RoutedEvent routedEvent, int first, int last) : RoutedEventArgs(routedEvent)
{
 public int First { get; } = first;
 public int Last { get; } = last;
}

/// <summary>Wrapping tile layout that only creates containers for the rows in view (plus one row either side). All tiles share
/// one size, so a tile's place follows from its index alone: the columns follow from the available width and the target
/// tile width, the tiles stretch to fill the row exactly, and the height comes from the first realized container. With a
/// <see cref="TileWidthRangeProperty"/> the columns stay when only the width changes (a side pane opens, the window is
/// resized) and the tiles grow or shrink instead, as long as they stay in that range.
/// When the columns or the tile size change, one tile keeps its place (see <see cref="AnchorNow"/>).
/// <para>The items may be a window of a longer result: <see cref="TotalCountProperty"/> sizes the scroll range for the whole
/// result and <see cref="WindowStartProperty"/> says where the loaded items sit in it. Both are set on the list, so they
/// survive switching between tile and list view. The panel reports what is in view; the owner loads another window.</para></summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
 public static readonly DependencyProperty TotalCountProperty = DependencyProperty.RegisterAttached("TotalCount", typeof(int), typeof(VirtualizingTilePanel),
  new FrameworkPropertyMetadata(0, (d, _) => Invalidate(d)));
 public static readonly DependencyProperty WindowStartProperty = DependencyProperty.RegisterAttached("WindowStart", typeof(int), typeof(VirtualizingTilePanel),
  new FrameworkPropertyMetadata(0, (d, _) => Invalidate(d)));
 /// <summary>One full-width row per item, for the list view.</summary>
 public static readonly DependencyProperty SingleColumnProperty = DependencyProperty.Register(nameof(SingleColumn), typeof(bool), typeof(VirtualizingTilePanel),
  new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));
 /// <summary>Smallest width a tile may have; the row is split into as many tiles of at least this width as fit.</summary>
 public static readonly DependencyProperty TargetTileWidthProperty = DependencyProperty.RegisterAttached("TargetTileWidth", typeof(double), typeof(VirtualizingTilePanel),
  new FrameworkPropertyMetadata(200.0, (d, _) => Invalidate(d)));
 /// <summary>Narrowest and widest tile while the columns stay over a change of width; empty keeps no columns.</summary>
 public static readonly DependencyProperty TileWidthRangeProperty = DependencyProperty.RegisterAttached("TileWidthRange", typeof(Size), typeof(VirtualizingTilePanel),
  new FrameworkPropertyMetadata(Size.Empty, (d, _) => Invalidate(d)));
 public static readonly RoutedEvent VisibleRangeChangedEvent = EventManager.RegisterRoutedEvent("VisibleRangeChanged", RoutingStrategy.Bubble,
  typeof(EventHandler<VisibleRangeEventArgs>), typeof(VirtualizingTilePanel));
 public static int GetTotalCount(DependencyObject d) => (int)d.GetValue(TotalCountProperty);
 public static void SetTotalCount(DependencyObject d, int value) => d.SetValue(TotalCountProperty, value);
 public static int GetWindowStart(DependencyObject d) => (int)d.GetValue(WindowStartProperty);
 public static void SetWindowStart(DependencyObject d, int value) => d.SetValue(WindowStartProperty, value);
 public static double GetTargetTileWidth(DependencyObject d) => (double)d.GetValue(TargetTileWidthProperty);
 public static void SetTargetTileWidth(DependencyObject d, double value) => d.SetValue(TargetTileWidthProperty, value);
 public static Size GetTileWidthRange(DependencyObject d) => (Size)d.GetValue(TileWidthRangeProperty);
 /// <summary>Width = narrowest, Height = widest tile.</summary>
 public static void SetTileWidthRange(DependencyObject d, Size value) => d.SetValue(TileWidthRangeProperty, value);
 public bool SingleColumn { get => (bool)GetValue(SingleColumnProperty); set => SetValue(SingleColumnProperty, value); }

 private Size _tile = new(200, 260);
 private bool _tileKnown;
 private Size _extent, _viewport;
 private Point _offset;
 private int _columns = 1;
 /// <summary>Target width the columns were chosen for; NaN chooses them again from the target.</summary>
 private double _columnsTarget = double.NaN;
 private (int First, int Last) _reported = (-1, -1);
 /// <summary>The tile that keeps its place while the columns or the tile size change. It stays the same over several
 /// changes in a row, so zooming out and in again comes back to the same place; scrolling clears it.</summary>
 private (int Index, double Fraction, int Selected)? _zoomAnchor;
 private const double LineStep = 48;
 /// <summary>Where a smooth wheel scroll is heading; null when no scroll animation runs.</summary>
 private double? _scrollTarget;
 /// <summary>Width of one tile as laid out; the window decodes thumbnails for it.</summary>
 internal double TileWidth => _tile.Width;
 internal int Columns => _columns;
 /// <summary>Containers that exist right now; tests use it to show that only visible tiles are built.</summary>
 internal int Realized => InternalChildren.Count;

 private static void Invalidate(DependencyObject owner)
 {
  if (owner is ItemsControl list && list.ItemContainerGenerator is { } && FindPanel(list) is { } panel) panel.InvalidateMeasure();
 }

 internal static VirtualizingTilePanel? FindPanel(DependencyObject root)
 {
  for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
  {
   var child = VisualTreeHelper.GetChild(root, i);
   if (child is VirtualizingTilePanel panel) return panel;
   if (FindPanel(child) is { } nested) return nested;
  }
  return null;
 }

 private ItemsControl? ItemsOwnerControl => ItemsControl.GetItemsOwner(this);
 private int LoadedCount => ItemsOwnerControl?.Items.Count ?? 0;
 private int Start => ItemsOwnerControl is { } owner ? GetWindowStart(owner) : 0;
 /// <summary>Length of the whole result; at least what is loaded.</summary>
 private int Total => Math.Max(ItemsOwnerControl is { } owner ? GetTotalCount(owner) : 0, Start + LoadedCount);
 private double Target => Math.Max(40, ItemsOwnerControl is { } owner ? GetTargetTileWidth(owner) : 200);

 protected override Size MeasureOverride(Size available)
 {
  var width = double.IsInfinity(available.Width) ? Target * 4 : available.Width;
  var height = double.IsInfinity(available.Height) ? _tile.Height * 3 : available.Height;
  var loaded = LoadedCount; var start = Start; var total = Total;
  // The tile that keeps its place, judged by the layout before a change of columns or tile size.
  var anchor = _tileKnown ? AnchorNow(total) : default;
  var (columns, tile) = (_columns, _tile);
  _columns = ColumnsFor(width);
  var cell = width / _columns;
  // Measure one tile before laying out a screenful, so a wrong first guess does not build (and load) tiles that are dropped again.
  if (!_tileKnown && loaded > 0)
  {
   Realize(0, 0, cell);
   if (InternalChildren.Count > 0 && InternalChildren[0].DesiredSize is { Height: > 0 } first) { _tile = new Size(cell, first.Height); _tileKnown = true; }
  }
  _tile = new Size(cell, _tile.Height);
  for (var pass = 0; pass < 3; pass++)
  {
   if (_tileKnown && (_columns != columns || Math.Abs(_tile.Height - tile.Height) >= .5))
   {
    if (_zoomAnchor is not { } kept || kept.Selected != anchor.Selected) _zoomAnchor = anchor;
    _offset.Y = AnchoredOffset(_zoomAnchor.Value, total, height);
   }
   var rows = (total + _columns - 1) / _columns;
   UpdateScrollInfo(new Size(width, rows * _tile.Height), new Size(width, height));
   var (firstIndex, lastIndex) = Range(total, 1);
   // Only the part of the range that is loaded gets containers; the owner is told to load the rest.
   Realize(Math.Max(firstIndex, start) - start, Math.Min(lastIndex, start + loaded - 1) - start, cell);
   // A changed width changes the tile height too; measure again with the real size.
   var measured = InternalChildren.Count > 0 ? InternalChildren[0].DesiredSize.Height : _tile.Height;
   if (measured <= 0 || Math.Abs(measured - _tile.Height) < .5) break;
   _tile = new Size(cell, measured);
  }
  Report(total);
  return new Size(width, double.IsInfinity(available.Height) ? Math.Min(_extent.Height, height) : height);
 }

 protected override Size ArrangeOverride(Size final)
 {
  var generator = ItemContainerGenerator; var start = Start;
  for (var i = 0; i < InternalChildren.Count; i++)
  {
   var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
   if (index < 0) continue;
   var absolute = start + index;
   var row = absolute / _columns; var column = absolute % _columns;
   InternalChildren[i].Arrange(new Rect(column * _tile.Width, row * _tile.Height - _offset.Y, _tile.Width, _tile.Height));
  }
  return final;
 }

 /// <summary>As many columns of at least the target width as fit; with a tile width range the columns stay when only the
 /// width changed, as long as the tiles stay in the range.</summary>
 private int ColumnsFor(double width)
 {
  if (SingleColumn) return 1;
  var target = Target;
  var range = ItemsOwnerControl is { } owner ? GetTileWidthRange(owner) : Size.Empty;
  if (range.IsEmpty || !_columnsTarget.Equals(target) || _columns < 1) { _columnsTarget = target; return Math.Max(1, (int)(width / target)); }
  // Outside the range the columns follow the target again, as they would after a fresh start.
  var tile = width / _columns;
  return tile >= range.Width && tile <= range.Height ? _columns : Math.Max(1, (int)(width / target));
 }

 /// <summary>Chooses the columns from the target width again at the next layout, also when the target is the same.</summary>
 internal void ChooseColumnsAgain() { _columnsTarget = double.NaN; InvalidateMeasure(); }

 /// <summary>Absolute indexes in view, widened by <paramref name="spareRows"/> rows on either side.</summary>
 private (int First, int Last) Range(int total, int spareRows)
 {
  if (total == 0) return (0, -1);
  var firstRow = Math.Max(0, (int)(_offset.Y / _tile.Height) - spareRows);
  var lastRow = (int)Math.Ceiling((_offset.Y + _viewport.Height) / _tile.Height) - 1 + spareRows;
  return (Math.Min(total - 1, firstRow * _columns), Math.Min(total - 1, (lastRow + 1) * _columns - 1));
 }

 /// <summary>The selected tile when its row is fully in view, at its height in the view; otherwise the first tile of the
 /// top row, counting a row as the top one when at least half of it shows.</summary>
 private (int Index, double Fraction, int Selected) AnchorNow(int total)
 {
  var selected = ItemsOwnerControl is Selector { SelectedIndex: >= 0 } list ? Start + list.SelectedIndex : -1;
  if (selected >= 0 && _viewport.Height > 0)
  {
   var top = selected / _columns * _tile.Height;
   if (top >= _offset.Y - .5 && top + _tile.Height <= _offset.Y + _viewport.Height + .5) return (selected, (top - _offset.Y) / _viewport.Height, selected);
  }
  var row = (int)Math.Round(_offset.Y / _tile.Height);
  return (Math.Max(0, Math.Min(total - 1, row * _columns)), 0, selected);
 }

 /// <summary>Offset that puts the anchor's row at the same height of the view again, wholly in view where the view is tall enough.</summary>
 private double AnchoredOffset((int Index, double Fraction, int Selected) anchor, int total, double viewport)
 {
  var top = Math.Min(anchor.Index, Math.Max(0, total - 1)) / _columns * _tile.Height;
  var offset = top - anchor.Fraction * viewport;
  if (top + _tile.Height > offset + viewport) offset = top + _tile.Height - viewport;
  if (top < offset) offset = top;
  return Math.Max(0, offset);
 }

 private void Report(int total)
 {
  var range = Range(total, 0);
  if (range == _reported) return;
  _reported = range;
  // Raised after layout, so a handler that loads another window does not run inside this measure pass.
  Dispatcher.BeginInvoke(() => RaiseEvent(new VisibleRangeEventArgs(VisibleRangeChangedEvent, range.First, range.Last)));
 }

 private void Realize(int first, int last, double width)
 {
  _ = InternalChildren; // The generator is only available after the children collection exists.
  var generator = ItemContainerGenerator;
  var constraint = new Size(width, double.PositiveInfinity);
  if (last >= first)
  {
   var start = generator.GeneratorPositionFromIndex(first);
   var child = start.Offset == 0 ? start.Index : start.Index + 1;
   using (generator.StartAt(start, GeneratorDirection.Forward, true))
    for (var index = first; index <= last; index++, child++)
    {
     if (generator.GenerateNext(out var isNew) is not UIElement element) break;
     if (isNew)
     {
      if (child >= InternalChildren.Count) AddInternalChild(element); else InsertInternalChild(child, element);
      generator.PrepareItemContainer(element);
     }
     element.Measure(constraint);
    }
  }
  for (var i = InternalChildren.Count - 1; i >= 0; i--)
  {
   var position = new GeneratorPosition(i, 0);
   var index = generator.IndexFromGeneratorPosition(position);
   // Keep the focused container, so keyboard navigation does not lose its place while scrolling.
   if (index >= first && index <= last || InternalChildren[i].IsKeyboardFocusWithin) continue;
   generator.Remove(position, 1);
   RemoveInternalChildRange(i, 1);
  }
 }

 protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
 {
  // A new window keeps the scroll position: tiles are placed by their index in the whole result.
  if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Move)
   RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
  base.OnItemsChanged(sender, args);
 }

 /// <summary>Scrolls so that the tile at <paramref name="absolute"/> (index in the whole result) is in the top row.</summary>
 internal void ScrollToIndex(int absolute) => SetVerticalOffset(Math.Max(0, absolute) / _columns * _tile.Height);

 protected override void BringIndexIntoView(int index) => BringAbsoluteIntoView(Start + index);

 private void BringAbsoluteIntoView(int absolute)
 {
  var top = absolute / _columns * _tile.Height;
  if (top < _offset.Y) SetVerticalOffset(top);
  else if (top + _tile.Height > _offset.Y + _viewport.Height) SetVerticalOffset(top + _tile.Height - _viewport.Height);
 }

 private void UpdateScrollInfo(Size extent, Size viewport)
 {
  var changed = extent != _extent || viewport != _viewport;
  _extent = extent; _viewport = viewport;
  var limit = Math.Max(0, _extent.Height - _viewport.Height);
  if (_offset.Y > limit) { _offset.Y = limit; changed = true; }
  if (changed) ScrollOwner?.InvalidateScrollInfo();
 }

 public bool CanHorizontallyScroll { get; set; }
 public bool CanVerticallyScroll { get; set; }
 public double ExtentWidth => _extent.Width;
 public double ExtentHeight => _extent.Height;
 public double ViewportWidth => _viewport.Width;
 public double ViewportHeight => _viewport.Height;
 public double HorizontalOffset => 0;
 public double VerticalOffset => _offset.Y;
 public ScrollViewer? ScrollOwner { get; set; }
 public void LineUp() => SetVerticalOffset(_offset.Y - LineStep);
 public void LineDown() => SetVerticalOffset(_offset.Y + LineStep);
 public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
 public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
 public void MouseWheelUp() => ScrollSmoothly(-SystemParameters.WheelScrollLines * LineStep);
 public void MouseWheelDown() => ScrollSmoothly(SystemParameters.WheelScrollLines * LineStep);
 public void LineLeft() { }
 public void LineRight() { }
 public void PageLeft() { }
 public void PageRight() { }
 public void MouseWheelLeft() { }
 public void MouseWheelRight() { }
 public void SetHorizontalOffset(double offset) { }
 public void SetVerticalOffset(double offset)
 {
  StopSmoothScroll();
  MoveTo(offset);
 }

 private readonly System.Diagnostics.Stopwatch _glideClock = new();
 /// <summary>The wheel glides to its target at the same pace on any screen, unless Windows turns animations off; further
 /// turns extend the target. Any other scrolling stops the glide.</summary>
 private void ScrollSmoothly(double delta)
 {
  if (!SystemParameters.ClientAreaAnimation) { SetVerticalOffset(_offset.Y + delta); return; }
  var limit = Math.Max(0, _extent.Height - _viewport.Height);
  var start = _scrollTarget is null;
  _scrollTarget = Math.Clamp((_scrollTarget ?? _offset.Y) + delta, 0, limit);
  if (start) { _glideClock.Restart(); CompositionTarget.Rendering += GlideStep; }
 }

 private void GlideStep(object? sender, EventArgs e)
 {
  if (_scrollTarget is not { } target || !IsLoaded) { StopSmoothScroll(); return; }
  var next = _offset.Y + (target - _offset.Y) * Motion.GlideShare(_glideClock.Elapsed.TotalMilliseconds);
  _glideClock.Restart();
  if (Math.Abs(target - next) < 1) { MoveTo(target); StopSmoothScroll(); return; }
  MoveTo(next);
 }

 private void StopSmoothScroll()
 {
  if (_scrollTarget is null) return;
  _scrollTarget = null;
  CompositionTarget.Rendering -= GlideStep;
 }

 private void MoveTo(double offset)
 {
  offset = Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));
  if (double.IsNaN(offset) || Math.Abs(offset - _offset.Y) < .1) return;
  _offset.Y = offset; _zoomAnchor = null;
  ScrollOwner?.InvalidateScrollInfo();
  InvalidateMeasure();
 }
 public Rect MakeVisible(Visual visual, Rect rectangle)
 {
  for (var i = 0; i < InternalChildren.Count; i++)
  {
   if (!ReferenceEquals(InternalChildren[i], visual) && !(visual is DependencyObject d && InternalChildren[i].IsAncestorOf(d))) continue;
   var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
   if (index >= 0) BringIndexIntoView(index);
   break;
  }
  return rectangle;
 }
}
