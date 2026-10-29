using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlixyPix.Core;

/// <summary>What the metadata of one media file would change. Tags under the empty category are tags without category;
/// <see cref="Rating"/> is on the catalog's scale of 0–10.</summary>
public sealed record MetadataChange(Guid MediaId, string MediaPath, string Source, string TitleBefore, string? Title, string? Description, int? Rating,
    DateTimeOffset? TakenAt, IReadOnlyDictionary<string, IReadOnlyList<string>> Tags, string DocumentSha256)
{
    public int TagCount => Tags.Values.Sum(t => t.Count);
}
public sealed record MetadataPlan(IReadOnlyList<MetadataChange> Changes, int Checked, int WithoutMetadata, IReadOnlyList<string> Problems);

/// <summary>Result of writing XMP sidecars: written, already up to date, and the files that could not be written.</summary>
public sealed record SidecarExport(int Written, int Unchanged, IReadOnlyList<string> Problems);

public sealed partial class CatalogRepository
{
    /// <summary>Reads the metadata of the given media (original "Import metadata"): the XMP sidecar first, then what
    /// <paramref name="embedded"/> finds in the file. Only fills what is still open: a title taken from the file name,
    /// an empty description, no rating; the capture date becomes the file date; tags are added. Nothing is written.</summary>
    public MetadataPlan PlanMetadataImport(IReadOnlyCollection<Guid> mediaIds, IFileMetadataReader? embedded, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);
        var general = GetPreferences().General;
        var changes = new List<MetadataChange>(); var problems = new List<string>(); int without = 0, checkedCount = 0;
        foreach (var id in mediaIds.Distinct())
        {
            token.ThrowIfCancellationRequested();
            if (LoadDocument(id) is not { } loaded) continue;
            var (item, document) = loaded;
            checkedCount++;
            FileMetadata? fromSidecar = null, fromFile = null; string? sidecar = null;
            try
            {
                sidecar = XmpSidecar.Find(item.Path);
                if (sidecar != null && (fromSidecar = XmpSidecar.Read(sidecar)) == null) problems.Add(Path.GetFileName(sidecar) + T(": not an XMP document"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { problems.Add(Path.GetFileName(sidecar ?? item.Path) + ": " + error.Message); }
            try { fromFile = embedded?.Read(item.Path, item.Kind); }
            catch (Exception error) when (error is not OperationCanceledException) { problems.Add(Path.GetFileName(item.Path) + ": " + error.Message); }
            var metadata = (fromSidecar ?? new FileMetadata()).Or(fromFile);
            if (metadata.IsEmpty) { without++; continue; }

            var defaultTitle = string.IsNullOrWhiteSpace(item.Title) || item.Title == Path.GetFileNameWithoutExtension(item.Path) || item.Title == general.ImportTitle(item.Path);
            var title = defaultTitle && metadata.Title is { } t && t != item.Title ? t : null;
            var description = string.IsNullOrWhiteSpace(item.Description) ? metadata.Description : null;
            int? rating = item.Rating == 0 && metadata.Stars is > 0 and var stars ? stars * 2 : null;
            var takenAt = metadata.TakenAt is { } taken && taken != item.FileCreatedAt ? taken : (DateTimeOffset?)null;
            var tags = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (category, values) in metadata.TagsByCategory())
            {
                var present = category.Length == 0 ? item.Tags : item.TagCategories.GetValueOrDefault(category) ?? [];
                var fresh = values.Where(v => !present.Contains(v, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (fresh.Length > 0) tags[category] = fresh;
            }
            if (title == null && description == null && rating == null && takenAt == null && tags.Count == 0) continue;
            var source = string.Join(" + ", new[] { fromSidecar != null ? Path.GetFileName(sidecar) : null, fromFile is { IsEmpty: false } ? T("in the file") : null }.OfType<string>());
            changes.Add(new(item.Id, item.Path, source, item.Title, title, description, rating, takenAt, tags, Hash(document)));
        }
        return new(changes, checkedCount, without, problems);
    }

    /// <summary>Applies the chosen changes in one transaction; a medium edited since the preview is skipped. Returns the number changed.</summary>
    public int ApplyMetadataImport(IReadOnlyList<MetadataChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        lock (gate)
        {
            ThrowIfDisposed(); using var transaction = connection.BeginTransaction(); var applied = 0;
            foreach (var change in changes)
            {
                using var command = Command("SELECT document FROM media WHERE id=$id", transaction, ("$id", change.MediaId.ToString()));
                if (command.ExecuteScalar() is not string document || Hash(document) != change.DocumentSha256) continue;
                var item = JsonSerializer.Deserialize<MediaItem>(document, Json)!;
                var categories = item.TagCategories.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.OrdinalIgnoreCase);
                foreach (var (category, tags) in change.Tags.Where(p => p.Key.Length > 0))
                    categories[category] = (categories.GetValueOrDefault(category) ?? []).Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                UpsertCore(item with
                {
                    Title = change.Title ?? item.Title, Description = change.Description ?? item.Description, Rating = change.Rating ?? item.Rating,
                    FileCreatedAt = change.TakenAt ?? item.FileCreatedAt, TagCategories = categories,
                    Tags = item.Tags.Concat(change.Tags.GetValueOrDefault("") ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Extra = new(item.Extra) { ["metadata.source"] = change.Source },
                }, transaction);
                applied++;
            }
            transaction.Commit(); return applied;
        }
    }

    /// <summary>Writes title, description, rating and tags of the given media into their XMP sidecars
    /// (<see cref="XmpSidecar.Write"/>); the media files stay untouched.</summary>
    public SidecarExport ExportSidecars(IReadOnlyCollection<Guid> mediaIds, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);
        int written = 0, unchanged = 0; var problems = new List<string>();
        foreach (var id in mediaIds.Distinct())
        {
            token.ThrowIfCancellationRequested();
            if (LoadDocument(id) is not { } loaded) continue;
            var item = loaded.Item;
            if (!File.Exists(item.Path)) { problems.Add(Path.GetFileName(item.Path) + T(": file missing")); continue; }
            try { if (XmpSidecar.Write(item)) written++; else unchanged++; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { problems.Add(Path.GetFileName(item.Path) + ": " + error.Message); }
        }
        return new(written, unchanged, problems);
    }

    private (MediaItem Item, string Document)? LoadDocument(Guid id)
    {
        string? document;
        lock (gate) { ThrowIfDisposed(); using var command = Command("SELECT document FROM media WHERE id=$id", null, ("$id", id.ToString())); document = command.ExecuteScalar() as string; }
        return document != null && JsonSerializer.Deserialize<MediaItem>(document, Json) is { } item ? (item, document) : null;
    }

    private static string Hash(string document) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)));
}
