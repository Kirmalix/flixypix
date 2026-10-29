using System.IO;
using System.Windows;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private void ShowArtwork()
 {
  if (Selected is not { } item) return;
  var window = new MediaArtworkWindow(this,_repository,item,Path.Combine(_settings.DataDirectory,"media-assets"),seconds => Play(item,seconds));
  window.Closed += (_, _) => Refresh(item.Id); window.Show();
 }
 private async Task RestoreAssetPreviewsAsync(CancellationToken token)
 {
  var items = await Task.Run(() => _repository.Search(),token);
  foreach (var item in items)
  {
   token.ThrowIfCancellationRequested();
   if (!string.IsNullOrEmpty(item.ThumbnailPath) && File.Exists(item.ThumbnailPath)) continue;
   var assets = await Task.Run(() => _repository.GetMediaAssets(item.Id),token);
   item.Extra.TryGetValue("thumbnail.assetId",out var selectedId);
   var preferred = assets.OrderByDescending(a => a.Id.ToString() == selectedId).ThenByDescending(a => a.Kind == MediaAssetKind.Thumbnail).ThenByDescending(a => a.IsCustom).ThenBy(a => a.Position)
    .FirstOrDefault(a => CatalogRepository.ImageExtension(a.Data) != null);
   if (preferred == null) continue;
   var path = await Task.Run(() => _repository.MaterializeMediaAsset(preferred,Path.Combine(_settings.DataDirectory,"media-assets")),token);
   _repository.Upsert(item with { ThumbnailPath = path });
  }
 }
}
