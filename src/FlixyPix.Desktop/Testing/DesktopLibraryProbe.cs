using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FlixyPix.Desktop;

// Opens a library like a normal start and reports what the first view shows. Only counts and the status line are
// written, never titles, tags, paths or pictures, so private catalogs can be probed safely.
public partial class MainWindow
{
 internal async Task RunLibraryProbeAsync(string directory)
 {
  await Startup; await _lastRefresh; await _previewReady;
  await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  _sidebarSections["folders"].IsExpanded = true; UpdateLayout();
  await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  static IEnumerable<DependencyObject> Visuals(DependencyObject root)
  {
   for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var deeper in Visuals(child)) yield return deeper; }
  }
  var folderTexts = Visuals(Folders).OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
  var cards = MediaList.ItemsSource as IEnumerable<MediaCard> ?? [];
  var withPath = cards.Where(c => c.Item.ThumbnailPath != null).ToList();
  var report = new
  {
   status = Status.Text,
   folderRoots = (Folders.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().Count() ?? 0,
   folderTexts = folderTexts.Count,
   folderTextsShowingTypeName = folderTexts.Count(t => t.StartsWith("FlixyPix.", StringComparison.Ordinal)),
   cards = cards.Count(),
   cardsWithThumbnailPath = withPath.Count,
   thumbnailFilesFound = withPath.Count(c => File.Exists(c.Item.ThumbnailPath)),
   thumbnailsInsideLibrary = withPath.Count(c => c.Item.ThumbnailPath!.StartsWith(_settings.DataDirectory, StringComparison.OrdinalIgnoreCase)),
  };
  File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
 }
}
