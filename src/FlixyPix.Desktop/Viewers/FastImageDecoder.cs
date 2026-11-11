using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlixyPix.Imaging;

namespace FlixyPix.Desktop;

/// <summary>Decodes still images in common formats with the Windows Imaging Component, the decoder other Windows viewers
/// use: a large JPEG takes a fraction of a second instead of seconds through ImageMagick, and the pixels go to the
/// window without a PNG in between. Anything it cannot show exactly like ImageMagick returns null and takes the
/// ImageMagick way: animations, several pages, CMYK, other formats, damaged files.</summary>
internal static class FastImageDecoder
{
    internal sealed record Result(BitmapSource Bitmap, ImageInfo Info);

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".tif", ".tiff", ".gif" };
    /// <summary>The same limit as the ImageMagick way.</summary>
    private const long MaximumPixels = 100_000_000;

    internal static bool Handles(string path) => Extensions.Contains(Path.GetExtension(path)) || FlixyPix.Core.MediaFormats.IsRaw(path);

    /// <param name="bound">Longest side of the result, or 0 for full size.</param>
    internal static Task<Result?> DecodeAsync(string path, int bound, CancellationToken token) =>
        Handles(path) ? Task.Run(() => Decode(path, bound, token), token) : Task.FromResult<Result?>(null);

    internal static Result? Decode(string path, int bound, CancellationToken token)
    {
        if (!Handles(path)) return null;
        try
        {
            if (FlixyPix.Core.MediaFormats.IsRaw(path)) return DecodeRaw(path, bound, token);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return Decode(stream, Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), bound, token);
        }
        catch (Exception error) when (error is FileFormatException or NotSupportedException or ArgumentException or COMException or InvalidOperationException or OverflowException or ExternalException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A camera RAW file is shown by the JPEG the camera put into it, which decodes like any JPEG. Null without a
    /// preview of a usable size: then the sensor data has to be developed, which ImageMagick does.</summary>
    private static Result? DecodeRaw(string path, int bound, CancellationToken token)
    {
        if (RawImages.ReadPreview(path, token) is not { } preview || !RawImages.Serves(preview.LongestSide, bound)) return null;
        using var stream = new MemoryStream(preview.Jpeg, writable: false);
        if (Decode(stream, "RAW", bound, token, preview.Orientation) is not { } shown) return null;
        var turned = preview.Turned;
        return shown with
        {
            Info = new ImageInfo(turned ? preview.RawHeight : preview.RawWidth, turned ? preview.RawWidth : preview.RawHeight, 1, false, [TimeSpan.Zero], "RAW", "")
            { IsRaw = true, PreviewWidth = turned ? preview.Height : preview.Width, PreviewHeight = turned ? preview.Width : preview.Height },
        };
    }

    /// <param name="turn">EXIF orientation to turn the picture by instead of the one the picture itself names.</param>
    private static Result? Decode(Stream stream, string format, int bound, CancellationToken token, int? turn = null)
    {
        {
            if (IsAnimatedPng(stream)) return null;
            stream.Position = 0;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count != 1) return null;
            var frame = decoder.Frames[0];
            var (width, height) = (frame.PixelWidth, frame.PixelHeight);
            if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels) return null;
            // WPF converts CMYK without the printer profile; ImageMagick shows those colours correctly.
            if (frame.Format == PixelFormats.Cmyk32) return null;
            var orientation = turn ?? Orientation(frame.Metadata as BitmapMetadata);
            token.ThrowIfCancellationRequested();

            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            // The decoder scales while reading (a JPEG decodes at an eighth, a quarter or half of its size directly). A
            // little smaller is slower than full size, so only a clearly larger image is scaled.
            if (bound > 0 && Math.Max(width, height) > bound * 3L / 2)
            {
                if (width >= height) image.DecodePixelWidth = bound; else image.DecodePixelHeight = bound;
            }
            image.EndInit();
            token.ThrowIfCancellationRequested();

            BitmapSource result = image;
            if (Transform(orientation) is { } transform) result = new TransformedBitmap(result, transform);
            if (result.Format != PixelFormats.Bgra32 && result.Format != PixelFormats.Pbgra32 && result.Format != PixelFormats.Bgr32)
                result = new FormatConvertedBitmap(result, PixelFormats.Bgra32, null, 0);
            // Pixels are worked out once here, beside the window, and not again on every draw.
            var cached = new CachedBitmap(result, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            cached.Freeze();
            var swap = orientation is 5 or 6 or 7 or 8;
            var info = new ImageInfo(swap ? height : width, swap ? width : height, 1, false, [TimeSpan.Zero], decoder.CodecInfo?.FriendlyName ?? format, "");
            return new(cached, info);
        }
    }

    /// <summary>EXIF orientation (1 to 8) of a JPEG or TIFF; 1 when none is given.</summary>
    private static int Orientation(BitmapMetadata? metadata)
    {
        if (metadata == null) return 1;
        foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is { } value && Convert.ToInt32(value) is >= 1 and <= 8 and var orientation) return orientation;
            }
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException or COMException or FormatException or InvalidCastException) { }
        }
        return 1;
    }

    /// <summary>What turns a stored image into the way it is meant to be seen: mirror first, then rotate clockwise.</summary>
    internal static Transform? Transform(int orientation)
    {
        var (mirror, degrees) = orientation switch { 2 => (true, 0), 3 => (false, 180), 4 => (true, 180), 5 => (true, 270), 6 => (false, 90), 7 => (true, 90), 8 => (false, 270), _ => (false, 0) };
        if (!mirror && degrees == 0) return null;
        var group = new TransformGroup();
        if (mirror) group.Children.Add(new ScaleTransform(-1, 1));
        if (degrees != 0) group.Children.Add(new RotateTransform(degrees));
        group.Freeze();
        return group;
    }

    /// <summary>An APNG has an acTL chunk before its image data; WIC would show only its first frame.</summary>
    private static bool IsAnimatedPng(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) != 8 || !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        Span<byte> chunk = stackalloc byte[8];
        while (stream.Read(chunk) == 8)
        {
            var length = (long)(uint)(chunk[0] << 24 | chunk[1] << 16 | chunk[2] << 8 | chunk[3]);
            var type = chunk[4..8];
            if (type.SequenceEqual("acTL"u8)) return true;
            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8)) return false;
            if (stream.Seek(length + 4, SeekOrigin.Current) >= stream.Length) return false;
        }
        return false;
    }
}
