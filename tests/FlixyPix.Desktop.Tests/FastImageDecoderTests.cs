using System.IO;
using System.Windows.Media.Imaging;
using ImageMagick;
using ImageMagick.Drawing;
using Xunit;

namespace FlixyPix.Desktop.Tests;

/// <summary>The Windows decoder must show an image exactly as the ImageMagick way does, or leave it to ImageMagick.</summary>
public sealed class FastImageDecoderTests : IDisposable
{
 private readonly string folder = Path.Combine(Path.GetTempPath(), "flixypix-fast-image-" + Guid.NewGuid().ToString("N"));
 public FastImageDecoderTests() => Directory.CreateDirectory(folder);
 public void Dispose() => Directory.Delete(folder, true);

 /// <summary>Four coloured quarters, so every turn and mirror shows.</summary>
 private static MagickImage Quarters()
 {
  var image = new MagickImage(MagickColors.White, 64, 32);
  new Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, 31, 15).FillColor(MagickColors.Lime).Rectangle(32, 0, 63, 15)
   .FillColor(MagickColors.Blue).Rectangle(0, 16, 31, 31).Draw(image);
  return image;
 }

 private static (byte R, byte G, byte B) Fast(BitmapSource bitmap, double x, double y)
 {
  var pixel = new byte[4];
  bitmap.CopyPixels(new System.Windows.Int32Rect((int)(x * (bitmap.PixelWidth - 1)), (int)(y * (bitmap.PixelHeight - 1)), 1, 1), pixel, 4, 0);
  return (pixel[2], pixel[1], pixel[0]);
 }

 private static (byte R, byte G, byte B) Magick(MagickImage image, double x, double y)
 {
  var color = image.GetPixels().GetPixel((int)(x * (image.Width - 1)), (int)(y * (image.Height - 1))).ToColor()!;
  return ((byte)(color.R / 257), (byte)(color.G / 257), (byte)(color.B / 257));
 }

 private static void Close((byte R, byte G, byte B) expected, (byte R, byte G, byte B) actual, int tolerance = 24) =>
  Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance, $"expected {expected}, got {actual}");

 [Theory]
 [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
 public void ExifOrientationMatchesImageMagick(int orientation)
 {
  var path = Path.Combine(folder, $"turned-{orientation}.jpg");
  using (var image = Quarters())
  {
   image.Orientation = (ImageMagick.Orientation)orientation;
   var exif = new ExifProfile(); exif.SetValue(ExifTag.Orientation, (ushort)orientation); image.SetProfile(exif);
   image.Quality = 100; image.Write(path, MagickFormat.Jpeg);
  }
  var fast = FastImageDecoder.Decode(path, 0, default) ?? throw new Xunit.Sdk.XunitException("The Windows decoder left a plain JPEG.");
  using var reference = new MagickImage(path); reference.AutoOrient();
  Assert.Equal(((int)reference.Width, (int)reference.Height), (fast.Bitmap.PixelWidth, fast.Bitmap.PixelHeight));
  Assert.Equal(((int)reference.Width, (int)reference.Height), (fast.Info.Width, fast.Info.Height));
  foreach (var (x, y) in new[] { (.25, .25), (.75, .25), (.25, .75), (.75, .75) }) Close(Magick(reference, x, y), Fast(fast.Bitmap, x, y));
 }

 [Fact]
 public void EmbeddedColourProfileIsConvertedToSrgb()
 {
  var path = Path.Combine(folder, "adobe.jpg");
  using (var image = new MagickImage(new MagickColor("#DC2828"), 32, 32))
  {
   image.SetProfile(ColorProfiles.SRGB); image.TransformColorSpace(ColorProfiles.SRGB, ColorProfiles.AdobeRGB1998);
   image.Quality = 100; image.Write(path, MagickFormat.Jpeg);
  }
  var fast = FastImageDecoder.Decode(path, 0, default)!;
  Close((220, 40, 40), Fast(fast.Bitmap, .5, .5), 10);
 }

 [Fact]
 public void LargeImageIsScaledWhileReading()
 {
  var path = Path.Combine(folder, "large.jpg");
  using (var image = new MagickImage(MagickColors.Gray, 4000, 3000)) { image.Quality = 90; image.Write(path, MagickFormat.Jpeg); }
  var fast = FastImageDecoder.Decode(path, 1600, default)!;
  Assert.Equal((1600, 1200), (fast.Bitmap.PixelWidth, fast.Bitmap.PixelHeight));
  Assert.Equal((4000, 3000), (fast.Info.Width, fast.Info.Height));
 }

 [Fact]
 public void AnimationsCmykAndOtherFormatsStayWithImageMagick()
 {
  var gif = Path.Combine(folder, "moving.gif");
  using (var frames = new MagickImageCollection())
  {
   frames.Add(new MagickImage(MagickColors.Red, 8, 8) { AnimationDelay = 10 }); frames.Add(new MagickImage(MagickColors.Blue, 8, 8) { AnimationDelay = 10 });
   frames.Write(gif, MagickFormat.Gif);
  }
  // ImageMagick writes APNG only through FFmpeg; an acTL chunk after the header is what marks one.
  var apng = Path.Combine(folder, "moving.png");
  using (var image = new MagickImage(MagickColors.Red, 8, 8))
  {
   var png = image.ToByteArray(MagickFormat.Png); var header = 8 + 8 + 13 + 4;
   byte[] animation = [0, 0, 0, 8, (byte)'a', (byte)'c', (byte)'T', (byte)'L', 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0];
   File.WriteAllBytes(apng, [.. png[..header], .. animation, .. png[header..]]);
  }
  var cmyk = Path.Combine(folder, "print.jpg");
  using (var image = new MagickImage(MagickColors.Red, 8, 8)) { image.ColorSpace = ColorSpace.CMYK; image.Write(cmyk, MagickFormat.Jpeg); }
  var webp = Path.Combine(folder, "other.webp");
  using (var image = new MagickImage(MagickColors.Red, 8, 8)) image.Write(webp, MagickFormat.WebP);
  var broken = Path.Combine(folder, "broken.jpg"); File.WriteAllBytes(broken, [0xff, 0xd8, 0xff, 0xe0, 1, 2, 3]);
  foreach (var path in new[] { gif, apng, cmyk, webp, broken }) Assert.True(FastImageDecoder.Decode(path, 0, default) == null, Path.GetFileName(path) + " should stay with ImageMagick.");
  var still = Path.Combine(folder, "still.png");
  using (var image = new MagickImage(MagickColors.Red, 8, 8)) image.Write(still, MagickFormat.Png);
  Assert.NotNull(FastImageDecoder.Decode(still, 0, default));
 }
}
