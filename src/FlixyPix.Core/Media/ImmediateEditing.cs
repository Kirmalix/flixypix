using System.Text.Json;

namespace FlixyPix.Core;

[Flags]
public enum ImmediateEditFields { Rating = 1, Favorite = 2, WatchLater = 4, Tags = 8, ReviewState = 16, All = 31 }
public sealed record ImmediateEditRow(Guid MediaId, ImmediateEditFields Fields, string Before, string After);
public sealed record ImmediateEditUndo(IReadOnlyList<ImmediateEditRow> Rows)
{ public long? TagNamespaceRevision { get; init; } }

public sealed partial class CatalogRepository
{
    private sealed record ImmediateValues(ReviewState? ReviewState, int? Rating, bool? IsFavorite, bool? WatchLater,
        List<string>? Tags, Dictionary<string, List<string>>? TagCategories);

    private static string ImmediateDocument(MediaItem item, ImmediateEditFields fields) => JsonSerializer.Serialize(new ImmediateValues(
        fields.HasFlag(ImmediateEditFields.ReviewState) ? item.ReviewState : null,
        fields.HasFlag(ImmediateEditFields.Rating) ? item.Rating : null,
        fields.HasFlag(ImmediateEditFields.Favorite) ? item.IsFavorite : null,
        fields.HasFlag(ImmediateEditFields.WatchLater) ? item.WatchLater : null,
        fields.HasFlag(ImmediateEditFields.Tags) ? item.Tags : null,
        fields.HasFlag(ImmediateEditFields.Tags) ? item.TagCategories.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value) : null), Json);

    private static ImmediateEditRow ImmediateRow(MediaItem before, MediaItem after, ImmediateEditFields fields) =>
        new(before.Id, fields, ImmediateDocument(before, fields), ImmediateDocument(after, fields));

    private static MediaItem ApplyImmediateDocument(MediaItem current, string document, ImmediateEditFields fields)
    {
        var values = JsonSerializer.Deserialize<ImmediateValues>(document, Json) ?? throw new ArgumentException("Invalid immediate-edit document.");
        return current with
        {
            ReviewState = fields.HasFlag(ImmediateEditFields.ReviewState) ? values.ReviewState ?? throw new ArgumentException("Missing review state.") : current.ReviewState,
            Rating = fields.HasFlag(ImmediateEditFields.Rating) ? values.Rating ?? throw new ArgumentException("Missing rating.") : current.Rating,
            IsFavorite = fields.HasFlag(ImmediateEditFields.Favorite) ? values.IsFavorite ?? throw new ArgumentException("Missing favorite.") : current.IsFavorite,
            WatchLater = fields.HasFlag(ImmediateEditFields.WatchLater) ? values.WatchLater ?? throw new ArgumentException("Missing watch-later state.") : current.WatchLater,
            Tags = fields.HasFlag(ImmediateEditFields.Tags) ? values.Tags ?? throw new ArgumentException("Missing tags.") : current.Tags,
            TagCategories = fields.HasFlag(ImmediateEditFields.Tags) ? values.TagCategories ?? throw new ArgumentException("Missing tag categories.") : current.TagCategories,
        };
    }

    public ImmediateEditUndo ApplyImmediateEdit(IReadOnlyCollection<Guid> mediaIds, ImmediateEditFields fields, Func<MediaItem, MediaItem> change)
    {
        ArgumentNullException.ThrowIfNull(mediaIds); ArgumentNullException.ThrowIfNull(change);
        if (fields == 0 || (fields & ~ImmediateEditFields.All) != 0) throw new ArgumentOutOfRangeException(nameof(fields));
        lock (gate)
        {
            ThrowIfDisposed(); var stamp = ChangeStamp(); using var transaction = connection.BeginTransaction();
            var rows = new List<ImmediateEditRow>();
            var changes = new List<(MediaItem Old, MediaItem New)>();
            foreach (var id in mediaIds.Distinct())
            {
                var before = ReadMediaDocument(id, transaction) ?? throw new InvalidOperationException("The edited medium no longer exists.");
                var proposed = ApplyImmediateDocument(before, ImmediateDocument(change(before), fields), fields);
                var changedFields = (ImmediateEditFields)0;
                foreach (var field in new[] { ImmediateEditFields.Rating, ImmediateEditFields.Favorite, ImmediateEditFields.WatchLater, ImmediateEditFields.Tags, ImmediateEditFields.ReviewState })
                    if (fields.HasFlag(field) && ImmediateDocument(before, field) != ImmediateDocument(proposed, field)) changedFields |= field;
                if (changedFields == 0) continue;
                var after = UpsertCore(proposed, transaction);
                changes.Add((before, after));
                rows.Add(ImmediateRow(before, after, changedFields));
            }
            var namespaceRevision = rows.Any(r => r.Fields.HasFlag(ImmediateEditFields.Tags)) ? (long?)ReadTagNamespaceRevision(transaction) : null;
            CommitMediaChanges(transaction, stamp, changes); return new(rows) { TagNamespaceRevision = namespaceRevision };
        }
    }

    /// <summary>Reverts only the edited fields. Every affected medium is checked before any write.</summary>
    public bool UndoImmediateEdit(ImmediateEditUndo undo)
    {
        ArgumentNullException.ThrowIfNull(undo);
        if (undo.Rows.Select(r => r.MediaId).Distinct().Count() != undo.Rows.Count ||
            undo.Rows.Any(r => r.Fields == 0 || (r.Fields & ~ImmediateEditFields.All) != 0)) throw new ArgumentException("Invalid immediate-edit undo.");
        lock (gate)
        {
            ThrowIfDisposed(); var stamp = ChangeStamp(); using var transaction = connection.BeginTransaction();
            if (undo.TagNamespaceRevision is { } revision && ReadTagNamespaceRevision(transaction) != revision) return false;
            var restored = new List<MediaItem>();
            var originals = new List<MediaItem>();
            foreach (var row in undo.Rows)
            {
                var current = ReadMediaDocument(row.MediaId, transaction);
                if (current == null || ImmediateDocument(current, row.Fields) != row.After) return false;
                restored.Add(ApplyImmediateDocument(current, row.Before, row.Fields));
                originals.Add(current);
            }
            var changes = restored.Select((item, index) => (originals[index], UpsertCore(item, transaction))).ToArray();
            CommitMediaChanges(transaction, stamp, changes); return true;
        }
    }
}
