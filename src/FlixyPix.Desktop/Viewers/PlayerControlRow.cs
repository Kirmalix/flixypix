using System.Windows;
using System.Windows.Controls;

namespace FlixyPix.Desktop;

/// <summary>The row of the player bar: playing on the left, tools on the right, always one line. When the width runs out,
/// tools move into the "more" menu in the order of their fold level, so the arrangement stays the same at every size.</summary>
internal sealed class PlayerControlRow : Panel
{
 private sealed record Item(Decorator Slot, bool Right, int Fold, Func<IReadOnlyList<MenuSpec>>? Entries);
 private readonly List<Item> items = [];
 private Item? more;
 private int foldedUpTo;

 /// <param name="fold">0 stays in the row; otherwise the lower numbers leave first, equal numbers together.</param>
 /// <param name="entries">What stands in the "more" menu while the element is folded.</param>
 internal void Add(UIElement element, bool right = false, int fold = 0, Func<IReadOnlyList<MenuSpec>>? entries = null)
 {
  var item = new Item(new Decorator { Child = element }, right, fold, entries);
  items.Add(item); Children.Add(item.Slot);
 }

 /// <summary>The button of the "more" menu; it shows only while something is folded.</summary>
 internal void AddMore(UIElement button)
 {
  Add(button, right: true);
  more = items[^1];
 }

 /// <summary>The entries of everything folded right now, in the order of the row.</summary>
 internal IReadOnlyList<MenuSpec> FoldedEntries() =>
  [.. items.Where(item => item.Slot.Visibility == Visibility.Collapsed && item.Entries != null).SelectMany(item => item.Entries!().Prepend(MenuSpec.Separator))];

 internal bool AnythingFolded => foldedUpTo > 0;

 protected override Size MeasureOverride(Size available)
 {
  var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);
  // A folded slot measures nothing, so the elements are asked directly: their width decides whether they fit again.
  double Width(Item item) { item.Slot.Child.Measure(unlimited); return item.Slot.Child.DesiredSize.Width; }
  var needed = items.Where(item => item != more).Sum(Width);
  foldedUpTo = 0;
  if (needed > available.Width)
  {
   if (more != null) needed += Width(more);
   foreach (var level in items.Where(item => item.Fold > 0).GroupBy(item => item.Fold).OrderBy(group => group.Key))
   {
    if (needed <= available.Width) break;
    needed -= level.Sum(Width); foldedUpTo = level.Key;
   }
  }
  double height = 0, width = 0;
  foreach (var item in items)
  {
   item.Slot.Visibility = (item == more ? foldedUpTo > 0 : item.Fold == 0 || item.Fold > foldedUpTo) ? Visibility.Visible : Visibility.Collapsed;
   item.Slot.Measure(unlimited);
   width += item.Slot.DesiredSize.Width; height = Math.Max(height, item.Slot.DesiredSize.Height);
  }
  return new Size(Math.Min(width, available.Width), height);
 }

 protected override Size ArrangeOverride(Size final)
 {
  var left = 0d;
  void Place(Item item, ref double x)
  {
   var size = item.Slot.DesiredSize;
   item.Slot.Arrange(new Rect(x, (final.Height - size.Height) / 2, size.Width, size.Height));
   x += size.Width;
  }
  foreach (var item in items.Where(item => !item.Right)) Place(item, ref left);
  var tools = items.Where(item => item.Right).ToArray();
  var right = Math.Max(left, final.Width - tools.Sum(item => item.Slot.DesiredSize.Width));
  foreach (var item in tools) Place(item, ref right);
  return final;
 }
}
