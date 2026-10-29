using System.Security.Cryptography;
using System.Text;

namespace FlixyPix.Core;

public sealed record GeneratedScenePreview(double Seconds, byte[] Data);

public sealed partial class CatalogRepository
{
    /// <summary>Registers one completed generation atomically. A repeated generation has stable asset identities.</summary>
    public IReadOnlyList<MediaAsset> ApplyGeneratedScenePreviews(Guid mediaId, Guid generationId,
        IEnumerable<GeneratedScenePreview> previews, IEnumerable<Guid>? replaceAssetIds = null, string? sourceSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(previews);
        if (generationId == Guid.Empty) throw new ArgumentException("Generation identity is required.", nameof(generationId));
        var materialized = previews.Take(1001).ToArray();
        if (materialized.Length is 0 or > 1000) throw new ArgumentException("A generation contains 1–1000 previews.");
        var assets = materialized.Select((preview, index) => new MediaAsset
        {
            Id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{generationId:D}:{index}"))[..16]),
            MediaId = mediaId, Kind = MediaAssetKind.ScenePreview, Position = index,
            PositionSeconds = preview.Seconds, Label = T("Scene image {0}", index + 1), Data = preview.Data,
            Extra = new() { ["generation.id"] = generationId.ToString("D"), ["generation.sourceSha256"] = sourceSha256 }
        }).ToArray();
        foreach (var asset in assets) ValidateAsset(asset);
        var replace = replaceAssetIds?.Distinct().ToArray() ?? [];
        lock (gate)
        {
            ThrowIfDisposed();
            var existing = ReadMediaAssetMetadata(mediaId).ToDictionary(asset => asset.Id);
            if (replace.Any(id => existing.TryGetValue(id, out var asset) &&
                (asset.Kind != MediaAssetKind.ScenePreview || !asset.Extra.ContainsKey("generation.id"))))
                throw new ArgumentException("Only explicitly selected generated scene previews can be replaced.");
            using var transaction = connection.BeginTransaction(); EnsureMediaExist([mediaId], transaction);
            foreach (var id in replace)
            {
                if (existing.ContainsKey(id) && !assets.Any(asset => asset.Id == id)) DeleteMediaAssetCore(id,transaction);
            }
            var saved = assets.Select(asset => SaveAsset(asset, transaction)).ToArray();
            transaction.Commit(); FlushReleasedAssets(); return saved;
        }
    }
}
