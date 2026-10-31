using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 internal async Task RunFolderSyncVisualTestAsync(string directory)
 {
  var fixtures = Path.Combine(directory, "fixtures"); Directory.CreateDirectory(fixtures);
  var drawing = new System.Windows.Media.DrawingVisual();
  using(var context = drawing.RenderOpen()) context.DrawRectangle(System.Windows.Media.Brushes.CornflowerBlue,null,new Rect(0,0,64,64));
  var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(64,64,96,96,System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(drawing);
  var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
  using(var file = File.Create(Path.Combine(fixtures,"sample.png"))) encoder.Save(file);
  await RunLibraryFolderSmokeTestAsync(directory, fixtures);
  var automatic = Path.Combine(directory,"automatic"); Directory.CreateDirectory(automatic);
  try {
   _repository.SaveLibraryFolders([new LibraryFolder { Path = automatic, AutoApply = true }]); RestartFolderWatcher();
   var path = Path.Combine(automatic,"automatic.png"); File.Copy(Path.Combine(fixtures,"sample.png"),path);
   await CheckLibraryFoldersAsync(background:true);
   var imported = _repository.GetMediaByPath(path) ?? throw new InvalidOperationException("Automatic import failed.");
   File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1)); await CheckLibraryFoldersAsync(background:true);
   if (_repository.GetFolderSyncReport()?.Result.Updated != 1) throw new InvalidOperationException("Automatic modification failed.");
   var sorted = Path.Combine(automatic,"sorted"); Directory.CreateDirectory(sorted); var moved = Path.Combine(sorted,"automatic.png"); File.Move(path,moved);
   await CheckLibraryFoldersAsync(background:true);
   if (_repository.GetMedia(imported.Id)?.Path != path || _folderPlan?.Count(FolderChangeKind.Moved) != 1) throw new InvalidOperationException("A path heuristic was applied automatically.");
   await ApplyFolderSyncAsync(_folderPlan,_folderPlan.Actionable,false);
   File.Delete(moved); await CheckLibraryFoldersAsync(background:true);
   if (!FolderBannerShown || _repository.GetMedia(imported.Id) == null || _folderPlan?.Count(FolderChangeKind.Missing) != 1) throw new InvalidOperationException("Missing-only changes need a banner and must retain the item.");
   if (await RemoveMissingFolderFilesAsync(_folderPlan,_folderPlan.Changes) != 1) throw new InvalidOperationException("Explicit removal failed.");
  } finally { _repository.SaveLibraryFolders([]); RestartFolderWatcher(); HideFolderBanner(); }
 }
 private static void CheckFolderTabLayout(LibraryFoldersWindow window)
 {
  var tabs = window.ReviewTabs!;
  var band = (FrameworkElement)tabs.Template.FindName("HeaderBand", tabs);
  var bandBounds = new Rect(band.TranslatePoint(new Point(), tabs), band.RenderSize);
  foreach (System.Windows.Controls.TabItem tab in tabs.Items) {
   var bounds = new Rect(tab.TranslatePoint(new Point(), tabs), tab.RenderSize);
   // Long translations can scroll horizontally, but no header may leave its row vertically.
   if (bounds.Top < bandBounds.Top || bounds.Bottom > bandBounds.Bottom)
    throw new InvalidOperationException("A tab is clipped by its header band.");
   var label = (FrameworkElement)tab.Template.FindName("Header",tab);
   var labelBounds = new Rect(label.TranslatePoint(new Point(),tab),label.RenderSize);
   if (Math.Abs(labelBounds.Top + labelBounds.Height / 2 - tab.ActualHeight / 2) > 1)
    throw new InvalidOperationException("A tab label is not vertically centered.");
  }
  var page = (FrameworkElement)tabs.Template.FindName("PART_SelectedContentHost", tabs);
  if (page.HorizontalAlignment != HorizontalAlignment.Stretch || page.VerticalAlignment != VerticalAlignment.Stretch)
   throw new InvalidOperationException("Header alignment leaked into the selected page.");
 }
 private static void CheckOpaqueFolderScreenshot(string path)
 {
  using var file = File.OpenRead(path);
  var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(file, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
  var frame = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoder.Frames[0], System.Windows.Media.PixelFormats.Bgra32,null,0);
  var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4]; frame.CopyPixels(pixels,frame.PixelWidth * 4,0);
  for (var index = 3; index < pixels.Length; index += 4)
   if (pixels[index] != 255) throw new InvalidOperationException("Transparent pixel in scaled screenshot: " + path);
 }
 /// <summary>Library folders: only watched folders are watched, a new file shows the banner, the window lists it and
 /// taking it over imports it; a removed file is not offered again.</summary>
 private async Task RunLibraryFolderSmokeTestAsync(string directory, string fixtureDirectory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  var watched = Path.Combine(directory, "library-folder"); var unwatched = Path.Combine(directory, "library-folder-unwatched");
  Directory.CreateDirectory(watched); Directory.CreateDirectory(unwatched);
  var sample = Directory.EnumerateFiles(fixtureDirectory, "*.png").First();
  var settle = FolderSettleTime; FolderSettleTime = TimeSpan.FromMilliseconds(300);
  try
  {
   _repository.SaveLibraryFolders([new LibraryFolder { Path = watched, Category = "Ordnertest" }, new LibraryFolder { Path = unwatched, Watch = false }]);
   RestartFolderWatcher();
   Require(FolderWatcher?.Watched.SequenceEqual([watched], StringComparer.OrdinalIgnoreCase) == true, "Only the folder marked for watching may be watched.");
   await CheckLibraryFoldersAsync(); Require(!FolderBannerShown, "The banner appeared without changes.");
   File.Copy(sample, Path.Combine(unwatched, "Unbeobachtet.png"));
   var arrived = Path.Combine(watched, "Neu im Ordner.png"); File.Copy(sample, arrived);
   for (var waited = 0; !FolderBannerShown && waited < 100; waited++) await Task.Delay(100);
   Require(FolderBannerShown, "A new file in a watched folder did not show the banner.");
   Require(FolderBannerText.Text.Contains(Tn(1, "1 new file", "{0:N0} new files"), StringComparison.Ordinal), "The banner did not count the new files: " + FolderBannerText.Text);
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture(RootContent, Path.Combine(directory, "desktop-folder-banner.png"));
   await CheckLibraryFoldersAsync();
   FolderBannerReview(this, new RoutedEventArgs());
   var window = _foldersWindow ?? throw new InvalidOperationException("The library folder window did not open.");
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Require(window.Rows.Count == 2 && window.Rows.All(r => r.Change.Kind == FolderChangeKind.New && r.Chosen), "The window did not list the new files ready to take over.");
   Capture((FrameworkElement)window.Content, Path.Combine(directory, "library-folders.png"));
   window.Rows.Single(r => r.Name == "Unbeobachtet.png").Chosen = false;
   await window.ApplyAsync();
   var item = _repository.GetMediaByPath(arrived) ?? throw new InvalidOperationException("Taking over did not import the new file.");
   Require(item.Category == "Ordnertest" && _repository.GetMediaByPath(Path.Combine(unwatched, "Unbeobachtet.png")) == null, "Folder category or the declined row was not honoured.");
   foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark }) {
    Theme.Apply(scheme); window.Width = 980; window.UpdateLayout();
    CheckFolderTabLayout(window);
    Capture((FrameworkElement)window.Content, Path.Combine(directory, "folder-" + scheme + ".png"));
    foreach (var tab in new[] { 1, 2 }) {
     window.ReviewTabs!.SelectedIndex = tab; window.UpdateLayout();
     Capture((FrameworkElement)window.Content, Path.Combine(directory, "folder-" + scheme + "-tab-" + tab + ".png"));
    }
    window.ReviewTabs!.SelectedIndex = 0;
    window.Width = 760; window.UpdateLayout(); CheckFolderTabLayout(window); Capture((FrameworkElement)window.Content, Path.Combine(directory, "folder-" + scheme + "-compact.png"));
   }
   foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark }) {
    Theme.Apply(scheme);
    foreach (var scale in new[] { 1.0, 1.5, 2.0 }) {
     window.Width = 980; window.ReviewTabs!.SelectedIndex = 0; window.UpdateLayout();
     var suffix = scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
     Capture((FrameworkElement)window.Content, Path.Combine(directory, "folder-" + scheme + "-scale-" + suffix + ".png"), scale);
     CheckOpaqueFolderScreenshot(Path.Combine(directory, "folder-" + scheme + "-scale-" + suffix + ".png"));
     if (scheme == ColorScheme.Dark)
      Capture((FrameworkElement)window.Content, Path.Combine(directory, "folder-scale-" + suffix + ".png"), scale);
    }
   }
   var originalPlan = window.Plan!;
   window.ShowPlan(originalPlan with { Changes = [] }); window.UpdateLayout(); Capture((FrameworkElement)window.Content, Path.Combine(directory,"folder-up-to-date.png"));
   window.ShowPlan(originalPlan with { Changes = [], Unreachable = [new LibraryFolder { Path = Path.Combine(directory,"offline-drive") }] }); window.UpdateLayout(); Capture((FrameworkElement)window.Content, Path.Combine(directory,"folder-offline.png"));
   window.SetBusy(true); window.ReportProgress(new FolderSyncProgress("Checking folders …",128,null,watched)); window.UpdateLayout(); Capture((FrameworkElement)window.Content, Path.Combine(directory,"folder-checking.png"));
   window.ReportProgress(new FolderSyncProgress("Apply selected",1,3,arrived)); window.UpdateLayout(); Capture((FrameworkElement)window.Content, Path.Combine(directory,"folder-applying.png"));
   window.SetBusy(false); window.ShowPlan(originalPlan);
   window.Close();
   _repository.Delete(item.Id); ForgetInLibraryFolders(item.Path);
   await CheckLibraryFoldersAsync();
   Require(_folderPlan?.Actionable.Count == 1, "A removed file was offered again or the declined file vanished.");
  }
  finally
  {
   FolderSettleTime = settle;
   _repository.SaveLibraryFolders([]); RestartFolderWatcher(); HideFolderBanner();
  }
 }
}
