using Xunit;
using Edge = FlixyPix.Desktop.PlayerWindowFit.Edge;

namespace FlixyPix.Desktop.Tests;

/// <summary>The player window follows the picture: no bars above and below for landscape up to square, side bars or a
/// window of their own for portrait videos, always inside the screen and never below the smallest window.</summary>
public sealed class PlayerWindowFitTests
{
 private const int ChromeWidth = 16, ChromeHeight = 140, MinWidth = 650, MinHeight = 460, AreaWidth = 2560, AreaHeight = 1400;

 private static (int Width, int Height) Size(int longSide, double aspect, bool fitPortrait = false, int areaHeight = AreaHeight) =>
  PlayerWindowFit.Size(longSide, PlayerWindowFit.Shape(aspect, fitPortrait), ChromeWidth, ChromeHeight, MinWidth, MinHeight, AreaWidth, areaHeight);

 [Theory]
 [InlineData(16.0 / 9, 1200, 675)]
 [InlineData(4.0 / 3, 1200, 900)]
 [InlineData(1.0, 1200, 1200)]
 [InlineData(2.39, 1200, 502)]
 public void LandscapeUpToSquareFillsTheVideoArea(double aspect, int videoWidth, int videoHeight) =>
  Assert.Equal((videoWidth + ChromeWidth, videoHeight + ChromeHeight), Size(1200, aspect));

 [Fact]
 public void PortraitKeepsTheLandscapeWindowWithSideBars()
 {
  Assert.Equal(Size(1280, 16.0 / 9), Size(1280, 9.0 / 16));
  Assert.Equal(Size(1280, 16.0 / 9), Size(1280, 4.0 / 5));
  Assert.False(PlayerWindowFit.FollowsPicture(9.0 / 16, fitPortrait: false));
 }

 [Fact]
 public void PortraitWindowOfItsOwnKeepsTheLongSide()
 {
  // 9:16 at a long side of 1080: a picture 608 wide and 1080 high, the window at least as wide as its controls.
  Assert.Equal((MinWidth, 1080 + ChromeHeight), Size(1080, 9.0 / 16, fitPortrait: true));
  Assert.Equal((1000 + ChromeWidth, 1250 + ChromeHeight), Size(1250, 4.0 / 5, fitPortrait: true));
  Assert.True(PlayerWindowFit.FollowsPicture(9.0 / 16, fitPortrait: true));
 }

 [Fact]
 public void TooHighShrinksEvenlyIntoTheScreen()
 {
  // A square picture 1280 wide does not fit 1000 pixels of height: it shrinks to 860 square.
  Assert.Equal((860 + ChromeWidth, 1000), Size(1280, 1, areaHeight: 1000));
 }

 [Fact]
 public void NeverBelowTheSmallestWindow() => Assert.Equal((MinWidth, MinHeight), Size(200, 16.0 / 9));

 [Theory]
 [InlineData(1920, 1080, 1.0, 16.0 / 9, 1920)]
 [InlineData(1920, 1080, 0.5, 16.0 / 9, 960)]
 [InlineData(1080, 1920, 1.0, 16.0 / 9, 3413)]
 [InlineData(1080, 1920, 1.0, 9.0 / 16, 1920)]
 public void ScaledPictureFitsCompletely(int width, int height, double scale, double shape, int longSide) =>
  Assert.Equal(longSide, PlayerWindowFit.LongSideFor(width, height, scale, shape));

 [Fact]
 public void DraggingKeepsTheShape()
 {
  var shape = 16.0 / 9; var rect = (Left: 100, Top: 100, Right: 100 + 1296, Bottom: 100 + 860);
  // Wider at the right edge: the bottom follows.
  Assert.Equal((100, 100, 1716, 100 + 900 + ChromeHeight), PlayerWindowFit.Drag((100, 100, 1716, 960), Edge.Right, shape, ChromeWidth, ChromeHeight, MinWidth, MinHeight));
  // Higher at the bottom edge: the right edge follows.
  Assert.Equal((100, 100, 100 + 1600 + ChromeWidth, 100 + 900 + ChromeHeight), PlayerWindowFit.Drag((100, 100, rect.Right, 100 + 900 + ChromeHeight), Edge.Bottom, shape, ChromeWidth, ChromeHeight, MinWidth, MinHeight));
  // At a top corner the bottom stays where it is.
  Assert.Equal((100, 960 - 900 - ChromeHeight, 1716, 960), PlayerWindowFit.Drag((100, 50, 1716, 960), Edge.TopLeft, shape, ChromeWidth, ChromeHeight, MinWidth, MinHeight));
 }

 [Fact]
 public void LongSideIsTheWidthForLandscapeAndTheHeightForPortrait()
 {
  Assert.Equal(1280, PlayerWindowFit.LongSide(1280, 720, 16.0 / 9));
  Assert.Equal(1080, PlayerWindowFit.LongSide(608, 1080, 9.0 / 16));
 }
}
