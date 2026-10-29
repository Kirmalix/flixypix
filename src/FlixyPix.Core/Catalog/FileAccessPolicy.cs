namespace FlixyPix.Core;

/// <summary>Application-level protection for immutable sources. Reads require no permission change.
/// Recheck immediately before publication/removal; this is not an operating-system security boundary.</summary>
public sealed class FileAccessPolicy
{
    private readonly string[] protectedPaths;
    private readonly string[] resolvedProtectedPaths;
    private static FileAccessPolicy current = new([@"U:\"]);
    public static FileAccessPolicy Current => Volatile.Read(ref current);
    public IReadOnlyList<string> ProtectedPaths => Array.AsReadOnly(protectedPaths);

    public FileAccessPolicy(IEnumerable<string> protectedPaths)
    {
        ArgumentNullException.ThrowIfNull(protectedPaths);
        this.protectedPaths = protectedPaths.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        resolvedProtectedPaths = this.protectedPaths.Where(p => !this.protectedPaths.Any(root => IsDriveRoot(root) && Contains(root,p)))
            .Select(ResolveExistingLinks).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static void ConfigureCurrent(IEnumerable<string> additionalProtectedPaths)
    {
        ArgumentNullException.ThrowIfNull(additionalProtectedPaths);
        Interlocked.Exchange(ref current, new FileAccessPolicy(new[] { @"U:\" }.Concat(additionalProtectedPaths)));
    }

    public bool IsWriteProtected(string path)
    {
        try { EnsureWritable(path); return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public void EnsureWritable(string path)
    {
        var full = Normalize(path);
        if (protectedPaths.Any(p => Contains(p, full))) Deny(); // Never query an offline protected drive.
        var resolved = ResolveExistingLinks(full);
        if (protectedPaths.Any(p => Contains(p, resolved))) Deny();
        // A protected root may itself be an alias, so compare its resolved location too.
        if (resolvedProtectedPaths.Any(p => Contains(p, resolved))) Deny();
        void Deny() => throw new UnauthorizedAccessException(T("The source is write-protected: ") + full);
    }

    private string ResolveExistingLinks(string path)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; depth < 32; depth++)
        {
            if (!seen.Add(path)) throw new UnauthorizedAccessException(T("A write path contains a cyclic link."));
            var root = Path.GetPathRoot(path)!;
            var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var prefix = root; var changed = false;
            for (var index = 0; index < parts.Length; index++)
            {
                prefix = Path.Combine(prefix, parts[index]);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(prefix); }
                catch (FileNotFoundException) { RejectDanglingLink(prefix); break; }
                catch (DirectoryNotFoundException) { RejectDanglingLink(prefix); break; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { throw new UnauthorizedAccessException(T("The write path cannot be checked safely: ") + prefix, error); }
                if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
                try
                {
                    FileSystemInfo link = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(prefix) : new FileInfo(prefix);
                    // Resolve one hop so a link into protected U: is rejected before accessing its target.
                    var target = link.ResolveLinkTarget(false) ?? throw new IOException(T("Unresolved reparse point."));
                    path = Normalize(Path.Combine(new[] { target.FullName }.Concat(parts.Skip(index + 1)).ToArray()));
                    if (protectedPaths.Any(p => Contains(p, path))) throw new UnauthorizedAccessException(T("The link leads into a write-protected source."));
                    _ = File.GetAttributes(target.FullName);
                    changed = true; break;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
                { throw new UnauthorizedAccessException(T("The link in the write path cannot be resolved safely: ") + prefix, error); }
            }
            if (!changed) return path;
        }
        throw new UnauthorizedAccessException(T("Too many links in the write path."));
    }

    private static void RejectDanglingLink(string path)
    {
        try
        {
            if (new DirectoryInfo(path).LinkTarget != null || new FileInfo(path).LinkTarget != null)
                throw new UnauthorizedAccessException(T("A link in the write path has no reachable target: ") + path);
        }
        catch (Exception error) when (error is IOException or NotSupportedException)
        { throw new UnauthorizedAccessException(T("The write path cannot be resolved safely: ") + path,error); }
    }

    private static bool IsDriveRoot(string path) => string.Equals(path, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase);
    private static bool Contains(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        // Device paths bypass ordinary Win32 normalization and must never be output targets.
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0)
            throw new UnauthorizedAccessException(T("Device paths and alternate data streams are not allowed write targets."));
        foreach(var part in full[Path.GetPathRoot(full)!.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries))
        {
            var stem=part.Split('.')[0].ToUpperInvariant();
            if(part.EndsWith('.')||part.EndsWith(' ')||stem is "CON" or "PRN" or "AUX" or "NUL"||
                stem.Length==4&&(stem.StartsWith("COM",StringComparison.Ordinal)||stem.StartsWith("LPT",StringComparison.Ordinal))&&stem[3] is >= '0' and <= '9')
                throw new UnauthorizedAccessException(T("Ambiguous Windows path names and device names are not allowed write targets."));
        }
        return Path.TrimEndingDirectorySeparator(full);
    }
}
