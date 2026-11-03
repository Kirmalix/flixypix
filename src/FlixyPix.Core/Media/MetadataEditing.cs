using System.Text.Json;

namespace FlixyPix.Core;

public sealed record MetadataValues(string Title, string? Category, string? Description, DateOnly? ReleaseDate)
{
    public static MetadataValues From(MediaItem item) => new(item.Title, item.Category, item.Description, item.ReleaseDate);
}

public sealed record MetadataPatch(Guid MediaId, MetadataValues Expected, MetadataValues Values);
public sealed record MetadataDraft(Guid MediaId, string Title, string Category, string Description, string ReleaseDate, MetadataValues Original);

public sealed class MetadataConflictException(Guid mediaId, bool missing = false)
    : InvalidOperationException(missing ? "The edited medium no longer exists." : "The edited metadata changed. Reload it before saving.")
{
    public Guid MediaId { get; } = mediaId;
    public bool Missing { get; } = missing;
}

public sealed partial class CatalogRepository
{
    private const string MetadataDraftKey = "metadata-edit-drafts-v1";

    public IReadOnlyList<MetadataDraft> GetMetadataDrafts()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("SELECT document FROM ui_state WHERE id=$id", null, ("$id", MetadataDraftKey));
            return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<List<MetadataDraft>>(json, Json) ?? [] : [];
        }
    }

    public void SaveMetadataDrafts(IReadOnlyList<MetadataDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("INSERT INTO ui_state(id,document) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET document=excluded.document", null,
                ("$id", MetadataDraftKey), ("$json", JsonSerializer.Serialize(drafts, Json)));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Validates all edited fields before writing. Unrelated immediate edits remain current.</summary>
    public void ApplyMetadataPatches(IReadOnlyList<MetadataPatch> patches, bool clearDrafts = false, IReadOnlyCollection<Guid>? clearDraftIds = null)
    {
        ArgumentNullException.ThrowIfNull(patches);
        if (patches.Select(p => p.MediaId).Distinct().Count() != patches.Count) throw new ArgumentException("Duplicate media patches.", nameof(patches));
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var changes = new List<MediaItem>();
            foreach (var patch in patches)
            {
                var current = ReadMediaDocument(patch.MediaId, transaction) ?? throw new MetadataConflictException(patch.MediaId, missing: true);
                if (MetadataValues.From(current) != patch.Expected) throw new MetadataConflictException(patch.MediaId);
                changes.Add(current with { Title = patch.Values.Title.Trim(), Category = patch.Values.Category,
                    Description = patch.Values.Description, ReleaseDate = patch.Values.ReleaseDate });
            }
            foreach (var item in changes) UpsertCore(item, transaction);
            if (clearDrafts)
            {
                using var command = Command("DELETE FROM ui_state WHERE id=$id", transaction, ("$id", MetadataDraftKey));
                command.ExecuteNonQuery();
            }
            else if (clearDraftIds is { Count: > 0 })
            {
                using var read = Command("SELECT document FROM ui_state WHERE id=$id", transaction, ("$id", MetadataDraftKey));
                if (read.ExecuteScalar() is string json)
                {
                    var remaining = (JsonSerializer.Deserialize<List<MetadataDraft>>(json, Json) ?? []).Where(d => !clearDraftIds.Contains(d.MediaId)).ToArray();
                    using var write = Command("UPDATE ui_state SET document=$json WHERE id=$id", transaction,
                        ("$id", MetadataDraftKey), ("$json", JsonSerializer.Serialize(remaining, Json)));
                    write.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }
    }
}
