using System.Collections.Frozen;

namespace FlixyPix.Core;

/// <summary>Import candidates, not a promise that every codec variant is decodable.</summary>
public static class MediaFormats
{
    /// <summary>Camera RAW files: unprocessed sensor data, usually with a JPEG preview of the camera inside.</summary>
    public static IReadOnlySet<string> RawExtensions { get; } = Extensions(".dng .cr2 .cr3 .crw .nef .nrw .arw .sr2 .raf .orf .rw2 .rwl .pef .srw .iiq .erf .mrw .x3f");
    /// <summary>Images that are a finished picture, so everything but camera RAW files: what artwork, tag images and
    /// image archives take.</summary>
    public static IReadOnlySet<string> PictureExtensions { get; } = Extensions(".jpg .jpeg .jpe .jfif .png .apng .gif .bmp .tif .tiff .webp .avif .heic .heif .ico");
    public static IReadOnlySet<string> ImageExtensions { get; } = PictureExtensions.Concat(RawExtensions).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlySet<string> VideoExtensions { get; } = Extensions(".mp4 .mkv .avi .mov .wmv .asf .m4v .webm .mpg .mpeg .mpe .m2v .ts .mts .m2ts .flv .vob .3gp .3g2 .ogv");
    public static IReadOnlySet<string> AudioExtensions { get; } = Extensions(".mp3 .flac .wav .ogg .oga .m4a .aac .wma .opus .aiff .aif");
    public static string ImageDialogFilter => T("Images|{0}|All files|*.*", Pattern(ImageExtensions));
    /// <summary>For choosing a picture to store with the library (artwork, thumbnails): no camera RAW files.</summary>
    public static string PictureDialogFilter => T("Images|{0}|All files|*.*", Pattern(PictureExtensions));
    public static string MediaDialogFilter => T("Media|{0}|All files|*.*", Pattern(ImageExtensions.Concat(VideoExtensions).Concat(AudioExtensions)));
    public static MediaKind? GetKind(string path)
    {
        var extension = Path.GetExtension(path);
        return ImageExtensions.Contains(extension) ? MediaKind.Image : VideoExtensions.Contains(extension) ? MediaKind.Video : AudioExtensions.Contains(extension) ? MediaKind.Audio : null;
    }
    public static bool IsRaw(string? path) => !string.IsNullOrEmpty(path) && RawExtensions.Contains(Path.GetExtension(path));
    private static FrozenSet<string> Extensions(string extensions) => extensions.Split(' ').ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static string Pattern(IEnumerable<string> extensions) => string.Join(';',extensions.Order(StringComparer.Ordinal).Select(e => "*" + e));
}
