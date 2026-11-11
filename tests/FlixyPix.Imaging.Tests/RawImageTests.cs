using FlixyPix.Core;
using FlixyPix.Imaging;
using ImageMagick;
using Xunit;
using PerceptualHash = FlixyPix.Core.PerceptualHash;

namespace FlixyPix.Imaging.Tests;

/// <summary>Camera RAW files, on small DNG files written for the test: a Bayer mosaic of a gradient with, as cameras do, a
/// JPEG preview inside. The preview is blue with a white corner, the mosaic is not, so each test sees which one it got.</summary>
public sealed class RawImageTests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-raw-" + Guid.NewGuid().ToString("N"));
 public RawImageTests() => Directory.CreateDirectory(root);
 public void Dispose() => Directory.Delete(root, true);

 private string Raw(string name = "shot.dng", int previewWidth = 1200, int orientation = 1, string? xmp = null)
 {
  var path = Path.Combine(root, name);
  SyntheticDng.Write(path, previewWidth: previewWidth, orientation: orientation, xmp: xmp);
  return path;
 }
 private ImageService Service() => new(Path.Combine(root, "cache"));
 private static IMagickColor<ushort> Pixel(byte[] png, int x, int y) { using var image = new MagickImage(png); return image.GetPixels().GetPixel(x, y).ToColor()!; }
 private static bool IsWhite(IMagickColor<ushort> color) => color.R > 60000 && color.G > 60000 && color.B > 60000;

 [Fact]
 public async Task ARawFileIsShownByThePreviewOfTheCamera()
 {
  var service = Service(); var path = Raw();
  var info = await service.GetInfoAsync(path);
  Assert.True(info.IsRaw); Assert.Equal("RAW", info.Format); Assert.Equal((96, 64), (info.Width, info.Height));
  Assert.Equal((1200, 800), (info.PreviewWidth, info.PreviewHeight)); Assert.True(info.ShownByPreview);
  Assert.Equal("FlixyPix Test Camera", info.Camera);
  var shown = await service.DecodeAsync(path);
  Assert.Equal((1200, 800), (shown.Width, shown.Height));
  Assert.True(IsWhite(Pixel(shown.PngBytes, 10, 10)), "The picture is not the camera's preview.");
  var small = await service.DecodeAsync(path, maxWidth: 390, maxHeight: 390);
  Assert.Equal((390, 260), (small.Width, small.Height)); Assert.True(IsWhite(Pixel(small.PngBytes, 5, 5)));
 }

 [Fact]
 public async Task DevelopingShowsTheSensorDataInstead()
 {
  var service = Service(); var path = Raw();
  var developed = await service.DecodeAsync(path, develop: true);
  Assert.Equal((96, 64), (developed.Width, developed.Height));
  var left = Pixel(developed.PngBytes, 4, 4); var right = Pixel(developed.PngBytes, 91, 4);
  Assert.False(IsWhite(left)); Assert.True(right.R > left.R + 20000, "The mosaic's red does not grow to the right.");
  // The two ways are kept apart in the cache.
  Assert.True(IsWhite(Pixel((await service.DecodeAsync(path)).PngBytes, 10, 10)));
  Assert.False(IsWhite(Pixel((await service.DecodeAsync(path, develop: true)).PngBytes, 4, 4)));
 }

 [Theory]
 [InlineData(0)] [InlineData(96)]
 public async Task WithoutAUsablePreviewTheSensorDataIsDeveloped(int previewWidth)
 {
  var service = Service(); var path = Raw(previewWidth: previewWidth);
  var info = await service.GetInfoAsync(path);
  Assert.True(info.IsRaw); Assert.False(info.ShownByPreview);
  var shown = await service.DecodeAsync(path, maxWidth: 390, maxHeight: 390);
  Assert.Equal((96, 64), (shown.Width, shown.Height)); Assert.False(IsWhite(Pixel(shown.PngBytes, 4, 4)));
 }

 [Fact]
 public async Task AShotTakenUprightIsTurnedOnBothWays()
 {
  var service = Service(); var path = Raw(orientation: 6);
  var info = await service.GetInfoAsync(path);
  Assert.Equal((64, 96), (info.Width, info.Height)); Assert.Equal((800, 1200), (info.PreviewWidth, info.PreviewHeight));
  var shown = await service.DecodeAsync(path);
  Assert.Equal((800, 1200), (shown.Width, shown.Height));
  // Turned to the right, the white corner of the preview is top right.
  Assert.True(IsWhite(Pixel(shown.PngBytes, 790, 10))); Assert.False(IsWhite(Pixel(shown.PngBytes, 10, 10)));
  var developed = await service.DecodeAsync(path, develop: true);
  Assert.Equal((64, 96), (developed.Width, developed.Height));
  var preview = RawImages.ReadPreview(path)!;
  Assert.Equal(6, preview.Orientation); Assert.True(preview.Turned); Assert.Equal((1200, 800, 96, 64), (preview.Width, preview.Height, preview.RawWidth, preview.RawHeight));
 }

 [Fact]
 public async Task ARawFileIsToldByItsNameAndMustBeOne()
 {
  var service = Service();
  // Read by its first bytes this file would be a TIFF whose first image is the preview; as RAW it is the sensor image.
  Assert.Equal((96, 64), ((await service.GetInfoAsync(Raw())).Width, (await service.GetInfoAsync(Raw())).Height));
  var text = Path.Combine(root, "notes.cr2"); File.WriteAllText(text, "This is no picture at all, only a few words.");
  await Assert.ThrowsAsync<InvalidDataException>(() => service.GetInfoAsync(text));
  using (var jpeg = new MagickImage(MagickColors.Red, 8, 8)) jpeg.Write(Path.Combine(root, "picture.nef"), MagickFormat.Jpeg);
  await Assert.ThrowsAsync<InvalidDataException>(() => service.GetInfoAsync(Path.Combine(root, "picture.nef")));
 }

 [Fact]
 public async Task AChangedRawFileIsReadAnew()
 {
  var service = Service(); var path = Raw(previewWidth: 1200);
  Assert.True((await service.GetInfoAsync(path)).ShownByPreview);
  SyntheticDng.Write(path, previewWidth: 0); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
  Assert.False((await service.GetInfoAsync(path)).ShownByPreview);
 }

 [Fact]
 public void MetadataComesFromTheXmpInsideAndTheCaptureDateFromTheCamera()
 {
  var path = Raw(xmp: "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:xmp='http://ns.adobe.com/xap/1.0/' xmlns:dc='http://purl.org/dc/elements/1.1/' xmp:Rating='4'><dc:subject><rdf:Bag><rdf:li>Harbour</rdf:li></rdf:Bag></dc:subject></rdf:Description></rdf:RDF></x:xmpmeta>");
  var metadata = new ImageMetadataReader().Read(path, MediaKind.Image)!;
  Assert.Equal(4, metadata.Stars); Assert.Contains("Harbour", metadata.Keywords);
  // "2024:05:17 14:03:21" on the camera's clock.
  Assert.Equal(new DateTime(2024, 5, 17, 14, 3, 21), metadata.TakenAt!.Value.LocalDateTime);
 }

 [Fact]
 public void ARawFileIsComparedByThePictureItShows()
 {
  var raw = Raw(); var preview = Path.Combine(root, "preview.jpg");
  File.WriteAllBytes(preview, RawImages.ReadPreview(raw)!.Jpeg);
  Assert.True(PerceptualHash.Distance(ImageFingerprints.Hash(raw), ImageFingerprints.Hash(preview)) <= 2);
  // Without a preview the sensor data is compared, which is another picture.
  Assert.True(PerceptualHash.Distance(ImageFingerprints.Hash(Raw("bare.dng", previewWidth: 0)), ImageFingerprints.Hash(preview)) > 6);
 }

 [Theory]
 [InlineData("NIKON CORPORATION", "NIKON D750", "NIKON D750")]
 [InlineData("Canon", "Canon EOS R6", "Canon EOS R6")]
 [InlineData("SONY", "ILCE-7M3", "Sony ILCE-7M3")]
 [InlineData("Apple", "iPhone 12 Pro", "Apple iPhone 12 Pro")]
 [InlineData(null, "X-T3", "X-T3")]
 public void TheCameraIsNamedOnceWithItsMaker(string? make, string model, string expected) => Assert.Equal(expected, RawImages.Camera(make, model));
}
