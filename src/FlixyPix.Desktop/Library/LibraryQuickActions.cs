using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Small actions of the original's media menu: thumbnail over the clipboard, thumbnails beside the videos,
/// inverting the selection, more from the same folder and titles from file names.</summary>
public partial class MainWindow
{
 /// <summary>Puts the thumbnail of the selected medium on the clipboard; false without a readable thumbnail.</summary>
 internal bool CopyThumbnail()
 {
  if (Selected is not { ThumbnailPath: { } path } || !File.Exists(path) || MediaCard.LoadBitmap(path, 0) is not { } bitmap) { Status.Text = T("This item has no thumbnail."); return false; }
  ImageViewerWindow.PutImage(bitmap); Status.Text = T("Thumbnail copied"); return true;
 }

 /// <summary>The picture on the clipboard becomes the own thumbnail of every selected medium; the number of media changed.</summary>
 internal async Task<int> PasteThumbnailAsync()
 {
  var items = SelectedMedia;
  if (items.Count == 0) return 0;
  if (DirectoryWindow.ClipboardImage() is not { } image) { Status.Text = T("The clipboard contains no image."); return 0; }
  var picture = Path.Combine(Path.GetTempPath(), "flixypix-thumbnail-" + Guid.NewGuid().ToString("N") + ".png");
  try
  {
   var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
   await using (var stream = File.Create(picture)) encoder.Save(stream);
   foreach (var item in items) await SetCustomThumbnail(_repository.GetMedia(item.Id) ?? item, picture);
  }
  catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
  { Message.Show(this, error.Message, T("Paste thumbnail"), MessageBoxButton.OK, MessageBoxImage.Warning); return 0; }
  finally { try { File.Delete(picture); } catch (IOException) { } }
  await RefreshAsync(Selected?.Id);
  Status.Text = Tn(items.Count, "Thumbnail pasted for 1 item", "Thumbnail pasted for {0:N0} items"); return items.Count;
 }

 /// <summary>Copies each video's thumbnail beside it as "&lt;video name&gt;.jpg" (or .png, as the thumbnail is), for players
 /// and file managers that show such pictures. Existing files are never replaced.</summary>
 internal async Task<(int Written, int Existing, int Missing)> ExportThumbnailsAsync(bool selectionOnly)
 {
  var result = (Written: 0, Existing: 0, Missing: 0); var done = false;
  await RunOperation(T("Thumbnails beside the videos"), async token =>
  {
   var view = _window; var selection = SelectedMedia;
   var items = selectionOnly ? selection : await Task.Run(() => ViewItems(view), token);
   result = await Task.Run(() =>
   {
    var counts = (Written: 0, Existing: 0, Missing: 0);
    foreach (var item in items.Where(i => i.Kind == MediaKind.Video))
    {
     token.ThrowIfCancellationRequested();
     if (item.ThumbnailPath is not { } thumbnail || !File.Exists(thumbnail) || !File.Exists(item.Path)) { counts.Missing++; continue; }
     var extension = Path.GetExtension(thumbnail).ToLowerInvariant() is ".png" ? ".png" : ".jpg";
     var target = Path.ChangeExtension(item.Path, extension);
     if (File.Exists(target)) { counts.Existing++; continue; }
     FileAccessPolicy.Current.EnsureWritable(target);
     try { File.Copy(thumbnail, target, overwrite: false); counts.Written++; }
     catch (IOException) when (File.Exists(target)) { counts.Existing++; }
    }
    return counts;
   }, token);
   done = true;
  }, refresh: false);
  if (done) Status.Text =T("Thumbnails beside the videos: {0:N0} written, {1:N0} already there, {2:N0} without thumbnail or file", result.Written, result.Existing, result.Missing);
  return result;
 }

 /// <summary>Shows everything in the folder of the selected medium; search, tags and filters are left aside.</summary>
 internal void ShowSameFolder()
 {
  if (Selected is not { } item || Path.GetDirectoryName(item.Path) is not { } folder) return;
  SearchBox.Text = ""; _view = "all"; _tag = null; ClearTagFilters(); _tagGroup = null; _category = null; _playlist = null; _advancedExpression = null; _filter = new();
  _folder = folder; _anchor = 0; ViewTitle.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : folder;
  Refresh(item.Id);
 }

 /// <summary>Selects what was not selected in the whole view, at most 100.000 media, also beyond the loaded window.</summary>
 internal async Task InvertSelectionAsync()
 {
  var view = _window; var selected = SelectedMedia.Select(i => i.Id).ToHashSet();
  var inverted = (await Task.Run(() => ViewItems(view))).Where(i => !selected.Contains(i.Id)).ToList();
  if (view != _window) return; // The view changed meanwhile; its selection is no longer the one inverted.
  _refreshing = true;
  try
  {
   MediaList.SelectedItems.Clear();
   var ids = inverted.Select(i => i.Id).ToHashSet(); var loaded = new HashSet<Guid>();
   foreach (var card in MediaList.Items.Cast<MediaCard>().Where(c => ids.Contains(c.Item.Id))) { MediaList.SelectedItems.Add(card); loaded.Add(card.Item.Id); }
   _offWindowSelection = inverted.Where(i => !loaded.Contains(i.Id)).ToList();
  }
  finally { _refreshing = false; }
  ShowDetails(); Status.Text = Tn(inverted.Count, "1 item selected", "{0:N0} items selected");
 }

 private void TitlesFromFileNames()
 {
  var items = SelectedMedia;
  if (items.Count == 0) return;
  var example = items[0];
  if (Message.Show(this, Tn(items.Count, "Replace the title of 1 item with its file name?\n\nExample: “{1}” becomes “{2}”.",
   "Replace the titles of {0:N0} items with their file names?\n\nExample: “{1}” becomes “{2}”.", example.Title, TitleFromFileName(example.Path)),
   T("Title from file name"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
  Status.Text = Tn(ApplyTitlesFromFileNames(items), "1 title changed", "{0:N0} titles changed");
 }

 /// <summary>Sets each title to the file name without extension; returns how many titles changed (also for tests).</summary>
 internal int ApplyTitlesFromFileNames(IReadOnlyList<MediaItem> items)
 {
  var changed = 0;
  foreach (var item in items.Select(i => _repository.GetMedia(i.Id) ?? i))
  {
   var title = TitleFromFileName(item.Path);
   if (title.Length == 0 || title == item.Title) continue;
   _repository.Upsert(item with { Title = title }); changed++;
  }
  if (changed > 0) Refresh(Selected?.Id);
  return changed;
 }

 private static string TitleFromFileName(string path) => Path.GetFileNameWithoutExtension(path).Trim();
}
