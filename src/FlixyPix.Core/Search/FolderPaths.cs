namespace FlixyPix.Core;

public static class FolderPaths
{
    /// <summary>The folder whose content can stand at the top of a folder tree: everything lies below it, and neither it nor
    /// the folders above it hold media of their own. Null when nothing can be left out, for example with several drives.</summary>
    /// <param name="folders">The folders that directly hold media.</param>
    public static string? HiddenBase(IReadOnlyCollection<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        string? common = null;
        foreach (var folder in folders)
        {
            common ??= folder;
            while (common is not null && !IsAtOrBelow(folder, common)) common = Path.GetDirectoryName(common);
            if (common is null) return null;
        }
        if (common is null) return null;
        // A common folder with media of its own stays selectable, so only what lies above it goes.
        return folders.Any(folder => Same(folder, common)) ? Path.GetDirectoryName(common) : common;
    }

    private static bool Same(string left, string right) =>
        Path.TrimEndingDirectorySeparator(left).Equals(Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsAtOrBelow(string path, string folder) =>
        Same(path, folder) || path.StartsWith(folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
