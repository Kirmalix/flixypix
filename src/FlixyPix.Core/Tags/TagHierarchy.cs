using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed record TagParentEdge(TagKey Parent, TagKey Child);
public sealed record TagHierarchy
{
    public List<TagParentEdge> Edges { get; init; } = [];
    public long Revision { get; init; }
    public long NamespaceRevision { get; init; }
}

public sealed partial class CatalogRepository
{
    private const string TagHierarchyTable = "tag_hierarchies";
    private sealed class TagParentEdgeComparer : IEqualityComparer<TagParentEdge>
    {
        internal static readonly TagParentEdgeComparer Instance = new();
        public bool Equals(TagParentEdge? x, TagParentEdge? y) => ReferenceEquals(x, y) || x != null && y != null && TagAssetTagComparer.Instance.Equals(x.Parent, y.Parent) && TagAssetTagComparer.Instance.Equals(x.Child, y.Child);
        public int GetHashCode(TagParentEdge edge) => HashCode.Combine(TagAssetTagComparer.Instance.GetHashCode(edge.Parent), TagAssetTagComparer.Instance.GetHashCode(edge.Child));
    }
    private void InitializeTagHierarchy(SqliteTransaction transaction)
    {
        using (var command = Command("CREATE TABLE IF NOT EXISTS tag_hierarchies(id TEXT PRIMARY KEY,document TEXT NOT NULL)", transaction)) command.ExecuteNonQuery();
        using var insert = Command("INSERT OR IGNORE INTO tag_hierarchies(id,document) VALUES($id,$json)", transaction, ("$id", Guid.Empty.ToString()), ("$json", JsonSerializer.Serialize(new TagHierarchy(), Json))); insert.ExecuteNonQuery();
    }
    private TagHierarchy ReadTagHierarchy(SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT document FROM tag_hierarchies WHERE id=$id", transaction, ("$id", Guid.Empty.ToString()));
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<TagHierarchy>(json, Json) ?? throw new InvalidDataException("Invalid tag hierarchy.") : new();
    }
    private IReadOnlyList<string> AuditTagHierarchy(SqliteTransaction transaction, CancellationToken token)
     => database.Run<IReadOnlyList<string>>(token, () =>
    {
        var invalid = new List<string>(); var found = false;
        using var command = Command("SELECT id,document FROM tag_hierarchies ORDER BY id", transaction);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested(); var id = reader.GetString(0);
            try
            {
                if (id != Guid.Empty.ToString()) throw new InvalidDataException("Unexpected hierarchy identity."); found = true;
                var graph = JsonSerializer.Deserialize<TagHierarchy>(reader.GetString(1), Json) ?? throw new InvalidDataException("Null hierarchy.");
                ValidateHierarchy(graph, token);
                if (graph.Edges.Distinct(TagParentEdgeComparer.Instance).Count() != graph.Edges.Count || graph.Edges.Any(e => e.Parent != NormalizeHierarchyKey(e.Parent) || e.Child != NormalizeHierarchyKey(e.Child))) throw new InvalidDataException("Noncanonical hierarchy.");
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException) { invalid.Add(id); }
        }
        if (!found) invalid.Add(Guid.Empty.ToString()); return invalid;
    });
    public IReadOnlyList<TagParentEdge> GetTagHierarchy()
    { lock (gate) { ThrowIfDisposed(); return ReadTagHierarchy().Edges.ToArray(); } }
    public IReadOnlyList<TagKey> GetTagParents(TagKey child)
    {
        var key = NormalizeHierarchyKey(child);
        lock (gate) { ThrowIfDisposed(); return SortHierarchyKeys(ReadTagHierarchy().Edges.Where(e => SameHierarchyKey(e.Child, key)).Select(e => e.Parent)); }
    }
    public IReadOnlyList<TagKey> GetTagChildren(TagKey parent)
    {
        var key = NormalizeHierarchyKey(parent);
        lock (gate) { ThrowIfDisposed(); return SortHierarchyKeys(ReadTagHierarchy().Edges.Where(e => SameHierarchyKey(e.Parent, key)).Select(e => e.Child)); }
    }
    public IReadOnlyList<TagKey> GetTagDescendants(TagKey parent, bool includeSelf = false, CancellationToken token = default)
    {
        var key = NormalizeHierarchyKey(parent);
        lock (gate)
        {
            ThrowIfDisposed(); var graph = ReadTagHierarchy(); var children = new Dictionary<TagKey, List<TagKey>>(TagAssetTagComparer.Instance);
            foreach (var edge in graph.Edges) { if (!children.TryGetValue(edge.Parent, out var list)) children[edge.Parent] = list = []; list.Add(edge.Child); }
            var visited = new HashSet<TagKey>(TagAssetTagComparer.Instance) { key }; var queue = new Queue<TagKey>(); queue.Enqueue(key);
            while (queue.TryDequeue(out var current)) { token.ThrowIfCancellationRequested(); if (children.TryGetValue(current, out var next)) foreach (var child in next) if (visited.Add(child)) queue.Enqueue(child); }
            if (!includeSelf) visited.Remove(key); return SortHierarchyKeys(visited);
        }
    }
    public TagChangeUndo SetTagParents(TagKey child, IEnumerable<TagKey> parents, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(parents); var desired = parents.Select(NormalizeHierarchyKey).ToArray(); return ChangeTagParents(child, _ => desired, token);
    }
    public TagChangeUndo SetTagParents(TagKey child, IEnumerable<TagKey> parents, IReadOnlyList<TagKey> expectedParents, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(parents); ArgumentNullException.ThrowIfNull(expectedParents);
        var desired = parents.Select(NormalizeHierarchyKey).ToArray(); var expected = expectedParents.Select(NormalizeHierarchyKey).ToArray();
        return ChangeTagParents(child, _ => desired, token, expected);
    }
    public TagChangeUndo AddTagParent(TagKey child, TagKey parent, CancellationToken token = default)
    { var adding = NormalizeHierarchyKey(parent); return ChangeTagParents(child, current => current.Append(adding), token); }
    public TagChangeUndo RemoveTagParent(TagKey child, TagKey parent, CancellationToken token = default)
    { var removing = NormalizeHierarchyKey(parent); return ChangeTagParents(child, current => current.Where(p => !SameHierarchyKey(p, removing)), token); }
    private TagChangeUndo ChangeTagParents(TagKey child, Func<IEnumerable<TagKey>, IEnumerable<TagKey>> change, CancellationToken token, IReadOnlyList<TagKey>? expectedParents = null)
    {
        var key = NormalizeHierarchyKey(child);
        lock (gate)
        {
            ThrowIfDisposed(); using var transaction = connection.BeginTransaction(); token.ThrowIfCancellationRequested(); var graph = ReadTagHierarchy(transaction);
            var preferences = ReadPreferences(transaction);
            key = CanonicalHierarchyKey(key, graph, preferences, transaction) ?? throw new ArgumentException(T("The tag is not in the directory."));
            if (expectedParents != null && !new HashSet<TagKey>(graph.Edges.Where(e => SameHierarchyKey(e.Child, key)).Select(e => e.Parent), TagAssetTagComparer.Instance).SetEquals(expectedParents)) throw new InvalidOperationException(T("The parents changed. Reload them before saving."));
            var parents = change(graph.Edges.Where(e => SameHierarchyKey(e.Child, key)).Select(e => e.Parent)).Select(NormalizeHierarchyKey)
             .Select(p => CanonicalHierarchyKey(p, graph, preferences, transaction) ?? throw new ArgumentException(T("The tag is not in the directory."))).Distinct(TagAssetTagComparer.Instance).ToArray();
            var before = HierarchyUndoSnapshot(transaction);
            var updated = graph with { Edges = NormalizeHierarchyEdges(graph.Edges.Where(e => !SameHierarchyKey(e.Child, key)).Concat(parents.Select(p => new TagParentEdge(p, key)))) };
            ValidateHierarchy(updated, token); SaveTagHierarchyIfChanged(graph, updated, transaction); var undo = UndoBetween(before, HierarchyUndoSnapshot(transaction));
            token.ThrowIfCancellationRequested(); transaction.Commit(); return undo;
        }
    }
    private TagKey? CanonicalHierarchyKey(TagKey key, TagHierarchy graph, CatalogPreferences preferences, SqliteTransaction transaction)
    {
        if (preferences.TagVocabulary.FirstOrDefault(t => Same(t.Tag, key.Value) && Same(t.Category, key.Category)) is { } entry) return new(entry.Category, entry.Tag);
        if (graph.Edges.SelectMany(e => new[] { e.Parent, e.Child }).FirstOrDefault(k => SameHierarchyKey(k, key)) is { } known) return known;
        using var command = Command("SELECT category,tag FROM media_tags WHERE tag=$tag COLLATE EFP_ORDINAL AND category=$category COLLATE EFP_ORDINAL ORDER BY category,tag LIMIT 1", transaction, ("$tag", key.Value), ("$category", key.Category));
        using var reader = command.ExecuteReader(); return reader.Read() ? new(reader.GetString(0), reader.GetString(1)) : null;
    }
    private static TagKey NormalizeHierarchyKey(TagKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Category == null || key.Category.Length > 256 || string.IsNullOrWhiteSpace(key.Value) || key.Value.Length > 256) throw new ArgumentException("Invalid hierarchy tag.");
        return new(key.Category.Trim(), key.Value.Trim());
    }
    private static bool SameHierarchyKey(TagKey left, TagKey right) => TagAssetTagComparer.Instance.Equals(left, right);
    private static IReadOnlyList<TagKey> SortHierarchyKeys(IEnumerable<TagKey> keys) => keys.Distinct(TagAssetTagComparer.Instance).OrderBy(k => k.Category, StringComparer.CurrentCultureIgnoreCase).ThenBy(k => k.Value, StringComparer.CurrentCultureIgnoreCase).ToArray();
    private static List<TagParentEdge> NormalizeHierarchyEdges(IEnumerable<TagParentEdge> edges) => edges.Select(e => new TagParentEdge(NormalizeHierarchyKey(e.Parent), NormalizeHierarchyKey(e.Child))).Distinct(TagParentEdgeComparer.Instance)
     .OrderBy(e => e.Parent.Category, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Parent.Value, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Child.Category, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Child.Value, StringComparer.OrdinalIgnoreCase).ToList();
    private static void ValidateHierarchy(TagHierarchy graph, CancellationToken token = default)
    {
        if (graph.Revision < 0 || graph.NamespaceRevision < 0 || graph.Edges == null || graph.Edges.Any(e => e == null)) throw new ArgumentException("Invalid tag hierarchy.");
        var children = new Dictionary<TagKey, List<TagKey>>(TagAssetTagComparer.Instance); var degrees = new Dictionary<TagKey, int>(TagAssetTagComparer.Instance);
        foreach (var edge in graph.Edges)
        {
            token.ThrowIfCancellationRequested(); _ = NormalizeHierarchyKey(edge.Parent); _ = NormalizeHierarchyKey(edge.Child);
            if (SameHierarchyKey(edge.Parent, edge.Child)) throw new ArgumentException(T("The tag hierarchy cannot contain a cycle."));
            if (!children.TryGetValue(edge.Parent, out var list)) children[edge.Parent] = list = []; list.Add(edge.Child);
            degrees.TryAdd(edge.Parent, 0); degrees[edge.Child] = degrees.GetValueOrDefault(edge.Child) + 1;
        }
        var queue = new Queue<TagKey>(degrees.Where(p => p.Value == 0).Select(p => p.Key)); var count = 0;
        while (queue.TryDequeue(out var node)) { token.ThrowIfCancellationRequested(); count++; if (children.TryGetValue(node, out var next)) foreach (var child in next) if (--degrees[child] == 0) queue.Enqueue(child); }
        if (count != degrees.Count) throw new ArgumentException(T("The tag hierarchy cannot contain a cycle."));
    }
    private Dictionary<(string Table, string Id), string> HierarchyUndoSnapshot(SqliteTransaction transaction)
    {
        var rows = new Dictionary<(string, string), string>(); if (ReadUndoDocument(TagHierarchyTable, Guid.Empty.ToString(), transaction) is { } document) rows[(TagHierarchyTable, Guid.Empty.ToString())] = document; return rows;
    }
    private void SaveTagHierarchyIfChanged(TagHierarchy before, TagHierarchy after, SqliteTransaction transaction, bool namespaceChange = false)
    {
        if (namespaceChange || JsonSerializer.Serialize(before, Json) != JsonSerializer.Serialize(after, Json)) SaveDocument(TagHierarchyTable, Guid.Empty, after with { Revision = checked(before.Revision + 1), NamespaceRevision = checked(before.NamespaceRevision + (namespaceChange ? 1 : 0)) }, transaction);
    }
    private long ReadTagNamespaceRevision(SqliteTransaction transaction)
    {
        using var command = Command("SELECT coalesce(json_extract(document,'$.namespaceRevision'),0) FROM tag_hierarchies WHERE id=$id", transaction, ("$id", Guid.Empty.ToString())); return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }
    private void RewriteTagHierarchy(Func<TagKey, TagKey?> rewrite, SqliteTransaction transaction, CancellationToken token)
    {
        var before = ReadTagHierarchy(transaction);
        var edges = new List<TagParentEdge>();
        foreach (var edge in before.Edges) { token.ThrowIfCancellationRequested(); var parent = rewrite(edge.Parent); var child = rewrite(edge.Child); if (parent != null && child != null) edges.Add(new(parent, child)); }
        var after = before with { Edges = NormalizeHierarchyEdges(edges) }; ValidateHierarchy(after, token); SaveTagHierarchyIfChanged(before, after, transaction, namespaceChange: true);
    }
}
