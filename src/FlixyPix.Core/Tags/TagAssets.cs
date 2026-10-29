using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>Image bytes associated with a categorized tag, independently of media. Null Tag retains orphaned bytes.
/// The catalog row keeps <see cref="Sha256"/> and <see cref="ByteLength"/>; the bytes lie in the <see cref="AssetStore"/>.</summary>
public sealed record TagAsset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public TagKey? Tag { get; init; }
    public string Label { get; init; } = "";
    public byte[] Data { get; init; } = [];
    public string Sha256 { get; init; } = "";
    public long ByteLength { get; init; }
    public string? Format { get; init; }
    public string? SourceSha256 { get; init; }
    public List<string> SourcePaths { get; init; } = [];
    public Dictionary<string, string?> Provenance { get; init; } = new();
    public List<TagKey> PreviousTags { get; init; } = [];
    public bool IsOpaque => Format is null;
}

public sealed record TagAssetSummary(Guid Id, TagKey? Tag, string Label, string Sha256, long ByteLength,
    string? Format, bool IsOpaque, string? SourceSha256, IReadOnlyList<string> SourcePaths, IReadOnlyList<TagKey> PreviousTags);

public sealed partial class CatalogRepository
{
    public const int MaximumTagAssetBytes = 32 * 1024 * 1024;
    private sealed class TagAssetTagComparer : IEqualityComparer<TagKey>
    {
        internal static readonly TagAssetTagComparer Instance = new();
        public bool Equals(TagKey? left, TagKey? right) => ReferenceEquals(left, right) || left != null && right != null &&
            StringComparer.OrdinalIgnoreCase.Equals(left.Category, right.Category) && StringComparer.OrdinalIgnoreCase.Equals(left.Value, right.Value);
        public int GetHashCode(TagKey key) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Category), StringComparer.OrdinalIgnoreCase.GetHashCode(key.Value));
    }

    private void InitializeTagAssetSchema(SqliteTransaction transaction)
    {
        using var command = Command("""
            CREATE TABLE IF NOT EXISTS tag_assets(id TEXT PRIMARY KEY, document TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS tag_assets_tag ON tag_assets(json_extract(document,'$.tag.category') COLLATE EFP_ORDINAL,json_extract(document,'$.tag.value') COLLATE EFP_ORDINAL,id);
            CREATE INDEX IF NOT EXISTS tag_assets_hash ON tag_assets(json_extract(document,'$.sha256'));
            """, transaction);
        command.ExecuteNonQuery();
    }

    /// <summary>Stores immutable bytes. Replacing existing bytes requires a new id; association/labels may change.</summary>
    public TagAsset UpsertTagAsset(TagAsset asset)
    {
        var normalized = NormalizeTagAsset(asset, requireChecksum: false, copyData: true);
        lock (gate)
        {
            ThrowIfDisposed(); using var transaction = connection.BeginTransaction(); InitializeTagAssetSchema(transaction);
            var saved = SaveTagAsset(normalized, transaction); transaction.Commit(); return saved;
        }
    }

    /// <summary>Loads at most one bounded binary payload and verifies its stored hash.</summary>
    public TagAsset? GetTagAsset(Guid id)
    {
        lock (gate)
        {
            ThrowIfDisposed(); if (!HasTagAssetSchema()) return null;
            using var command = Command("SELECT document FROM tag_assets WHERE id=$id", null, ("$id", id.ToString()));
            if (command.ExecuteScalar() is not string json) return null;
            var stored = JsonSerializer.Deserialize<TagAsset>(json, Json) ?? throw new InvalidDataException("Invalid tag asset.");
            var data = LoadAssetBytes(stored.Sha256, stored.ByteLength);
            if (data.Length == 0) throw new InvalidDataException("The picture file of this tag artwork is missing or damaged; run the catalog check.");
            return NormalizeTagAsset(stored with { Data = data }, requireChecksum: true, copyData: false);
        }
    }

    /// <summary>Pages metadata without bringing binary payloads into managed memory. Null key selects all associations.</summary>
    public IReadOnlyList<TagAssetSummary> GetTagAssetSummaries(TagKey? key = null, bool unassignedOnly = false, int offset = 0, int limit = 120)
    {
        if (offset < 0 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "Offset must be nonnegative and page size 1–1000.");
        if (key != null && unassignedOnly) throw new ArgumentException("Select a tag or unassigned assets, not both.");
        if (key != null) key = NormalizeTagKey(key);
        lock (gate)
        {
            ThrowIfDisposed(); if (!HasTagAssetSchema()) return [];
            var where = key != null ? "WHERE json_extract(document,'$.tag.category')=$category COLLATE EFP_ORDINAL AND json_extract(document,'$.tag.value')=$value COLLATE EFP_ORDINAL" :
                unassignedOnly ? "WHERE json_extract(document,'$.tag') IS NULL" : "";
            using var command = Command($"SELECT json_remove(document,'$.data') FROM tag_assets {where} ORDER BY id LIMIT $limit OFFSET $offset", null,
                ("$limit", limit), ("$offset", offset));
            if (key != null) { command.Parameters.AddWithValue("$category", key.Category); command.Parameters.AddWithValue("$value", key.Value); }
            using var reader = command.ExecuteReader(); var results = new List<TagAssetSummary>();
            while (reader.Read())
            {
                var asset = JsonSerializer.Deserialize<TagAsset>(reader.GetString(0), Json) ?? throw new InvalidDataException("Invalid tag asset metadata.");
                ValidateTagAssetMetadata(asset);
                results.Add(new(asset.Id, asset.Tag, asset.Label, asset.Sha256, asset.ByteLength, asset.Format, asset.IsOpaque, asset.SourceSha256, asset.SourcePaths, asset.PreviousTags));
            }
            return results;
        }
    }

    /// <summary>Makes a new picture the one a tag shows: it is stored first, then the other pictures of exactly this tag are
    /// released (kept unassigned, with the tag among their previous tags). A failed import changes nothing.</summary>
    public TagAsset ReplaceTagPicture(TagKey key, Func<TagAsset> import)
    {
        ArgumentNullException.ThrowIfNull(import); key = NormalizeTagKey(key);
        var fresh = import();
        foreach (var summary in GetTagAssetSummaries(key, limit: 1000).Where(s => s.Id != fresh.Id))
            if (GetTagAsset(summary.Id) is { } earlier) UpsertTagAsset(earlier with { Tag = null, PreviousTags = [.. earlier.PreviousTags, key] });
        return fresh;
    }

    /// <summary>Explicitly removes the catalog's byte copy. Deleting a tag itself only orphans its assets.</summary>
    public bool DeleteTagAsset(Guid id)
    {
        lock (gate)
        {
            ThrowIfDisposed(); if (!HasTagAssetSchema()) return false;
            using var command = Command("DELETE FROM tag_assets WHERE id=$id", null, ("$id", id.ToString())); var deleted = command.ExecuteNonQuery() != 0;
            FlushReleasedAssets(); return deleted;
        }
    }

    /// <summary>Reads only the explicitly selected file. No directory enumeration, media probing or original type loading.</summary>
    public TagAsset ImportTagAssetFile(string sourcePath, TagKey key, string? label = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath); var tag = NormalizeTagKey(key); var source = Path.GetFullPath(sourcePath);
        cancellationToken.ThrowIfCancellationRequested(); byte[] data; string hash;
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var length = input.Length;
            if (length is <= 0 or > MaximumTagAssetBytes) throw new InvalidDataException("Tag artwork input must contain 1 byte through 32 MiB. The selected source is unchanged and was not imported.");
            data = new byte[(int)length]; var offset = 0;
            while (offset < data.Length)
            {
                cancellationToken.ThrowIfCancellationRequested(); var count = input.Read(data, offset, Math.Min(1024 * 1024, data.Length - offset));
                if (count == 0) throw new IOException("Tag artwork source became incomplete while reading."); offset += count;
            }
            if (input.ReadByte() != -1 || input.Length != length) throw new IOException("Tag artwork source changed length while reading.");
            hash = Convert.ToHexStringLower(SHA256.HashData(data)); input.Position = 0;
            using var secondPass = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[1024 * 1024]; int read;
            while ((read = input.Read(buffer)) != 0) { cancellationToken.ThrowIfCancellationRequested(); secondPass.AppendData(buffer, 0, read); }
            if (Convert.ToHexStringLower(secondPass.GetHashAndReset()) != hash) throw new IOException("Tag artwork source changed while reading; no asset was stored.");
        }
        return StoreTagAssetData(data, hash, tag, label ?? Path.GetFileName(source), source, "explicit-local-file", cancellationToken);
    }

    /// <summary>Stores picture bytes that come from no file of their own, such as the clipboard or a still of a video.
    /// Only recognized picture formats are accepted; the same bytes for the same tag are stored once.</summary>
    public TagAsset ImportTagAssetData(byte[] data, TagKey key, string label, string method, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data); ArgumentException.ThrowIfNullOrWhiteSpace(label); ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (data.Length is <= 0 or > MaximumTagAssetBytes) throw new InvalidDataException("Tag artwork input must contain 1 byte through 32 MiB.");
        if (ImageExtension(data) == null) throw new InvalidDataException("Tag artwork must be a picture in a supported format.");
        return StoreTagAssetData(data.ToArray(), Convert.ToHexStringLower(SHA256.HashData(data)), NormalizeTagKey(key), label, null, method, cancellationToken);
    }

    private TagAsset StoreTagAssetData(byte[] data, string hash, TagKey tag, string label, string? source, string method, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed(); using var transaction = connection.BeginTransaction(); InitializeTagAssetSchema(transaction);
            using var duplicate = Command("""
                SELECT document FROM tag_assets WHERE json_extract(document,'$.sha256')=$hash
                AND json_extract(document,'$.tag.category')=$category COLLATE EFP_ORDINAL
                AND json_extract(document,'$.tag.value')=$tag COLLATE EFP_ORDINAL ORDER BY id LIMIT 1
                """, transaction, ("$hash", hash), ("$category", tag.Category), ("$tag", tag.Value));
            var previous = duplicate.ExecuteScalar() is string json ? JsonSerializer.Deserialize<TagAsset>(json, Json)! : null;
            var asset = previous == null ? new TagAsset
            {
                Id = AllocateTagAssetId(hash, tag, transaction),
                Tag = tag, Label = label, Data = data, Sha256 = hash, ByteLength = data.Length,
                SourceSha256 = hash, SourcePaths = source == null ? [] : [source], Provenance = new() { ["import.method"] = method }
            } : previous with { Data = data, SourcePaths = (source == null ? previous.SourcePaths : previous.SourcePaths.Append(source)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), SourceSha256 = previous.SourceSha256 ?? hash };
            var normalized = NormalizeTagAsset(asset, requireChecksum: previous != null, copyData: false);
            cancellationToken.ThrowIfCancellationRequested(); var saved = SaveTagAsset(normalized, transaction); transaction.Commit(); return saved;
        }
    }

    public Task<TagAsset> ImportTagAssetFileAsync(string sourcePath, TagKey key, string? label = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportTagAssetFile(sourcePath, key, label, cancellationToken), cancellationToken);

    /// <summary>Exports original bytes only to a new file, including opaque artwork.</summary>
    public string ExportTagAsset(Guid id, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); var target = Path.GetFullPath(path);
        FileAccessPolicy.Current.EnsureWritable(target); cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Tag artwork export requires a new file.");
        var asset = GetTagAsset(id) ?? throw new ArgumentException("Tag artwork not found.");
        WriteTagAssetFile(asset, target, cancellationToken); return target;
    }

    /// <summary>Materializes only a content-signature-recognized image into a hash-named cache file.</summary>
    public string MaterializeTagAsset(TagAsset asset, string cacheDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        var normalized = NormalizeTagAsset(asset, requireChecksum: false, copyData: false);
        if (normalized.Format == null) throw new InvalidDataException("This tag artwork is opaque. Its original bytes remain available for explicit export.");
        var directory = Path.GetFullPath(cacheDirectory); FileAccessPolicy.Current.EnsureWritable(directory);
        var target = Path.Combine(directory, normalized.Sha256 + normalized.Format); FileAccessPolicy.Current.EnsureWritable(target);
        cancellationToken.ThrowIfCancellationRequested(); Directory.CreateDirectory(directory);
        if (File.Exists(target))
        {
            VerifyTagAssetFile(target, normalized, cancellationToken); return target;
        }
        WriteTagAssetFile(normalized, target, cancellationToken); return target;
    }

    private static void WriteTagAssetFile(TagAsset asset, string target, CancellationToken cancellationToken)
    {
        FileAccessPolicy.Current.EnsureWritable(target);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            FileAccessPolicy.Current.EnsureWritable(temporary);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                for (var offset = 0; offset < asset.Data.Length; offset += 1024 * 1024)
                { cancellationToken.ThrowIfCancellationRequested(); output.Write(asset.Data, offset, Math.Min(1024 * 1024, asset.Data.Length - offset)); }
                output.Flush(flushToDisk: true);
            }
            VerifyTagAssetFile(temporary, asset, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); FileAccessPolicy.Current.EnsureWritable(target); File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void VerifyTagAssetFile(string path, TagAsset asset, CancellationToken cancellationToken)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != asset.ByteLength) throw new InvalidDataException("Tag artwork file length differs from its preserved bytes.");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[1024 * 1024]; int count;
        while ((count = input.Read(buffer)) != 0) { cancellationToken.ThrowIfCancellationRequested(); digest.AppendData(buffer, 0, count); }
        if (!string.Equals(Convert.ToHexStringLower(digest.GetHashAndReset()), asset.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Tag artwork file checksum differs from its preserved bytes.");
    }

    private bool HasTagAssetSchema()
    {
        using var command = Command("SELECT 1 FROM sqlite_master WHERE type='table' AND name='tag_assets'", null); return command.ExecuteScalar() != null;
    }

    private Guid AllocateTagAssetId(string hash, TagKey key, SqliteTransaction transaction)
    {
        // A former content/key id may still be owned by an asset whose association was renamed.
        // Never take that id away from the existing association when importing the old key again.
        for (var ordinal = 0; ordinal < 1024; ordinal++)
        {
            var seed = JsonSerializer.Serialize(new[] { "tag-asset", hash, key.Category.ToUpperInvariant(), key.Value.ToUpperInvariant(), ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16));
            using var exists = Command("SELECT 1 FROM tag_assets WHERE id=$id", transaction, ("$id", id.ToString()));
            if (exists.ExecuteScalar() == null) return id;
        }
        throw new InvalidOperationException("Could not allocate a distinct tag artwork identity; no changes were retained.");
    }

    private TagAsset SaveTagAsset(TagAsset asset, SqliteTransaction transaction)
    {
        using var read = Command("SELECT json_remove(document,'$.data') FROM tag_assets WHERE id=$id", transaction, ("$id", asset.Id.ToString()));
        if (read.ExecuteScalar() is string metadata)
        {
            var existing = JsonSerializer.Deserialize<TagAsset>(metadata, Json) ?? throw new InvalidDataException("Invalid tag artwork metadata.");
            ValidateTagAssetMetadata(existing);
            if (!string.Equals(existing.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Tag artwork bytes are immutable. Store different bytes using a new asset id.");
            var changedAssociation = existing.Tag != null && (asset.Tag == null || !Same(existing.Tag.Category, asset.Tag.Category) || !Same(existing.Tag.Value, asset.Tag.Value));
            asset = asset with
            {
                PreviousTags = existing.PreviousTags.Concat(asset.PreviousTags).Concat(changedAssociation ? [existing.Tag!] : [])
                    .Distinct(TagAssetTagComparer.Instance).ToList(),
                SourcePaths = existing.SourcePaths.Concat(asset.SourcePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
            ValidateTagAssetMetadata(asset);
        }
        // Bytes first, then the row without them.
        if (assets.Write(asset.Data) != asset.Sha256) throw new InvalidDataException("Tag artwork checksum does not match its original bytes.");
        var json = WithoutData(asset);
        if (!KeepLegacyExisting("tag_assets", asset.Id.ToString(), json, transaction))
        {
            using var command = Command("INSERT INTO tag_assets(id,document) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET document=excluded.document",
                transaction, ("$id", asset.Id.ToString()), ("$json", json));
            command.ExecuteNonQuery();
        }
        return asset;
    }

    private void SavePortableTagAsset(JsonElement document, SqliteTransaction transaction)
    {
        var asset = document.Deserialize<TagAsset>(Json) ?? throw new InvalidDataException("Null tag artwork.");
        var normalized = NormalizeTagAsset(asset, requireChecksum: true, copyData: false); SaveTagAsset(normalized, transaction);
    }

    private static TagKey NormalizeTagKey(TagKey key)
    {
        var condition = HasCategorizedTag(key).Condition!; return new(condition.TagCategory!, condition.Text!);
    }

    private static TagAsset NormalizeTagAsset(TagAsset asset, bool requireChecksum, bool copyData)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Data == null || asset.Data.Length is 0 or > MaximumTagAssetBytes) throw new ArgumentException("Tag artwork must contain 1 byte through 32 MiB.");
        var data = copyData ? asset.Data.ToArray() : asset.Data;
        var hash = Convert.ToHexStringLower(SHA256.HashData(data)); var format = ImageExtension(data);
        if ((requireChecksum || !string.IsNullOrEmpty(asset.Sha256)) && !string.Equals(asset.Sha256, hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Tag artwork checksum does not match its original bytes.");
        if (requireChecksum && asset.ByteLength != data.LongLength) throw new InvalidDataException("Tag artwork length does not match its original bytes.");
        if (requireChecksum && asset.Format != format) throw new InvalidDataException("Tag artwork format marker does not match its content signature.");
        var normalized = asset with
        {
            Data = data, Sha256 = hash, ByteLength = data.LongLength, Format = format, Tag = asset.Tag == null ? null : NormalizeTagKey(asset.Tag),
            SourceSha256 = asset.SourceSha256?.ToLowerInvariant(), SourcePaths = asset.SourcePaths?.Distinct(StringComparer.OrdinalIgnoreCase).ToList()!,
            Provenance = asset.Provenance == null ? null! : new Dictionary<string, string?>(asset.Provenance),
            PreviousTags = asset.PreviousTags?.Select(NormalizeTagKey).Distinct(TagAssetTagComparer.Instance).ToList()!
        };
        ValidateTagAssetMetadata(normalized); return normalized;
    }

    private static void ValidateTagAssetMetadata(TagAsset asset)
    {
        if (asset.Id == Guid.Empty || asset.Label == null || asset.Label.Length > 4096 || asset.ByteLength is <= 0 or > MaximumTagAssetBytes ||
            asset.Sha256 == null || asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit) || asset.SourceSha256 is { } sourceHash && (sourceHash.Length != 64 || !sourceHash.All(Uri.IsHexDigit)) ||
            asset.SourcePaths == null || asset.SourcePaths.Count > 1024 || asset.SourcePaths.Any(path => path == null || path.Length > 32768) || asset.SourcePaths.Sum(path => (long)path.Length) > 1024 * 1024 ||
            asset.Provenance == null || asset.Provenance.Count > 64 || asset.Provenance.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 256 || pair.Value?.Length > 32768) ||
            asset.PreviousTags == null || asset.PreviousTags.Count > 1024 || asset.PreviousTags.Any(tag => tag == null))
            throw new ArgumentException("Invalid bounded tag artwork metadata.");
        if (asset.Tag != null) _ = NormalizeTagKey(asset.Tag);
        foreach (var tag in asset.PreviousTags) _ = NormalizeTagKey(tag);
    }

    private void RewriteTagAssets(string tag, string? replacement, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        RewriteTagAssetAssociations(transaction, cancellationToken,
            asset => asset.Tag != null && Same(asset.Tag.Value, tag),
            asset => replacement == null ? null : asset.Tag! with { Value = replacement });
    }

    private void MoveTagAssetCategory(string tag, string fromCategory, string toCategory, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        RewriteTagAssetAssociations(transaction, cancellationToken,
            asset => asset.Tag != null && Same(asset.Tag.Value, tag) && Same(asset.Tag.Category, fromCategory),
            asset => asset.Tag! with { Category = toCategory });
    }

    private void RewriteTagAssetAssociations(SqliteTransaction transaction, CancellationToken token, Func<TagAsset, bool> match, Func<TagAsset, TagKey?> replacement)
    {
        using (var exists = Command("SELECT 1 FROM sqlite_master WHERE type='table' AND name='tag_assets'", transaction)) if (exists.ExecuteScalar() == null) return;
        // Fetch one small metadata document at a time; the bytes stay in their files.
        using var select = Command("SELECT id,json_remove(document,'$.data') FROM tag_assets ORDER BY id", transaction);
        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested(); var asset = JsonSerializer.Deserialize<TagAsset>(reader.GetString(1), Json) ?? throw new InvalidDataException("Invalid tag artwork metadata.");
            ValidateTagAssetMetadata(asset); if (!match(asset)) continue;
            var target = replacement(asset); if (target != null) target = NormalizeTagKey(target);
            if (asset.Tag != null && target != null && Same(asset.Tag.Category, target.Category) && Same(asset.Tag.Value, target.Value)) continue;
            var history = asset.PreviousTags.Append(asset.Tag!).Distinct(TagAssetTagComparer.Instance).ToList();
            if (history.Count > 1024) throw new InvalidOperationException("Tag artwork association history exceeds 1024 entries; no changes were retained.");
            using var update = Command("UPDATE tag_assets SET document=json_set(document,'$.tag',json($tag),'$.previousTags',json($history)) WHERE id=$id", transaction,
                ("$tag", JsonSerializer.Serialize(target, Json)), ("$history", JsonSerializer.Serialize(history, Json)), ("$id", reader.GetString(0)));
            update.ExecuteNonQuery();
        }
    }
}
