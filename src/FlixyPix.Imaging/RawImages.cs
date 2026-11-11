using System.Globalization;
using System.Runtime.CompilerServices;
using ImageMagick;

namespace FlixyPix.Imaging;

using FlixyPix.Core;

/// <summary>The JPEG a camera embeds in a RAW file, as the camera rendered the shot.</summary>
/// <param name="Orientation">EXIF orientation (1 to 8) the picture is to be turned by; the JPEG is stored unturned.</param>
/// <param name="Width">Width of the JPEG as stored.</param>
/// <param name="RawWidth">Width of the sensor image as stored.</param>
public sealed record RawPreview(byte[] Jpeg, int Orientation, int Width, int Height, int RawWidth, int RawHeight)
{
    public bool Turned => Orientation is >= 5 and <= 8;
    public int LongestSide => Math.Max(Width, Height);
}

/// <summary>Camera RAW files through LibRaw, which ImageMagick brings along. A RAW file is shown by the JPEG preview of
/// the camera inside it: it is there in a few hundredths of a second and looks as the camera meant the shot. Developing
/// the sensor data takes seconds and looks different, so that happens only without a usable preview or on request.</summary>
public static class RawImages
{
    /// <summary>Sensor sizes up to medium format (the 100 megapixel limit of other images would cut off 102 MP cameras).</summary>
    public const long MaximumPixels = 160_000_000;
    /// <summary>A preview at least this long counts as the picture; thumbnails of 160 pixels do not.</summary>
    public const int UsablePreview = 1000;
    private const string PreviewProfile = "dng:thumbnail";

    static RawImages() => RuntimeHelpers.RunClassConstructor(typeof(ImageService).TypeHandle);

    /// <summary>The format is given, never detected: read as what their first bytes say, most RAW files are a TIFF whose
    /// first image is a thumbnail of 160 pixels.</summary>
    /// <param name="fast">Simple interpolation: several times faster, good enough for a result scaled down anyway.</param>
    internal static MagickReadSettings Settings(bool preview = false, bool fast = false)
    {
        var settings = new MagickReadSettings { Format = MagickFormat.Dng };
        if (preview) settings.SetDefine(MagickFormat.Dng, "read-thumbnail", "true");
        if (fast) settings.SetDefine(MagickFormat.Dng, "interpolation-quality", "0");
        return settings;
    }

    /// <summary>Whether a preview of this size serves a request for <paramref name="bound"/> pixels (0: full size).</summary>
    public static bool Serves(int previewLongestSide, int bound) => previewLongestSide >= (bound > 0 ? Math.Min(bound, UsablePreview) : UsablePreview);

    /// <summary>The embedded preview of a RAW file; null when it has none.</summary>
    public static RawPreview? ReadPreview(string path, CancellationToken token = default)
    {
        using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        ImageService.ValidateSignature(stream);
        token.ThrowIfCancellationRequested();
        try { using var image = Ping(stream); return Preview(image); }
        catch (MagickException error) { throw new InvalidDataException("Bild konnte nicht gelesen werden: " + error.Message, error); }
    }

    internal static MagickImage Ping(Stream stream)
    {
        stream.Position = 0;
        var image = new MagickImage();
        try { image.Ping(stream, Settings(preview: true)); return image; }
        catch { image.Dispose(); throw; }
    }

    internal static RawPreview? Preview(IMagickImage pinged)
    {
        if (pinged.GetProfile(PreviewProfile)?.ToByteArray() is not { Length: > 4 } bytes || bytes[0] != 0xff || bytes[1] != 0xd8) return null;
        try
        {
            using var jpeg = new MagickImage(); jpeg.Ping(bytes);
            var orientation = pinged.Orientation != ImageMagick.Orientation.Undefined ? (int)pinged.Orientation : (int)jpeg.Orientation;
            return new(bytes, orientation is >= 1 and <= 8 ? orientation : 1, (int)jpeg.Width, (int)jpeg.Height, (int)pinged.Width, (int)pinged.Height);
        }
        catch (MagickException) { return null; }
    }

    /// <summary>"Nikon D750": maker and model as the file names them, the maker only once.</summary>
    internal static string? Camera(IMagickImage pinged) => Camera(pinged.GetAttribute("dng:make"), pinged.GetAttribute("dng:camera.model.name"));

    internal static string? Camera(string? make, string? model)
    {
        make = FileMetadata.Clean(make); model = FileMetadata.Clean(model);
        if (model == null) return null;
        if (make == null || model.StartsWith(make.Split(' ')[0], StringComparison.OrdinalIgnoreCase)) return model;
        // "NIKON CORPORATION", "OLYMPUS IMAGING CORP." and the like: the first word is the name people use.
        var name = make.Split(' ')[0];
        return (name.Length > 3 && name.All(char.IsUpper) ? name[..1] + name[1..].ToLowerInvariant() : name) + " " + model;
    }

    /// <summary>When the picture was taken. LibRaw reports the camera's local time as if it were this computer's.</summary>
    internal static DateTimeOffset? Taken(IMagickImage pinged) =>
        DateTimeOffset.TryParse(pinged.GetAttribute("dng:create.date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) && date.Year > 1980 ? date.ToLocalTime() : null;
}
