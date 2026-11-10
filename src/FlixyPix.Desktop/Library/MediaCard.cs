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

internal sealed class MediaCardContext
{
 internal int DecodeWidth { get; set; } = 390;
 internal GeneralPreferences Display { get; set; } = new();
 internal HashSet<Guid> TargetMembers { get; set; } = [];
 internal HashSet<Guid> FinishedMembers { get; set; } = [];
 internal HashSet<Guid> QuickMembers { get; set; } = [];
}

internal sealed class MediaCard : INotifyPropertyChanged
{
 /// <summary>Pixel width thumbnails are decoded at; the window sets it from the tile width as laid out.</summary>
 private int DecodeWidth => context.DecodeWidth;
 public MediaItem Item { get; private set; }
 /// <summary>New data for the same medium and file (for example after a tag edit); thumbnail and file check stay.</summary>
 internal void Replace(MediaItem item) { Item = item; PropertyChanged?.Invoke(this, new(string.Empty)); }
 private readonly bool showExtension;
 /// <summary>What tiles show (title or file name, wrapping, progress); set by the main window before it makes tiles.</summary>
 private GeneralPreferences Display => context.Display;
 public Stretch ThumbnailStretch => Display.CropThumbnails?Stretch.UniformToFill:Stretch.Uniform;
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
  Item.FileSize.HasValue ? SizeText(Item.FileSize.Value) : null, Item.Extra.ContainsKey("folders.previewStale") ? T("Changed") : null }.Where(x => x != null));
 /// <summary>The rating on the catalog's scale of 0–10, for the stars on the tile.</summary>
 public int Rating => Item.Rating;
 public string FileName => System.IO.Path.GetFileName(Item.Path);
 /// <summary>What the pointer shows over a tile, below the file name: title, size and technical data, dates, tags and the
 /// folder. Read from the catalog only, so it costs nothing while browsing.</summary>
 public string HoverDetails
 {
  get
  {
   static string Line(string label, string value) => label + ": " + value;
   var lines = new List<string>();
   if (!string.IsNullOrWhiteSpace(Item.Title) && Item.Title != System.IO.Path.GetFileNameWithoutExtension(Item.Path) && Item.Title != FileName) lines.Add(Line(T("Title"), Item.Title));
   if (Subtitle.Length > 0) lines.Add(Subtitle);
   if (MainWindow.TechnicalSummary(Item.Technical) is { Length: > 0 } technical) lines.Add(technical);
   if (Item.FileCreatedAt is { } created) lines.Add(Line(T("File date"), created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
   lines.Add(Line(T("Added"), Item.AddedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)));
   var tags = Item.Tags.Concat(Item.TagCategories.Values.SelectMany(values => values)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
   if (tags.Count > 0) lines.Add(Line(T("Tags"), string.Join(", ", tags.Take(HoverTags)) + (tags.Count > HoverTags ? " …" : "")));
   if (System.IO.Path.GetDirectoryName(Item.Path) is { Length: > 0 } folder) lines.Add(Line(T("Folder"), folder));
   return string.Join(Environment.NewLine, lines);
  }
 }
 private const int HoverTags = 14;
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
 private HashSet<Guid> TargetMembers => context.TargetMembers;
 /// <summary>Media an edit moved out of the current filter; they stay in the list until it is updated.</summary>
 private HashSet<Guid> FinishedMembers => context.FinishedMembers;
 public bool IsFinished => FinishedMembers.Contains(Item.Id);
 public Visibility FinishedVisibility => IsFinished ? Visibility.Visible : Visibility.Collapsed;
 private HashSet<Guid> QuickMembers => context.QuickMembers;
 public Visibility QuickVisibility => QuickMembers.Contains(Item.Id)?Visibility.Visible:Visibility.Collapsed;
 public string QuickGlyph => QuickMembers.Contains(Item.Id)?"●":"○";
 /// <summary>The quick collection button under the pointer shows once the quick collection holds something; until then B fills it.</summary>
 public Visibility QuickActionVisibility => Display.TileHoverActions.Contains(TileHoverAction.QuickCollection) && QuickMembers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
 public Visibility WatchLaterActionVisibility => Display.TileHoverActions.Contains(TileHoverAction.WatchLater) ? Visibility.Visible : Visibility.Collapsed;
 public Visibility FavoriteActionVisibility => Display.TileHoverActions.Contains(TileHoverAction.Favorite) ? Visibility.Visible : Visibility.Collapsed;
 public string QuickHint => QuickMembers.Contains(Item.Id)?T("Remove from quick collection"):T("Add to quick collection");
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
 private readonly MediaCardContext context;
 public MediaCard(MediaItem item,bool loadThumbnail=true,bool showExtension=false,bool markMissing=false,MediaCardContext? context=null)
 {
  this.context = context ?? new MediaCardContext();
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
