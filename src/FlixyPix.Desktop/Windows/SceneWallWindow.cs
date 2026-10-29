using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Opens the scene wall; a click on a tile plays its video from the mark.</summary>
 internal SceneWallWindow ShowSceneWall()
 {
  var window = new SceneWallWindow(this, _repository, ToolExists(_settings.Ffmpeg, "ffmpeg") ? _tools : null, ClipFolder, (item, seconds) => Play(item, seconds));
  // The media menu of the tile's video, led by playing from the mark.
  window.MenuRequested = tile => OpenMediaMenu(tile.Mark.Media, MediaMenuPlace.Wall, [MenuSpec.Command(T("Play from here"), () => _ = window.Open(tile), "\uE768")]);
  window.Show();
  return window;
 }

 /// <summary>Place of a mark clip in the clip cache: media id first, so it goes with its video.</summary>
 internal static string MarkClipPath(string folder, WallMark mark, double seconds, int width) =>
  Path.Combine(folder, mark.Media.Id.ToString("N") + "-at" + Math.Round(mark.StartSeconds * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture)
   + "-" + Math.Round(seconds * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) + "ms-" + width + ".mp4");
}

/// <summary>One tile of the scene wall.</summary>
internal sealed class WallTile(WallMark mark) : INotifyPropertyChanged
{
 private bool loaded; private ImageSource? picture;
 public WallMark Mark { get; } = mark;
 public string Title => string.IsNullOrWhiteSpace(Mark.Title) ? (Mark.Kind == WallMarkKind.Scene ? T("Scene") : T("Bookmark")) : Mark.Title;
 public string Subtitle => (string.IsNullOrWhiteSpace(Mark.Media.Title) ? Path.GetFileNameWithoutExtension(Mark.Media.Path) : Mark.Media.Title);
 public string Time => JumpMarks.FormatTime(Mark.StartSeconds) + (Mark.EndSeconds is { } end ? " – " + JumpMarks.FormatTime(end) : "");
 public string Icon => Mark.Kind == WallMarkKind.Scene ? "" : "";
 /// <summary>The picture of the mark, else the thumbnail of its video; read when the tile first shows.</summary>
 public ImageSource? Picture
 {
  get
  {
   if (loaded) return picture;
   loaded = true;
   picture = Mark.PreviewAssetId is { } asset ? MarkThumbnailConverter.Resolve?.Invoke(asset, false) : null;
   if (picture == null && Mark.Media.ThumbnailPath is { } path && File.Exists(path)) picture = MediaCard.LoadBitmap(path, 360);
   return picture;
  }
 }
 public Uri? Clip { get; private set; }
 internal void ShowClip(string? path) { Clip = path == null ? null : new Uri(path); PropertyChanged?.Invoke(this, new(nameof(Clip))); }
 public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Scene wall (feature plan P3): every scene and bookmark of the library's videos as a tile, filtered by text,
/// tags (performers and studios included) and kind. Hovering a tile plays a short silent clip from its place, made once
/// into the clip cache; a click plays the video from there.</summary>
internal sealed class SceneWallWindow : Window
{
 /// <summary>Length of the clip of a mark; a shorter scene plays whole.</summary>
 internal const double ClipSeconds = 5;
 private readonly CatalogRepository repository;
 private readonly MediaToolService? tools;
 private readonly string clipFolder;
 private readonly Func<MediaItem, double, Task> play;
 private readonly WindowFrame frame;
 private readonly TextBox text = new() { Padding = new Thickness(8, 6, 8, 6), MinWidth = 240 };
 private readonly TextBox tags = new() { Padding = new Thickness(8, 6, 8, 6), MinWidth = 240 };
 private readonly RadioButton[] kinds = [new() { Content = T("All") }, new() { Content = T("Scenes") }, new() { Content = T("Bookmarks") }];
 private readonly ListBox wall = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
 private readonly DispatcherTimer typing = new() { Interval = TimeSpan.FromMilliseconds(300) };
 private readonly CheckBox autoplay = new() { Content = T("Play by themselves"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 0, 6) };
 private readonly List<WallTile> playing = [];
 private CancellationTokenSource? loading, hovering, autoRun;
 private WallTile? hovered;
 private int first = -1, last = -1;

 internal IReadOnlyList<WallTile> Tiles { get; private set; } = [];
 internal Task Loading { get; private set; } = Task.CompletedTask;
 internal Task Hovering { get; private set; } = Task.CompletedTask;

 internal SceneWallWindow(Window owner, CatalogRepository repository, MediaToolService? tools, string clipFolder, Func<MediaItem, double, Task> play)
 {
  Owner = owner; this.repository = repository; this.tools = tools; this.clipFolder = clipFolder; this.play = play;
  Title = T("Scene wall · FlixyPix"); Width = 1180; Height = 780; MinWidth = 700; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
  AutomationProperties.SetName(text, T("Search scenes and bookmarks")); AutomationProperties.SetName(tags, T("Tags, performers or studios"));
  text.ToolTip = T("Searches the titles and descriptions of scenes, bookmarks and videos.");
  tags.ToolTip = T("Every tag must be on the scene or its video. Separate several with commas.");
  var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
  bar.Children.Add(Labeled(T("Search"), text)); bar.Children.Add(Labeled(T("Tags"), tags));
  var segments = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
  var group = "k" + Guid.NewGuid().ToString("N");
  for (var i = 0; i < kinds.Length; i++)
  {
   kinds[i].Style = (Style)Application.Current.Resources["Segment"]; kinds[i].GroupName = group; kinds[i].IsChecked = i == 0; kinds[i].Padding = new Thickness(12, 5, 12, 5);
   kinds[i].Checked += (_, _) => Reload(); segments.Children.Add(kinds[i]);
  }
  bar.Children.Add(segments);
  autoplay.ToolTip = T("The clips of the tiles in view play without pointing at them, as many at once as the video wall allows.");
  autoplay.IsChecked = repository.GetPreferences().Previews.SceneWallAutoplay;
  RoutedEventHandler remember = (_, _) =>
  {
   var previews = repository.GetPreferences().Previews;
   if (previews.SceneWallAutoplay != (autoplay.IsChecked == true)) repository.SavePreviewPreferences(previews with { SceneWallAutoplay = autoplay.IsChecked == true });
   UpdateAutoplay();
  };
  autoplay.Checked += remember; autoplay.Unchecked += remember;
  bar.Children.Add(autoplay);
  wall.AddHandler(VirtualizingTilePanel.VisibleRangeChangedEvent, new EventHandler<VisibleRangeEventArgs>((_, e) => { first = e.First; last = e.Last; UpdateAutoplay(); }));
  typing.Tick += (_, _) => { typing.Stop(); Reload(); };
  text.TextChanged += (_, _) => { typing.Stop(); typing.Start(); }; tags.TextChanged += (_, _) => { typing.Stop(); typing.Start(); };
  wall.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingTilePanel)));
  VirtualizingTilePanel.SetTargetTileWidth(wall, 250);
  wall.ItemTemplate = TileTemplate();
  ScrollViewer.SetVerticalScrollBarVisibility(wall, ScrollBarVisibility.Auto);
  wall.MouseMove += (_, e) => HoverOver(e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(wall, source) is ListBoxItem { DataContext: WallTile tile } ? tile : null);
  wall.MouseLeave += (_, _) => HoverOver(null);
  wall.MouseLeftButtonUp += async (_, e) => { if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(wall, source) is ListBoxItem { DataContext: WallTile tile }) await Open(tile); };
  wall.KeyDown += async (_, e) => { if (e.Key == Key.Enter && wall.SelectedItem is WallTile tile) { e.Handled = true; await Open(tile); } };
  wall.MouseRightButtonUp += (_, e) =>
  {
   if (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(wall, source) is not ListBoxItem { DataContext: WallTile tile } container) return;
   e.Handled = true; container.IsSelected = true; HoverOver(null); MenuRequested?.Invoke(tile);
  };
  wall.PreviewKeyDown += (_, e) =>
  {
   if ((e.Key == Key.Apps || e.Key == Key.System && e.SystemKey == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift) && wall.SelectedItem is WallTile tile) { e.Handled = true; MenuRequested?.Invoke(tile); }
  };
  var body = new DockPanel(); DockPanel.SetDock(bar, Dock.Top); body.Children.Add(bar); body.Children.Add(wall);
  frame = new WindowFrame(T("Scene wall"), T("All scenes and bookmarks of the library. Point at a tile to see a short clip from its place; a click plays the video from there."), body, "", scroll: false);
  frame.Add(T("Close"), Close, primary: true, cancel: true);
  Content = frame;
  Closing += (_, _) => { loading?.Cancel(); autoRun?.Cancel(); HoverOver(null); foreach (var tile in playing) tile.ShowClip(null); playing.Clear(); };
  Loaded += (_, _) => Reload();
 }

 private static StackPanel Labeled(string label, Control control)
 {
  var panel = new StackPanel { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Bottom };
  panel.Children.Add(new TextBlock { Text = label, Foreground = SettingsUi.Muted, Margin = new Thickness(0, 0, 0, 4) }); panel.Children.Add(control);
  return panel;
 }

 private static DataTemplate TileTemplate()
 {
  // Picture with the clip above it, the time and kind in the corners, title and video below.
  var picture = new FrameworkElementFactory(typeof(Image)); picture.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(WallTile.Picture))); picture.SetValue(Image.StretchProperty, Stretch.UniformToFill);
  var clip = new FrameworkElementFactory(typeof(LoopingClip)); clip.SetBinding(LoopingClip.SourceProperty, new System.Windows.Data.Binding(nameof(WallTile.Clip)));
  var time = Badge(nameof(WallTile.Time), HorizontalAlignment.Right, false); var kind = Badge(nameof(WallTile.Icon), HorizontalAlignment.Left, true);
  var stage = new FrameworkElementFactory(typeof(Grid)); stage.SetResourceReference(Panel.BackgroundProperty, "MediaBackground");
  stage.SetValue(UIElement.ClipToBoundsProperty, true); stage.SetValue(Privacy.BlurProperty, true);
  stage.AppendChild(picture); stage.AppendChild(clip); stage.AppendChild(time); stage.AppendChild(kind);
  var box = new FrameworkElementFactory(typeof(AspectBox)); box.SetValue(AspectBox.RatioProperty, 0.5625); box.AppendChild(stage);
  var title = Line(nameof(WallTile.Title), FontWeights.SemiBold, new Thickness(2, 8, 2, 2)); var video = Line(nameof(WallTile.Subtitle), FontWeights.Normal, new Thickness(2, 0, 2, 2));
  video.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
  var stack = new FrameworkElementFactory(typeof(StackPanel)); stack.AppendChild(box); stack.AppendChild(title); stack.AppendChild(video);
  var card = new FrameworkElementFactory(typeof(Border)); card.SetResourceReference(Border.BackgroundProperty, "CardBackground");
  card.SetValue(Border.CornerRadiusProperty, new CornerRadius(10)); card.SetValue(Border.PaddingProperty, new Thickness(7)); card.AppendChild(stack);
  return new DataTemplate { VisualTree = card };

  static FrameworkElementFactory Badge(string path, HorizontalAlignment side, bool icon)
  {
   var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(path));
   if (icon) label.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
   var badge = new FrameworkElementFactory(typeof(Border)); badge.SetResourceReference(StyleProperty, "BadgeFrame");
   badge.SetValue(VerticalAlignmentProperty, VerticalAlignment.Bottom); badge.SetValue(HorizontalAlignmentProperty, side); badge.AppendChild(label);
   return badge;
  }
  static FrameworkElementFactory Line(string path, FontWeight weight, Thickness margin)
  {
   var line = new FrameworkElementFactory(typeof(TextBlock)); line.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(path));
   line.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); line.SetValue(TextBlock.FontWeightProperty, weight); line.SetValue(MarginProperty, margin);
   return line;
  }
 }

 /// <summary>For tests: the filter as a user would type it.</summary>
 internal void Filter(string search, string tagList, WallMarkKind? kind)
 {
  text.Text = search; tags.Text = tagList; kinds[kind switch { WallMarkKind.Scene => 1, WallMarkKind.Bookmark => 2, _ => 0 }].IsChecked = true;
  typing.Stop(); Reload();
 }

 private void Reload()
 {
  if (!IsLoaded) return;
  loading?.Cancel(); var source = loading = new CancellationTokenSource(); var token = source.Token;
  var query = new WallQuery(text.Text, tags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
   Scenes: kinds[2].IsChecked != true, Bookmarks: kinds[1].IsChecked != true);
  frame.Status.Text = T("Loading …");
  Loading = LoadAsync();
  async Task LoadAsync()
  {
   try
   {
    var marks = await Task.Run(() => repository.GetWallMarks(query, token), token);
    if (token.IsCancellationRequested) return;
    HoverOver(null);
    foreach (var tile in playing) tile.ShowClip(null);
    playing.Clear(); first = last = -1;
    Tiles = marks.Select(mark => new WallTile(mark)).ToList(); wall.ItemsSource = Tiles;
    frame.Status.Text = Tn(Tiles.Count, "1 scene or bookmark", "{0:N0} scenes and bookmarks");
   }
   catch (OperationCanceledException) { }
  }
 }

 /// <summary>Starts the clip of the tile under the pointer after a short pause, making it first if needed; leaving the tile
 /// stops it.</summary>
 internal void HoverOver(WallTile? tile)
 {
  if (ReferenceEquals(tile, hovered)) return;
  hovering?.Cancel(); if (hovered != null && !playing.Contains(hovered)) hovered.ShowClip(null); hovered = tile;
  // While the tiles play by themselves, hovering changes nothing.
  if (tile == null || tools == null || autoplay.IsChecked == true) return;
  var source = hovering = new CancellationTokenSource();
  Hovering = ShowClipAsync(tile, source.Token);
 }

 private async Task ShowClipAsync(WallTile tile, CancellationToken token)
 {
  try
  {
   await Task.Delay(TimeSpan.FromMilliseconds(250), token);
   if (await ClipOfAsync(tile, token) is { } path) { tile.ShowClip(path); ClipsShown++; }
  }
  catch (OperationCanceledException) { }
 }
 internal int ClipsShown { get; private set; }

 /// <summary>The clip of a mark, made now if it is missing; null when the video is unavailable or the clip fails (the
 /// status line says why).</summary>
 private async Task<string?> ClipOfAsync(WallTile tile, CancellationToken token)
 {
  try
  {
   var mark = tile.Mark; var width = repository.GetPreferences().Previews.ClipWidth;
   var duration = mark.Media.DurationSeconds is > 0 and var known ? known - mark.StartSeconds : ClipSeconds;
   var seconds = Math.Max(0.5, Math.Min(Math.Min(ClipSeconds, mark.EndSeconds is { } end ? end - mark.StartSeconds : ClipSeconds), duration));
   var path = MainWindow.MarkClipPath(clipFolder, mark, seconds, width);
   if (!File.Exists(path))
   {
    if (!File.Exists(mark.Media.Path)) return null;
    FileAccessPolicy.Current.EnsureWritable(clipFolder);
    await tools!.CreateMarkClipAsync(mark.Media.Path, path, mark.StartSeconds, seconds, width, token);
   }
   token.ThrowIfCancellationRequested();
   return path;
  }
  catch (Exception error) when (!token.IsCancellationRequested && error is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
  { frame.Status.Text = T("Clip not possible · ") + error.Message; return null; }
 }

 /// <summary>"Play by themselves": the clips of the first tiles in view play without hovering, at most as many as the
 /// video wall allows; missing ones are made top first, and scrolling stops what left the view.</summary>
 private void UpdateAutoplay()
 {
  autoRun?.Cancel(); autoRun = null;
  var wanted = autoplay.IsChecked == true && tools != null && first >= 0
   ? Tiles.Skip(first).Take(Math.Max(0, last - first + 1)).Take(repository.GetPreferences().Previews.WallClips).ToList() : [];
  foreach (var tile in playing.Except(wanted).ToList()) { tile.ShowClip(null); playing.Remove(tile); }
  if (wanted.All(playing.Contains)) return;
  var source = autoRun = new CancellationTokenSource();
  Autoplaying = PlayAllAsync(wanted, source.Token);
 }

 private async Task PlayAllAsync(IReadOnlyList<WallTile> tiles, CancellationToken token)
 {
  try
  {
   await Task.Delay(TimeSpan.FromMilliseconds(150), token);
   foreach (var tile in tiles)
   {
    if (playing.Contains(tile) || await ClipOfAsync(tile, token) is not { } path) continue;
    tile.ShowClip(path); playing.Add(tile); ClipsShown++;
   }
  }
  catch (OperationCanceledException) { }
 }
 internal Task Autoplaying { get; private set; } = Task.CompletedTask;
 internal int PlayingCount => playing.Count;

 /// <summary>For tests: switches "Play by themselves" as a user would.</summary>
 internal void SetAutoplay(bool on) => autoplay.IsChecked = on;

 /// <summary>Right click, context menu key or Shift+F10 on a tile.</summary>
 internal Action<WallTile>? MenuRequested { get; set; }

 internal async Task Open(WallTile tile)
 {
  HoverOver(null);
  await play(tile.Mark.Media, tile.Mark.StartSeconds);
 }
}
