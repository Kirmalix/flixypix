using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>An immutable runtime snapshot; mutable persistence DTOs never escape into its state.</summary>
internal sealed class BrowseState
{
    private readonly ViewState value;
    internal BrowseState(ViewState source)
    {
        value = Copy(source);
        ReviewStates = value.ReviewStates.AsReadOnly();
        RequiredTags = value.RequiredTags.AsReadOnly();
        ExcludedTags = value.ExcludedTags.AsReadOnly();
        Selection = value.Selection.AsReadOnly();
    }
    internal string View => value.View;
    internal MediaKind? KindFilter => value.KindFilter;
    internal Guid? DetailId => value.DetailId;
    internal IReadOnlyList<ReviewState> ReviewStates { get; }
    internal DirectoryViewState? Directory => value.Directory;
    internal Guid? ImageSetId => value.ImageSetId;
    internal Guid? PublicationGroupId => value.PublicationGroupId;
    internal Guid? PlaylistId => value.PlaylistId;
    internal Guid? SavedSearchId => value.SavedSearchId;
    internal string? SearchText => value.SearchText;
    internal string? Tag => value.Tag;
    internal string? TagCategory => value.TagCategory;
    internal string? Category => value.Category;
    internal string? Folder => value.Folder;
    internal bool IncludeSubfolders => value.IncludeSubfolders;
    internal IReadOnlyList<string> RequiredTags { get; }
    internal IReadOnlyList<string> ExcludedTags { get; }
    internal SearchExpression? Expression => CopyExpression(value.Expression);
    internal MediaFilter Filter => CopyFilter(value.Filter);
    internal bool TagsMatchAny => value.TagsMatchAny;
    internal int MinimumStars => value.MinimumStars;
    internal int SortOrder => value.SortOrder;
    internal int ViewMode => value.ViewMode;
    internal int TileWidth => value.TileWidth;
    internal int Position => value.Position;
    internal Guid? AnchorId => value.AnchorId;
    internal IReadOnlyList<Guid> Selection { get; }
    internal Guid? LastPlayedId => value.LastPlayedId;
    internal DateTimeOffset SavedAt => value.SavedAt;
    internal ViewState ToViewState() => Copy(value);
    internal bool SameView(ViewState other) => value.SameView(other);
    internal static ViewState Copy(ViewState state) => state with
    {
        ReviewStates = state.ReviewStates.ToList(),
        RequiredTags = state.RequiredTags.ToList(),
        ExcludedTags = state.ExcludedTags.ToList(),
        Selection = state.Selection.ToList(),
        Filter = CopyFilter(state.Filter),
        Expression = CopyExpression(state.Expression),
    };
    private static MediaFilter CopyFilter(MediaFilter filter) => filter with
    {
        Include = filter.Include.ToList(),
        Exclude = filter.Exclude.ToList(),
        AnyOf = filter.AnyOf.ToList(),
        Profiles = filter.Profiles.ToList(),
        MarkTags = filter.MarkTags.ToList(),
        VideoCodecs = filter.VideoCodecs.ToList(),
    };
    private static SearchExpression? CopyExpression(SearchExpression? expression) => expression is null ? null :
     expression with { Children = expression.Children.Select(child => CopyExpression(child)!).ToList() };
}
