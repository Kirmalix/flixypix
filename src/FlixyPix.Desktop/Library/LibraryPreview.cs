using System.IO;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private async Task LoadSelectedPreviewAsync(MediaItem? item)
 {
  var version = ++_previewVersion; Preview.Source = null;
  var source = item?.ThumbnailPath ?? (item?.Kind == MediaKind.Image ? item.Path : null);
  if (string.IsNullOrEmpty(source) || !File.Exists(source)) return;
  try
  {
   var bitmap = await DesktopImaging.LoadBitmapAsync(source,800);
   if (version == _previewVersion && !_closing) Preview.Source = bitmap;
  }
  catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
  { if (version == _previewVersion && !_closing) Status.Text = T("Preview not available: ") + error.Message; }
 }
}
