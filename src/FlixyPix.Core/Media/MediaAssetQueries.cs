using System.Text.Json;

namespace FlixyPix.Core;

public sealed record MediaAssetSummary(Guid Id,Guid MediaId,MediaAssetKind Kind,string Label,int Position,double? PositionSeconds,bool IsCustom,long ByteLength);

public sealed partial class CatalogRepository
{
 public IReadOnlyList<MediaAssetSummary> GetMediaAssetSummaries(Guid mediaId)
 {
  lock(gate)
  {
   ThrowIfDisposed();
   return ReadMediaAssetMetadata(mediaId).Select(asset=>new MediaAssetSummary(asset.Id,asset.MediaId,asset.Kind,asset.Label,asset.Position,asset.PositionSeconds,asset.IsCustom,asset.ByteLength)).ToList();
  }
 }
 public MediaAsset? GetMediaAsset(Guid id)
 {
  lock(gate){ThrowIfDisposed();using var command=Command("SELECT document FROM media_assets WHERE id=$id",null,("$id",id.ToString()));return command.ExecuteScalar() is string json&&JsonSerializer.Deserialize<MediaAsset>(json,Json) is {} asset?WithBytes(asset):null;}
 }
}
