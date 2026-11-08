namespace FlixyPix.Core;

/// <summary>Folders the user protects must never cut FlixyPix off from its own data, or it could no longer start
/// and the protection could no longer be changed.</summary>
public static class ProtectedFolders
{
    /// <summary>True when protecting <paramref name="folder"/> would also protect one of <paramref name="ownFolders"/>
    /// or lie inside one of them. A folder that cannot be checked counts as colliding.</summary>
    public static bool CollidesWithOwnData(string folder, IEnumerable<string> ownFolders)
    {
        try
        {
            var protectedFolder = FileAccessPolicy.Normalize(folder);
            _ = new FileAccessPolicy([protectedFolder]);
            return ownFolders.Any(own => Collides(protectedFolder, FileAccessPolicy.Normalize(own)));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>The folders that can really be protected; stored settings may name others.</summary>
    public static IReadOnlyList<string> Usable(IEnumerable<string> folders, IEnumerable<string> ownFolders)
    {
        var own = ownFolders.ToList();
        return folders.Where(folder => !CollidesWithOwnData(folder, own)).ToList();
    }

    private static bool Collides(string folder, string own)
    {
        if (FileAccessPolicy.Contains(folder, own) || FileAccessPolicy.Contains(own, folder)) return true;
        // Links can only be compared where both ends are reachable; a library on an absent drive must not block anything.
        if (!Directory.Exists(folder) || !Directory.Exists(own)) return false;
        return new FileAccessPolicy([folder]).IsWriteProtected(own) || new FileAccessPolicy([own]).IsWriteProtected(folder);
    }
}
