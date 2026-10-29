using System.IO;
using System.Windows.Media.Imaging;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>The small media actions: thumbnail beside the video, thumbnail over the (stubbed) clipboard, titles from
 /// file names, more from the same folder and inverting the selection over the whole view.</summary>
 private async Task RunQuickActionsSmokeTestAsync(Guid videoId)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  void Select(Guid id) { MediaList.SelectedItems.Clear(); MediaList.SelectedItem = MediaList.Items.Cast<MediaCard>().First(card => card.Item.Id == id); }
  await RefreshAsync(videoId); Select(videoId);
  var video = _repository.GetMedia(videoId)!;

  // Beside the video, never replacing a file that is already there.
  var exported = await ExportThumbnailsAsync(selectionOnly: true);
  var beside = Path.ChangeExtension(video.Path, Path.GetExtension(video.ThumbnailPath!).ToLowerInvariant() == ".png" ? ".png" : ".jpg");
  try
  {
   Require(exported == (1, 0, 0) && File.ReadAllBytes(beside).AsSpan().SequenceEqual(File.ReadAllBytes(video.ThumbnailPath!)), "The thumbnail was not written beside the video.");
   Require(await ExportThumbnailsAsync(selectionOnly: true) == (0, 1, 0), "An existing picture beside the video was not left alone.");
  }
  finally { File.Delete(beside); }

  // Copy and paste go through the viewer's and the directory's clipboard hooks, so the user's clipboard stays untouched.
  var putBefore = ImageViewerWindow.PutImage; var clipboardBefore = DirectoryWindow.ClipboardImage; BitmapSource? copied = null;
  try
  {
   ImageViewerWindow.PutImage = image => copied = image; DirectoryWindow.ClipboardImage = () => copied;
   Require(CopyThumbnail() && copied?.PixelWidth == MediaCard.LoadBitmap(video.ThumbnailPath, 0)?.PixelWidth, "The thumbnail was not copied in its own size.");
   Require(await PasteThumbnailAsync() == 1 && _repository.GetMedia(videoId)!.Extra.ContainsKey("thumbnail.assetId"), "The pasted picture did not become the thumbnail.");
  }
  finally { ImageViewerWindow.PutImage = putBefore; DirectoryWindow.ClipboardImage = clipboardBefore; }

  var title = _repository.GetMedia(videoId)!.Title;
  _repository.Upsert(_repository.GetMedia(videoId)! with { Title = "Ein anderer Titel" });
  Require(ApplyTitlesFromFileNames([video]) == 1 && ApplyTitlesFromFileNames([video]) == 0 && _repository.GetMedia(videoId)!.Title == Path.GetFileNameWithoutExtension(video.Path), "The title did not follow the file name.");
  _repository.Upsert(_repository.GetMedia(videoId)! with { Title = title });

  // The folder of the video holds the six pictures as well; inverting leaves out exactly the video.
  await RefreshAsync(videoId); Select(videoId);
  ShowSameFolder(); await _lastRefresh;
  Require(_folder == Path.GetDirectoryName(video.Path) && _total == 7, $"More from the same folder showed {_total} media.");
  Select(videoId); await InvertSelectionAsync();
  Require(SelectedMedia.Count == 6 && SelectedMedia.All(item => item.Id != videoId), "Inverting the selection did not select the others.");
  MediaList.SelectedItems.Clear(); _folder = null; ViewTitle.Text = T("All items"); await RefreshAsync(videoId);
 }
}
