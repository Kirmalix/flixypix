using System.Text.Json;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    /// <summary>Loads saved selections in bounded batches, retaining their order and ignoring deleted media.</summary>
    public IReadOnlyList<MediaItem> GetViewSelection(IReadOnlyList<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count > 100_000) throw new ArgumentException("Selection is too large.", nameof(ids));
        lock (gate)
        {
            ThrowIfDisposed();
            var found = new Dictionary<Guid, MediaItem>();
            foreach (var chunk in ids.Distinct().Chunk(500))
            {
                using var command = Command("SELECT document FROM media WHERE id IN (SELECT value FROM json_each($ids))", null,
                    ("$ids", JsonSerializer.Serialize(chunk.Select(id => id.ToString()))));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    if (JsonSerializer.Deserialize<MediaItem>(reader.GetString(0), Json) is { } item) found[item.Id] = item;
            }
            return ids.Distinct().Where(found.ContainsKey).Select(id => found[id]).ToArray();
        }
    }
}
