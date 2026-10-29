using System.Windows;
using System.Windows.Controls;

namespace FlixyPix.Desktop;

/// <summary>Shared layout of secondary windows: heading with symbol and hint, a body that scrolls on its own, and a fixed
/// footer with a status line on the left and the buttons on the right, so actions stay visible however long the body is.</summary>
internal sealed class WindowFrame : DockPanel
{
 internal TextBlock Status { get; }
 internal StackPanel Buttons { get; } = new() { Orientation = Orientation.Horizontal };

 /// <param name="status">A status line the window already updates; otherwise the frame makes one.</param>
 internal WindowFrame(string title, string? hint, UIElement body, string? glyph = null, bool scroll = true, TextBlock? status = null)
 {
  Status = status ?? new TextBlock { Foreground = SettingsUi.Muted };
  Status.VerticalAlignment = VerticalAlignment.Center; Status.TextTrimming = TextTrimming.CharacterEllipsis; Status.TextWrapping = TextWrapping.NoWrap; Status.Margin = new Thickness(0, 0, 12, 0);
  var header = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
  var heading = new StackPanel { Orientation = Orientation.Horizontal };
  if (glyph != null) { var symbol = SettingsUi.Icon(glyph, 22); symbol.Foreground = SettingsUi.Accent; symbol.Margin = new Thickness(0, 0, 12, 0); heading.Children.Add(symbol); }
  heading.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
  header.Children.Add(heading);
  if (hint != null) header.Children.Add(SettingsUi.Hint(hint));
  var footer = new DockPanel { Margin = new Thickness(24, 12, 24, 18) };
  DockPanel.SetDock(Buttons, Dock.Right); footer.Children.Add(Buttons); footer.Children.Add(Status);
  var divider = new Border { Height = 1, Background = SettingsUi.CardBorder };
  foreach (var (element, dock) in new (UIElement, Dock)[] { (header, Dock.Top), (footer, Dock.Bottom), (divider, Dock.Bottom) }) { SetDock(element, dock); Children.Add(element); }
  var content = new Border { Padding = new Thickness(24, 0, 24, 12), Child = body };
  Children.Add(scroll ? new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } : content);
 }

 /// <summary>Adds a footer button; the first primary button also answers Enter.</summary>
 internal Button Add(string text, Action click, bool primary = false, bool cancel = false)
 {
  var button = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), MinWidth = 96, IsCancel = cancel };
  if (primary) { button.Style = (Style)Application.Current.Resources["Primary"]; button.IsDefault = !Buttons.Children.OfType<Button>().Any(b => b.IsDefault); }
  button.Click += (_, _) => click();
  Buttons.Children.Add(button); return button;
 }
}
