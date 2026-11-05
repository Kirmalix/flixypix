using FlixyPix.Core;

namespace FlixyPix.Desktop;

internal enum LibraryViewKind { All, Video, Image, Audio, Favorites, WatchLater, Inbox, Home, Performers, Studios, ImageSets, PublicationGroups }

/// <summary>Library navigation state, independent of controls and persisted through the existing ViewState contract.</summary>
internal sealed class LibraryBrowseController
{
 internal ViewState State { get; set; } = new();
 internal Playlist? Playlist { get; set; }
 internal int Anchor { get; set; }
 internal List<MediaItem> OffWindowSelection { get; set; } = [];
 internal WorklistState Worklist { get; } = new();
 internal LibraryViewKind Kind => State.View switch
 {
  "Video" => LibraryViewKind.Video, "Image" => LibraryViewKind.Image, "Audio" => LibraryViewKind.Audio,
  "favorites" => LibraryViewKind.Favorites, "watchlater" => LibraryViewKind.WatchLater,
  "inbox" => LibraryViewKind.Inbox, "home" => LibraryViewKind.Home,
  "performers" => LibraryViewKind.Performers, "studios" => LibraryViewKind.Studios,
  "image-sets" => LibraryViewKind.ImageSets, "publication-groups" => LibraryViewKind.PublicationGroups,
  _ => LibraryViewKind.All,
 };

 internal ViewState Capture(ViewState state)
 {
  state = Copy(state);
  State = Copy(state);
  return state;
 }

 internal ViewState ResolveForLoad(ViewState state)
 {
  state = Copy(state);
  var collectionView = state.View is "image-sets" or "publication-groups";
  if (collectionView && (state.Folder is not null || state.PlaylistId is not null || state.Tag is not null ||
      state.TagCategory is not null || state.RequiredTags.Count > 0 || !state.Filter.IsEmpty || state.Expression is not null))
   state = state with {View="all"};
  State = Copy(state);
  return state;
 }

 private static ViewState Copy(ViewState state) => state with
 {
  ReviewStates=state.ReviewStates.ToList(), RequiredTags=state.RequiredTags.ToList(),
  ExcludedTags=state.ExcludedTags.ToList(), Selection=state.Selection.ToList(),
  Expression=CopyExpression(state.Expression),
  Filter=state.Filter with
  {
   Include=state.Filter.Include.ToList(), Exclude=state.Filter.Exclude.ToList(), AnyOf=state.Filter.AnyOf.ToList(),
   Profiles=state.Filter.Profiles.ToList(), MarkTags=state.Filter.MarkTags.ToList(), VideoCodecs=state.Filter.VideoCodecs.ToList(),
  },
 };
 private static SearchExpression? CopyExpression(SearchExpression? expression) => expression is null ? null :
  expression with {Children=expression.Children.Select(child => CopyExpression(child)!).ToList()};
}
