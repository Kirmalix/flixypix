namespace FlixyPix.Core;

/// <summary>Imports filesystem metadata only; never launches media, scripts or original executables.</summary>
public sealed class MediaScanner(CatalogRepository repository)
{

    public Task<ImportResult> ScanAsync(IEnumerable<string> paths, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var roots = paths.ToArray();
        return Task.Run(() => Scan(roots, progress, cancellationToken), cancellationToken);
    }

    public static MediaKind? GetKind(string path)
    {
        return MediaFormats.GetKind(path);
    }

    private ImportResult Scan(string[] paths, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        // File types follow the library settings, as in the original video/image settings.
        var preferences = repository.GetPreferences();
        var kindOf = FolderKinds(preferences.Previews);
        var pending = new Stack<string>(paths.Reverse());
        // Files chosen one by one are always imported when FlixyPix knows their type; the type lists only
        // narrow folder imports. Folders chosen directly are imported even when hidden.
        var chosen = new HashSet<string>(paths.Select(Full).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var warnings = new List<string>();
        var imported = 0;
        var skipped = 0;
        var processed = 0;
        while (pending.TryPop(out var candidate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var path = System.IO.Path.GetFullPath(candidate);
                if (!seen.Add(CatalogRepository.PathKey(path))) { skipped++; continue; }
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (!preferences.General.IncludeHiddenFolders && attributes.HasFlag(FileAttributes.Hidden) && !chosen.Contains(path) && Path.GetPathRoot(path) != path)
                    {
                        skipped++;
                        continue;
                    }
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        warnings.Add($"Directory link skipped to prevent traversal outside the selection: {path}");
                        skipped++;
                        continue;
                    }
                    // Enumerate incrementally so cancellation and access errors do not lose the other roots.
                    foreach (var child in Directory.EnumerateFileSystemEntries(path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pending.Push(child);
                    }
                    continue;
                }
                processed++;
                var kind = kindOf(path) ?? (chosen.Contains(path) ? GetKind(path) : null);
                // Images with a name part to leave out are skipped in folders, never when chosen one by one.
                if (kind == MediaKind.Image && !chosen.Contains(path) && preferences.General.SkipsImage(path)) kind = null;
                if (kind is null) skipped++;
                else
                {
                    var info = new FileInfo(path);
                    var item = new MediaItem { Path = path, Title = preferences.General.ImportTitle(path), Kind = kind.Value, FileSize = info.Length, FileModifiedAtUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), FileCreatedAt = FileDate(info) };
                    if (repository.AddScannedItem(item)) imported++; else skipped++;
                }
                progress?.Report(new ScanProgress(path, processed, imported));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                errors.Add($"{candidate}: {ex.Message}");
                skipped++;
            }
        }
        return new ImportResult { Imported = imported, Skipped = skipped, Errors = errors, Warnings = warnings };
    }

    /// <summary>Date of a medium's file: the original stored the creation time ("filedate"). A copy gets a new creation time
    /// but keeps the time of the last change, so the earlier of the two is taken.</summary>
    internal static DateTimeOffset? FileDate(FileInfo info)
    {
        try
        {
            var created = info.CreationTimeUtc; var written = info.LastWriteTimeUtc;
            var date = created < written ? created : written;
            return date.Year < 1980 ? null : new DateTimeOffset(date, TimeSpan.Zero);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Media kind of a file found inside a folder, following the library's type lists.</summary>
    internal static Func<string, MediaKind?> FolderKinds(PreviewPreferences previews)
    {
        var images = new HashSet<string>(previews.ImageExtensions, StringComparer.OrdinalIgnoreCase);
        var videos = new HashSet<string>(previews.VideoExtensions, StringComparer.OrdinalIgnoreCase);
        return file => { var extension = Path.GetExtension(file); return images.Contains(extension) ? MediaKind.Image : videos.Contains(extension) ? MediaKind.Video : MediaFormats.AudioExtensions.Contains(extension) ? MediaKind.Audio : null; };
    }

    private static string? Full(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
