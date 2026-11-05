using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Desktop.Tests;

public sealed class LibraryBrowseControllerTests
{
 [Theory]
 [InlineData("image-sets")]
 [InlineData("publication-groups")]
 public void RestoreAndLiveNavigationResolveMediaFiltersTheSameWay(string view)
 {
  var controller = new LibraryBrowseController();
  var state = new ViewState {View=view,Folder="folder",SearchText="keep search",Selection=[Guid.NewGuid()]};
  var restored = controller.ResolveForLoad(state);
  var live = controller.ResolveForLoad(controller.Capture(state));
  Assert.Equal("all",restored.View);Assert.True(restored.SameView(live));
  Assert.Equal(state.SearchText,live.SearchText);Assert.Equal(state.Selection,live.Selection);
 }
 [Fact]
 public void ApplyingAStoredViewDoesNotMutateItsFilterLists()
 {
  var controller = new LibraryBrowseController();
  var saved = new ViewState {RequiredTags=["Keep"],ExcludedTags=["Exclude"],ReviewStates=[ReviewState.Reviewed],Filter=new() {VideoCodecs=["h264"]}};
  controller.ResolveForLoad(saved);
  controller.State.RequiredTags.Clear(); controller.State.ExcludedTags.Clear(); controller.State.ReviewStates.Clear();
  Assert.Equal(["Keep"],saved.RequiredTags);Assert.Equal(["Exclude"],saved.ExcludedTags);
  Assert.Equal([ReviewState.Reviewed],saved.ReviewStates);
  controller.State.Filter.VideoCodecs.Clear();Assert.Equal(["h264"],saved.Filter.VideoCodecs);
 }
 [Fact]
 public void CaptureOfVisiblePlaceDoesNotUndoRequestedScrollReset()
 {
  var controller = new LibraryBrowseController {Anchor=0};
  controller.Capture(new() {View="Video",Position=800});
  Assert.Equal(0,controller.Anchor);Assert.Equal(LibraryViewKind.Video,controller.Kind);
  Assert.Equal(800,controller.State.Position);
 }
}
