using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlixyPix.Core;

public sealed record LegacyTagImageCandidate(string Path, long Length, string Sha256, IReadOnlyList<TagKey> Tags)
{
    public bool IsUnambiguous => Tags.Count == 1;
}
public sealed record LegacyTagImagePreview(string Root, string VocabularySha256, IReadOnlyList<string> SuffixCategories,
    IReadOnlyList<LegacyTagImageCandidate> Files);
public sealed record LegacyTagImageImportResult(int Imported, int Unmatched, int Ambiguous);

public sealed partial class CatalogRepository
{
    // Original: getTagImageFilename 302460ff.; accepted extensions 262141.
    // Suffix fallback is enabled only for explicitly supplied category identities.
    private static readonly string[] LegacyTagImageExtensions = [".jpg", ".png", ".jpeg", ".gif", ".bmp"];

    public LegacyTagImagePreview PreviewLegacyTagImages(string root, IReadOnlyList<string>? suffixCategories = null,
        CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(root); RejectLegacyImageLinks(full);
        var fallback = (suffixCategories ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (fallback.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256)) throw new ArgumentException("Invalid suffix category.");
        var vocabulary = GetPreferences().TagVocabulary.Select(value => new TagKey(value.Category, value.Tag))
            .Distinct(TagAssetTagComparer.Instance).OrderBy(value => value.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Value, StringComparer.OrdinalIgnoreCase).ToArray();
        var vocabularyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(vocabulary, Json))));
        var result = new List<LegacyTagImageCandidate>();
        foreach (var category in Directory.EnumerateDirectories(full).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested(); RejectLegacyImageLinks(category);
            var categoryName = Path.GetFileName(category);
            var tags = vocabulary.Where(value => Same(value.Category, categoryName)).ToArray();
            foreach (var file in Directory.EnumerateFiles(category).Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!LegacyTagImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                if (result.Count == 10_000) throw new InvalidDataException("Tag image preview exceeds 10000 files; select a smaller source.");
                RejectLegacyImageLinks(file);
                var stem = Path.GetFileNameWithoutExtension(file);
                var matches = tags.Where(value => Same(value.Value, stem)).ToList();
                if (fallback.Contains(categoryName, StringComparer.OrdinalIgnoreCase) && Path.GetExtension(file).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
                    matches.AddRange(tags.Where(value => stem.StartsWith(value.Value + "_", StringComparison.OrdinalIgnoreCase)));
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length is <= 0 or > MaximumTagAssetBytes) throw new InvalidDataException("Tag image must contain 1 byte through 32 MiB.");
                var hash = HashLegacyTagImage(stream, cancellationToken);
                result.Add(new(file, stream.Length, hash, matches.Distinct(TagAssetTagComparer.Instance).ToArray()));
            }
        }
        return new(full, vocabularyHash, fallback, result);
    }

    public LegacyTagImageImportResult ImportLegacyTagImages(LegacyTagImagePreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        lock (gate)
        {
            ThrowIfDisposed();
            var current = VerifyLegacyTagImages(preview, cancellationToken);
            using var transaction = connection.BeginTransaction();
            var result = WriteLegacyTagImages(current, transaction, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
            return result;
        }
    }

    /// <summary>A fresh preview that must equal the one the user saw; call before opening the write transaction.</summary>
    private LegacyTagImagePreview VerifyLegacyTagImages(LegacyTagImagePreview preview, CancellationToken cancellationToken)
    {
        var current = PreviewLegacyTagImages(preview.Root, preview.SuffixCategories, cancellationToken);
        if (JsonSerializer.Serialize(current, Json) != JsonSerializer.Serialize(preview, Json))
            throw new InvalidOperationException("Tag image sources or vocabulary changed; create a new preview.");
        return current;
    }

    private LegacyTagImageImportResult WriteLegacyTagImages(LegacyTagImagePreview current, Microsoft.Data.Sqlite.SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var imported = 0;
        foreach (var file in current.Files.Where(value => value.IsUnambiguous))
        {
            cancellationToken.ThrowIfCancellationRequested(); RejectLegacyImageLinks(file.Path);
            using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != file.Length) throw new IOException("Tag image source changed.");
            var data = new byte[checked((int)input.Length)]; input.ReadExactly(data);
            if (Convert.ToHexStringLower(SHA256.HashData(data)) != file.Sha256) throw new IOException("Tag image source changed.");
            var key = file.Tags.Single();
            using var duplicate = Command("SELECT id FROM tag_assets WHERE json_extract(document,'$.sha256')=$hash AND json_extract(document,'$.tag.category')=$category COLLATE EFP_ORDINAL AND json_extract(document,'$.tag.value')=$tag COLLATE EFP_ORDINAL LIMIT 1", transaction,
                ("$hash", file.Sha256), ("$category", key.Category), ("$tag", key.Value));
            var id = duplicate.ExecuteScalar() is string existing ? Guid.Parse(existing) : AllocateTagAssetId(file.Sha256, key, transaction);
            var asset = NormalizeTagAsset(new TagAsset { Id = id, Tag = key, Label = Path.GetFileName(file.Path), Data = data,
                Sha256 = file.Sha256, ByteLength = file.Length, SourceSha256 = file.Sha256, SourcePaths = [file.Path],
                Provenance = new() { ["import.method"] = "legacy-tag-image-folder", ["mapping"] = "unique-category-and-tag", ["source.root"] = current.Root } }, false, false);
            SaveTagAsset(asset, transaction); imported++;
        }
        return new(imported, current.Files.Count(value => value.Tags.Count == 0), current.Files.Count(value => value.Tags.Count > 1));
    }

    private static string HashLegacyTagImage(Stream stream, CancellationToken token)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[64 * 1024]; int count;
        while ((count = stream.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); digest.AppendData(buffer, 0, count); }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static void RejectLegacyImageLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked tag image inputs are not followed.");
    }
}
