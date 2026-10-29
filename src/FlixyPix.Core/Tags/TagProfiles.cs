using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>One document as it was before a tag change (null when the change created it) and a fingerprint of what the
/// change left there.</summary>
public sealed record TagUndoRow(string Table, string Id, string? Before, string AfterSha256);

/// <summary>Takes back a merge or deletion of the directory once: the documents it changed, as they were. It lives only in
/// memory and is refused when one of those documents has been changed since.</summary>
public sealed record TagChangeUndo(IReadOnlyList<TagUndoRow> Rows)
{
    public bool IsEmpty => Rows.Count == 0;
}

/// <summary>The profiles of the directory: pictures of tags (portraits, logos), the undo of merging and deleting, and
/// assigning performers and studios in their own category from the details.</summary>
public sealed partial class CatalogRepository
{
    // Besides the media of the tag, these are the tables a tag change rewrites (see ChangeTagCore).
    private static readonly string[] TagChangeTables = ["saved_searches", "scenes", "filename_rules", "preferences", "tag_presets", "tag_assets"];

    /// <summary>The first picture that can be shown (not opaque) of every tag that has one; the key is category and name.</summary>
    public IReadOnlyDictionary<TagKey, Guid> GetTagPictureIds()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var result = new Dictionary<TagKey, Guid>(TagAssetTagComparer.Instance);
            if (!HasTagAssetSchema()) return result;
            using var command = Command("""
                SELECT id, json_extract(document,'$.tag.category'), json_extract(document,'$.tag.value') FROM tag_assets
                WHERE json_extract(document,'$.tag') IS NOT NULL AND json_extract(document,'$.format') IS NOT NULL ORDER BY id
                """, null);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(1) && !reader.IsDBNull(2)) result.TryAdd(new TagKey(reader.GetString(1), reader.GetString(2)), Guid.Parse(reader.GetString(0)));
            return result;
        }
    }

    /// <summary>Adds and removes tags of one category on several media in one transaction. A removed tag also leaves the
    /// flat tags unless another category of the medium still has it. Returns the number of media changed.</summary>
    public int UpdateCategoryTags(IReadOnlyCollection<Guid> mediaIds, string category, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaIds); ArgumentException.ThrowIfNullOrWhiteSpace(category); ArgumentNullException.ThrowIfNull(add); ArgumentNullException.ThrowIfNull(remove);
        if (add.Concat(remove).Any(string.IsNullOrWhiteSpace)) throw new ArgumentException(T("Tags must not be empty."));
        var name = category.Trim();
        var adding = add.Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var removing = new HashSet<string>(remove.Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
        if (adding.Any(removing.Contains)) throw new ArgumentException(T("A tag cannot be added and removed at the same time."));
        lock (gate)
        {
            ThrowIfDisposed();
            var before = ChangeStamp();
            var changes = new List<(MediaItem Old, MediaItem New)>();
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var id in mediaIds.Distinct())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var command = Command("SELECT document FROM media WHERE id=$id", transaction, ("$id", id.ToString()));
                    if (command.ExecuteScalar() is not string json) continue;
                    var item = JsonSerializer.Deserialize<MediaItem>(json, Json)!;
                    var categories = item.TagCategories.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.OrdinalIgnoreCase);
                    var own = (categories.GetValueOrDefault(name) ?? []).Where(t => !removing.Contains(t)).ToList();
                    own.AddRange(adding.Where(t => !own.Contains(t, StringComparer.OrdinalIgnoreCase)));
                    if (own.Count > 0) categories[name] = own; else categories.Remove(name);
                    var elsewhere = categories.Values.SelectMany(t => t).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var tags = item.Tags.Where(t => !removing.Contains(t) || elsewhere.Contains(t)).ToList();
                    var changed = !tags.SequenceEqual(item.Tags) || categories.Count != item.TagCategories.Count
                        || categories.Any(p => !item.TagCategories.TryGetValue(p.Key, out var old) || !old.SequenceEqual(p.Value));
                    if (!changed) continue;
                    changes.Add((item, UpsertCore(item with { Tags = tags, TagCategories = categories }, transaction)));
                }
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            if (changes.Count > 0) CarryCachesAcross(before, changes);
            return changes.Count;
        }
    }

    /// <summary>Takes a merge or deletion back when nothing it changed has been changed since; otherwise nothing happens
    /// and the result is false.</summary>
    public bool UndoTagChange(TagChangeUndo undo)
    {
        ArgumentNullException.ThrowIfNull(undo);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            foreach (var row in undo.Rows)
                if (Hash(ReadUndoDocument(row.Table, row.Id, transaction) ?? "") != row.AfterSha256) { transaction.Rollback(); return false; }
            foreach (var row in undo.Rows)
            {
                if (row.Before is null)
                {
                    using var delete = Command($"DELETE FROM {row.Table} WHERE id=$id", transaction, ("$id", row.Id)); delete.ExecuteNonQuery();
                }
                else if (row.Table == "media") UpsertCore(JsonSerializer.Deserialize<MediaItem>(row.Before, Json)!, transaction);
                else if (row.Table == "scenes")
                {
                    // A tag change rewrites only the scene's tags, never the medium it belongs to.
                    using var update = Command("UPDATE scenes SET document=$json WHERE id=$id", transaction, ("$json", row.Before), ("$id", row.Id)); update.ExecuteNonQuery();
                }
                else if (row.Table == "tag_assets")
                {
                    // The row holds no picture bytes; they stay in their file.
                    using var update = Command("UPDATE tag_assets SET document=$json WHERE id=$id", transaction,
                        ("$json", row.Before), ("$id", row.Id));
                    update.ExecuteNonQuery();
                }
                else
                {
                    using var upsert = Command($"INSERT INTO {row.Table}(id,document) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET document=excluded.document", transaction,
                        ("$id", row.Id), ("$json", row.Before));
                    upsert.ExecuteNonQuery();
                }
            }
            transaction.Commit();
            cachedMedia = null;
            return true;
        }
    }

    /// <summary>The documents a change of these tags can touch: the media that have them and every row of the other tables.</summary>
    private Dictionary<(string Table, string Id), string> SnapshotTagChange(IReadOnlyCollection<string> mediaIds, SqliteTransaction transaction)
    {
        var rows = new Dictionary<(string Table, string Id), string>();
        foreach (var id in mediaIds)
            if (ReadUndoDocument("media", id, transaction) is { } document) rows[("media", id)] = document;
        foreach (var table in TagChangeTables)
        {
            using (var exists = Command("SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name", transaction, ("$name", table)))
                if (exists.ExecuteScalar() is null) continue;
            using var command = Command(table == "tag_assets" ? "SELECT id, json_remove(document,'$.data') FROM tag_assets" : $"SELECT id, document FROM {table}", transaction);
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows[(table, reader.GetString(0))] = reader.GetString(1);
        }
        return rows;
    }

    private string? ReadUndoDocument(string table, string id, SqliteTransaction transaction)
    {
        using (var exists = Command("SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name", transaction, ("$name", table)))
            if (exists.ExecuteScalar() is null) return null;
        using var command = Command(table == "tag_assets" ? "SELECT json_remove(document,'$.data') FROM tag_assets WHERE id=$id" : $"SELECT document FROM {table} WHERE id=$id",
            transaction, ("$id", id));
        return command.ExecuteScalar() as string;
    }

    private static TagChangeUndo UndoBetween(Dictionary<(string Table, string Id), string> before, Dictionary<(string Table, string Id), string> after) =>
        new(before.Keys.Union(after.Keys)
            .Where(key => before.GetValueOrDefault(key) != after.GetValueOrDefault(key))
            .Select(key => new TagUndoRow(key.Table, key.Id, before.GetValueOrDefault(key), Hash(after.GetValueOrDefault(key) ?? "")))
            .ToList());

    private List<string> MediaIdsWithTags(IEnumerable<string> tags, SqliteTransaction transaction)
    {
        var ids = new HashSet<string>();
        foreach (var tag in tags)
        {
            using var command = Command("SELECT DISTINCT media_id FROM media_tags WHERE tag=$tag COLLATE EFP_ORDINAL", transaction, ("$tag", tag));
            using var reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        return ids.ToList();
    }
}
