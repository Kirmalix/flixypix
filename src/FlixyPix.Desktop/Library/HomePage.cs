using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>The start page to carry on from: what is left to do, then rows that scroll sideways, led by continue watching,
/// recently added and recently tagged. Cards share the width of the window: as many as fit,
/// at least two. Everything comes from <see cref="HomeFeed"/>; the page only shows it.</summary>
internal sealed class HomePage : Grid
{
 internal Action<MediaItem>? Open { get; set; }
 internal Action<MediaItem>? ToggleQuick { get; set; }
 /// <summary>A card outside "continue watching" leads to the page of its medium.</summary>
 internal Action<MediaItem>? OpenDetails { get; set; }
 internal Action<HomeRow>? ShowAll { get; set; }
 internal Action<DirectoryEntry>? ShowEntity { get; set; }
 internal Action? Import { get; set; }
 internal Action? ContentPack { get; set; }
 /// <summary>The welcome page offers the bundled genre vocabulary without requiring a legacy installation.</summary>
 internal Func<bool>? ContentPackAvailable { get; set; }
 /// <summary>Right click, context menu key or Shift+F10 on a card or the banner: the media menu of that medium.</summary>
 internal Action<MediaItem>? MenuRequested { get; set; }

 private readonly ScrollViewer scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
 private readonly StackPanel content = new() { Margin = new Thickness(0, 0, 0, 24) };
 private readonly List<FrameworkElement> cards = [];
 private readonly WrapPanel tasks = new() { Margin = new Thickness(2, 0, 0, 4), Visibility = Visibility.Collapsed };
 internal int RowCount { get; private set; }
 internal int TaskCount => tasks.Children.OfType<Button>().Count();
 internal IReadOnlyList<HomeRowKind> Order { get; private set; } = [];
 internal double CardWidth { get; private set; } = 240;

 private readonly MediaCardContext _cardContext;
 internal HomePage(MediaCardContext context)
 {
  _cardContext = context;
  scroller.Content = content; Children.Add(scroller);
  SizeChanged += (_, _) => Fit();
 }

 /// <summary>Grey placeholders while the feed is being built.</summary>
 internal void ShowLoading()
 {
  content.Children.Clear(); cards.Clear();
  for (var row = 0; row < 2; row++)
  {
   var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 34, 0, 18) };
   for (var i = 0; i < 5; i++)
   {
    var block = new Border { Width = 240, Height = 135, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 12, 0) };
    block.SetResourceReference(Border.BackgroundProperty, "CardBackground"); strip.Children.Add(block);
   }
   content.Children.Add(strip);
  }
 }

 /// <summary>The three rows to carry on from lead, whatever order the others were chosen in.</summary>
 private static int Rank(HomeRowKind kind) => kind switch { HomeRowKind.ContinueWatching => 0, HomeRowKind.RecentlyAdded => 1, HomeRowKind.RecentlyTagged => 2, _ => 3 };

 internal void Show(HomeFeed feed, bool libraryEmpty)
 {
  // A rebuilt page keeps the place it was scrolled to.
  var offset = scroller.VerticalOffset;
  content.Children.Clear(); cards.Clear();
  if (libraryEmpty) { content.Children.Add(Welcome()); RowCount = 0; return; }
  content.Children.Add(tasks);
  var rows = feed.Rows.OrderBy(row => Rank(row.Kind)).ToList();
  foreach (var row in rows) content.Children.Add(Row(row));
  RowCount = rows.Count; Order = rows.Select(row => row.Kind).ToList();
  Fit();
  scroller.ScrollToVerticalOffset(offset);
 }

 /// <summary>A small strip of what is left to do; hidden when nothing is.</summary>
 internal void ShowTasks(IReadOnlyList<(string Label, long Count, Action Open)> open)
 {
  tasks.Children.Clear();
  tasks.Visibility = open.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
  if (open.Count == 0) return;
  tasks.Children.Add(new TextBlock { Text = T("To do:"), Foreground = SettingsUi.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) });
  foreach (var (label, count, go) in open)
  {
   var button = new Button { Content = count.ToString("N0", CultureInfo.CurrentCulture) + " · " + label, Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
   button.Click += (_, _) => go();
   tasks.Children.Add(button);
  }
 }

 private FrameworkElement Welcome()
 {
  var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 120, 0, 0) };
  panel.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/flixypix-mark-192.png")), Width = 120, Height = 120 });
  panel.Children.Add(new TextBlock { Text = T("Welcome to FlixyPix"), FontSize = 26, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 8) });
  panel.Children.Add(new TextBlock { Text = T("Import a folder and the home page fills with continue watching, suggestions and more."), Foreground = SettingsUi.Muted, HorizontalAlignment = HorizontalAlignment.Center });
  var import = new Button { Content = T("Import folder …"), Style = (Style)Application.Current.Resources["Primary"], HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
  import.Click += (_, _) => Import?.Invoke();
  panel.Children.Add(import);
  if (ContentPackAvailable?.Invoke() == true)
  {
   var pack = new Button { Content = T("Adult Content Pack"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,12,0,0) };
   pack.Click += (_, _) => ContentPack?.Invoke(); panel.Children.Add(pack);
  }
  return panel;
 }

 // ----- Rows -----

 private FrameworkElement Row(HomeRow row)
 {
  var panel = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
  var header = new DockPanel { Margin = new Thickness(2, 0, 4, 10) };
  if (ShowAll != null && row.Kind is not (HomeRowKind.FavoritePerformers or HomeRowKind.Studios or HomeRowKind.ContinueWatching or HomeRowKind.RandomOfTheDay or HomeRowKind.RecentlyWatched or HomeRowKind.RecentlyTagged))
  {
   var all = new Button { Content = T("Show all ›"), Style = (Style)Application.Current.Resources["Quiet"], FontSize = 13 };
   all.Click += (_, _) => ShowAll?.Invoke(row);
   DockPanel.SetDock(all, Dock.Right); header.Children.Add(all);
  }
  var title = new TextBlock { Text = row.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
  title.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
  header.Children.Add(title);
  panel.Children.Add(header);
  var strip = new StackPanel { Orientation = Orientation.Horizontal };
  if (row.Entities.Count > 0) foreach (var entity in row.Entities) strip.Children.Add(EntityCard(entity, row.Kind == HomeRowKind.FavoritePerformers));
  else foreach (var card in row.Cards) strip.Children.Add(MediaTile(card, row.Kind == HomeRowKind.ContinueWatching));
  panel.Children.Add(Slider(strip, row.Title));
  return panel;
 }

 /// <summary>A sideways scrolling strip with arrows that page by the visible width; the wheel keeps scrolling the page.</summary>
 private static Grid Slider(StackPanel strip, string name)
 {
  var view = new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 6, 0, 8) };
  view.PreviewMouseWheel += (_, e) =>
  {
   if (Keyboard.Modifiers != ModifierKeys.Shift) { e.Handled = true; var page = FindParent<ScrollViewer>(view); page?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent }); return; }
   e.Handled = true; view.ScrollToHorizontalOffset(view.HorizontalOffset - e.Delta);
  };
  var grid = new Grid(); grid.Children.Add(view);
  foreach (var (glyph, direction, align) in new[] { ("", -1, HorizontalAlignment.Left), ("", 1, HorizontalAlignment.Right) })
  {
   var arrow = new Button { Content = glyph, Style = (Style)Application.Current.Resources["TileAction"], Width = 40, Height = 40, FontSize = 16, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center,
    Margin = new Thickness(align == HorizontalAlignment.Left ? 4 : 0, 0, align == HorizontalAlignment.Right ? 4 : 0, 18), Visibility = Visibility.Collapsed };
   AutomationProperties.SetName(arrow, direction < 0 ? T("Scroll back in “{0}”", name) : T("Scroll forward in “{0}”", name));
   arrow.Click += (_, _) => Glide(view, view.HorizontalOffset + direction * Math.Max(200, view.ViewportWidth - 80));
   grid.Children.Add(arrow);
   void Update() => arrow.Visibility = grid.IsMouseOver && (direction < 0 ? view.HorizontalOffset > 1 : view.HorizontalOffset < view.ScrollableWidth - 1) ? Visibility.Visible : Visibility.Collapsed;
   grid.MouseEnter += (_, _) => Update(); grid.MouseLeave += (_, _) => Update(); view.ScrollChanged += (_, _) => Update();
  }
  return grid;
 }

 /// <summary>Horizontal glide with ease-out in a few frames (immediately when Windows turns animations off).</summary>
 private static void Glide(ScrollViewer view, double target)
 {
  target = Math.Clamp(target, 0, view.ScrollableWidth);
  if (!Motion.Enabled) { view.ScrollToHorizontalOffset(target); return; }
  EventHandler? step = null;
  step = (_, _) =>
  {
   var next = view.HorizontalOffset + (target - view.HorizontalOffset) / 4;
   if (Math.Abs(target - next) < 1) { view.ScrollToHorizontalOffset(target); CompositionTarget.Rendering -= step; return; }
   view.ScrollToHorizontalOffset(next);
  };
  CompositionTarget.Rendering += step;
 }

 private FrameworkElement MediaTile(HomeCard card, bool showProgress)
 {
  var tile = new MediaCard(card.Item, context: _cardContext);
  var picture = new Grid(); picture.SetResourceReference(BackgroundProperty, "MediaBackground");
  RoundedClip.SetRadius(picture, 8);
  picture.Children.Add(new TextBlock { Text = tile.Icon, FontFamily = SettingsUi.Icons, FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush("IconMuted") });
  var image = new Image { Stretch = Stretch.UniformToFill };
  image.SetBinding(Image.SourceProperty, new Binding(nameof(MediaCard.Thumbnail)) { Source = tile, NotifyOnTargetUpdated = true });
  Motion.SetFadeIn(image, true);
  picture.Children.Add(image); Privacy.SetBlur(picture, true);
  if (tile.DurationLabel.Length > 0)
   picture.Children.Add(new Border { Style = (Style)Application.Current.Resources["BadgeFrame"], VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Child = new TextBlock { Text = tile.DurationLabel } });
  if (showProgress && tile.ProgressVisibility == Visibility.Visible)
  {
   var bar = new Border { Background = Theme.Brush("Accent"), RenderTransformOrigin = new Point(0, .5), RenderTransform = new ScaleTransform(tile.Progress, 1) };
   picture.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = 4, Background = new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0)), Child = bar });
  }
  var collect=QuickButton(()=>card.Item);collect.HorizontalAlignment=HorizontalAlignment.Right;collect.VerticalAlignment=VerticalAlignment.Top;picture.Children.Add(collect);
  var frame = new AspectBox { Ratio = .5625, Child = picture };
  var title = new TextBlock { Text = tile.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 8, 2, 1) };
  var reason = new TextBlock { Text = card.Reason ?? tile.Subtitle, FontSize = 12, Foreground = SettingsUi.Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 2, 0) };
  var stack = new StackPanel(); stack.Children.Add(frame); stack.Children.Add(title); stack.Children.Add(reason);
  var button = CardButton(stack, card.Item.Title, () => { if (showProgress || OpenDetails == null) Open?.Invoke(card.Item); else OpenDetails(card.Item); });
  button.ToolTip = card.Item.Path;
  OffersMenu(button, () => card.Item);
  _ = tile.EnsureLoaded();
  cards.Add(button);
  return button;
 }

 private FrameworkElement EntityCard(HomeEntity entity, bool person)
 {
  var size = person ? 128.0 : 0;
  var picture = new Grid();
  FrameworkElement shape;
  if (person)
  {
   var circle = new Ellipse { Width = size, Height = size };
   circle.SetResourceReference(Shape.FillProperty, "CardBackground");
   picture.Children.Add(circle);
   picture.Children.Add(new TextBlock { Text = "", FontFamily = SettingsUi.Icons, FontSize = 36, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush("IconMuted") });
   if (entity.Cover is { } cover)
   {
    var face = new Ellipse { Width = size, Height = size };
    var tile = new MediaCard(cover, context: _cardContext);
    void Paint() { if (tile.Thumbnail is { } source) face.Fill = new ImageBrush(source) { Stretch = Stretch.UniformToFill }; }
    tile.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MediaCard.Thumbnail)) Paint(); };
    Paint(); _ = tile.EnsureLoaded();
    Privacy.SetBlur(face, true); picture.Children.Add(face);
   }
   shape = picture;
  }
  else
  {
   // Studios as wide tiles with a dimmed picture and the name on it.
   picture.SetResourceReference(BackgroundProperty, "MediaBackground"); RoundedClip.SetRadius(picture, 8);
   if (entity.Cover is { } cover)
   {
    var tile = new MediaCard(cover, context: _cardContext);
    var image = new Image { Stretch = Stretch.UniformToFill, Opacity = .45 };
    image.SetBinding(Image.SourceProperty, new Binding(nameof(MediaCard.Thumbnail)) { Source = tile });
    Privacy.SetBlur(image, true); picture.Children.Add(image); _ = tile.EnsureLoaded();
   }
   picture.Children.Add(new TextBlock { Text = entity.Entry.Tag, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10) });
   shape = new AspectBox { Ratio = .5625, Child = picture };
  }
  var stack = new StackPanel();
  stack.Children.Add(shape);
  if (person) stack.Children.Add(new TextBlock { Text = entity.Entry.Tag, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 9, 0, 1) });
  stack.Children.Add(new TextBlock { Text = entity.Entry.MediaCount.ToString("N0", CultureInfo.CurrentCulture) + " Medien" + (entity.Entry.IsFavorite ? " · ★" : ""), FontSize = 12, Foreground = SettingsUi.Muted,
   HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, person ? 0 : 7, 0, 0) });
  var button = CardButton(stack, entity.Entry.Tag, () => ShowEntity?.Invoke(entity.Entry));
  if (person) button.Width = size + 24; else cards.Add(button);
  return button;
 }

 private void OffersMenu(UIElement element, Func<MediaItem?> item)
 {
  void Request(RoutedEventArgs e) { if (item() is { } media && MenuRequested != null) { e.Handled = true; MenuRequested(media); } }
  element.MouseRightButtonUp += (_, e) => Request(e);
  element.KeyDown += (_, e) => { if (e.Key == Key.Apps || (e.Key == Key.F10 || e.Key == Key.System && e.SystemKey == Key.F10) && Keyboard.Modifiers == ModifierKeys.Shift) Request(e); };
 }

 private Button QuickButton(Func<MediaItem?> get)
 {
  var button=new Button {Content="○",FontFamily=new FontFamily("Segoe UI"),Style=(Style)Application.Current.Resources["TileAction"],Width=30,Height=30,Margin=new Thickness(5)};
  void Update() {if(get() is {} item) {button.Content=_cardContext.QuickMembers.Contains(item.Id)?"●":"○";button.ToolTip=_cardContext.QuickMembers.Contains(item.Id)?T("Remove from quick collection"):T("Add to quick collection");AutomationProperties.SetName(button,(string)button.ToolTip);} }
  button.Loaded+=(_,_)=>Update();button.MouseEnter+=(_,_)=>Update();button.Click+=(_,e)=> {e.Handled=true;if(get() is {} item)ToggleQuick?.Invoke(item);Update();};return button;
 }
 private static Button CardButton(FrameworkElement content, string name, Action click)
 {
  var button = new Button { Content = content, Margin = new Thickness(0, 0, 14, 0), Padding = new Thickness(0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top,
   Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Button\"><ContentPresenter/></ControlTemplate>") };
  AutomationProperties.SetName(button, name);
  Motion.SetHoverLift(button, true);
  button.Click += (_, e) => {if(!e.Handled)click();};
  return button;
 }

 /// <summary>Cards share the visible width: as many as fit at about 240 px, at least two.</summary>
 private void Fit()
 {
  var width = Math.Max(300, ActualWidth - 20);
  var visible = Math.Max(2, (int)((width + 14) / (240 + 14)));
  CardWidth = Math.Floor((width + 14) / visible - 14);
  foreach (var card in cards) card.Width = CardWidth;
 }

 private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
 {
  for (var parent = VisualTreeHelper.GetParent(child); parent != null; parent = VisualTreeHelper.GetParent(parent))
   if (parent is T found) return found;
  return null;
 }
}
