using System.IO;
using System.Windows;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>An image from the viewer as the picture of a tag or as the thumbnail of another medium. The original offered
/// "Set as thumbnail for" the current category or folder; FlixyPix has no folder pictures and offers tags and media instead.</summary>
public partial class MainWindow
{
 private void ChooseTagPicture(MediaItem image, ImageViewerWindow viewer)
 {
  // Tags of the image come first: usually the performer or studio it shows.
  var own = image.Tags.Concat(image.TagCategories.Values.SelectMany(tags => tags)).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var entries = _repository.GetDirectory().Where(entry => entry.Category.Length > 0)
   .OrderByDescending(entry => own.Contains(entry.Tag)).ThenByDescending(entry => entry.MediaCount).ThenBy(entry => entry.Tag, StringComparer.OrdinalIgnoreCase).ToList();
  var dialog = new ChoiceDialog<DirectoryEntry>(viewer, T("Use as tag image"), T("The image becomes the image of this tag, such as a portrait or logo; an earlier image is kept under “Tag images”."),
   text => entries.Where(entry => text.Length == 0 || entry.Tag.Contains(text, StringComparison.OrdinalIgnoreCase) || entry.Category.Contains(text, StringComparison.OrdinalIgnoreCase))
    .Take(60).Select(entry => (entry, entry.Tag + "  ·  " + entry.Category)).ToList());
  if (dialog.ShowDialog() != true || dialog.Chosen is not { } chosen) return;
  try { ApplyTagPicture(image, chosen); viewer.Tell(T("Image of “{0}” set", chosen.Tag)); }
  catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
  { Message.Show(viewer, error.Message, T("Use as tag image"), MessageBoxButton.OK, MessageBoxImage.Warning); }
 }

 internal void ApplyTagPicture(MediaItem image, DirectoryEntry entry)
 {
  var key = new TagKey(entry.Category, entry.Tag);
  _repository.ReplaceTagPicture(key, () => _repository.ImportTagAssetFile(image.Path, key));
 }

 private async void ChooseThumbnailTarget(MediaItem image, ImageViewerWindow viewer)
 {
  var dialog = new ChoiceDialog<MediaItem>(viewer, T("Use as thumbnail"), T("The image becomes the custom thumbnail of the chosen item. Type a title or file name."),
   text => _repository.QueryPage(new MediaQuery { Text = text.Length == 0 ? null : text, Limit = 40 }).Items.Where(item => item.Id != image.Id)
    .Select(item => (item, (string.IsNullOrWhiteSpace(item.Title) ? Path.GetFileName(item.Path) : item.Title) + "  ·  " + Path.GetFileName(Path.GetDirectoryName(item.Path) ?? ""))).ToList());
  if (dialog.ShowDialog() != true || dialog.Chosen is not { } target) return;
  try { await ApplyThumbnailFrom(image, target); viewer.Tell(T("Thumbnail of “{0}” set", string.IsNullOrWhiteSpace(target.Title) ? Path.GetFileName(target.Path) : target.Title)); }
  catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
  { Message.Show(viewer, error.Message, T("Use as thumbnail"), MessageBoxButton.OK, MessageBoxImage.Warning); }
 }

 internal async Task ApplyThumbnailFrom(MediaItem image, MediaItem target)
 {
  await SetCustomThumbnail(_repository.GetMedia(target.Id) ?? target, image.Path);
  Refresh(target.Id);
 }
}
