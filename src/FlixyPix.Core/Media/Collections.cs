using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>A tag of bookmarks and scenes and how many of them carry it.</summary>
public sealed record MarkTagCount(string Tag, int Count);

/// <summary>Collection sets, the quick collection, the target collection of key B and tags of jump marks (UX plan U6/U7).</summary>
public sealed partial class CatalogRepository
{
    private const string TargetCollectionKey = "collections.target";

    public IReadOnlyList<CollectionSet> GetCollectionSets()
    {
        lock (gate) { ThrowIfDisposed(); return ReadDocuments<CollectionSet>("collection_sets").OrderBy(x => x.Order).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    public CollectionSet UpsertCollectionSet(CollectionSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Id == Guid.Empty || string.IsNullOrWhiteSpace(set.Name) || set.Name.Length > 256)
            throw new ArgumentException("A collection set needs an id and a name of up to 256 characters.", nameof(set));
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            EnsureSetParent(set.ParentId, set.Id, transaction);
            var saved = set with { Name = set.Name.Trim() };
            SaveDocument("collection_sets", saved.Id, saved, transaction);
            transaction.Commit();
            return saved;
        }
    }

    /// <summary>Deletes the set but keeps what it holds: its collections, smart collections and sets move up to its parent.</summary>
    public void DeleteCollectionSet(Guid id)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            if (ReadEntity<CollectionSet>("collection_sets", id, transaction) is not { } set) return;
            foreach (var child in ReadDocuments<CollectionSet>("collection_sets", transaction).Where(x => x.ParentId == id))
                SaveDocument("collection_sets", child.Id, child with { ParentId = set.ParentId }, transaction);
            foreach (var playlist in ReadDocuments<Playlist>("playlists", transaction).Where(x => x.ParentId == id))
                SavePlaylist(playlist with { ParentId = set.ParentId }, transaction);
            foreach (var search in ReadDocuments<SavedSearch>("saved_searches", transaction).Where(x => x.ParentId == id))
                SaveDocument("saved_searches", search.Id, search with { ParentId = set.ParentId }, transaction);
            using (var command = Command("DELETE FROM collection_sets WHERE id=$id", transaction, ("$id", id.ToString()))) command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    /// <summary>The quick collection of the library; it is made on first use.</summary>
    public Playlist GetQuickCollection()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var quick = QuickCollection(transaction);
            if (quick is null) { quick = new Playlist { Name = "Quick collection", Kind = PlaylistKind.Quick }; SavePlaylist(quick, transaction); }
            transaction.Commit();
            return quick;
        }
    }

    /// <summary>The collection key B fills: the one chosen as target, otherwise the quick collection.</summary>
    public Playlist GetTargetCollection() => FindTargetCollection(create: true)!;

    /// <summary>Like <see cref="GetTargetCollection"/>, but null instead of making the quick collection, so that showing the
    /// library writes nothing.</summary>
    public Playlist? FindTargetCollection(bool create = false)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using (var command = Command("SELECT document FROM ui_state WHERE id=$id", null, ("$id", TargetCollectionKey)))
                if (command.ExecuteScalar() is string json && JsonSerializer.Deserialize<Guid?>(json, Json) is { } id &&
                    ReadEntity<Playlist>("playlists", id, null) is { } target) return target;
            return create ? GetQuickCollection() : QuickCollection(null);
        }
    }

    /// <summary>Makes the collection the target of key B; null (or the quick collection) goes back to the quick collection.</summary>
    public void SetTargetCollection(Guid? id)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (id is { } chosen && ReadEntity<Playlist>("playlists", chosen, null) is null) throw new ArgumentException("Unknown collection.", nameof(id));
            using var command = Command("INSERT INTO ui_state(id,document) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET document=excluded.document", null,
                ("$id", TargetCollectionKey), ("$json", JsonSerializer.Serialize(id, Json)));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Adds the media to the collection, or takes them out when all of them are in it already (key B).
    /// Taking a medium out also removes its scene clips there.</summary>
    public (Playlist Collection, bool Added) ToggleInCollection(Guid collectionId, IReadOnlyCollection<Guid> mediaIds)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var collection = ReadEntity<Playlist>("playlists", collectionId, transaction) ?? throw new ArgumentException("Unknown collection.", nameof(collectionId));
            EnsureMediaExist(mediaIds.ToList(), transaction);
            var entries = collection.Entries.Count > 0 ? collection.Entries.ToList() : collection.MediaIds.Select(id => new PlaylistEntry(id)).ToList();
            var present = entries.Select(x => x.MediaId).ToHashSet();
            var add = !mediaIds.All(present.Contains);
            if (add) entries.AddRange(mediaIds.Distinct().Where(id => !present.Contains(id)).Select(id => new PlaylistEntry(id)));
            else { var gone = mediaIds.ToHashSet(); entries.RemoveAll(x => gone.Contains(x.MediaId)); }
            var saved = collection with { Entries = entries, MediaIds = entries.Select(x => x.MediaId).ToList() };
            SavePlaylist(saved, transaction);
            transaction.Commit();
            return (saved, add);
        }
    }

    /// <summary>Tags of all bookmarks and scenes, most used first.</summary>
    public IReadOnlyList<MarkTagCount> GetMarkTags()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("""
                SELECT min(j.value), count(*) FROM (SELECT document FROM bookmarks UNION ALL SELECT document FROM scenes) d, json_each(d.document,'$.tags') j
                WHERE j.type='text' GROUP BY j.value COLLATE EFP_ORDINAL ORDER BY 2 DESC, 1 COLLATE EFP_ORDINAL
                """, null);
            using var reader = command.ExecuteReader();
            var result = new List<MarkTagCount>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetInt32(1)));
            return result;
        }
    }

    private Playlist? QuickCollection(SqliteTransaction? transaction) =>
        ReadDocuments<Playlist>("playlists", transaction).Where(x => x.Kind == PlaylistKind.Quick).OrderBy(x => x.Id).FirstOrDefault();

    /// <summary>The parent must be an existing set, and a set must not end up inside itself.</summary>
    private void EnsureSetParent(Guid? parentId, Guid? self, SqliteTransaction transaction)
    {
        var sets = ReadDocuments<CollectionSet>("collection_sets", transaction).ToDictionary(x => x.Id);
        var seen = new HashSet<Guid>();
        for (var current = parentId; current is { } id; current = sets[id].ParentId)
        {
            if (!sets.ContainsKey(id)) throw new ArgumentException("The collection set does not exist.");
            if (id == self || !seen.Add(id)) throw new ArgumentException("A collection set cannot lie inside itself.");
        }
    }

    /// <summary>Checks the rules of the collection tree before a collection is saved.</summary>
    private void EnsureCollectionPlacement(Playlist playlist, SqliteTransaction transaction)
    {
        EnsureSetParent(playlist.ParentId, null, transaction);
        if (playlist.Kind == PlaylistKind.Quick && QuickCollection(transaction) is { } quick && quick.Id != playlist.Id)
            throw new ArgumentException("A library has only one quick collection.");
    }

    /// <summary>On import: an unknown set becomes the top level, and a second quick collection a normal one.</summary>
    private Playlist PortablePlacement(Playlist playlist, SqliteTransaction transaction)
    {
        var placed = playlist with { ParentId = ExistingSet(playlist.ParentId, transaction) };
        return placed.Kind == PlaylistKind.Quick && QuickCollection(transaction) is { } quick && quick.Id != placed.Id ? placed with { Kind = PlaylistKind.Playlist } : placed;
    }

    private Guid? ExistingSet(Guid? id, SqliteTransaction transaction) =>
        id is { } set && ReadEntity<CollectionSet>("collection_sets", set, transaction) is not null ? id : null;

    private void ImportStagedCollectionSets(List<string> errors, ref int skipped, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var staged = ReadStagedPortable<CollectionSet>("collectionsets", transaction, cancellationToken).ToList();
        foreach (var set in staged)
        {
            if (set is null || set.Id == Guid.Empty || string.IsNullOrWhiteSpace(set.Name)) { AddPortableError(errors, "Collection set: invalid set."); skipped++; continue; }
            SaveDocument("collection_sets", set.Id, set with { Name = set.Name.Trim() }, transaction);
        }
        // Parents are checked once all sets are there; missing ones and cycles put a set on the top level.
        foreach (var set in staged.Where(x => x is { ParentId: not null } && x.Id != Guid.Empty))
        {
            try { EnsureSetParent(set.ParentId, set.Id, transaction); }
            catch (ArgumentException) { SaveDocument("collection_sets", set.Id, set with { Name = set.Name.Trim(), ParentId = null }, transaction); }
        }
    }
}
