using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Keyboard and layout conveniences of the main window: folding sidebar, Home/End over the whole result,
/// a selection that survives scrolling out of the loaded window, and a large view on the space bar.</summary>
public partial class MainWindow
{
 /// <summary>Selected media whose tiles are outside the loaded window; they count as selected until the user picks anew.</summary>
 private List<MediaItem> _offWindowSelection = [];
 internal int OffWindowSelectionCount => _offWindowSelection.Count;
 internal QuickLookWindow? QuickLook { get; private set; }

 /// <summary>Scrolls to an index of the whole result, selects that medium and gives its tile the focus.</summary>
 internal async Task SelectIndexAsync(int index)
 {
  if (_total == 0) return;
  index = Math.Clamp(index, 0, _total - 1);
  await ScrollToAsync(index);
  var local = index - _windowStart;
  if (local < 0 || local >= MediaList.Items.Count) return;
  var card = MediaList.Items[local];
  MediaList.SelectedItem = card;
  (MediaList.ItemContainerGenerator.ContainerFromItem(card) as ListBoxItem)?.Focus();
 }

 /// <summary>Space bar: images open in the viewer, videos and audio show their thumbnail large; space or Esc closes again.</summary>
 private void ShowQuickLook(MediaItem item)
 {
  if (item.Kind == MediaKind.Image) { ShowImage(item); return; }
  QuickLook?.Close();
  QuickLook = new QuickLookWindow(this, item, async () => { StopSlideshow(); await Play(item); });
  QuickLook.Closed += (_, _) => QuickLook = null;
  QuickLook.Show();
 }
}

/// <summary>Large view of a video's thumbnail with its facts; Enter plays, space or Esc closes.</summary>
internal sealed class QuickLookWindow : Window
{
 internal bool HasPicture { get; }
 internal QuickLookWindow(Window owner, MediaItem item, Func<Task> play)
 {
  Owner = owner; Title = item.Title + T(" · Large view"); ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
  Width = Math.Max(640, owner.ActualWidth * .78); Height = Math.Max(420, owner.ActualHeight * .82); Background = Theme.Brush("MediaBackground");
  var picture = MediaCard.LoadBitmap(item.ThumbnailPath, 1920);
  HasPicture = picture != null;
  var card = new MediaCard(item, false);
  var caption = new StackPanel { Margin = new Thickness(20, 12, 20, 16) };
  caption.Children.Add(new TextBlock { Text = card.Title, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
  caption.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { card.Subtitle, File.Exists(item.Path) ? null : T("File currently unavailable") }.Where(s => !string.IsNullOrEmpty(s))), Foreground = SettingsUi.Muted, Margin = new Thickness(0, 3, 0, 0) });
  caption.Children.Add(new TextBlock { Text = T("Enter plays · Space or Esc closes"), Foreground = SettingsUi.Muted, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
  UIElement view = picture != null ? new Image { Source = picture, Stretch = Stretch.Uniform, Margin = new Thickness(16, 16, 16, 0) }
   : new TextBlock { Text = MediaCard.KindGlyph(item.Kind), FontFamily = SettingsUi.Icons, FontSize = 72, Foreground = Theme.Brush("IconMuted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
  var layout = new DockPanel();
  DockPanel.SetDock(caption, Dock.Bottom); layout.Children.Add(caption); layout.Children.Add(view);
  Content = layout;
  PreviewKeyDown += async (_, e) =>
  {
   if (e.Key is Key.Space or Key.Escape) { e.Handled = true; Close(); }
   else if (e.Key == Key.Enter) { e.Handled = true; Close(); await play(); }
  };
 }
}
