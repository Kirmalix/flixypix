using System.Globalization;
using System.Runtime.CompilerServices;
using ImageMagick;

namespace FlixyPix.Imaging;

using FlixyPix.Core;

/// <summary>How a photo was taken, as its file tells it; each part is null where the file is silent.</summary>
/// <param name="FocalLength">In millimetres.</param>
/// <param name="Aperture">The f-number.</param>
/// <param name="ExposureSeconds">How long the shutter was open.</param>
public sealed record CaptureData(string? Camera, string? Lens, double? FocalLength, double? Aperture, double? ExposureSeconds, int? Iso)
{
    public bool IsEmpty => Camera == null && Lens == null && FocalLength == null && Aperture == null && ExposureSeconds == null && Iso == null;

    /// <summary>"Nikon D750 · 24-70 mm f/2.8 · 50 mm · f/4 · 1/250 s · ISO 400", with what is known.</summary>
    public string Line => string.Join(" · ", new[]
    {
        Camera, Lens,
        FocalLength is { } length ? Number(length, length < 10 ? "0.#" : "0") + " mm" : null,
        Aperture is { } aperture ? "f/" + Number(aperture, "0.#") : null,
        ExposureSeconds is { } seconds ? Exposure(seconds) : null,
        Iso is { } iso ? "ISO " + iso.ToString(CultureInfo.CurrentCulture) : null,
    }.Where(part => !string.IsNullOrEmpty(part)));

    private static string Number(double value, string format) => value.ToString(format, CultureInfo.CurrentCulture);

    /// <summary>"1/250 s" below a third of a second, as cameras write it; "0.5 s" and "30 s" above.</summary>
    internal static string Exposure(double seconds) =>
        seconds < .3 ? "1/" + Math.Round(1 / seconds).ToString("0", CultureInfo.CurrentCulture) + " s" : Number(seconds, seconds < 10 ? "0.#" : "0") + " s";
}

/// <summary>Reads the capture data of an image: from the EXIF block of a picture, and for a camera RAW file from what
/// LibRaw reports, completed by the EXIF block of the camera's preview. Only the header is read.</summary>
public static class CaptureFacts
{
    static CaptureFacts() => RuntimeHelpers.RunClassConstructor(typeof(ImageService).TypeHandle);

    /// <summary>Null when the file tells nothing or cannot be read; never an error for a picture without EXIF.</summary>
    public static CaptureData? Read(string path, CancellationToken token = default)
    {
        try
        {
            using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
            token.ThrowIfCancellationRequested();
            CaptureData? data;
            if (ImageService.IsRaw(stream))
            {
                ImageService.ValidateSignature(stream);
                using var raw = RawImages.Ping(stream);
                data = new(RawImages.Camera(raw), null, null, null, null, null);
                token.ThrowIfCancellationRequested();
                data = Merge(data, Tiff(stream));
                if (RawImages.Preview(raw) is { } preview)
                {
                    using var jpeg = new MagickImage(); jpeg.Ping(preview.Jpeg);
                    data = Merge(data, Exif(jpeg.GetExifProfile()));
                }
            }
            else
            {
                using var image = new MagickImage(); image.Ping(stream, ImageService.Settings(stream));
                data = Exif(image.GetExifProfile());
            }
            return data is { IsEmpty: false } ? data : null;
        }
        catch (Exception error) when (error is MagickException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static CaptureData Merge(CaptureData first, CaptureData? second) => second == null ? first : new(
        first.Camera ?? second.Camera, first.Lens ?? second.Lens, first.FocalLength ?? second.FocalLength,
        first.Aperture ?? second.Aperture, first.ExposureSeconds ?? second.ExposureSeconds, first.Iso ?? second.Iso);

    internal static CaptureData? Exif(IExifProfile? exif)
    {
        if (exif == null) return null;
        static double? Positive(Rational? value) => value is { Denominator: > 0 } rational && rational.ToDouble() is > 0 and var number && double.IsFinite(number) ? number : null;
        var iso = exif.GetValue(ExifTag.ISOSpeedRatings)?.Value is { Length: > 0 } speeds && speeds[0] > 0 ? speeds[0] : (int?)null;
        return new(
            RawImages.Camera(exif.GetValue(ExifTag.Make)?.Value, exif.GetValue(ExifTag.Model)?.Value),
            FileMetadata.Clean(exif.GetValue(ExifTag.LensModel)?.Value),
            Positive(exif.GetValue(ExifTag.FocalLength)?.Value), Positive(exif.GetValue(ExifTag.FNumber)?.Value),
            Positive(exif.GetValue(ExifTag.ExposureTime)?.Value), iso);
    }

    /// <summary>Most RAW files are built like a TIFF file (DNG, CR2, NEF, ARW, PEF and others), and read as one they tell
    /// their EXIF block exactly: "exif:FNumber" = "3.500000", "exif:ExposureTime" = "0.003125". What LibRaw reports of
    /// the same comes through ImageMagick rounded to one digit ("dng:iso.setting" = "1e+02") and is not used. Null for
    /// RAW files of another build (ORF, RW2, RAF, CR3); those tell it through the camera's preview.</summary>
    private static CaptureData? Tiff(Stream stream)
    {
        try
        {
            stream.Position = 0;
            using var tiff = new MagickImage(); tiff.Ping(stream, new MagickReadSettings { Format = MagickFormat.Tiff });
            return new(
                RawImages.Camera(tiff.GetAttribute("tiff:make"), tiff.GetAttribute("tiff:model")), FileMetadata.Clean(tiff.GetAttribute("exif:LensModel")),
                Number(tiff.GetAttribute("exif:FocalLength")), Number(tiff.GetAttribute("exif:FNumber")), Seconds(tiff.GetAttribute("exif:ExposureTime")),
                Number(tiff.GetAttribute("exif:ISOSpeedRatings")) is { } iso and >= 1 and < 10_000_000 ? (int)Math.Round(iso) : null);
        }
        catch (MagickException) { return null; }
    }

    /// <summary>The first number of a text, in the invariant format; null for none and for zero.</summary>
    internal static double? Number(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var digits = new string(text.Trim().TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 && double.IsFinite(value) ? value : null;
    }

    /// <summary>"1/250" or "0.5", in seconds.</summary>
    internal static double? Seconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split('/');
        if (parts.Length == 2) return Number(parts[0]) is { } above && Number(parts[1]) is { } below ? above / below : null;
        return Number(text);
    }
}
