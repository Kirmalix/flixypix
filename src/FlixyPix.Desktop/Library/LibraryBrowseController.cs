using FlixyPix.Core;

namespace FlixyPix.Desktop;

internal enum LibraryViewKind { All, Video, Image, Audio, Favorites, WatchLater, Inbox, Home, Performers, Studios, ImageSets, PublicationGroups }

/// <summary>Library navigation state, independent of controls and persisted through the existing ViewState contract.</summary>
internal sealed class LibraryBrowseController
{
    private readonly Dictionary<string, BrowseState> areas = new(StringComparer.Ordinal);

    internal void RememberArea(ViewState state)
    {
        if (state.PlaylistId is null && state.SavedSearchId is null && ViewState.Views.Contains(state.View))
            areas[state.View] = new(state);
    }

    internal ViewState? RecallArea(string view) => areas.TryGetValue(view, out var state) ? state.ToViewState() : null;

    internal BrowseState State { get; private set; } = new(new ViewState());
    internal long Revision { get; private set; }
    internal void Invalidate() => ++Revision;
    internal void Change(Func<ViewState, ViewState> change)
    {
        var next = change(State.ToViewState());
        if (!State.SameView(next)) Invalidate();
        State = new(next);
    }
    internal BrowseState Accepted { get; private set; } = new(new ViewState());
    internal BrowseRequest CaptureRequest(IEnumerable<Guid> selection) => new(Revision, State, Array.AsReadOnly(selection.ToArray()));
    internal BrowseRequest BeginLoad(IEnumerable<Guid> selection) { Invalidate(); return CaptureRequest(selection); }
    internal bool IsCurrent(BrowseRequest request) => request.Revision == Revision;
    internal bool IsCurrentSelection(BrowseRequest request, IEnumerable<Guid> selection) => IsCurrent(request) && request.Selection.SequenceEqual(selection);
    internal bool TryPublish<T>(BrowseResult<T> result, Action<T> publish)
    {
        if (!IsCurrent(result.Request)) return false;
        publish(result.Value);
        return true;
    }
    internal bool TryPublishSelection<T>(BrowseResult<T> result, IEnumerable<Guid> selection, Action<T> publish)
    {
        return IsCurrentSelection(result.Request, selection) && TryPublish(result, publish);
    }
    internal Playlist? Playlist { get; private set; }
    internal void SelectPlaylist(Playlist? playlist)
    {
        Playlist = playlist is null ? null : playlist with { MediaIds = playlist.MediaIds.ToList(), Entries = playlist.Entries.ToList(), Extra = new(playlist.Extra) };
        Change(state => state with { PlaylistId = playlist?.Id });
    }
    internal int Anchor { get; set; }
    internal IReadOnlyList<MediaItem> OffWindowSelection { get; private set; } = [];
    internal void SetOffWindowSelection(IEnumerable<MediaItem> items) => OffWindowSelection = Array.AsReadOnly(items.ToArray());
    internal WorklistState Worklist { get; } = new();
    internal void SetTagMode(string tag, int mode)
    {
        if (mode is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(mode));
        static List<string> Set(List<string> tags, string tag, bool include) => include
         ? tags.Contains(tag, StringComparer.OrdinalIgnoreCase) ? tags : [.. tags, tag]
         : tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToList();
        Change(state => state with { RequiredTags = Set(state.RequiredTags, tag, mode == 1), ExcludedTags = Set(state.ExcludedTags, tag, mode == -1) });
    }
    internal void ToggleTag(string tag, bool exclude)
    {
        var active = (exclude ? State.ExcludedTags : State.RequiredTags).Contains(tag, StringComparer.OrdinalIgnoreCase);
        SetTagMode(tag, active ? 0 : exclude ? -1 : 1);
    }
    internal void ClearTags() => Change(state => state with { RequiredTags = [], ExcludedTags = [] });
    internal void RemoveIncludedTag(string tag) => Change(state => state with { RequiredTags = state.RequiredTags.Where(t => t != tag).ToList() });
    internal void RemoveExcludedTag(string tag) => Change(state => state with { ExcludedTags = state.ExcludedTags.Where(t => t != tag).ToList() });
    internal LibraryViewKind Kind => State.View switch
    {
        "Video" => LibraryViewKind.Video,
        "Image" => LibraryViewKind.Image,
        "Audio" => LibraryViewKind.Audio,
        "favorites" => LibraryViewKind.Favorites,
        "watchlater" => LibraryViewKind.WatchLater,
        "inbox" => LibraryViewKind.Inbox,
        "home" => LibraryViewKind.Home,
        "performers" => LibraryViewKind.Performers,
        "studios" => LibraryViewKind.Studios,
        "image-sets" => LibraryViewKind.ImageSets,
        "publication-groups" => LibraryViewKind.PublicationGroups,
        _ => LibraryViewKind.All,
    };

    internal ViewState Capture(ViewState state)
    {
        return BrowseState.Copy(state);
    }

    internal ViewState ApplyTransition(ViewState state)
    {
        state = ResolveTransition(state);
        Change(_ => state);
        Accepted = State;
        return state;
    }

    internal static ViewState ResolveTransition(ViewState state)
    {
        state = BrowseState.Copy(state);
        var collectionView = state.View is "image-sets" or "publication-groups";
        if (collectionView && (state.Folder is not null || state.PlaylistId is not null || state.Tag is not null ||
            state.TagCategory is not null || state.RequiredTags.Count > 0 || !state.Filter.IsEmpty || state.Expression is not null))
            state = state with { View = "all" };
        var roleView = state.View is "performers" or "studios";
        if (roleView && (state.Folder is not null || state.PlaylistId is not null || state.Tag is not null || state.TagCategory is not null ||
            state.RequiredTags.Count > 0 || state.ExcludedTags.Count > 0 || !state.Filter.IsEmpty || state.Expression is not null)) state = state with { View = "all" };
        if (state.View == "home" && (state.SearchText is { Length: > 0 } || state.Tag is not null || state.TagCategory is not null || state.Category is not null ||
            state.Folder is not null || state.PlaylistId is not null || state.Expression is not null || state.RequiredTags.Count > 0 || state.ExcludedTags.Count > 0 ||
            !state.Filter.IsEmpty || state.MinimumStars > 0)) state = state with { View = "all" };
        return state;
    }

}

internal sealed record BrowseRequest(long Revision, BrowseState State, IReadOnlyList<Guid> Selection)
{
    internal BrowseResult<T> Complete<T>(T value) => new(this, value);
}
internal sealed record BrowseResult<T>(BrowseRequest Request, T Value);
