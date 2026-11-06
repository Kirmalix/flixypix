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
 public string? StorageOrigin { get; init; }
 public string? ApplicationOrigin { get; init; }
 public string DataDirectory { get; init; } = AppStorage.Current.Library;
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
 /// <summary>The filter panel beside the media is open, and which of its tabs shows (tags or more).</summary>
 public bool FilterPanelOpen { get; init; }
 public string FilterPanelTab { get; init; } = "tags";
 /// <summary>The folder view shows its tree; the path and subfolders above the media stay either way.</summary>
 public bool FolderTreeHidden { get; init; }
 /// <summary>Width of the panel beside the media as the user dragged it.</summary>
 public int BrowsePanelWidth { get; init; } = 272;
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
    public double InterfaceScale { get; init; } = 1;
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
 public static string SettingsPath => AppStorage.Current.Settings;
 public static DesktopSettings Load()
 {
  var basePath=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
  if (AppStorage.Current.Mode == StorageMode.Profile) SettingsTransition.EnsureMigrated(Path.Combine(basePath,ProductIdentity.PreviousApplicationDirectory),Path.Combine(basePath,ProductIdentity.Name));
  var settings = LoadFrom(SettingsPath);
  if (AppStorage.Current.Mode != StorageMode.Portable || settings.StorageOrigin == null) return settings;
  string Map(string value)
  {
   foreach (var (from,to) in new[] { (settings.StorageOrigin,AppStorage.Current.Root), (settings.ApplicationOrigin,AppStorage.Current.ApplicationDirectory), (Path.GetPathRoot(settings.StorageOrigin),Path.GetPathRoot(AppStorage.Current.Root)) })
   {
    if (from == null || to == null) continue;
    var prefix = from.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
    if (value.Equals(prefix,StringComparison.OrdinalIgnoreCase) || value.StartsWith(prefix + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
     return to.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar) + value[prefix.Length..];
   }
   return value;
  }
  return settings with { DataDirectory = Map(settings.DataDirectory), Libraries = settings.Libraries.Select(l=>l with { Directory=Map(l.Directory) }).ToList(),
   Ffmpeg=settings.Ffmpeg==null?null:Map(settings.Ffmpeg),Ffprobe=settings.Ffprobe==null?null:Map(settings.Ffprobe),Mpv=settings.Mpv==null?null:Map(settings.Mpv),
   ExternalPrograms=settings.ExternalPrograms.Select(p=>p with {Path=Map(p.Path)}).ToList(),QuickAccessFolders=settings.QuickAccessFolders.Select(Map).ToList(),ProtectedSourcePaths=settings.ProtectedSourcePaths.Select(Map).ToList() };

 }
 internal byte[] SerializeFor(string root,string applicationDirectory) => JsonSerializer.SerializeToUtf8Bytes(this with { StorageOrigin=Path.GetFullPath(root),ApplicationOrigin=Path.GetFullPath(applicationDirectory) },new JsonSerializerOptions {WriteIndented=true});
 public void Save() => SaveTo(SettingsPath);
 internal void SaveTo(string path)
 {
  FileAccessPolicy.Current.EnsureWritable(path);
  Directory.CreateDirectory(Path.GetDirectoryName(path)!);
  var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
  File.WriteAllText(temp, JsonSerializer.Serialize(this with { StorageOrigin = Path.GetDirectoryName(Path.GetFullPath(path)), ApplicationOrigin = AppStorage.Current.ApplicationDirectory }, new JsonSerializerOptions { WriteIndented = true }));
  File.Move(temp, path, true);
  // The copy stands in when the file is later damaged beyond repair.
  try { File.Copy(path, LastGoodPath(path), true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
 }
}

internal static class DesktopImaging
{
 internal static ImageService Service { get; private set; } = new(Path.Combine(AppStorage.Current.Root, "image-cache"));
 internal static void Configure(string dataDirectory) => Service = new(Path.Combine(dataDirectory,"image-cache"));
 internal static BitmapImage Bitmap(byte[] bytes)
 {
  using var input = new MemoryStream(bytes); var image = new BitmapImage(); image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad;
  image.StreamSource=input; image.EndInit(); image.Freeze(); return image;
 }
 internal static async Task<BitmapImage> LoadBitmapAsync(string path, int width=800, CancellationToken token=default) =>
  Bitmap((await Service.DecodeAsync(path,maxWidth:width,maxHeight:width,cancellationToken:token)).PngBytes);
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
