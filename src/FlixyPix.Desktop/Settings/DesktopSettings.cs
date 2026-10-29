using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.ComponentModel;
using FlixyPix.Imaging;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Where a window was: its normal bounds and whether it was maximized.</summary>
internal sealed record WindowPlace(double Left, double Top, double Width, double Height, bool Maximized);

internal sealed partial record DesktopSettings
{
 public string DataDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlixyPix", "Library");
 public string? Ffmpeg { get; init; }
 public string? Ffprobe { get; init; }
 public string? Mpv { get; init; }
 public bool AutoGeneratePreviews { get; init; } = true;
 public int ViewMode { get; init; }
 public int SortOrder { get; init; }
 public int TileWidth { get; init; } = 192;
 /// <summary>Library view shown last (all, Video, Image, Audio, favorites, watchlater); used when the library asks to restore it.</summary>
 public string LastView { get; init; } = "all";
 /// <summary>Main window place on the screen (original "rememberform"); null until the window was closed once.</summary>
 public WindowPlace? MainWindowPlace { get; init; }
 /// <summary>Hides all of FlixyPix at once, leaving only a tray icon; empty switches it off.</summary>
 public string BossKey { get; init; } = "Escape";
 /// <summary>Minimizing hides FlixyPix into the notification area instead of the taskbar.</summary>
 public bool MinimizeToTray { get; init; }
 /// <summary>Boss key behavior, lock, blurred pictures and traces in Windows.</summary>
 public PrivacySettings Privacy { get; init; } = new();
 /// <summary>Volume of the last video, used when the library remembers the volume.</summary>
 public int PlayerVolume { get; init; } = 100;
 /// <summary>The library sidebar is folded to its rail of symbols (Strg+B).</summary>
 public bool SidebarCollapsed { get; init; }
 /// <summary>Width of the unfolded sidebar as the user dragged it.</summary>
 public int SidebarWidth { get; init; } = 232;
 /// <summary>The details on the right are folded to their rail of symbols (Strg+D).</summary>
 public bool DetailsCollapsed { get; init; }
 public int DetailsWidth { get; init; } = 336;
 /// <summary>The details show details, tags and scenes as tabs instead of one below the other.</summary>
 public bool DetailsTabs { get; init; }
 /// <summary>Player: at the end continue with the next video of the view (and wrap); pick the next one at random.</summary>
 public bool PlayerRepeat { get; init; }
 public bool PlayerShuffle { get; init; }
 /// <summary>"system" (follow Windows), "light" or "dark".</summary>
 public string Theme { get; init; } = "system";
 /// <summary>"system" (display language of Windows) or a language code such as "en"; applies on the next start.</summary>
 public string Language { get; init; } = Texts.Automatic;
 public List<string> ProtectedSourcePaths { get; init; } = [];
 /// <summary>Libraries of this computer (original: databases of the database manager); the open one is <see cref="DataDirectory"/>.</summary>
 public List<KnownLibrary> Libraries { get; init; } = [];
 /// <summary>On start, ask which library to open when there are several (original "BypassDBPrompt", reversed; asks by default).</summary>
 public bool AskForLibraryAtStart { get; init; } = true;
 /// <summary>Programs of "Öffnen mit" on this computer (original: custom media players).</summary>
 public List<ExternalProgram> ExternalPrograms { get; init; } = [];
 /// <summary>Fixed targets of "Kopieren nach" and "Verschieben nach" on this computer (original "Quick access").</summary>
 public List<string> QuickAccessFolders { get; init; } = [];
 /// <summary>Providers of "Im Browser suchen"; null means the defaults.</summary>
 public List<WebSearchProvider>? WebSearchProviders { get; init; }
 public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlixyPix", "appsettings.json");
 public static DesktopSettings Load()
 {
  var basePath=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
  SettingsTransition.EnsureMigrated(Path.Combine(basePath,ProductIdentity.PreviousApplicationDirectory),Path.Combine(basePath,ProductIdentity.Name));
  return LoadFrom(SettingsPath);
 }
 public void Save() => SaveTo(SettingsPath);
 internal void SaveTo(string path)
 {
  FileAccessPolicy.Current.EnsureWritable(path);
  Directory.CreateDirectory(Path.GetDirectoryName(path)!);
  var temp = path + ".tmp";
  File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
  File.Move(temp, path, true);
  // The copy stands in when the file is later damaged beyond repair.
  try { File.Copy(path, LastGoodPath(path), true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
 }
}

internal static class DesktopImaging
{
 internal static ImageService Service { get; private set; } = new(Path.Combine(Path.GetTempPath(), "FlixyPix", "image-cache"));
 internal static void Configure(string dataDirectory) => Service = new(Path.Combine(dataDirectory,"image-cache"));
 internal static BitmapImage Bitmap(byte[] bytes)
 {
  using var input = new MemoryStream(bytes); var image = new BitmapImage(); image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad;
  image.StreamSource=input; image.EndInit(); image.Freeze(); return image;
 }
 internal static async Task<BitmapImage> LoadBitmapAsync(string path, int width=800, CancellationToken token=default) =>
  Bitmap((await Service.DecodeAsync(path,maxWidth:width,maxHeight:width,cancellationToken:token)).PngBytes);
}

internal sealed class MediaCard : INotifyPropertyChanged
{
 /// <summary>Pixel width thumbnails are decoded at; the window sets it from the tile width as laid out.</summary>
 internal static int DecodeWidth { get; set; } = 390;
 public MediaItem Item { get; private set; }
 /// <summary>New data for the same medium and file (for example after a tag edit); thumbnail and file check stay.</summary>
 internal void Replace(MediaItem item) { Item = item; PropertyChanged?.Invoke(this, new(string.Empty)); }
 private readonly bool showExtension;
 /// <summary>What tiles show (title or file name, wrapping, progress); set by the main window before it makes tiles.</summary>
 internal static GeneralPreferences Display { get; set; } = new();
 public string Title => !Display.ShowTitles ? (showExtension ? System.IO.Path.GetFileName(Item.Path) : System.IO.Path.GetFileNameWithoutExtension(Item.Path))
  : string.IsNullOrWhiteSpace(Item.Title) ? System.IO.Path.GetFileName(Item.Path) : Item.Title + (showExtension ? System.IO.Path.GetExtension(Item.Path) : "");
 public TextWrapping TitleWrapping => Display.FullTitles ? TextWrapping.Wrap : TextWrapping.NoWrap;
 /// <summary>Two lines for every tile when titles wrap, so the rows of the grid keep one height.</summary>
 public double TitleHeight => Display.FullTitles ? 38 : double.NaN;
 /// <summary>The media file was not reachable when the tile was first shown (only checked when the library marks missing files).</summary>
 public bool IsMissing { get; private set; }
 public Visibility MissingVisibility { get { EnsureLoaded(); return IsMissing ? Visibility.Visible : Visibility.Collapsed; } }
 public string Path => Item.Path;
 public string Icon => KindGlyph(Item.Kind);
 public string KindLabel => Item.Kind switch { MediaKind.Image => T("IMAGE"), MediaKind.Video => T("VIDEO"), _ => T("AUDIO") };
 /// <summary>Half stars as text; empty for unrated media so tiles stay quiet.</summary>
 public string Stars => Item.Rating > 0 ? new string('★', Math.Clamp(Item.Rating, 0, 10) / 2) + (Item.Rating % 2 == 1 ? "½" : "") : "";
 public string Subtitle => string.Join(" · ", new[] { Item.Width.HasValue ? $"{Item.Width} × {Item.Height}" : null,
  Item.Kind != MediaKind.Image && Item.DurationSeconds.HasValue ? DurationText(Item.DurationSeconds.Value) : null,
  Item.FileSize.HasValue ? SizeText(Item.FileSize.Value) : null }.Where(x => x != null));
 public string DurationLabel => Item.Kind != MediaKind.Image && Item.DurationSeconds is > 0 and var seconds ? DurationText(seconds) : "";
 /// <summary>Resolution class like 720p, 1080p, 4K or 8K, for videos and images from 720 lines up (the original's overlays said HD/FHD/UHD).</summary>
 public string Quality => QualityOf(Item.Width, Item.Height);
 /// <summary>Badges top left, as chosen under "Tiles": resolution, HDR, high frame rate, video codec, number of files.</summary>
 public IReadOnlyList<string> Badges
 {
  get
  {
   var shown = Display.TileBadges; var badges = new List<string>();
   if (shown.Contains(TileBadge.Resolution) && Quality.Length > 0) badges.Add(Quality);
   if (Item.Kind == MediaKind.Video && Item.Technical is { } technical)
   {
    if (shown.Contains(TileBadge.Hdr) && technical.Hdr) badges.Add("HDR");
    if (shown.Contains(TileBadge.FrameRate) && TechnicalLabels.HighFrameRate(technical.FrameRate)) badges.Add(TechnicalLabels.FrameRate(technical.FrameRate));
    if (shown.Contains(TileBadge.Codec) && technical.VideoCodec is { Length: > 0 } codec) badges.Add(TechnicalLabels.Codec(codec));
   }
   if (shown.Contains(TileBadge.Versions) && Item.Files.Count > 0) badges.Add(Tn(Item.Files.Count + 1, "1 file", "{0} files"));
   return badges;
  }
 }
 public bool IsFavorite => Item.IsFavorite;
 public bool WatchLater => Item.WatchLater;
 public Visibility DurationVisibility => DurationLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
 /// <summary>Share already watched (original "showvideoprogress"); shown between 1 % and 95 %, like "continue watching".</summary>
 public double Progress => Item.Kind != MediaKind.Image && Item.DurationSeconds is > 0 and var length ? Math.Clamp(Item.PlaybackPositionSeconds / length, 0, 1) : 0;
 public Visibility ProgressVisibility => Progress is > .01 and < .95 ? Visibility.Visible : Visibility.Collapsed;
 public Visibility TileProgressVisibility => Display.ShowProgress ? ProgressVisibility : Visibility.Collapsed;
 public string ProgressLabel => ProgressVisibility == Visibility.Visible ? T("Watched up to {0} ({1:P0})", DurationText(Item.PlaybackPositionSeconds), Progress) : "";
 public Visibility QualityVisibility => Quality.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
 public Visibility FavoriteVisibility => Item.IsFavorite ? Visibility.Visible : Visibility.Collapsed;
 public Visibility WatchLaterVisibility => Item.WatchLater ? Visibility.Visible : Visibility.Collapsed;
 /// <summary>Media in the collection key B fills; set by the main window before it makes tiles.</summary>
 internal static HashSet<Guid> TargetMembers { get; set; } = [];
 public Visibility TargetVisibility => TargetMembers.Contains(Item.Id) ? Visibility.Visible : Visibility.Collapsed;
 public string PlayGlyph => Item.Kind == MediaKind.Image ? "" : "";
 public string PlayLabel => Item.Kind == MediaKind.Image ? T("View") : T("Play");
 internal static string KindGlyph(MediaKind kind) => kind switch { MediaKind.Image => "", MediaKind.Video => "", _ => "" };
 internal static string DurationText(double seconds) { var time = TimeSpan.FromSeconds(Math.Round(seconds)); return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss", CultureInfo.InvariantCulture); }
 internal static string SizeText(long bytes) => bytes switch
 {
  < 1024 => $"{bytes} B",
  < 1024 * 1024 => $"{bytes / 1024d:0} KB",
  < 1024L * 1024 * 1024 => $"{bytes / 1048576d:0.#} MB",
  _ => $"{bytes / 1073741824d:0.##} GB",
 };
 internal static string QualityOf(int? width, int? height)
 {
  if (width is not > 0 || height is not > 0) return "";
  var lines = Math.Min(width.Value, height.Value);
  return lines >= 720 ? TechnicalLabels.Resolution(width, height) : "";
 }
 private ImageSource? _thumbnail;
 public ImageSource? Thumbnail { get { EnsureLoaded(); return _thumbnail; } }
 /// <summary>Frame of the hover preview drawn over the thumbnail; null when no preview runs.</summary>
 public ImageSource? PreviewFrame { get; private set; }
 public string PreviewTime { get; private set; } = "";
 public Visibility PreviewTimeVisibility => PreviewTime.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
 /// <summary>Preview clip played over the thumbnail; null when no clip runs.</summary>
 public Uri? PreviewClip { get; private set; }
 public bool IsPreviewing => PreviewFrame != null || PreviewClip != null;
 /// <summary>Shows a frame of the hover preview; null ends the preview, a running clip included.</summary>
 internal void ShowPreviewFrame(ImageSource? frame, string time)
 {
  PreviewFrame = frame; PreviewTime = frame == null ? "" : time;
  if (frame == null) PreviewClip = null;
  foreach (var name in new[] { nameof(PreviewFrame), nameof(PreviewTime), nameof(PreviewTimeVisibility), nameof(PreviewClip), nameof(IsPreviewing) }) PropertyChanged?.Invoke(this, new(name));
 }
 internal void ShowPreviewClip(string path)
 {
  PreviewClip = new Uri(path);
  foreach (var name in new[] { nameof(PreviewClip), nameof(IsPreviewing) }) PropertyChanged?.Invoke(this, new(name));
 }
 public event PropertyChangedEventHandler? PropertyChanged;
 private readonly bool loadThumbnail, markMissing;
 private Task? _ready;
 /// <summary>Completes once the file check and thumbnail of a shown tile are done; tiles never shown do no work.</summary>
 internal Task ReadyTask => _ready ?? Task.CompletedTask;
 internal bool LoadStarted => _ready != null;
 public MediaCard(MediaItem item,bool loadThumbnail=true,bool showExtension=false,bool markMissing=false)
 {
  Item = item; this.showExtension = showExtension; this.loadThumbnail = loadThumbnail; this.markMissing = markMissing;
 }
 /// <summary>Starts the file check and thumbnail when a tile first shows them, so a page reads only the media in view.</summary>
 internal Task EnsureLoaded() => _ready ??= LoadAsync();
 private async Task LoadAsync()
 {
  if (markMissing && !await Task.Run(() => MediaPresence.Exists(Item.Path)))
  {
   IsMissing = true; PropertyChanged?.Invoke(this, new(nameof(IsMissing))); PropertyChanged?.Invoke(this, new(nameof(MissingVisibility)));
   if (Item.ThumbnailPath == null) return;
  }
  if (loadThumbnail) await LoadThumbnailAsync(Item.ThumbnailPath ?? (Item.Kind == MediaKind.Image ? Item.Path : null));
 }
 private async Task LoadThumbnailAsync(string? path)
 {
  if (string.IsNullOrEmpty(path) || !await Task.Run(() => File.Exists(path))) return;
  try
  {
   var bitmap=await DesktopImaging.LoadBitmapAsync(path,DecodeWidth);
   void SetThumbnail() { _thumbnail=bitmap; PropertyChanged?.Invoke(this,new(nameof(Thumbnail))); }
   if(Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())await dispatcher.InvokeAsync(SetThumbnail); else SetThumbnail();
  }
  catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException) { }
 }
 internal static BitmapImage? LoadBitmap(string? path, int width = 800)
 {
  if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
  try { var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; if (width > 0) image.DecodePixelWidth = width; image.UriSource = new Uri(System.IO.Path.GetFullPath(path)); image.EndInit(); image.Freeze(); return image; }
  catch { return null; }
 }
}

internal static class Dialogs
{
 internal static string[]? Fields(Window owner, string title, params (string Label, string Value)[] fields)
 {
  var panel = new StackPanel();
  var inputs = new List<TextBox>();
  foreach (var field in fields)
  {
   panel.Children.Add(new TextBlock { Text = field.Label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, inputs.Count == 0 ? 0 : 12, 0, 4) });
   var input = new TextBox { Text = field.Value }; System.Windows.Automation.AutomationProperties.SetName(input, field.Label); panel.Children.Add(input); inputs.Add(input);
  }
  var dialog = new Window { Owner = owner, Title = title, Width = 520, SizeToContent = SizeToContent.Height, MaxHeight = 800, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
  var frame = new WindowFrame(title, null, panel);
  frame.Add(T("Apply"), () => dialog.DialogResult = true, primary: true); frame.Add(T("Cancel"), dialog.Close, cancel: true);
  dialog.Content = frame;
  dialog.Loaded += (_, _) => { if (inputs.Count > 0) { inputs[0].Focus(); inputs[0].SelectAll(); } };
  return dialog.ShowDialog() == true ? inputs.Select(i => i.Text.Trim()).ToArray() : null;
 }
 internal static void Text(Window owner, string title, string text) => TextWindow(owner, title, text).ShowDialog();
 internal static Window TextWindow(Window owner, string title, string text)
 {
  var dialog = new Window { Owner = owner, Title = title, Width = 720, Height = 570, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
  var body = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8) };
  var frame = new WindowFrame(title, null, body, scroll: false);
  frame.Add(T("Close"), dialog.Close, primary: true, cancel: true);
  var copy = frame.Add(T("Copy"), () => Clipboard.SetText(text)); frame.Buttons.Children.Remove(copy); frame.Buttons.Children.Insert(0, copy);
  dialog.Content = frame; return dialog;
 }
}

/// <summary>Whether media files are reachable. A drive or share that is offline is remembered for a while, so a page
/// of tiles from an unplugged disk does not wait for every single file.</summary>
internal static class MediaPresence
{
 private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Available, DateTime Checked)> roots = new(StringComparer.OrdinalIgnoreCase);
 internal static bool Exists(string path)
 {
  try
  {
   var root = System.IO.Path.GetPathRoot(path);
   if (!string.IsNullOrEmpty(root))
   {
    var now = DateTime.UtcNow;
    if (!roots.TryGetValue(root, out var state) || now - state.Checked > TimeSpan.FromSeconds(30)) roots[root] = state = (Directory.Exists(root), now);
    if (!state.Available) return false;
   }
   return File.Exists(path);
  }
  catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
 }
}
