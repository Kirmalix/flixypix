using FlixyPix.Core;
using ImageMagick;
using ImageMagick.Formats;
using PerceptualHash = FlixyPix.Core.PerceptualHash;

namespace FlixyPix.Imaging;

/// <summary>The 32 × 32 greyscale picture of an image file for its perceptual hash (<see cref="PerceptualHash"/>); the first
/// frame of animations and multi-page files counts.</summary>
public static class ImageFingerprints
{
 public static ulong Hash(string path)
 {
  using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
  var settings = ImageService.Settings(stream); settings.FrameIndex = 0; settings.FrameCount = 1;
  // JPEG decodes straight at a small size, which saves most of the work for large photos.
  settings.SetDefines(new JpegReadDefines { Size = new MagickGeometry(128, 128) });
  using var image = new MagickImage(stream, settings);
  image.AutoOrient();
  image.Grayscale(PixelIntensityMethod.Rec601Luma);
  image.Resize(new MagickGeometry(PerceptualHash.Size, PerceptualHash.Size) { IgnoreAspectRatio = true });
  image.Depth = 8;
  return PerceptualHash.FromGray(image.ToByteArray(MagickFormat.Gray));
 }
}
