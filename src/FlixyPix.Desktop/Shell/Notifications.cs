using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FlixyPix.Desktop;

/// <summary>Message window in the colour scheme of the app, replacing the system message box (which ignores dark mode).
/// Same calls as <see cref="MessageBox.Show(Window, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult)"/>;
/// the text can be selected and copied, Enter takes the default and Esc the cancelling choice.</summary>
internal static class Message
{
 internal static MessageBoxResult Show(Window? owner, string text, string caption = "FlixyPix", MessageBoxButton button = MessageBoxButton.OK,
  MessageBoxImage icon = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
  => new MessageWindow(owner, text, caption, button, icon, defaultResult).Ask();

 internal static MessageBoxResult Show(string text, string caption = "FlixyPix", MessageBoxButton button = MessageBoxButton.OK,
  MessageBoxImage icon = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
  => Show(Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive), text, caption, button, icon, defaultResult);

 /// <summary>Glyph and colour for a message kind, shared with the notifications.</summary>
 internal static (string Glyph, Brush Brush) Look(MessageBoxImage icon) => icon switch
 {
  MessageBoxImage.Error => ("", Theme.Brush("Error")),
  MessageBoxImage.Warning => ("", Theme.Brush("Gold")),
  MessageBoxImage.Question => ("", Theme.Brush("Accent")),
  MessageBoxImage.Information => ("", Theme.Brush("Accent")),
  _ => ("", Theme.Brush("Accent")),
 };
}

internal sealed class MessageWindow : Window
{
 private MessageBoxResult _result;
 private readonly MessageBoxResult _cancel;
 internal IReadOnlyList<Button> Choices { get; }

 internal MessageWindow(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
 {
  if (owner is { IsLoaded: true }) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; }
  else { WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = true; } // A message without window must stay findable.
  Title = caption; Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; FontSize = 14;
  Background = Theme.Brush("WindowBackground");
  (string Label, MessageBoxResult Result)[] choices = button switch
  {
   MessageBoxButton.OKCancel => [("OK", MessageBoxResult.OK), (T("Cancel"), MessageBoxResult.Cancel)],
   MessageBoxButton.YesNo => [(T("Yes"), MessageBoxResult.Yes), (T("No"), MessageBoxResult.No)],
   MessageBoxButton.YesNoCancel => [(T("Yes"), MessageBoxResult.Yes), (T("No"), MessageBoxResult.No), (T("Cancel"), MessageBoxResult.Cancel)],
   _ => [("OK", MessageBoxResult.OK)],
  };
  _cancel = choices.Any(c => c.Result == MessageBoxResult.Cancel) ? MessageBoxResult.Cancel : choices.Any(c => c.Result == MessageBoxResult.No) ? MessageBoxResult.No : choices[0].Result;
  _result = _cancel;
  var preferred = choices.Any(c => c.Result == defaultResult) ? defaultResult : choices[0].Result;

  var (glyph, brush) = Message.Look(icon);
  var body = new DockPanel { Margin = new Thickness(24, 22, 24, 8) };
  if (glyph.Length > 0)
  {
   var symbol = SettingsUi.Icon(glyph, 30); symbol.Foreground = brush; symbol.VerticalAlignment = VerticalAlignment.Top; symbol.Margin = new Thickness(0, 2, 16, 0);
   DockPanel.SetDock(symbol, Dock.Left); body.Children.Add(symbol);
  }
  var message = new TextBox
  {
   Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
   Foreground = Theme.Brush("Text"), MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 14, Focusable = true, IsTabStop = false,
  };
  AutomationProperties.SetName(message, caption);
  body.Children.Add(message);

  var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 14, 24, 20) };
  var buttons = new List<Button>();
  foreach (var (label, result) in choices)
  {
   var choice = new Button { Content = label, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = result == preferred, IsCancel = result == _cancel };
   if (result == preferred) choice.Style = (Style)Application.Current.Resources["Primary"];
   choice.Click += (_, _) => { _result = result; DialogResult = true; };
   footer.Children.Add(choice); buttons.Add(choice);
  }
  Choices = buttons;
  var layout = new DockPanel();
  var divider = new Border { Height = 1, Background = Theme.Brush("Divider") };
  DockPanel.SetDock(footer, Dock.Bottom); DockPanel.SetDock(divider, Dock.Bottom);
  layout.Children.Add(footer); layout.Children.Add(divider); layout.Children.Add(body);
  Content = layout;
  Loaded += (_, _) => buttons.First(b => b.IsDefault).Focus();
  // Ctrl+C copies the whole message, as the system message box does.
  PreviewKeyDown += (_, e) => { if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && message.SelectionLength == 0) { Clipboard.SetText(caption + Environment.NewLine + text); e.Handled = true; } };
 }

 internal MessageBoxResult Ask() { ShowDialog(); return _result; }
}

/// <summary>Short notifications at the bottom right of the main window (as the original's frmNotification): they do not
/// take focus, disappear on their own and can carry one action.</summary>
public partial class MainWindow
{
 private readonly StackPanel _toasts = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 360, 40), Width = 380 };
 internal IEnumerable<string> VisibleToasts => _toasts.Children.OfType<Border>().Select(b => (string)b.Tag);

 private void InitializeToasts()
 {
  Grid.SetRowSpan(_toasts, Math.Max(1, RootContent.RowDefinitions.Count)); Panel.SetZIndex(_toasts, 100);
  RootContent.Children.Add(_toasts);
 }

 internal void ShowToast(string text, MessageBoxImage kind = MessageBoxImage.Information, string? action = null, Action? onAction = null, double seconds = 5)
 {
  var (glyph, brush) = Message.Look(kind);
  var symbol = SettingsUi.Icon(glyph.Length > 0 ? glyph : "", 18); symbol.Foreground = brush; symbol.Margin = new Thickness(0, 0, 12, 0); symbol.VerticalAlignment = VerticalAlignment.Top;
  var row = new DockPanel();
  DockPanel.SetDock(symbol, Dock.Left); row.Children.Add(symbol);
  var close = new Button { Content = "", Style = (Style)Application.Current.Resources["IconButton"], FontSize = 10, Padding = new Thickness(5), VerticalAlignment = VerticalAlignment.Top, ToolTip = T("Close") };
  AutomationProperties.SetName(close, T("Close note"));
  DockPanel.SetDock(close, Dock.Right); row.Children.Add(close);
  var content = new StackPanel();
  content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("Text"), VerticalAlignment = VerticalAlignment.Center });
  var toast = new Border
  {
   Background = Theme.Brush("ChromeBackground"), BorderBrush = Theme.Brush("Divider"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
   Padding = new Thickness(14, 10, 8, 10), Margin = new Thickness(0, 8, 0, 0), Child = row, Tag = text, Opacity = 0,
   Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = .35 },
  };
  AutomationProperties.SetName(toast, text); AutomationProperties.SetLiveSetting(toast, AutomationLiveSetting.Polite);
  void Remove()
  {
   var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
   fade.Completed += (_, _) => _toasts.Children.Remove(toast);
   toast.BeginAnimation(OpacityProperty, fade);
  }
  if (action != null && onAction != null)
  {
   var act = new Button { Content = action, Style = (Style)Application.Current.Resources["Quiet"], Padding = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Foreground = Theme.Brush("Accent") };
   act.Click += (_, _) => { Remove(); onAction(); };
   content.Children.Add(act);
  }
  row.Children.Add(content);
  close.Click += (_, _) => Remove();
  while (_toasts.Children.Count >= 3) _toasts.Children.RemoveAt(0);
  _toasts.Children.Add(toast);
  toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
  var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
  // A notification under the pointer stays until the pointer leaves it.
  timer.Tick += (_, _) => { if (toast.IsMouseOver) return; timer.Stop(); Remove(); };
  timer.Start();
 }
}
