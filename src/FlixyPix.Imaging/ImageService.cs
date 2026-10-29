using System.Security.Cryptography;
using System.Text;
using ImageMagick;

namespace FlixyPix.Imaging;

using FlixyPix.Core;

public sealed record ImageInfo(int Width, int Height, int FrameCount, bool IsAnimated,
 IReadOnlyList<TimeSpan> FrameDurations, string Format, string SourceSha256)
{
 /// <summary>Number of complete animation plays, or zero for unlimited repeats.</summary>
 public uint AnimationIterations { get; init; }
}
public sealed record DecodedImage(byte[] PngBytes, int Width, int Height, int OriginalWidth, int OriginalHeight, string SourceSha256);

/// <summary>Read-only image decoding with content-addressed, bounded PNG caches.</summary>
public sealed class ImageService
{
 static ImageService()
 {
  ResourceLimits.Memory=256UL*1024*1024; ResourceLimits.Disk=2UL*1024*1024*1024;
  ResourceLimits.Thread=2; ResourceLimits.ListLength=4096; ResourceLimits.MaxProfileSize=16UL*1024*1024;
 }
 private readonly string cacheDirectory;
 private readonly long memoryLimit, diskLimit;
 private readonly SemaphoreSlim gate = new(1, 1);
 private readonly Dictionary<string, (DecodedImage Image, LinkedListNode<string> Node)> memory = new();
 private readonly LinkedList<string> lru = new();
 private readonly Dictionary<string, ImageInfo> metadata = new();
 private long memoryBytes;
 private static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase)
  { "JPEG", "JPG", "PNG", "PNG8", "PNG24", "PNG32", "PNG48", "PNG64", "APNG", "BMP", "BMP2", "BMP3", "TIFF", "TIFF64", "GIF", "GIF87", "WEBP", "HEIC", "HEIF", "AVIF", "ICO", "ICON" };

 public ImageService(string cacheDirectory, long memoryLimitBytes = 256L * 1024 * 1024, long diskLimitBytes = 2L * 1024 * 1024 * 1024)
 {
  if (memoryLimitBytes < 0 || diskLimitBytes < 0) throw new ArgumentOutOfRangeException(nameof(memoryLimitBytes));
  this.cacheDirectory = Path.GetFullPath(cacheDirectory); memoryLimit = memoryLimitBytes; diskLimit = diskLimitBytes;
  FileAccessPolicy.Current.EnsureWritable(this.cacheDirectory);
 }
 public Task<ImageInfo> GetInfoAsync(string path, CancellationToken cancellationToken = default) => Execute(path, (stream, hash) => GetInfo(stream, hash, cancellationToken), cancellationToken);
 public Task<DecodedImage> DecodeAsync(string path, int frameIndex = 0, int maxWidth = 0, int maxHeight = 0, CancellationToken cancellationToken = default)
 {
  if (frameIndex < 0 || maxWidth < 0 || maxHeight < 0 || maxWidth > 32768 || maxHeight > 32768) throw new ArgumentOutOfRangeException(nameof(frameIndex));
  return Execute(path, (stream, hash) => Decode(stream, hash, frameIndex, maxWidth, maxHeight, cancellationToken), cancellationToken);
 }
 /// <summary>Creates a normal PNG suitable for simple UI thumbnails; its lifetime follows the disk cache.</summary>
 public async Task<string> GetPreviewPathAsync(string path, int maxWidth = 390, int maxHeight = 390, CancellationToken cancellationToken = default)
 {
  var image = await DecodeAsync(path, 0, maxWidth, maxHeight, cancellationToken).ConfigureAwait(false);
  var key = Key(image.SourceSha256, 0, maxWidth, maxHeight);
  // A requested persistent thumbnail remains available even if caching was disabled.
  var output = Path.Combine(cacheDirectory, key + ".png");
  if (!File.Exists(output)) await WriteAtomic(output, image.PngBytes, cancellationToken).ConfigureAwait(false);
  return output;
 }
 private async Task<T> Execute<T>(string path, Func<FileStream, string, T> work, CancellationToken token)
 {
  await gate.WaitAsync(token).ConfigureAwait(false);
  try
  {
   return await Task.Run(() =>
   {
    token.ThrowIfCancellationRequested();
    using var input = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
    var hash = Convert.ToHexStringLower(SHA256.HashData(input)); input.Position = 0;
    token.ThrowIfCancellationRequested();
    try { var result = work(input, hash); token.ThrowIfCancellationRequested(); return result; }
    catch (MagickException error) { throw new InvalidDataException("Bild konnte nicht gelesen werden: " + error.Message, error); }
   }, token).ConfigureAwait(false);
  }
  finally { gate.Release(); }
 }
 private ImageInfo GetInfo(Stream stream, string hash,CancellationToken token=default)
 {
  if (metadata.TryGetValue(hash, out var existing)) return existing;
  stream.Position = 0;
  ValidateSignature(stream);
  var apng=ApngDocument.TryRead(stream,token);
  if(apng!=null)
  {
   var animationInfo=new ImageInfo(apng.Width,apng.Height,apng.Frames.Count,true,apng.Frames.Select(f=>f.Duration).ToArray(),"APNG",hash) { AnimationIterations=apng.AnimationIterations };
   if(metadata.Count>=4096)metadata.Clear(); metadata[hash]=animationInfo; return animationInfo;
  }
  using var images = new MagickImageCollection(); images.Ping(stream, Settings(stream));
  if (images.Count == 0 || images.Count > 4096) throw new InvalidDataException("Das Bild enthält keine oder mehr als 4096 Seiten/Frames.");
  var first = images[0];
  if (!Formats.Contains(first.Format.ToString())) throw new InvalidDataException("Dieses Bildformat gehört nicht zum unterstützten Bildprofil: " + first.Format);
  foreach (var image in images)
   if (image.Width == 0 || image.Height == 0 || (long)image.Width * image.Height > 100_000_000) throw new InvalidDataException("Eine Bildseite überschreitet die Grenze von 100 Megapixeln.");
  var animated = images.Count > 1 && first.Format.ToString().ToUpperInvariant() is "GIF" or "GIF87" or "WEBP" or "PNG" or "APNG" or "AVIF" or "HEIC" or "HEIF" && images.Any(i => i.AnimationDelay > 0);
  var canvasWidth=animated?images.Max(i=>Math.Max(i.Width,i.Page.Width)):first.Width;
  var canvasHeight=animated?images.Max(i=>Math.Max(i.Height,i.Page.Height)):first.Height;
  if (animated && ((long)canvasWidth*canvasHeight > 32_000_000L/images.Count)) throw new InvalidDataException("Diese Animation überschreitet das dekodierbare Gesamtbudget von 32 Megapixeln.");
  var swap = first.Orientation is Orientation.RightTop or Orientation.RightBottom or Orientation.LeftTop or Orientation.LeftBottom;
  var result = new ImageInfo(checked((int)(swap ? canvasHeight : canvasWidth)), checked((int)(swap ? canvasWidth : canvasHeight)), images.Count, animated,
   images.Select(i => TimeSpan.FromSeconds(Math.Max(.01, i.AnimationDelay / (double)Math.Max(1, i.AnimationTicksPerSecond)))).ToArray(), first.Format.ToString(), hash) { AnimationIterations=first.AnimationIterations };
  if (metadata.Count >= 4096) metadata.Clear(); metadata[hash] = result; return result;
 }
 private DecodedImage Decode(Stream stream, string hash, int frame, int width, int height, CancellationToken token)
 {
  var info = GetInfo(stream, hash,token);
  if (frame >= info.FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
  var key = Key(hash, frame, width, height);
  if (memory.TryGetValue(key, out var cached)) { lru.Remove(cached.Node); lru.AddFirst(cached.Node); return Clone(cached.Image); }
  var disk = Path.Combine(cacheDirectory, key + ".png");
  if (File.Exists(disk))
  {
   try
   {
    using var diskImage = new MagickImage(disk);
    var result = new DecodedImage(File.ReadAllBytes(disk), (int)diskImage.Width, (int)diskImage.Height, info.Width, info.Height, hash);
    File.SetLastWriteTimeUtc(disk, DateTime.UtcNow); Remember(key, result); return Clone(result);
   }
   catch (Exception error) when (error is IOException or MagickException) { FileAccessPolicy.Current.EnsureWritable(disk); File.Delete(disk); }
  }
  stream.Position = 0; token.ThrowIfCancellationRequested();
  using var images = new MagickImageCollection();
  using var image = ReadFrame();
  MagickImage ReadFrame()
  {
   if(info.Format=="APNG")return ApngDocument.TryRead(stream,token)!.Decode(frame,token);
   if (info.IsAnimated) { images.Read(stream); images.Coalesce(); }
   else { var settings = Settings(stream); settings.FrameIndex = (uint)frame; settings.FrameCount = 1; images.Read(stream, settings); }
   return (MagickImage)images[info.IsAnimated ? frame : 0].Clone();
  }
  image.AutoOrient();
  if (image.GetColorProfile() != null) image.TransformColorSpace(ColorProfiles.SRGB);
  else image.ColorSpace = ColorSpace.sRGB;
  if (width > 0 || height > 0) image.Resize(new MagickGeometry((uint)width, (uint)height) { Greater = true });
  image.Strip(); image.Depth = 8;
  token.ThrowIfCancellationRequested();
  var decoded = new DecodedImage(image.ToByteArray(MagickFormat.Png), (int)image.Width, (int)image.Height, info.Width, info.Height, hash);
  if (diskLimit > 0 && decoded.PngBytes.LongLength <= diskLimit)
  {
   FileAccessPolicy.Current.EnsureWritable(cacheDirectory); Directory.CreateDirectory(cacheDirectory); PruneDisk(decoded.PngBytes.LongLength, disk);
   WriteAtomic(disk, decoded.PngBytes, token).GetAwaiter().GetResult();
  }
  Remember(key, decoded); return Clone(decoded);
 }
 private static string Key(string hash, int frame, int width, int height) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"image-v1|{hash}|{frame}|{width}|{height}")));
 internal static MagickReadSettings Settings(Stream stream)
 {
  // ICO is not detected by ImageMagick's stream sniffing on all native builds.
  Span<byte> header = stackalloc byte[4]; var count = stream.Read(header); stream.Position = 0;
  return count == 4 && header.SequenceEqual(new byte[] {0,0,1,0}) ? new MagickReadSettings { Format = MagickFormat.Ico } : new MagickReadSettings();
 }
 internal static void ValidateSignature(Stream stream)
 {
  Span<byte> header=stackalloc byte[32]; var count=stream.Read(header); header=header[..count]; stream.Position=0;
  var brands=count>=12?Encoding.ASCII.GetString(header[8..]):"";
  var known=header.StartsWith(new byte[]{0xff,0xd8,0xff}) || header.StartsWith(new byte[]{137,80,78,71,13,10,26,10}) ||
   header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8) || header.StartsWith("BM"u8) || header.StartsWith("II*\0"u8) ||
   header.StartsWith("MM\0*"u8) || header.StartsWith("II+\0"u8) || header.StartsWith("MM\0+"u8) || header.StartsWith(new byte[]{0,0,1,0}) ||
   count>=12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8) ||
   count>=12 && header[4..8].SequenceEqual("ftyp"u8) && new[]{"avif","avis","heic","heix","hevc","hevx","mif1","msf1"}.Any(brand=>brands.Contains(brand,StringComparison.Ordinal));
  if(!known)throw new InvalidDataException("Die Datei enthält kein unterstütztes Bildformat.");
 }
 private static DecodedImage Clone(DecodedImage image) => image with { PngBytes = (byte[])image.PngBytes.Clone() };
 private void Remember(string key, DecodedImage image)
 {
  var bytes = image.PngBytes.LongLength + (long)image.Width * image.Height * 4;
  if (bytes > memoryLimit) return;
  while (memoryBytes + bytes > memoryLimit && lru.Last is { } last)
  { var removed = memory[last.Value].Image; memoryBytes -= removed.PngBytes.LongLength + (long)removed.Width * removed.Height * 4; memory.Remove(last.Value); lru.RemoveLast(); }
  var node = lru.AddFirst(key); memory[key] = (image, node); memoryBytes += bytes;
 }
 private void PruneDisk(long needed, string preserve)
 {
  var files = new DirectoryInfo(cacheDirectory).EnumerateFiles("*.png").Where(f => f.Name.Length == 68).OrderBy(f => f.LastWriteTimeUtc).ToArray();
  var size = files.Sum(f => f.Length);
  foreach (var file in files)
  { if (size + needed <= diskLimit) break; if (file.FullName == preserve) continue; size -= file.Length; file.Delete(); }
 }
 private static async Task WriteAtomic(string path, byte[] bytes, CancellationToken token)
 {
  FileAccessPolicy.Current.EnsureWritable(path);
  Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
  try { await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false); File.Move(temporary, path, true); }
  finally { if (File.Exists(temporary)) File.Delete(temporary); }
 }
}
