using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FlixyPix.Desktop;

/// <summary>Renaming in place, as in Explorer: a text field over the title of a list row. Enter or leaving the field keeps
/// the name, Esc keeps the old one. The row template marks its title with <c>Tag="title"</c>.</summary>
internal static class InlineEdit
{
 /// <summary>The field of the rename in progress (tests).</summary>
 internal static TextBox? Current { get; private set; }

 /// <returns>False when the row is not shown, for example in a hidden list.</returns>
 internal static bool Begin(ListBox list, object item, string text, Action<string> commit)
 {
  list.ScrollIntoView(item); list.UpdateLayout();
  if (list.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem row || Title(row) is not { } title || !title.IsVisible) return false;
  var left = title.TranslatePoint(new Point(), list).X;
  var box = new TextBox { Text = text, FontSize = title.FontSize, Padding = new Thickness(2, 0, 2, 0), Width = Math.Max(120, list.ActualWidth - left - 14) };
  System.Windows.Automation.AutomationProperties.SetName(box, T("Name"));
  var popup = new Popup { PlacementTarget = title, Placement = PlacementMode.Relative, HorizontalOffset = -3, VerticalOffset = -2, Child = box, StaysOpen = false, AllowsTransparency = true };
  var done = false;
  void Finish(bool keep)
  {
   if (done) return;
   done = true; Current = null; popup.IsOpen = false;
   var name = box.Text.Trim();
   if (keep && name.Length > 0 && name != text) commit(name);
   list.Focus();
  }
  box.PreviewKeyDown += (_, e) =>
  {
   if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
   else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
  };
  box.LostKeyboardFocus += (_, _) => Finish(true);
  popup.Closed += (_, _) => Finish(true);
  popup.IsOpen = true; Current = box;
  box.Focus(); Keyboard.Focus(box); box.SelectAll();
  return true;
 }

 private static TextBlock? Title(DependencyObject parent)
 {
  for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
  {
   var child = VisualTreeHelper.GetChild(parent, index);
   if (child is TextBlock { Tag: "title" } title) return title;
   if (Title(child) is { } found) return found;
  }
  return null;
 }
}
