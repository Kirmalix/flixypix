using System.Globalization;

namespace FlixyPix.Core;

/// <summary>A bookmark or scene whose picture is missing or was made for another position.</summary>
public sealed record MarkPreviewNeed(Guid Id, bool IsScene, double PositionSeconds);

/// <summary>Pictures for bookmarks and scenes. Taken-over pictures (original bcache/ccache) and pictures chosen by hand stay;
/// FlixyPix makes the missing ones from the video and renews its own when a mark moves.</summary>
public sealed partial class CatalogRepository
{
    /// <summary>Marks an asset as made by FlixyPix from the video, so it may be replaced when the mark moves.</summary>
    public const string GeneratedPreviewKey = "preview.generated";

    public IReadOnlyList<MarkPreviewNeed> GetMarkPreviewNeeds(Guid mediaId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var known = ReadMediaAssetMetadata(mediaId).ToDictionary(a => a.Id);
            bool Needs(Guid? assetId, double position) => assetId is not { } id || !known.TryGetValue(id, out var asset) ||
                asset.Extra.ContainsKey(GeneratedPreviewKey) && Math.Abs((asset.PositionSeconds ?? -1) - position) > .5;
            return GetBookmarks(mediaId).Where(b => Needs(b.PreviewAssetId, b.PositionSeconds)).Select(b => new MarkPreviewNeed(b.Id, false, b.PositionSeconds))
                .Concat(GetScenes(mediaId).Where(s => Needs(s.PreviewAssetId, s.StartSeconds)).Select(s => new MarkPreviewNeed(s.Id, true, s.StartSeconds)))
                .ToList();
        }
    }

    /// <summary>Stores a picture made from the video for a mark and links it; a picture FlixyPix made earlier for it goes.
    /// Returns false when the mark no longer exists or moved meanwhile.</summary>
    public bool SetGeneratedMarkPreview(MarkPreviewNeed need, byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(need); ArgumentNullException.ThrowIfNull(jpeg);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var bookmark = need.IsScene ? null : ReadEntity<Bookmark>("bookmarks", need.Id, transaction);
            var scene = need.IsScene ? ReadEntity<Scene>("scenes", need.Id, transaction) : null;
            var position = bookmark?.PositionSeconds ?? scene?.StartSeconds;
            if (position is not { } at || Math.Abs(at - need.PositionSeconds) > .5) return false;
            var mediaId = bookmark?.MediaId ?? scene!.MediaId;
            var previous = bookmark != null ? bookmark.PreviewAssetId : scene!.PreviewAssetId;
            var asset = new MediaAsset
            {
                MediaId = mediaId, Kind = MediaAssetKind.ScenePreview, PositionSeconds = at, Data = jpeg,
                Label = (bookmark != null ? T("Bookmark image at ") : T("Scene image at ")) + TimeSpan.FromSeconds(at).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture),
                Extra = new() { [GeneratedPreviewKey] = "flixypix" },
            };
            ValidateAsset(asset); SaveAsset(asset, transaction);
            if (bookmark != null) SaveBookmark(bookmark with { PreviewAssetId = asset.Id }, transaction);
            else SaveScene(scene! with { PreviewAssetId = asset.Id }, transaction);
            MediaAsset? former = null;
            if (previous is { } earlier)
            {
                using var read = Command("SELECT document FROM media_assets WHERE id=$id", transaction, ("$id", earlier.ToString()));
                if (read.ExecuteScalar() is string json) former = System.Text.Json.JsonSerializer.Deserialize<MediaAsset>(json, Json);
            }
            if (previous is { } old && former is not null && former.Extra.ContainsKey(GeneratedPreviewKey))
            {
                using var stillUsed = Command("SELECT 1 FROM bookmarks WHERE json_extract(document,'$.previewAssetId')=$id UNION ALL SELECT 1 FROM scenes WHERE json_extract(document,'$.previewAssetId')=$id LIMIT 1",
                    transaction, ("$id", old.ToString()));
                if (stillUsed.ExecuteScalar() is null) { using var delete = Command("DELETE FROM media_assets WHERE id=$id", transaction, ("$id", old.ToString())); delete.ExecuteNonQuery(); }
            }
            transaction.Commit(); FlushReleasedAssets();
            return true;
        }
    }
}
