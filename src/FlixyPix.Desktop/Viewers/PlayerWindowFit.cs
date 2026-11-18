namespace FlixyPix.Desktop;

/// <summary>Where the player window was and how large the user wanted the picture, in pixels of the screen.
/// <see cref="LongSide"/> is the longer side of the video area the user chose: the width for landscape, the height of a
/// portrait window. It carries the chosen size from one video to the next whatever their shapes.
/// The chrome (frame, control bar, mark list) is kept so the next window opens at its final size before the layout ran.</summary>
internal sealed record PlayerWindowPlace(int Left, int Top, int Width, int Height, bool Maximized, bool Fullscreen, int LongSide, int ChromeWidth, int ChromeHeight);

/// <summary>The size of the player window for a video: the picture fills the video area without bars above and below
/// for landscape up to square videos; portrait videos keep the landscape window with bars at the sides, or, when chosen,
/// get a window of their own shape. Everything stays inside the free part of the screen.</summary>
internal static class PlayerWindowFit
{
 /// <summary>Shape of the window that portrait videos keep their side bars in.</summary>
 internal const double LandscapeShape = 16d / 9;

 /// <summary>The shape (width / height) the video area takes for a video of this shape.</summary>
 internal static double Shape(double aspect, bool fitPortrait) => aspect >= 1 || fitPortrait ? aspect : LandscapeShape;

 /// <summary>Whether the window follows the picture exactly; portrait videos with side bars do not.</summary>
 internal static bool FollowsPicture(double aspect, bool fitPortrait) => aspect >= 1 || fitPortrait;

 /// <summary>The longer side of a video area of this size and shape.</summary>
 internal static int LongSide(int videoWidth, int videoHeight, double shape) => shape >= 1 ? videoWidth : videoHeight;

 /// <summary>Window size for the chosen long side of the picture: the video area has the given shape, the chrome is
 /// added around it, and the whole is shrunk evenly until it fits the area, but never below the smallest window.</summary>
 internal static (int Width, int Height) Size(int longSide, double shape, int chromeWidth, int chromeHeight, int minWidth, int minHeight, int areaWidth, int areaHeight)
 {
  if (!double.IsFinite(shape) || shape <= 0) throw new ArgumentOutOfRangeException(nameof(shape));
  double videoWidth = shape >= 1 ? longSide : longSide * shape, videoHeight = shape >= 1 ? longSide / shape : longSide;
  var room = Math.Min((areaWidth - chromeWidth) / videoWidth, (areaHeight - chromeHeight) / videoHeight);
  if (room < 1) { videoWidth *= room; videoHeight *= room; }
  var width = Math.Max(minWidth, (int)Math.Round(videoWidth) + chromeWidth); var height = Math.Max(minHeight, (int)Math.Round(videoHeight) + chromeHeight);
  return (Math.Min(width, Math.Max(minWidth, areaWidth)), Math.Min(height, Math.Max(minHeight, areaHeight)));
 }

 /// <summary>The long side that shows a picture of this size at this scale (1 = one screen pixel per video pixel) completely.</summary>
 internal static int LongSideFor(int pictureWidth, int pictureHeight, double scale, double shape)
 {
  double width = pictureWidth * scale, height = pictureHeight * scale;
  return (int)Math.Round(shape >= 1 ? Math.Max(width, height * shape) : Math.Max(height, width / shape));
 }

 /// <summary>Edges of the Windows sizing loop (WM_SIZING): which side or corner the user drags.</summary>
 internal enum Edge { Left = 1, Right, Top, TopLeft, TopRight, Bottom, BottomLeft, BottomRight }

 /// <summary>Keeps the shape of the picture while the user drags an edge: the side and the corners follow the width,
 /// the top and the bottom edge the height; the opposite edge of what moved stays where it is.</summary>
 internal static (int Left, int Top, int Right, int Bottom) Drag((int Left, int Top, int Right, int Bottom) rect, Edge edge, double shape, int chromeWidth, int chromeHeight, int minWidth, int minHeight)
 {
  var (left, top, right, bottom) = rect;
  if (edge is Edge.Top or Edge.Bottom)
  {
   var width = Math.Max(minWidth, (int)Math.Round((bottom - top - chromeHeight) * shape) + chromeWidth);
   return (left, top, left + width, bottom);
  }
  var height = Math.Max(minHeight, (int)Math.Round((right - left - chromeWidth) / shape) + chromeHeight);
  return edge is Edge.TopLeft or Edge.TopRight ? (left, bottom - height, right, bottom) : (left, top, right, top + height);
 }
}
