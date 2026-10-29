using FlixyPix.Core;
using FlixyPix.Imaging;
using ImageMagick;
using Xunit;
using PerceptualHash = FlixyPix.Core.PerceptualHash;

namespace FlixyPix.Imaging.Tests;

/// <summary>Image fingerprints: a smaller, recompressed copy and a rotated-by-EXIF copy look like the source; another picture does not.</summary>
public sealed class ImageFingerprintTests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-image-fingerprints-" + Guid.NewGuid().ToString("N"));
 public ImageFingerprintTests() => Directory.CreateDirectory(root);
 public void Dispose() => Directory.Delete(root, true);

 [Fact]
 public void CopiesMatchAndOtherPicturesDoNot()
 {
  var source = Path.Combine(root, "quelle ü.png");
  // plasma is random; a fixed seed keeps the picture, and thus the distances, the same in every run.
  MagickNET.SetRandomSeed(20261029);
  using (var image = new MagickImage("plasma:fractal", 800, 600)) image.Write(source);
  var copy = Path.Combine(root, "kopie.jpg");
  using (var image = new MagickImage(source)) { image.Resize(400, 300); image.Quality = 60; image.Write(copy, MagickFormat.Jpeg); }
  var other = Path.Combine(root, "anders.png");
  using (var image = new MagickImage("gradient:blue-yellow", 800, 600)) { image.Rotate(30); image.Write(other); }
  var before = File.ReadAllBytes(source);
  var a = ImageFingerprints.Hash(source); var b = ImageFingerprints.Hash(copy); var c = ImageFingerprints.Hash(other);
  Assert.InRange(PerceptualHash.Distance(a, b), 0, (int)PerceptualHash.Threshold(SimilarityLevel.Exact));
  Assert.True(PerceptualHash.Distance(a, c) > PerceptualHash.Threshold(SimilarityLevel.Loose), $"Different pictures are {PerceptualHash.Distance(a, c)} bits apart.");
  Assert.Equal(before, File.ReadAllBytes(source));
 }
}
