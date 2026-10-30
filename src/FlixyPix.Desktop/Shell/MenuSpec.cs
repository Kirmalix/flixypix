using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FlixyPix.Desktop;

/// <summary>One entry of a menu, described once and built for the right click, the "⋯" buttons and the keyboard alike
/// (UX plan U5.1). Submenus open on hover and with ←/→; entries that do not apply stay visible but disabled.</summary>
internal abstract record MenuSpec
{
 internal static MenuSpec Separator { get; } = new MenuSeparator();
 internal static MenuSpec Heading(string label) => new MenuHeading(label);
 internal static MenuCommand Command(string label, Action run, string? icon = null, string? shortcut = null, bool enabled = true) =>
  new(label, run) { Icon = icon, Shortcut = shortcut, Enabled = enabled };
 internal static MenuSubmenu Submenu(string label, IEnumerable<MenuSpec> items, string? icon = null) => new(label, [.. items]) { Icon = icon };
}
internal sealed record MenuCommand(string Label, Action Run) : MenuSpec
{
 /// <summary>A glyph of the icon font.</summary>
 public string? Icon { get; init; }
 /// <summary>Shown on the right, for example "Ctrl+T"; the key itself is handled elsewhere.</summary>
 public string? Shortcut { get; init; }
 public bool Enabled { get; init; } = true;
 /// <summary>Null for a plain command; true or false shows the entry as a switch with or without a check mark.</summary>
 public bool? Checked { get; init; }
}
internal sealed record MenuSubmenu(string Label, IReadOnlyList<MenuSpec> Items) : MenuSpec
{
 public string? Icon { get; init; }
}
internal sealed record MenuSeparator : MenuSpec;
internal sealed record MenuHeading(string Label) : MenuSpec;

internal static class Menus
{
 /// <summary>Opens a menu below <paramref name="target"/>, or at the mouse pointer without one.</summary>
 /// <param name="failed">Gets the entry's label and the error when a command throws.</param>
 internal static ContextMenu Open(UIElement? target, IEnumerable<MenuSpec> items, Action<string, Exception>? failed = null, PlacementMode placement = PlacementMode.Bottom)
 {
  var menu = Create(items, failed);
  if (target != null) { menu.PlacementTarget = target; menu.Placement = placement; menu.VerticalOffset = placement == PlacementMode.Bottom ? 4 : 0; }
  var previous=Keyboard.FocusedElement;
  menu.Closed+=(_,_)=> {if(previous is UIElement {IsVisible:true,IsEnabled:true} element)element.Focus();};
  menu.IsOpen = true; return menu;
 }
 /// <summary>The menu without opening it, for callers that place it themselves.</summary>
 internal static ContextMenu Create(IEnumerable<MenuSpec> items, Action<string, Exception>? failed = null)
 {
  var menu = new ContextMenu();
  Fill(menu.Items, items, failed);
  return menu;
 }

 /// <summary>Adds the entries; separators at the start, at the end or twice in a row and empty submenus are left out.</summary>
 internal static void Fill(ItemCollection target, IEnumerable<MenuSpec> items, Action<string, Exception>? failed = null)
 {
  var pendingSeparator = false;
  foreach (var spec in items)
  {
   if (spec is MenuSeparator) { pendingSeparator = target.Count > 0; continue; }
   if (spec is MenuSubmenu { Items.Count: 0 }) continue;
   if (pendingSeparator) { target.Add(new Separator()); pendingSeparator = false; }
   target.Add(Build(spec, failed));
  }
 }

 private static MenuItem Build(MenuSpec spec, Action<string, Exception>? failed)
 {
  switch (spec)
  {
   case MenuHeading heading:
    return new MenuItem { Header = heading.Label, IsEnabled = false, Style = (Style)Application.Current.Resources["MenuHeading"] };
   case MenuSubmenu submenu:
   {
    var item = new MenuItem { Header = submenu.Label, Icon = Glyph(submenu.Icon) };
    AutomationProperties.SetName(item, submenu.Label);
    Fill(item.Items, submenu.Items, failed);
    return item;
   }
   case MenuCommand command:
   {
    var item = new MenuItem { Header = command.Label, Icon = Glyph(command.Icon), InputGestureText = command.Shortcut ?? "", IsEnabled = command.Enabled,
     IsCheckable = command.Checked != null, IsChecked = command.Checked == true };
    AutomationProperties.SetName(item, command.Label);
    if (command.Shortcut is { Length: > 0 } shortcut) AutomationProperties.SetAcceleratorKey(item, shortcut);
    // A switch shows its new state only through the command; the check mark is not toggled by the click itself.
    item.Click += (_, e) => { e.Handled = true; if (item.IsCheckable) item.IsChecked = command.Checked == true; Run(command, failed); };
    return item;
   }
   default: throw new ArgumentOutOfRangeException(nameof(spec));
  }
 }

 private static void Run(MenuCommand command, Action<string, Exception>? failed)
 {
  try { command.Run(); }
  catch (Exception error) when (failed != null) { failed(command.Label, error); }
 }

 private static TextBlock? Glyph(string? icon) => icon == null ? null : new TextBlock { Text = icon, FontFamily = SettingsUi.Icons, FontSize = 14,
  HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

 /// <summary>The entries of a built menu as their labels, with submenus as "Label ▸ Entry" (tests).</summary>
 internal static IEnumerable<string> Labels(ItemsControl menu, string prefix = "")
 {
  foreach (var item in menu.Items.OfType<MenuItem>())
  {
   if (item.Style == Application.Current.Resources["MenuHeading"]) continue;
   var label = prefix + item.Header;
   yield return label;
   foreach (var inner in Labels(item, label + " ▸ ")) yield return inner;
  }
 }
}
