using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public enum MediaAssetKind { Artwork, Thumbnail, ScenePreview }
public sealed record MediaAsset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid MediaId { get; init; }
    public MediaAssetKind Kind { get; init; }
    public string Label { get; init; } = "";
    public int Position { get; init; }
    public double? PositionSeconds { get; init; }
    public bool IsCustom { get; init; }
    /// <summary>The bytes. In the catalog only <see cref="Sha256"/> and <see cref="ByteLength"/> are stored; the bytes lie
    /// in the <see cref="AssetStore"/>. Both are set on saving.</summary>
    public byte[] Data { get; init; } = [];
    public string Sha256 { get; init; } = "";
    public long ByteLength { get; init; }
    public Dictionary<string, string?> Extra { get; init; } = new();
}

public sealed partial class CatalogRepository
{
    public const int MaximumAssetBytes = 32 * 1024 * 1024;
    public IReadOnlyList<MediaAsset> GetMediaAssets(Guid mediaId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("SELECT document FROM media_assets WHERE media_id=$media ORDER BY position,id", null, ("$media", mediaId.ToString()));
            using var reader = command.ExecuteReader(); var result = new List<MediaAsset>();
            while (reader.Read()) result.Add(WithBytes(JsonSerializer.Deserialize<MediaAsset>(reader.GetString(0), Json) ?? throw new InvalidDataException("Invalid media asset.")));
            return result;
        }
    }

    /// <summary>The graphics of a medium without their bytes, for decisions that need only hash, kind and position.</summary>
    private List<MediaAsset> ReadMediaAssetMetadata(Guid mediaId, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT document FROM media_assets WHERE media_id=$media ORDER BY position,id", transaction, ("$media", mediaId.ToString()));
        using var reader = command.ExecuteReader(); var result = new List<MediaAsset>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<MediaAsset>(reader.GetString(0), Json) ?? throw new InvalidDataException("Invalid media asset."));
        return result;
    }

    private MediaAsset WithBytes(MediaAsset asset) => asset with { Data = LoadAssetBytes(asset.Sha256, asset.ByteLength) };

    public MediaAsset UpsertMediaAsset(MediaAsset asset)
    {
        ValidateAsset(asset);
        lock (gate)
        {
            ThrowIfDisposed(); using var transaction = connection.BeginTransaction();
            EnsureMediaExist([asset.MediaId], transaction); var saved = SaveAsset(asset, transaction); transaction.Commit();
            return saved with { Data = asset.Data.ToArray(), Extra = new(asset.Extra) };
        }
    }
    public void DeleteMediaAsset(Guid id)
    {
        lock(gate) { ThrowIfDisposed(); using var transaction=connection.BeginTransaction(); DeleteMediaAssetCore(id,transaction); transaction.Commit(); FlushReleasedAssets(); }
    }

    public string MaterializeMediaAsset(MediaAsset asset, string cacheDirectory)
    {
        ValidateAsset(asset);
        var extension = ImageExtension(asset.Data) ?? throw new InvalidDataException(T("The image data is not in a supported file format. The original bytes remain in the catalog."));
        var directory = System.IO.Path.GetFullPath(cacheDirectory); FileAccessPolicy.Current.EnsureWritable(directory); Directory.CreateDirectory(directory);
        var hash = Convert.ToHexStringLower(SHA256.HashData(asset.Data));
        var target = System.IO.Path.Combine(directory, hash + extension);
        if (File.Exists(target))
        {
            using var input = File.OpenRead(target);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(input), SHA256.HashData(asset.Data)))
                throw new InvalidDataException(T("The image cache was changed."));
            return target;
        }
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, asset.Data); File.Move(temporary, target, overwrite: false); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return target;
    }

    public static string? ImageExtension(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(new byte[] {137,80,78,71,13,10,26,10})) return ".png";
        if (data.StartsWith(new byte[] {255,216,255})) return ".jpg";
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return ".gif";
        if (data.StartsWith("BM"u8)) return ".bmp";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8,4).SequenceEqual("WEBP"u8)) return ".webp";
        if (data.StartsWith(new byte[] {73,73,42,0}) || data.StartsWith(new byte[] {77,77,0,42})) return ".tiff";
        if (data.StartsWith(new byte[] {0,0,1,0})) return ".ico";
        if (data.Length >= 16 && data.Slice(4,4).SequenceEqual("ftyp"u8))
        {
            var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data);
            if (size >= 16 && size <= data.Length)
            {
                var heic = false; var heif = false;
                for (var offset = 8; offset + 4 <= size; offset += 4)
                {
                    if (offset == 12) continue; // minor version is not a compatible brand
                    var brand = data.Slice(offset,4);
                    if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return ".avif";
                    heic |= brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8);
                    heif |= brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8);
                }
                if (heic) return ".heic";
                if (heif) return ".heif";
            }
        }
        return null;
    }
    private static void ValidateAsset(MediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Id == Guid.Empty || asset.MediaId == Guid.Empty || !Enum.IsDefined(asset.Kind) || asset.Label is null ||
            asset.Extra is null || asset.Data is null || asset.Data.Length is 0 or > MaximumAssetBytes ||
            asset.PositionSeconds is { } time && (!double.IsFinite(time) || time < 0))
            throw new ArgumentException("Invalid media asset, timepoint or binary payload (1 byte through 32 MiB).", nameof(asset));
    }
    /// <summary>Writes the bytes to the picture store, then the row with their hash. Returns the stored asset.</summary>
    private MediaAsset SaveAsset(MediaAsset asset, SqliteTransaction transaction)
    {
        var stored = asset with { Sha256 = assets.Write(asset.Data), ByteLength = asset.Data.LongLength };
        var json = WithoutData(stored);
        if(KeepLegacyExisting("media_assets",asset.Id.ToString(),json,transaction))return stored;
        using var command = Command("INSERT INTO media_assets(id,media_id,position,document) VALUES($id,$media,$position,$json) ON CONFLICT(id) DO UPDATE SET media_id=excluded.media_id,position=excluded.position,document=excluded.document",
            transaction, ("$id", asset.Id.ToString()), ("$media", asset.MediaId.ToString()), ("$position", asset.Position), ("$json", json));
        command.ExecuteNonQuery();
        return stored;
    }

    private void ImportLegacyAssets(Func<string, IEnumerable<JsonElement>> rows, string source,
        LegacyMediaMap media, SqliteTransaction transaction, List<string> warnings)
    {
        foreach (var table in new[] { "mediaart", "videoscenes", "videopreview", "thumbnails" })
        {
            var ordinal = 0;
            foreach (var values in rows(table))
            {
                ordinal++;
                var imageValue=Property(values,"img");
                var binary = Property(imageValue, "$binary");
                if (binary.ValueKind != JsonValueKind.String && Property(imageValue,"$binaryRef").ValueKind!=JsonValueKind.Object) continue;
                if (table == "mediaart" && Text(values, "flag") != "M") continue;
                MediaItem? item = null;
                if (table == "mediaart") media.TryGetValue((Number(values, "mediatype") == 2 ? "movies:" : Number(values, "mediatype") == 3 ? "pictures:" : "unsupported:") + Text(values, "remoteid"), out item);
                else if (table == "thumbnails")
                {
                    var video = Text(values, "video");
                    var matches = media.Values.Where(m => m.Kind == MediaKind.Video && string.Equals(m.Path, video, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                    if (matches.Length == 1) item = matches[0];
                }
                else media.TryGetValue("movies:" + Text(values, "videoid"), out item);
                if (item is null) { warnings.Add($"{table}/{ordinal}: artwork media reference unresolved; bytes retained raw."); continue; }
                try
                {
                    if (binary.ValueKind==JsonValueKind.String && binary.GetString()!.Length > (long)MaximumAssetBytes * 4 / 3 + 4) throw new InvalidDataException("Image payload exceeds 32 MiB.");
                    var asset = new MediaAsset
                    {
                        Id = StableLegacyId(source, ":asset:" + table + ":" + (Text(values, "ID") ?? (table == "thumbnails" ? Text(values,"video") : null) ?? ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                        MediaId = item.Id, Kind = table == "mediaart" ? MediaAssetKind.Artwork : table == "thumbnails" ? MediaAssetKind.Thumbnail : MediaAssetKind.ScenePreview,
                        Label = Text(values, "description") ?? Text(values, "name1") ?? table,
                        Position = (int)Math.Clamp(Number(values, "position") ?? ordinal, int.MinValue, int.MaxValue),
                        PositionSeconds = Number(values, "secs") is >= 0 and { } seconds ? seconds : null,
                        IsCustom = string.Equals(Text(values, "custom"), "true", StringComparison.OrdinalIgnoreCase),
                        Data = ReadLegacyAssetBytes(imageValue,transaction),
                        Extra = new() { ["legacy.table"] = table, ["legacy.id"] = Text(values, "ID") }
                    };
                    ValidateAsset(asset); SaveAsset(asset, transaction);
                    AuditLegacy(table,Text(values,"ID"),"partially-projected","media-graphic",transaction,asset.Id);
                    if(table=="videoscenes" && Text(values,"ID") is {} sceneId)
                    {
                        var bookmarkId=StableLegacyId(source,":videoscenes:"+sceneId);
                        var bookmark=ReadEntity<Bookmark>("bookmarks",bookmarkId,transaction);
                        if(bookmark!=null && bookmark.MediaId==asset.MediaId)SaveBookmark(bookmark with { PreviewAssetId=asset.Id },transaction);
                    }
                }
                catch (Exception error) when (error is FormatException or ArgumentException or InvalidDataException)
                { warnings.Add($"{table}/{ordinal}: {error.Message} Original row retained raw."); }
            }
        }
    }
}
