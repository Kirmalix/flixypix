using System.Text.Json;

namespace FlixyPix.Core;

/// <summary>How much of the last view a library restores on start (original "loadlastvideoview" only knew the view).</summary>
public enum RestoreScope
{
    /// <summary>Which view (media kind, favourites, collection, saved search), sorting and display.</summary>
    View,
    /// <summary>Also search text, filters, scroll position and selection.</summary>
    All
}

/// <summary>What the main window shows. It is kept in the catalog, so an encrypted library keeps it encrypted, and it
/// doubles as an entry of the back/forward history.</summary>
public sealed record ViewState
{
    /// <summary>all, Video, Image, Audio, favorites, watchlater.</summary>
    public string View { get; init; } = "all";
    public Guid? PlaylistId { get; init; }
    public Guid? SavedSearchId { get; init; }
    public string? SearchText { get; init; }
    public string? Tag { get; init; }
    public string? TagCategory { get; init; }
    public string? Category { get; init; }
    public string? Folder { get; init; }
    public List<string> RequiredTags { get; init; } = [];
    public List<string> ExcludedTags { get; init; } = [];
    public SearchExpression? Expression { get; init; }
    /// <summary>The filter bar (performers, studios, ranges, dates).</summary>
    public MediaFilter Filter { get; init; } = new();
    /// <summary>The tag filters need only one match instead of all.</summary>
    public bool TagsMatchAny { get; init; }
    /// <summary>Minimum stars of the rating filter (0–5).</summary>
    public int MinimumStars { get; init; }
    /// <summary>Index of the main window's sort choice.</summary>
    public int SortOrder { get; init; }
    public int ViewMode { get; init; }
    public int TileWidth { get; init; }
    /// <summary>Index of the first visible medium and the medium that was there, so the place survives a changed result.</summary>
    public int Position { get; init; }
    public Guid? AnchorId { get; init; }
    public List<Guid> Selection { get; init; } = [];
    /// <summary>The video played last, for "continue watching" after a restart.</summary>
    public Guid? LastPlayedId { get; init; }
    public DateTimeOffset SavedAt { get; init; }

    /// <summary>"home" is the start page; as a query it stands for all media.</summary>
    public static readonly string[] Views = ["home", "all", "Video", "Image", "Audio", "favorites", "watchlater"];

    /// <summary>The part of the state a scope restores; the rest falls back to an empty view.</summary>
    public ViewState Restricted(RestoreScope scope) => scope == RestoreScope.All ? this : new ViewState
    {
        View = View, PlaylistId = PlaylistId, SavedSearchId = SavedSearchId, SortOrder = SortOrder, ViewMode = ViewMode, TileWidth = TileWidth, SavedAt = SavedAt
    };

    /// <summary>Same view, filters and order; position and selection do not count (used by the history).</summary>
    public bool SameView(ViewState other) =>
        View == other.View && PlaylistId == other.PlaylistId && SavedSearchId == other.SavedSearchId && SearchText == other.SearchText &&
        Tag == other.Tag && TagCategory == other.TagCategory && Category == other.Category && Folder == other.Folder &&
        RequiredTags.SequenceEqual(other.RequiredTags) && ExcludedTags.SequenceEqual(other.ExcludedTags) && MinimumStars == other.MinimumStars &&
        SortOrder == other.SortOrder && TagsMatchAny == other.TagsMatchAny &&
        JsonSerializer.Serialize(Expression, CatalogRepository.Json) == JsonSerializer.Serialize(other.Expression, CatalogRepository.Json) &&
        JsonSerializer.Serialize(Filter, CatalogRepository.Json) == JsonSerializer.Serialize(other.Filter, CatalogRepository.Json);

    /// <summary>Sort choices of the main window: title A–Z, Z–A, newest, longest, best rated, collection order, file date old/new,
    /// highest O counter, highest resolution, highest bit rate.</summary>
    public static (MediaSort Sort, bool Descending) SortFor(int choice) => choice switch
    {
        1 => (MediaSort.Title, true), 2 => (MediaSort.AddedAt, true), 3 => (MediaSort.Duration, true), 4 => (MediaSort.Rating, true),
        6 => (MediaSort.FileDate, false), 7 => (MediaSort.FileDate, true), 8 => (MediaSort.OCount, true), 9 => (MediaSort.Resolution, true), 10 => (MediaSort.Bitrate, true), _ => (MediaSort.Title, false),
    };

    /// <summary>The query this state shows (without paging); relative dates count from <paramref name="now"/>.</summary>
    public MediaQuery ToQuery(DateTimeOffset now)
    {
        static SearchExpression Has(string tag) => SearchExpression.Where(new() { Field = SearchField.Tag, Comparison = SearchComparison.Equals, Text = tag });
        var parts = new List<SearchExpression>();
        if (Expression is not null) parts.Add(Expression);
        if (Filter.ToExpression(now) is { } filter) parts.Add(filter);
        var anyTags = TagsMatchAny && RequiredTags.Count > 1;
        if (anyTags) parts.Add(SearchExpression.Any(RequiredTags.Select(Has).ToArray()));
        var (sort, descending) = SortFor(SortOrder);
        return new MediaQuery
        {
            Text = SearchText, Tag = Tag, MinimumRating = Math.Clamp(MinimumStars, 0, 5) * 2,
            IsFavorite = View == "favorites" ? true : null, WatchLater = View == "watchlater" ? true : null,
            Kind = Enum.TryParse<MediaKind>(View, out var kind) ? kind : null,
            Category = Category, TagCategory = TagCategory, Folder = Folder,
            RequiredTags = anyTags ? [] : RequiredTags, ExcludedTags = ExcludedTags,
            Expression = parts.Count switch { 0 => null, 1 => parts[0], _ => SearchExpression.All(parts.ToArray()) },
            Sort = sort, Descending = descending,
        };
    }
}

/// <summary>Back and forward through the views of the main window, like a browser. Only changes of view or filters make
/// a new entry; scrolling updates the current one.</summary>
public sealed class ViewHistory(int capacity = 50)
{
    private readonly List<ViewState> entries = [];
    private int current = -1;

    private int capacity = capacity is >= 1 and <= 1000 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    /// <summary>How many views are kept; lowering it drops the oldest ones.</summary>
    public int Capacity
    {
        get => capacity;
        set
        {
            capacity = value is >= 1 and <= 1000 ? value : throw new ArgumentOutOfRangeException(nameof(value));
            while (entries.Count > capacity) { entries.RemoveAt(0); current = Math.Max(0, current - 1); }
        }
    }
    public bool CanGoBack => current > 0;
    public bool CanGoForward => current >= 0 && current < entries.Count - 1;
    public ViewState? Current => current >= 0 ? entries[current] : null;

    public void Record(ViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Current is { } now && now.SameView(state)) { entries[current] = state; return; }
        entries.RemoveRange(current + 1, entries.Count - current - 1);
        entries.Add(state);
        if (entries.Count > Capacity) entries.RemoveAt(0);
        current = entries.Count - 1;
    }

    public ViewState? Back() => CanGoBack ? entries[--current] : null;
    public ViewState? Forward() => CanGoForward ? entries[++current] : null;
}

public sealed partial class CatalogRepository
{
    private const string ViewStateKey = "view";

    public ViewState? GetViewState()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("SELECT document FROM ui_state WHERE id=$id", null, ("$id", ViewStateKey));
            if (command.ExecuteScalar() is not string json) return null;
            try { return JsonSerializer.Deserialize<ViewState>(json, Json); }
            catch (JsonException) { return null; } // a damaged state only costs the restore
        }
    }

    /// <summary>Stores the state without invalidating the browsing caches, so saving while browsing stays cheap.</summary>
    public void SaveViewState(ViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.RequiredTags is null || state.ExcludedTags is null || state.Selection is null || state.Selection.Count > 100_000)
            throw new ArgumentException("Invalid view state.", nameof(state));
        if (state.Expression is not null) ValidateSearchExpression(state.Expression);
        lock (gate)
        {
            ThrowIfDisposed();
            var before = ChangeStamp();
            using (var command = Command("INSERT INTO ui_state(id,document) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET document=excluded.document", null,
                ("$id", ViewStateKey), ("$json", JsonSerializer.Serialize(state with { SavedAt = DateTimeOffset.UtcNow }, Json))))
                command.ExecuteNonQuery();
            CarryCachesAcross(before, []);
        }
    }

    /// <summary>The state with everything removed that no longer exists in the library; <paramref name="dropped"/> names it.</summary>
    public ViewState Sanitize(ViewState state, out IReadOnlyList<string> dropped)
    {
        ArgumentNullException.ThrowIfNull(state);
        var removed = new List<string>();
        var result = state;
        lock (gate)
        {
            ThrowIfDisposed();
            var facets = GetFacets();
            var preferences = ReadPreferences();
            var tags = new HashSet<string>(facets.Tags.Concat(preferences.TagVocabulary.Select(t => t.Tag)), StringComparer.OrdinalIgnoreCase);
            if (!ViewState.Views.Contains(state.View)) { removed.Add(T("View {0}", state.View)); result = result with { View = "all" }; }
            if (state.PlaylistId is { } playlist && ReadDocuments<Playlist>("playlists").All(p => p.Id != playlist))
            { removed.Add(T("Collection")); result = result with { PlaylistId = null }; }
            if (state.SavedSearchId is { } search && ReadDocuments<SavedSearch>("saved_searches").All(s => s.Id != search))
            { removed.Add(T("saved search")); result = result with { SavedSearchId = null }; }
            if (state.Tag is { } tag && !tags.Contains(tag)) { removed.Add(T("Tag “{0}”", tag)); result = result with { Tag = null }; }
            if (state.TagCategory is { } group && !facets.TagCategories.Contains(group, StringComparer.OrdinalIgnoreCase))
            { removed.Add(T("Tag category “{0}”", group)); result = result with { TagCategory = null }; }
            if (state.Category is { } category && !facets.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
            { removed.Add(T("Category “{0}”", category)); result = result with { Category = null }; }
            if (state.Folder is { } folder && !facets.Folders.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                f.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            { removed.Add(T("Folder “{0}”", folder)); result = result with { Folder = null }; }
            List<string> Known(List<string> list)
            {
                var kept = list.Where(tags.Contains).ToList();
                removed.AddRange(list.Except(kept, StringComparer.OrdinalIgnoreCase).Select(t => T("Tag “{0}”", t)));
                return kept;
            }
            result = result with { RequiredTags = Known(state.RequiredTags), ExcludedTags = Known(state.ExcludedTags) };
            List<TagFacet> KnownFacets(List<TagFacet> list)
            {
                var kept = list.Where(f => tags.Contains(f.Tag)).ToList();
                removed.AddRange(list.Except(kept).Select(f => T("“{0}”", f.Tag)));
                return kept;
            }
            result = result with { Filter = state.Filter with { Include = KnownFacets(state.Filter.Include), Exclude = KnownFacets(state.Filter.Exclude) } };
            if (state.Expression is not null)
            {
                try { ValidateSearchExpression(state.Expression); }
                catch (ArgumentException) { removed.Add("erweiterte Suche"); result = result with { Expression = null }; }
            }
            if (state.Selection.Count > 0)
            {
                var existing = new HashSet<Guid>();
                foreach (var chunk in state.Selection.Chunk(500))
                {
                    using var command = Command("SELECT id FROM media WHERE id IN (SELECT value FROM json_each($ids))", null,
                        ("$ids", JsonSerializer.Serialize(chunk.Select(id => id.ToString()))));
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) existing.Add(Guid.Parse(reader.GetString(0)));
                }
                result = result with { Selection = state.Selection.Where(existing.Contains).ToList() };
            }
            if (state.AnchorId is { } anchor && GetMedia(anchor) is null) result = result with { AnchorId = null };
            result = result with { MinimumStars = Math.Clamp(state.MinimumStars, 0, 5), Position = Math.Max(0, state.Position) };
        }
        dropped = removed;
        return result;
    }
}
