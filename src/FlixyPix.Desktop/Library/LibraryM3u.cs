using System.IO;
using System.Windows;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

/// <summary>Saving the selection or the whole view (also an open collection in its order) as an M3U playlist.</summary>
public partial class MainWindow
{
 private const int M3uLimit = 100_000;

 /// <summary>The media of the whole view in its order, at most 100.000; runs off the interface thread.</summary>
 private IReadOnlyList<MediaItem> ViewItems((MediaQuery Query, Playlist? Playlist, bool CollectionOrder)? view)
 {
  if (view is not { } window) return [];
  var items = new List<MediaItem>();
  for (var offset = 0; items.Count < M3uLimit; offset += 1000)
  {
   var page = Page(window, offset, 1000);
   items.AddRange(page);
   if (page.Count < 1000) break;
  }
  return items;
 }

 private async Task SaveM3uAsync(bool selectionOnly, string? path = null)
 {
  if (path == null)
  {
   var picker = new SaveFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Save as playlist"), Filter = T("Playlist (UTF-8)|*.m3u8|Playlist|*.m3u"), FileName = ViewTitle.Text + ".m3u8" };
   if (picker.ShowDialog(this) != true) return;
   path = picker.FileName;
  }
  await RunOperation(T("Save playlist"), async token =>
  {
   var view = _window; var selection = SelectedMedia;
   var items = selectionOnly ? selection : await Task.Run(() => ViewItems(view), token);
   await Task.Run(() => M3uPlaylists.Write(path, items), token);
   Status.Text = Tn(items.Count, "1 item saved as playlist · {1}", "{0:N0} items saved as playlist · {1}", Path.GetFileName(path));
  }, refresh: false);
 }
 internal Task SaveM3uForTestAsync(bool selectionOnly, string path) => SaveM3uAsync(selectionOnly, path);
}
