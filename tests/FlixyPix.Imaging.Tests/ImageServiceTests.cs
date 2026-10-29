using System.Security.Cryptography;
using FlixyPix.Imaging;
using ImageMagick;
using Xunit;

namespace FlixyPix.Imaging.Tests;

public sealed class ImageServiceTests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-imaging-" + Guid.NewGuid().ToString("N"));
 public ImageServiceTests() => Directory.CreateDirectory(root);
 public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }
 private string Make(string name, MagickFormat format, int width = 80, int height = 40, string color = "red")
 {
  var path = Path.Combine(root, name); using var image = new MagickImage(new MagickColor(color), (uint)width, (uint)height); image.Write(path, format); return path;
 }
 [Theory]
 [InlineData(MagickFormat.Jpeg, "jpg")]
 [InlineData(MagickFormat.Png, "png")]
 [InlineData(MagickFormat.Bmp, "bmp")]
 [InlineData(MagickFormat.Tiff, "tiff")]
 [InlineData(MagickFormat.Gif, "gif")]
 [InlineData(MagickFormat.WebP, "webp")]
 [InlineData(MagickFormat.Avif, "avif")]
 
 [InlineData(MagickFormat.Ico, "ico")]
 public async Task AllPromisedStaticFamiliesHaveWorkingNativeDecoder(MagickFormat format, string extension)
 {
  var path = Make("source." + extension, format); var hash = SHA256.HashData(File.ReadAllBytes(path));
  var service = new ImageService(Path.Combine(root,"cache")); var info = await service.GetInfoAsync(path);
  Assert.Equal(80,info.Width); Assert.Equal(40,info.Height);
  var decoded = await service.DecodeAsync(path,maxWidth:20,maxHeight:20);
  Assert.Equal(20,decoded.Width); Assert.Equal(10,decoded.Height);
  using var png = new MagickImage(decoded.PngBytes); Assert.Equal(MagickFormat.Png,png.Format);
  Assert.Equal(hash,SHA256.HashData(File.ReadAllBytes(path)));
 }
 [Theory]
 [InlineData(MagickFormat.Gif,"gif")]
 [InlineData(MagickFormat.WebP,"webp")]
 [InlineData(MagickFormat.Avif,"avif")]
 public async Task AnimatedImagesExposeTimingAndDistinctCompositedFrames(MagickFormat format,string extension)
 {
  var path = Path.Combine(root,"animation."+extension);
  using (var frames = new MagickImageCollection())
  {
   frames.Add(new MagickImage(MagickColors.Red,32,20) { AnimationDelay=15, AnimationTicksPerSecond=100 });
   frames.Add(new MagickImage(MagickColors.Blue,32,20) { AnimationDelay=25, AnimationTicksPerSecond=100 });
   frames.Write(path,format);
  }
  var service = new ImageService(Path.Combine(root,"cache")); var info = await service.GetInfoAsync(path);
  Assert.True(info.IsAnimated); Assert.Equal(2,info.FrameCount);
  Assert.InRange(info.FrameDurations[0].TotalMilliseconds,140,160);
  Assert.InRange(info.FrameDurations[1].TotalMilliseconds,240,260);
  Assert.NotEqual((await service.DecodeAsync(path,0)).PngBytes,(await service.DecodeAsync(path,1)).PngBytes);
 }
 [Fact]
 public async Task TiffPagesAreIndependentAndHaveTheirOwnSize()
 {
  var path = Path.Combine(root,"pages.tiff");
  using (var frames = new MagickImageCollection()) { frames.Add(new MagickImage(MagickColors.Red,80,40)); frames.Add(new MagickImage(MagickColors.Blue,30,60)); frames.Write(path,MagickFormat.Tiff); }
  var service = new ImageService(Path.Combine(root,"cache")); var info = await service.GetInfoAsync(path);
  Assert.False(info.IsAnimated); Assert.Equal(2,info.FrameCount);
  var page = await service.DecodeAsync(path,1); Assert.Equal(30,page.Width); Assert.Equal(60,page.Height);
  await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>service.DecodeAsync(path,2));
 }
 [Fact]
 public async Task ExifOrientationIsAppliedWithoutChangingSource()
 {
  var path=Path.Combine(root,"oriented.jpg");
  using(var image=new MagickImage(MagickColors.Red,80,40)) { var exif=new ExifProfile(); exif.SetValue(ExifTag.Orientation,(ushort)6); image.SetProfile(exif); image.Orientation=Orientation.RightTop; image.Write(path,MagickFormat.Jpeg); }
  var before=File.ReadAllBytes(path); var service=new ImageService(Path.Combine(root,"cache"));
  var info=await service.GetInfoAsync(path); var decoded=await service.DecodeAsync(path);
  Assert.Equal(40,info.Width); Assert.Equal(80,info.Height); Assert.Equal(40,decoded.Width); Assert.Equal(80,decoded.Height); Assert.Equal(before,File.ReadAllBytes(path));
 }
 [Fact]
 public async Task CacheUsesSourceContentEvenWhenLengthAndTimestampDoNotChange()
 {
  var path=Make("same.bmp",MagickFormat.Bmp); var timestamp=File.GetLastWriteTimeUtc(path); var length=new FileInfo(path).Length;
  var service=new ImageService(Path.Combine(root,"cache")); var first=await service.DecodeAsync(path);
  Make("same.bmp",MagickFormat.Bmp,color:"blue"); Assert.Equal(length,new FileInfo(path).Length); File.SetLastWriteTimeUtc(path,timestamp);
  var second=await service.DecodeAsync(path); Assert.NotEqual(first.SourceSha256,second.SourceSha256); Assert.NotEqual(first.PngBytes,second.PngBytes);
  first.PngBytes[0]=0; Assert.Equal((byte)137,(await service.DecodeAsync(path)).PngBytes[0]);
 }
 [Fact]
 public async Task CorruptDiskCacheRegeneratesAndDiskBudgetEvictsOldFiles()
 {
  var path=Make("source.png",MagickFormat.Png); var cache=Path.Combine(root,"cache"); var service=new ImageService(cache,0,10000);
  await service.DecodeAsync(path); var output=Directory.GetFiles(cache,"*.png").Single(); File.WriteAllText(output,"broken");
  Assert.Equal((byte)137,(await service.DecodeAsync(path)).PngBytes[0]);
  var stale=Path.Combine(cache,new string('a',64)+".png"); File.WriteAllBytes(stale,new byte[10000]); File.SetLastWriteTimeUtc(stale,DateTime.UtcNow.AddDays(-1));
  await service.DecodeAsync(Make("second.png",MagickFormat.Png,color:"blue")); Assert.False(File.Exists(stale)); Assert.True(Directory.GetFiles(cache).Sum(p=>new FileInfo(p).Length)<=10000);
 }
 [Fact]
 public async Task CancellationAndInvalidInputsAreExplicit()
 {
  var path=Make("source.png",MagickFormat.Png); var service=new ImageService(Path.Combine(root,"cache"));
  await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.DecodeAsync(path,cancellationToken:new CancellationToken(true)));
  var broken=Path.Combine(root,"bad.png"); File.WriteAllText(broken,"not an image"); await Assert.ThrowsAsync<InvalidDataException>(()=>service.DecodeAsync(broken));
  Assert.Throws<ArgumentOutOfRangeException>((Action)(()=> { _ = service.DecodeAsync(path,-1); }));
 }
 [Fact]
 public async Task OptimizedAnimationBudgetCountsFullCompositedCanvases()
 {
  var path=Path.Combine(root,"oversized.gif");
  using(var frames=new MagickImageCollection())
  {
   frames.Add(new MagickImage(MagickColors.Red,1024,768) { AnimationDelay=10 });
   for(var i=0;i<42;i++)frames.Add(new MagickImage(i%2==0?MagickColors.Blue:MagickColors.Green,1,1) { AnimationDelay=10, Page=new MagickGeometry(1024,768) });
   frames.Write(path,MagickFormat.Gif);
  }
  await Assert.ThrowsAsync<InvalidDataException>(()=>new ImageService(Path.Combine(root,"cache")).GetInfoAsync(path));
 }
}
