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
 /// <summary>A camera RAW file; <see cref="Width"/> and <see cref="Height"/> are those of the sensor image.</summary>
 public bool IsRaw { get; init; }
 /// <summary>Size of the camera's preview inside a RAW file, turned as it is shown; null without one.</summary>
 public int? PreviewWidth { get; init; }
 public int? PreviewHeight { get; init; }
 /// <summary>Maker and model of the camera, where the file names them.</summary>
 public string? Camera { get; init; }
 /// <summary>The preview is what a RAW file is shown by; false when it has none of a usable size and is developed instead.</summary>
 public bool ShownByPreview => IsRaw && PreviewWidth is { } width && PreviewHeight is { } height && Math.Max(width, height) >= RawImages.UsablePreview;
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
 /// <param name="develop">For a camera RAW file: work out the sensor data instead of taking the camera's preview.</param>
 public Task<DecodedImage> DecodeAsync(string path, int frameIndex = 0, int maxWidth = 0, int maxHeight = 0, CancellationToken cancellationToken = default, bool develop = false)
 {
  if (frameIndex < 0 || maxWidth < 0 || maxHeight < 0 || maxWidth > 32768 || maxHeight > 32768) throw new ArgumentOutOfRangeException(nameof(frameIndex));
  return Execute(path, (stream, hash) => Decode(stream, hash, frameIndex, maxWidth, maxHeight, cancellationToken, develop), cancellationToken);
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
    // A RAW file has tens of megabytes; place, size and date say as well whether it is still the same.
    var hash = MediaFormats.IsRaw(input.Name) ? RawHash(input.Name, input.Length, File.GetLastWriteTimeUtc(input.Name)) : Convert.ToHexStringLower(SHA256.HashData(input));
    input.Position = 0;
    token.ThrowIfCancellationRequested();
    try { var result = work(input, hash); token.ThrowIfCancellationRequested(); return result; }
    catch (MagickException error) { throw new InvalidDataException("Bild konnte nicht gelesen werden: " + error.Message, error); }
   }, token).ConfigureAwait(false);
  }
  finally { gate.Release(); }
 }
 private static string RawHash(string fullPath, long length, DateTime modifiedUtc) =>
  Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"raw|{fullPath.ToUpperInvariant()}|{length}|{modifiedUtc.Ticks}")));

 /// <summary>The development of a RAW file that is still in the cache, as a PNG file: the one fitted to
 /// <paramref name="bound"/> pixels or, where only that exists, the one in full size. Null when the file was never
 /// developed, has changed since, or the cache has let the development go. Nothing is decoded and no other work of the
 /// service is waited for, so this answers at once.</summary>
 public string? FindDeveloped(string path, int bound)
 {
  try
  {
   var file = new FileInfo(Path.GetFullPath(path));
   if (!MediaFormats.IsRaw(file.FullName) || !file.Exists) return null;
   var hash = RawHash(file.FullName, file.Length, file.LastWriteTimeUtc) + "|developed";
   foreach (var size in bound > 0 ? new[] { bound, 0 } : [0])
   {
    var disk = Path.Combine(cacheDirectory, Key(hash, 0, size, size) + ".png");
    if (!File.Exists(disk)) continue;
    // Looked at again: the cache keeps it longer.
    try { File.SetLastWriteTimeUtc(disk, DateTime.UtcNow); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    return disk;
   }
   return null;
  }
  catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
 }

 private ImageInfo GetInfo(Stream stream, string hash,CancellationToken token=default)
 {
  if (metadata.TryGetValue(hash, out var existing)) return existing;
  stream.Position = 0;
  ValidateSignature(stream);
  if (IsRaw(stream))
  {
   using var pinged = RawImages.Ping(stream);
   if (pinged.Width == 0 || pinged.Height == 0 || (long)pinged.Width * pinged.Height > RawImages.MaximumPixels) throw new InvalidDataException("Das Bild überschreitet die Grenze von 160 Megapixeln.");
   var preview = RawImages.Preview(pinged);
   var turned = preview?.Turned ?? pinged.Orientation is Orientation.RightTop or Orientation.RightBottom or Orientation.LeftTop or Orientation.LeftBottom;
   var rawInfo = new ImageInfo((int)(turned ? pinged.Height : pinged.Width), (int)(turned ? pinged.Width : pinged.Height), 1, false, [TimeSpan.Zero], "RAW", hash)
   {
    IsRaw = true, Camera = RawImages.Camera(pinged),
    PreviewWidth = preview == null ? null : turned ? preview.Height : preview.Width, PreviewHeight = preview == null ? null : turned ? preview.Width : preview.Height,
   };
   if (metadata.Count >= 4096) metadata.Clear(); metadata[hash] = rawInfo; return rawInfo;
  }
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
   images.Select(i => TimeSpan.FromSeconds(Math.Max(.01, i.AnimationDelay / (double)Math.Max(1, i.AnimationTicksPerSecond)))).ToArray(), first.Format.ToString(), hash)
   { AnimationIterations=first.AnimationIterations, Camera=first.GetExifProfile() is { } exif ? RawImages.Camera(exif.GetValue(ExifTag.Make)?.Value, exif.GetValue(ExifTag.Model)?.Value) : null };
  if (metadata.Count >= 4096) metadata.Clear(); metadata[hash] = result; return result;
 }
 private DecodedImage Decode(Stream stream, string hash, int frame, int width, int height, CancellationToken token, bool develop = false)
 {
  var info = GetInfo(stream, hash,token);
  if (frame >= info.FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
  develop &= info.IsRaw;
  var key = Key(develop ? hash + "|developed" : hash, frame, width, height);
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
   if (info.IsRaw)
   {
    var bound = Math.Max(width, height);
    if (!develop)
    {
     using var pinged = RawImages.Ping(stream);
     if (RawImages.Preview(pinged) is { } preview && RawImages.Serves(preview.LongestSide, bound))
      return new MagickImage(preview.Jpeg) { Orientation = (Orientation)preview.Orientation };
     stream.Position = 0; token.ThrowIfCancellationRequested();
    }
    // Without a usable preview the sensor data is worked out; simply, when the result is scaled down to less than half.
    var developed = new MagickImage();
    try { developed.Read(stream, RawImages.Settings(fast: bound > 0 && bound * 2 <= Math.Max(info.Width, info.Height))); return developed; }
    catch { developed.Dispose(); throw; }
   }
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
 /// <summary>RAW files are told by their name: most start like a TIFF, and what is read as RAW must be one.</summary>
 internal static bool IsRaw(Stream stream) => stream is FileStream file && MediaFormats.IsRaw(file.Name);
 internal static void ValidateSignature(Stream stream)
 {
  Span<byte> header=stackalloc byte[32]; var count=stream.Read(header); header=header[..count]; stream.Position=0;
  var brands=count>=12?Encoding.ASCII.GetString(header[8..]):"";
  if(IsRaw(stream))
  {
   // TIFF and its relatives (DNG, CR2, NEF, ARW, PEF …, ORF, RW2), Fuji, Canon CR3 and CRW, Minolta, Sigma.
   var raw=header.StartsWith("II*\0"u8) || header.StartsWith("MM\0*"u8) || header.StartsWith("IIRO"u8) || header.StartsWith("IIRS"u8) || header.StartsWith("MMOR"u8) ||
    header.StartsWith("IIU\0"u8) || header.StartsWith("FUJIFILMCCD-RAW"u8) || header.StartsWith("\0MRM"u8) || header.StartsWith("FOVb"u8) ||
    count>=14 && header[..2].SequenceEqual("II"u8) && header[6..14].SequenceEqual("HEAPCCDR"u8) ||
    count>=12 && header[4..8].SequenceEqual("ftyp"u8) && brands.Contains("crx",StringComparison.Ordinal);
   if(!raw)throw new InvalidDataException("Die Datei enthält kein unterstütztes Bildformat.");
   return;
  }
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
