using System.Globalization;
using FlixyPix.Imaging;
using ImageMagick;
using Xunit;

namespace FlixyPix.Imaging.Tests;

/// <summary>Camera, lens and exposure of a shot, read from a picture's EXIF block and from a camera RAW file.</summary>
public sealed class CaptureFactsTests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-capture-" + Guid.NewGuid().ToString("N"));
 private readonly CultureInfo culture = CultureInfo.CurrentCulture;
 public CaptureFactsTests() { Directory.CreateDirectory(root); CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; }
 public void Dispose() { CultureInfo.CurrentCulture = culture; Directory.Delete(root, true); }

 [Fact]
 public void APictureTellsCameraLensAndExposure()
 {
  var path = Path.Combine(root, "shot.jpg");
  using (var image = new MagickImage(MagickColors.SteelBlue, 64, 48))
  {
   var exif = new ExifProfile();
   exif.SetValue(ExifTag.Make, "NIKON CORPORATION"); exif.SetValue(ExifTag.Model, "NIKON D750"); exif.SetValue(ExifTag.LensModel, "24.0-70.0 mm f/2.8");
   exif.SetValue(ExifTag.FocalLength, new Rational(50, 1)); exif.SetValue(ExifTag.FNumber, new Rational(40, 10));
   exif.SetValue(ExifTag.ExposureTime, new Rational(1, 250)); exif.SetValue(ExifTag.ISOSpeedRatings, [(ushort)400]);
   image.SetProfile(exif); image.Write(path, MagickFormat.Jpeg);
  }
  var data = CaptureFacts.Read(path);
  Assert.NotNull(data);
  Assert.Equal(("NIKON D750", "24.0-70.0 mm f/2.8", 50d, 4d, 400), (data.Camera, data.Lens, data.FocalLength, data.Aperture, data.Iso));
  Assert.Equal(1 / 250d, data.ExposureSeconds!.Value, 6);
  Assert.Equal("NIKON D750 · 24.0-70.0 mm f/2.8 · 50 mm · f/4 · 1/250 s · ISO 400", data.Line);
 }

 [Fact]
 public void APictureWithoutExifTellsNothing()
 {
  var path = Path.Combine(root, "plain.png");
  using (var image = new MagickImage(MagickColors.SteelBlue, 64, 48)) image.Write(path, MagickFormat.Png);
  Assert.Null(CaptureFacts.Read(path));
  Assert.Null(CaptureFacts.Read(Path.Combine(root, "missing.jpg")));
 }

 [Fact]
 public void ARawFileTellsCameraAndExposure()
 {
  var path = Path.Combine(root, "shot.dng");
  SyntheticDng.Write(path, previewWidth: 1200);
  var data = CaptureFacts.Read(path);
  Assert.NotNull(data);
  Assert.Equal("FlixyPix Test Camera", data.Camera);
  Assert.Equal((2.8, 200), (data.Aperture, data.Iso));
  Assert.Equal(1 / 250d, data.ExposureSeconds!.Value, 6);
  Assert.Contains("f/2.8 · 1/250 s · ISO 200", data.Line);
 }

 [Theory]
 [InlineData(1 / 4000d, "1/4000 s")]
 [InlineData(1 / 60d, "1/60 s")]
 [InlineData(.5, "0.5 s")]
 [InlineData(2.5, "2.5 s")]
 [InlineData(30, "30 s")]
 public void ExposureReadsLikeOnTheCamera(double seconds, string text) => Assert.Equal(text, CaptureData.Exposure(seconds));

 [Theory]
 [InlineData("1/250", 1 / 250d)]
 [InlineData("0.5", .5)]
 [InlineData("30", 30d)]
 public void RawExposureTextIsRead(string text, double seconds) => Assert.Equal(seconds, CaptureFacts.Seconds(text)!.Value, 6);


 /// <summary>Files of real cameras, where a folder of them is named: what each tells is written beside the test output.</summary>
 [Fact]
 public void RealCameraFilesTellTheirCaptureData()
 {
  if (Environment.GetEnvironmentVariable("FLIXYPIX_TEST_RAW_SAMPLES") is not { Length: > 0 } samples || !Directory.Exists(samples)) return;
  var lines = new List<string>();
  foreach (var path in Directory.GetFiles(samples).Where(FlixyPix.Core.MediaFormats.IsRaw).Order(StringComparer.OrdinalIgnoreCase))
  {
   var data = CaptureFacts.Read(path);
   lines.Add(Path.GetFileName(path) + ": " + (data?.Line ?? "nothing"));
   Assert.NotNull(data);
   Assert.NotNull(data.Camera);
  }
  File.WriteAllLines(Path.Combine(samples, "capture-data.txt"), lines);
 }
}
