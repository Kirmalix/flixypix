using System.Windows;
using System.Windows.Controls;

namespace FlixyPix.Desktop;

internal enum RowZone { Left, Center, Right }

/// <summary>The row of the player bar: the playback controls exactly in the middle, a group on either side, always one line.
/// Buttons keep their size. When the width runs out, the side that lacks room gives way first: an element changes to its
/// compact form or moves into the "more" menu, in the order of its fold level, and returns as soon as it fits again.</summary>
internal sealed class PlayerControlRow : Panel
{
 private sealed record Item(Decorator Slot, RowZone Zone, int Fold, Func<IReadOnlyList<MenuSpec>>? Entries, Decorator? Compact);
 private readonly List<Item> items = [];
 private readonly HashSet<int> folded = [];
 private Item? more;

 /// <param name="fold">0 stays in the row; otherwise the lower numbers leave first, equal numbers together.</param>
 /// <param name="entries">What stands in the "more" menu while the element is folded.</param>
 /// <param name="compact">A smaller form that stands in the row instead while the element is folded.</param>
 internal void Add(UIElement element, RowZone zone, int fold = 0, Func<IReadOnlyList<MenuSpec>>? entries = null, UIElement? compact = null)
 {
  var item = new Item(new Decorator { Child = element }, zone, fold, entries, compact == null ? null : new Decorator { Child = compact });
  items.Add(item); Children.Add(item.Slot);
  if (item.Compact != null) Children.Add(item.Compact);
 }

 /// <summary>The button of the "more" menu, in the right group; it shows only while the menu has something to offer.</summary>
 internal void AddMore(UIElement button)
 {
  Add(button, RowZone.Right);
  more = items[^1];
 }

 private bool IsFolded(Item item) => item.Fold > 0 && folded.Contains(item.Fold);
 /// <summary>A folded element that is hidden anyway, like a switched-off O counter, offers nothing.</summary>
 private bool InMenu(Item item) => IsFolded(item) && item.Entries != null && item.Slot.Child.DesiredSize.Width > 0;

 /// <summary>The entries of everything folded right now, in the order of the row.</summary>
 internal IReadOnlyList<MenuSpec> FoldedEntries() => [.. items.Where(InMenu).SelectMany(item => item.Entries!().Prepend(MenuSpec.Separator))];
 internal bool AnythingFolded => folded.Count > 0;
 internal bool MenuShown => more?.Slot.Visibility == Visibility.Visible;

 protected override Size MeasureOverride(Size available)
 {
  var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);
  // A folded slot measures nothing, so the elements are asked directly: their width decides whether they fit again.
  foreach (var item in items) { item.Slot.Child.Measure(unlimited); item.Compact?.Child.Measure(unlimited); }
  double Width(Item item) => item == more ? 0 : IsFolded(item) ? item.Compact?.Child.DesiredSize.Width ?? 0 : item.Slot.Child.DesiredSize.Width;
  double Side(RowZone zone) => items.Where(item => item.Zone == zone).Sum(Width) + (zone == RowZone.Right && more != null && items.Any(InMenu) ? more.Slot.Child.DesiredSize.Width : 0);
  // The middle stays in the middle, so the wider side counts twice.
  double Needed() => Math.Max(Side(RowZone.Left), Side(RowZone.Right)) * 2 + Side(RowZone.Center);
  folded.Clear();
  while (Needed() > available.Width)
  {
   var tight = Side(RowZone.Left) >= Side(RowZone.Right) ? RowZone.Left : RowZone.Right;
   var open = items.Where(item => item.Fold > 0 && !IsFolded(item)).ToArray();
   var next = open.Where(item => item.Zone == tight || item.Zone == RowZone.Center).MinBy(item => item.Fold) ?? open.MinBy(item => item.Fold);
   if (next == null) break;
   folded.Add(next.Fold);
  }
  var height = 0d;
  foreach (var item in items)
  {
   item.Slot.Visibility = (item == more ? items.Any(InMenu) : !IsFolded(item)) ? Visibility.Visible : Visibility.Collapsed;
   item.Slot.Measure(unlimited); height = Math.Max(height, item.Slot.DesiredSize.Height);
   if (item.Compact == null) continue;
   item.Compact.Visibility = IsFolded(item) ? Visibility.Visible : Visibility.Collapsed;
   item.Compact.Measure(unlimited); height = Math.Max(height, item.Compact.DesiredSize.Height);
  }
  return new Size(double.IsInfinity(available.Width) ? Needed() : available.Width, height);
 }

 protected override Size ArrangeOverride(Size final)
 {
  double Total(RowZone zone) => items.Where(item => item.Zone == zone).Sum(item => item.Slot.DesiredSize.Width + (item.Compact?.DesiredSize.Width ?? 0));
  void Place(RowZone zone, double x)
  {
   foreach (var item in items.Where(item => item.Zone == zone))
    foreach (var slot in new[] { item.Slot, item.Compact }.OfType<Decorator>())
    {
     var size = slot.DesiredSize;
     slot.Arrange(new Rect(x, (final.Height - size.Height) / 2, size.Width, size.Height));
     x += size.Width;
    }
  }
  var left = Total(RowZone.Left); var center = Total(RowZone.Center); var rightStart = Math.Max(left + center, final.Width - Total(RowZone.Right));
  Place(RowZone.Left, 0);
  Place(RowZone.Center, Math.Clamp((final.Width - center) / 2, left, Math.Max(left, rightStart - center)));
  Place(RowZone.Right, rightStart);
  return final;
 }
}
