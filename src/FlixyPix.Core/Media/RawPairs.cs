namespace FlixyPix.Core;

/// <summary>Cameras set to "RAW + JPEG" write two files per shot: <c>IMG_0042.CR2</c> and <c>IMG_0042.JPG</c> in the same
/// folder. FlixyPix keeps such a pair as one medium: the JPEG is the picture shown, the RAW file its further version, and
/// the medium falls back to the RAW file when the JPEG is gone.</summary>
public static class RawPairs
{
    /// <summary>What a camera writes beside a RAW file, most usual first.</summary>
    private static readonly string[] PictureTypes = [".jpg", ".jpeg", ".jpe", ".heic", ".heif"];

    public static bool CanHaveRaw(string picturePath) => PictureTypes.Contains(Path.GetExtension(picturePath), StringComparer.OrdinalIgnoreCase);

    /// <summary>The camera picture beside a RAW file; null when there is none.</summary>
    public static string? PictureBeside(string rawPath, Func<string, bool>? exists = null)
    {
        if (!MediaFormats.IsRaw(rawPath)) return null;
        return PictureTypes.Select(type => Path.ChangeExtension(rawPath, type)).Select(path => Existing(path, exists)).FirstOrDefault(path => path != null);
    }

    /// <summary>The RAW file beside a camera picture; null when there is none.</summary>
    public static string? RawBeside(string picturePath, Func<string, bool>? exists = null)
    {
        if (!CanHaveRaw(picturePath)) return null;
        return MediaFormats.RawExtensions.Order(StringComparer.Ordinal).Select(type => Path.ChangeExtension(picturePath, type)).Select(path => Existing(path, exists)).FirstOrDefault(path => path != null);
    }

    /// <summary>The file a medium shows: its main file, or the first further version that exists when the main file is gone
    /// (the RAW file of a pair whose JPEG was deleted).</summary>
    public static string DisplayPath(MediaItem item) =>
        item.Kind != MediaKind.Image || item.Files.Count == 0 || File.Exists(item.Path) ? item.Path : item.Files.Select(file => file.Path).FirstOrDefault(File.Exists) ?? item.Path;

    /// <summary>The RAW file among the files of an image, main file included; null when it has none.</summary>
    public static string? RawOf(MediaItem item) =>
        item.Kind != MediaKind.Image ? null : MediaFormats.IsRaw(item.Path) ? item.Path : item.Files.Select(file => file.Path).FirstOrDefault(MediaFormats.IsRaw);

    /// <summary>A version entry for a file as it lies on the drive.</summary>
    public static MediaFile FileOf(string path)
    {
        var info = new FileInfo(path);
        return new() { Path = path, FileSize = info.Length, FileModifiedAtUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) };
    }

    private static string? Existing(string path, Func<string, bool>? exists)
    {
        if (exists != null) return exists(path) ? path : null;
        // The name as the drive spells it (IMG_0042.JPG, not .jpg).
        var folder = Path.GetDirectoryName(path);
        if (folder == null || !File.Exists(path)) return null;
        try { return Directory.EnumerateFiles(folder, Path.GetFileName(path)).FirstOrDefault() ?? path; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return path; }
    }

    /// <summary>The files of one folder at a time, so a scan asks the drive once per folder and not for every possible
    /// partner of every file.</summary>
    public sealed class FolderListing
    {
        private string? folder;
        private Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The path as the drive spells it; null when the file does not exist.</summary>
        public string? Find(string path)
        {
            var parent = Path.GetDirectoryName(path);
            if (parent == null) return null;
            if (!string.Equals(parent, folder, StringComparison.OrdinalIgnoreCase))
            {
                folder = parent; files = new(StringComparer.OrdinalIgnoreCase);
                try { foreach (var file in Directory.EnumerateFiles(parent)) files[Path.GetFileName(file)] = file; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            return files.GetValueOrDefault(Path.GetFileName(path));
        }

        public string? PictureBeside(string rawPath) => MediaFormats.IsRaw(rawPath) ? PictureTypes.Select(type => Find(Path.ChangeExtension(rawPath, type))).FirstOrDefault(path => path != null) : null;
        public string? RawBeside(string picturePath) => CanHaveRaw(picturePath) ? MediaFormats.RawExtensions.Order(StringComparer.Ordinal).Select(type => Find(Path.ChangeExtension(picturePath, type))).FirstOrDefault(path => path != null) : null;
    }
}
