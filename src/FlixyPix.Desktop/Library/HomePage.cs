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

/// <summary>The start page: a large banner with up to five media that take turns, and rows that scroll sideways (continue
/// watching, suggestions, recently added, performers, studios …). Cards share the width of the window: as many as fit,
/// at least two. Everything comes from <see cref="HomeFeed"/>; the page only shows it.</summary>
internal sealed class HomePage : Grid
{
 internal Action<MediaItem>? Open { get; set; }
 internal Action<MediaItem>? ToggleQuick { get; set; }
 internal Func<MediaItem,CancellationToken,Task<ImageSource?>>? FeaturedVideoArtwork { get; set; }
 internal Action<MediaItem>? ToggleWatchLater { get; set; }
 internal Action<HomeRow>? ShowAll { get; set; }
 internal Action<DirectoryEntry>? ShowEntity { get; set; }
 internal Action? Import { get; set; }
 internal Action? ContentPack { get; set; }
 /// <summary>The welcome page offers the content pack only when an installation of the original can supply its names.</summary>
 internal Func<bool>? ContentPackAvailable { get; set; }
 /// <summary>Right click, context menu key or Shift+F10 on a card or the banner: the media menu of that medium.</summary>
 internal Action<MediaItem>? MenuRequested { get; set; }

 private readonly ScrollViewer scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
 private readonly StackPanel content = new() { Margin = new Thickness(0, 0, 0, 24) };
 private readonly List<FrameworkElement> cards = [];
 private readonly DispatcherTimer heroTimer = new() { Interval = TimeSpan.FromSeconds(9) };
 private IReadOnlyList<HomeCard> heroes = [];
 private long artworkVersion;
 private CancellationTokenSource? featuredRequest;
 private int heroIndex;
 private Grid? hero;
 private Border sideFade = new(), bottomFade = new();
 private Image? heroPicture;
 private TextBlock? heroTitle, heroReason;
 private Button? heroPlay, heroLater;
 private StackPanel? heroDots;
 internal int RowCount { get; private set; }
 internal int HeroCount => heroes.Count;
 internal double CardWidth { get; private set; } = 240;

 internal HomePage()
 {
  scroller.Content = content; Children.Add(scroller);
  SizeChanged += (_, _) => Fit();
  heroTimer.Tick += (_, _) => ShowHero(heroIndex + 1);
  // The theme outlives a page; subscribe only while it is in a window, so a closed library window is not kept alive.
  Loaded += (_, _) => { Theme.Changed -= PaintFades; Theme.Changed += PaintFades; PaintFades(); };
  Unloaded += (_, _) => Theme.Changed -= PaintFades;
  IsVisibleChanged += (_, _) => { if (IsVisible && heroes.Count > 1) heroTimer.Start(); else {heroTimer.Stop();featuredRequest?.Cancel();} };
 }

 /// <summary>Grey placeholders while the feed is being built.</summary>
 internal void ShowLoading()
 {
  featuredRequest?.Cancel();content.Children.Clear(); cards.Clear(); hero = null; heroTimer.Stop();
  var banner = new Border { Height = 300, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 0, 24) };
  banner.SetResourceReference(Border.BackgroundProperty, "CardBackground");
  content.Children.Add(banner);
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

 internal void Show(HomeFeed feed, bool libraryEmpty)
 {
  // A rebuilt page keeps the place it was scrolled to and the banner entry on show.
  var offset = scroller.VerticalOffset; var shownId = CurrentHero?.Item.Id;
  featuredRequest?.Cancel();content.Children.Clear(); cards.Clear(); hero = null; heroTimer.Stop();
  if (libraryEmpty) { content.Children.Add(Welcome()); RowCount = 0; heroes = []; return; }
  artworkVersion++;
  heroes = feed.Hero;

  if (heroes.Count > 0)
  {
   content.Children.Add(Hero());
   ShowHero(Math.Max(0, heroes.ToList().FindIndex(h => h.Item.Id == shownId)), animate: false);
   if (heroes.Count > 1 && IsVisible) heroTimer.Start();
  }
  foreach (var row in feed.Rows) content.Children.Add(Row(row));
  RowCount = feed.Rows.Count;
  Fit();
  scroller.ScrollToVerticalOffset(offset);
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

 // ----- Banner -----

 private FrameworkElement Hero()
 {
  hero = new Grid { Height = 360, Margin = new Thickness(0, 0, 0, 10) };
  RoundedClip.SetRadius(hero, 14);
  hero.Loaded+=(_,_)=> {if(CurrentHero is {} current)_=ShowHeroPictureAsync(current.Item,false);};
  hero.IsVisibleChanged+=(_,_)=> {if(hero?.IsVisible==true&&CurrentHero is {} current)_=ShowHeroPictureAsync(current.Item,false);else featuredRequest?.Cancel();};
  hero.SetResourceReference(BackgroundProperty, "MediaBackground");
  heroPicture = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Right };
  Privacy.SetBlur(heroPicture, true); hero.Children.Add(heroPicture);
  // The picture fades into the page towards the text on the left and the rows below.
  sideFade = new Border(); bottomFade = new Border(); PaintFades();
  hero.Children.Add(sideFade); hero.Children.Add(bottomFade);
  var text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(36, 0, 36, 34), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
  heroTitle = new TextBlock { FontSize = 34, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 92 };
  heroTitle.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
  heroReason = new TextBlock { FontSize = 14, Margin = new Thickness(0, 6, 0, 16), Foreground = SettingsUi.Muted, TextTrimming = TextTrimming.CharacterEllipsis };
  heroPlay = new Button { Style = (Style)Application.Current.Resources["Primary"], Padding = new Thickness(20, 10, 22, 10), Margin = new Thickness(0, 0, 10, 0) };
  heroPlay.Click += (_, _) => { if (CurrentHero is { } card) Open?.Invoke(card.Item); };
  heroLater = new Button { Padding = new Thickness(16, 10, 16, 10) };
  heroLater.Click += (_, _) => { if (CurrentHero is { } card) { ToggleWatchLater?.Invoke(card.Item); heroes = heroes.Select(h => h.Item.Id == card.Item.Id ? h with { Item = h.Item with { WatchLater = !h.Item.WatchLater } } : h).ToList(); ShowHero(heroIndex, animate: false); } };
  var buttons = new StackPanel { Orientation = Orientation.Horizontal }; buttons.Children.Add(heroPlay); buttons.Children.Add(heroLater);
  heroDots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
  text.Children.Add(heroTitle); text.Children.Add(heroReason); text.Children.Add(buttons); text.Children.Add(heroDots);
  var collect=QuickButton(()=>CurrentHero?.Item);collect.HorizontalAlignment=HorizontalAlignment.Right;collect.VerticalAlignment=VerticalAlignment.Top;collect.Margin=new Thickness(12);hero.Children.Add(collect);
  hero.Children.Add(text);
  OffersMenu(hero, () => CurrentHero?.Item);
  hero.MouseEnter += (_, _) => heroTimer.Stop();
  hero.MouseLeave += (_, _) => { if (heroes.Count > 1) heroTimer.Start(); };
  return hero;
 }

 /// <summary>The fades end in the page colour, so they follow a change of the colour scheme.</summary>
 private void PaintFades()
 {
  var page = Theme.Color("WindowBackground", Theme.IsDark);
  Color Alpha(byte alpha) => Color.FromArgb(alpha, page.R, page.G, page.B);
  sideFade.Background = new LinearGradientBrush(new GradientStopCollection { new(page, 0), new(Alpha(200), .38), new(Alpha(0), .75) }, 0);
  bottomFade.Background = new LinearGradientBrush(new GradientStopCollection { new(Alpha(0), .55), new(page, 1) }, 90);
 }

 private HomeCard? CurrentHero => heroes.Count == 0 ? null : heroes[heroIndex % heroes.Count];

 internal void ShowHero(int index, bool animate = true)
 {
  if (heroes.Count == 0 || heroPicture is null) return;
  heroIndex = (index % heroes.Count + heroes.Count) % heroes.Count;
  var card = heroes[heroIndex];
  var tile = new MediaCard(card.Item, loadThumbnail: false);
  _ = ShowHeroPictureAsync(card.Item, animate);
  heroTitle!.Text = tile.Title;
  heroReason!.Text = string.Join(" · ", new[] { card.Reason, tile.DurationLabel, tile.Stars }.Where(s => !string.IsNullOrWhiteSpace(s)));
  var started = tile.ProgressVisibility == Visibility.Visible;
  heroPlay!.Content = Label(card.Item.Kind == MediaKind.Image ? "" : "", card.Item.Kind == MediaKind.Image ? T("View") : started ? T("Continue watching") : T("Play"));
  heroLater!.Content = Label(card.Item.WatchLater ? "" : "", card.Item.WatchLater ? T("On the watch list") : T("Watch later"));
  heroDots!.Children.Clear();
  for (var i = 0; i < heroes.Count; i++)
  {
   var target = i;
   var dot = new Button { Width = i == heroIndex ? 22 : 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, ToolTip = heroes[i].Item.Title, Focusable = false,
    Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Button\"><Border Background=\"{TemplateBinding Background}\" CornerRadius=\"4\"/></ControlTemplate>") };
   dot.SetResourceReference(BackgroundProperty, i == heroIndex ? "Accent" : "ScrollThumb");
   dot.Click += (_, _) => ShowHero(target);
   heroDots.Children.Add(dot);
  }
 }

 /// <summary>The banner is wide, so it decodes its picture larger than a tile does; pictures stay cached while the page lives.</summary>
 private async Task ShowHeroPictureAsync(MediaItem item, bool animate)
 {
  featuredRequest?.Cancel();featuredRequest?.Dispose();featuredRequest=new();
  var token=featuredRequest.Token;
  var version=artworkVersion;
  var path = item.ThumbnailPath ?? (item.Kind == MediaKind.Image ? item.Path : null);
  var picture = item.Kind==MediaKind.Video && hero?.IsVisible==true && ActualHeight>=560 && FeaturedVideoArtwork!=null
   ? await FeaturedVideoArtwork(item,token) : await Task.Run(() => MediaCard.LoadBitmap(path, MediaCard.DecodeWidth));
  if(token.IsCancellationRequested||version!=artworkVersion)return;
  if (CurrentHero?.Item.Id != item.Id || heroPicture is null) return;
  heroPicture.Source = picture;
  heroPicture.Stretch=picture is System.Windows.Media.Imaging.BitmapSource bitmap && bitmap.PixelWidth<960?Stretch.None:Stretch.UniformToFill;
  if(picture is null)hero!.Height=260;
  if (animate && Motion.Enabled) heroPicture.BeginAnimation(OpacityProperty, new DoubleAnimation(.2, 1, TimeSpan.FromMilliseconds(600)));
 }

 private static StackPanel Label(string glyph, string text)
 {
  var panel = new StackPanel { Orientation = Orientation.Horizontal };
  panel.Children.Add(new TextBlock { Text = glyph, FontFamily = SettingsUi.Icons, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 9, 0) });
  panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
  return panel;
 }

 // ----- Rows -----

 private FrameworkElement Row(HomeRow row)
 {
  var panel = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
  var header = new DockPanel { Margin = new Thickness(2, 0, 4, 10) };
  if (ShowAll != null && row.Kind is not (HomeRowKind.FavoritePerformers or HomeRowKind.Studios or HomeRowKind.ContinueWatching or HomeRowKind.RandomOfTheDay or HomeRowKind.RecentlyWatched))
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
  var tile = new MediaCard(card.Item);
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
  var button = CardButton(stack, card.Item.Title, () => Open?.Invoke(card.Item));
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
    var tile = new MediaCard(cover);
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
    var tile = new MediaCard(cover);
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
  void Update() {if(get() is {} item) {button.Content=MediaCard.QuickMembers.Contains(item.Id)?"●":"○";button.ToolTip=MediaCard.QuickMembers.Contains(item.Id)?T("Remove from quick collection"):T("Add to quick collection");AutomationProperties.SetName(button,(string)button.ToolTip);} }
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

 /// <summary>Cards share the visible width: as many as fit at about 240 px, at least two; the banner follows the height.</summary>
 private void Fit()
 {
  var width = Math.Max(300, ActualWidth - 20);
  var visible = Math.Max(2, (int)((width + 14) / (240 + 14)));
  CardWidth = Math.Floor((width + 14) / visible - 14);
  foreach (var card in cards) card.Width = CardWidth;
  if (hero != null)
  {
   hero.Height = Math.Clamp(ActualHeight * .5, 260, 460);
   hero.Visibility = ActualHeight < 560 ? Visibility.Collapsed : Visibility.Visible;
  }
 }

 private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
 {
  for (var parent = VisualTreeHelper.GetParent(child); parent != null; parent = VisualTreeHelper.GetParent(parent))
   if (parent is T found) return found;
  return null;
 }
}
