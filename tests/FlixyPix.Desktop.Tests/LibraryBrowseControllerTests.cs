using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Desktop.Tests;

public sealed class LibraryBrowseControllerTests
{
 [Fact]
 public void ProposedScopeDoesNotReplaceAcceptedRoleUntilTheUiConfirmsLeaving()
 {
  var controller = new LibraryBrowseController();
  controller.ApplyTransition(new() {View="performers",SearchText="Jane",Directory=new(new("Cast","Jane"))});
  controller.Change(state => state with {Folder="folder"});
  var proposed = LibraryBrowseController.ResolveTransition(controller.State.ToViewState());
  Assert.Equal("all",proposed.View);
  Assert.Equal("performers",controller.Accepted.View); Assert.Null(controller.Accepted.Folder);
  controller.ApplyTransition(controller.Accepted.ToViewState());
  Assert.Equal("performers",controller.State.View); Assert.Equal("Jane",controller.State.SearchText);
  Assert.Null(controller.State.Folder);
 }
 [Fact]
 public void PlaylistAndOffWindowSelectionDetachCallerOwnedLists()
 {
  var controller = new LibraryBrowseController(); var playlist = new Playlist {MediaIds=[Guid.NewGuid()]};
  controller.SelectPlaylist(playlist);
  playlist.MediaIds.Clear();
  Assert.Single(controller.Playlist!.MediaIds); Assert.Equal(playlist.Id,controller.State.PlaylistId);
  var items = new List<MediaItem> {new() {Path="synthetic.jpg"}};
  controller.SetOffWindowSelection(items); items.Clear();
  Assert.Single(controller.OffWindowSelection);
  controller.SelectPlaylist(null); Assert.Null(controller.State.PlaylistId);
 }
 [Theory]
 [InlineData("home",true)]
 [InlineData("performers",false)]
 [InlineData("studios",false)]
 [InlineData("image-sets",false)]
 [InlineData("publication-groups",false)]
 public void MediaScopeTransitionsAreResolvedBeforeAnyLoad(string view,bool searchLeaves)
 {
  Assert.Equal("all",LibraryBrowseController.ResolveTransition(new() {View=view,Folder="folder"}).View);
  Assert.Equal(searchLeaves?"all":view,LibraryBrowseController.ResolveTransition(new() {View=view,SearchText="search"}).View);
 }
 [Fact]
 public void OnlyCurrentViewAndSelectionMayPublishResults()
 {
  var controller = new LibraryBrowseController(); var selected = Guid.NewGuid();
  var first = controller.BeginLoad([selected]);
  var second = controller.BeginLoad([selected]);
  var applied = new List<string>();
  Assert.False(controller.TryPublish(first.Complete("old"),applied.Add));
  Assert.False(controller.TryPublishSelection(second.Complete("wrong selection"),[Guid.NewGuid()],applied.Add));
  Assert.True(controller.TryPublishSelection(second.Complete("current"),[selected],applied.Add));
  Assert.Equal(["current"],applied);
  controller.SetTagMode("filter",1);
  Assert.False(controller.TryPublish(second.Complete("stale after filter"),applied.Add));
 }
 [Fact]
 public void CapturedRequestsKeepDetachedFilterAndSelectionInputs()
 {
  var controller = new LibraryBrowseController(); var selection = new List<Guid> {Guid.NewGuid()};
  controller.ApplyTransition(new() {Filter=new() {VideoCodecs=["h264"]}});
  var request = controller.CaptureRequest(selection);
  selection.Clear(); request.State.Filter.VideoCodecs.Clear();
  Assert.Single(request.Selection); Assert.Equal(["h264"],request.State.Filter.VideoCodecs);
 }
 [Fact]
 public void RuntimeStateCannotBeMutatedThroughInputsOrPersistenceSnapshots()
 {
  var source = new ViewState {RequiredTags=["First"],Filter=new() {VideoCodecs=["h264"]}};
  var controller = new LibraryBrowseController();
  controller.ApplyTransition(source);
  source.RequiredTags.Clear(); source.Filter.VideoCodecs.Clear();
  Assert.Equal(["First"],controller.State.RequiredTags);
  Assert.Throws<NotSupportedException>(() => ((IList<string>)controller.State.RequiredTags).Clear());
  controller.State.Filter.VideoCodecs.Clear();
  Assert.Equal(["h264"],controller.State.Filter.VideoCodecs);
 }
 [Fact]
 public void TagTransitionsPreserveOrderAndCasingAndExcludeConflictingMode()
 {
  var controller = new LibraryBrowseController();
  controller.ApplyTransition(new() {RequiredTags=["First","Second"]});
  controller.SetTagMode("FIRST",1);
  Assert.Equal(["First","Second"],controller.State.RequiredTags);
  controller.SetTagMode("FIRST",-1);
  Assert.Equal(["Second"],controller.State.RequiredTags);
  Assert.Equal(["FIRST"],controller.State.ExcludedTags);
  controller.ToggleTag("first",true);
  Assert.Empty(controller.State.ExcludedTags);
 }
 [Theory]
 [InlineData("image-sets")]
 [InlineData("publication-groups")]
 public void RestoreAndLiveNavigationResolveMediaFiltersTheSameWay(string view)
 {
  var controller = new LibraryBrowseController();
  var state = new ViewState {View=view,Folder="folder",SearchText="keep search",Selection=[Guid.NewGuid()]};
  var restored = controller.ApplyTransition(state);
  var live = controller.ApplyTransition(controller.Capture(state));
  Assert.Equal("all",restored.View);Assert.True(restored.SameView(live));
  Assert.Equal(state.SearchText,live.SearchText);Assert.Equal(state.Selection,live.Selection);
 }
 [Fact]
 public void ApplyingAStoredViewDoesNotMutateItsFilterLists()
 {
  var controller = new LibraryBrowseController();
  var saved = new ViewState {RequiredTags=["Keep"],ExcludedTags=["Exclude"],ReviewStates=[ReviewState.Reviewed],Filter=new() {VideoCodecs=["h264"]}};
  controller.ApplyTransition(saved);
  var detached = controller.State.ToViewState();
  detached.RequiredTags.Clear(); detached.ExcludedTags.Clear(); detached.ReviewStates.Clear();
  Assert.Equal(["Keep"],controller.State.RequiredTags);
  Assert.Equal(["Keep"],saved.RequiredTags);Assert.Equal(["Exclude"],saved.ExcludedTags);
  Assert.Equal([ReviewState.Reviewed],saved.ReviewStates);
  controller.State.Filter.VideoCodecs.Clear();Assert.Equal(["h264"],saved.Filter.VideoCodecs);
 }
 [Fact]
 public void CaptureOfVisiblePlaceDoesNotUndoRequestedScrollReset()
 {
  var controller = new LibraryBrowseController {Anchor=0};
  var captured = controller.Capture(new() {View="Video",Position=800});
  Assert.Equal(0,controller.Anchor);Assert.Equal(LibraryViewKind.All,controller.Kind);
  Assert.Equal(0,controller.State.Position);
  Assert.Equal("Video",captured.View); Assert.Equal(800,captured.Position);
 }
}
