using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FlixyPix.Core;

/// <summary>What the takeover assistant can copy from an installation besides the catalog itself.</summary>
public enum LegacyDataGroup { VideoThumbnails, BookmarkImages, ClipPreviews, TagImages }

public sealed record LegacyFolderSummary(LegacyDataGroup Group, string Folder, int Files, long Bytes);

/// <summary>An installation of the original found on this computer. Everything here is read only; nothing in it is ever changed.</summary>
public sealed record LegacyInstallation
{
    public required string ProgramFolder { get; init; }
    public string? Version { get; init; }
    /// <summary>The catalog the original opened by default (<c>defaultUserDatabase</c>); other catalogs are not offered.</summary>
    public required string CatalogPath { get; init; }
    public long CatalogBytes { get; init; }
    /// <summary><c>&lt;cache root&gt;\cache\&lt;catalog file name&gt;</c>, holding vcache, bcache and ccache.</summary>
    public required string CacheFolder { get; init; }
    public required string TagImageFolder { get; init; }
    /// <summary>The user.config the cache root and default catalog were read from, if any.</summary>
    public string? UserConfigPath { get; init; }
    /// <summary>The FlixyPix language for the original's <c>Language</c> in user.config; null when unset or not spoken by FlixyPix.</summary>
    public string? Language { get; init; }
    public IReadOnlyList<LegacyFolderSummary> Folders { get; init; } = [];
    public bool HasCatalog => CatalogBytes > 0;
    public LegacyFolderSummary? Folder(LegacyDataGroup group) => Folders.FirstOrDefault(f => f.Group == group);
}

/// <summary>Where to look; tests replace the defaults with a synthetic installation.</summary>
public sealed record LegacyInstallationSearch
{
    public IReadOnlyList<string> ProgramFolders { get; init; } = DefaultProgramFolders();
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string[] DefaultProgramFolders() =>
        new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(Environment.GetFolderPath).Where(p => p.Length > 0)
            .Select(p => Path.Combine(p, LegacyInstallations.FolderName)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>Finds installations of the original from their usual folders, uninstall.log and the per-version user.config,
/// following the original's own rules (doSetupCacheFolder): the cache root is the program folder unless CacheFolder names
/// an existing folder, and the catalog's cache lives in <c>cache\&lt;catalog file name&gt;</c>.</summary>
public static partial class LegacyInstallations
{
    public const string ProgramFile = "EazyFlixPix.exe";
    public static string FolderName => Path.GetFileNameWithoutExtension(ProgramFile);
    private const string DefaultCatalog = "mmvdb.sdf";

    [GeneratedRegex(@"^\d+\.jpg$", RegexOptions.IgnoreCase)] internal static partial Regex VideoThumbnailName();
    [GeneratedRegex(@"^\d+_\d+\.jpg$", RegexOptions.IgnoreCase)] internal static partial Regex BookmarkImageName();
    internal static readonly string[] TagImageExtensions = [".jpg", ".png", ".jpeg", ".gif", ".bmp"];

    public static IReadOnlyList<LegacyInstallation> Find(LegacyInstallationSearch? search = null)
    {
        search ??= new();
        return CandidateFolders(search).Select(f => Inspect(f, search)).OfType<LegacyInstallation>().ToList();
    }

    /// <summary>Program folders that hold the program file, without inspecting catalogs or caches.</summary>
    public static IReadOnlyList<string> ProgramFolders(LegacyInstallationSearch? search = null) =>
        CandidateFolders(search ?? new()).Where(f => Safe(() => File.Exists(Path.Combine(f, ProgramFile)))).ToList();

    private static List<string> CandidateFolders(LegacyInstallationSearch search)
    {
        var folders = new List<string>();
        void Add(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            try { folder = Path.GetFullPath(folder.Trim()).TrimEnd(Path.DirectorySeparatorChar); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return; }
            if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) folders.Add(folder);
        }
        foreach (var folder in search.ProgramFolders) Add(folder);
        // uninstall.log lists every installed file, so it also names earlier install locations (for example on another drive).
        foreach (var folder in folders.ToArray()) foreach (var earlier in UninstallLogFolders(folder)) Add(earlier);
        return folders;
    }

    /// <summary>The private SQL Server Compact 4.0 copy the original installs next to its program. FlixyPix does not ship
    /// SQL Server Compact; its exporter loads these files from the original's folder instead.</summary>
    public static readonly IReadOnlyList<string> SqlCeRuntimeFiles = ["System.Data.SqlServerCe.dll", "sqlceme40.dll", "sqlcese40.dll", "sqlceqp40.dll", "sqlceer40EN.dll"];

    public static bool HasSqlCeRuntime(string folder) => SqlCeRuntimeFiles.All(file => Safe(() => File.Exists(Path.Combine(folder, file))));

    /// <summary>Where the exporter finds SQL Server Compact: <paramref name="preferred"/> (the chosen installation), else any
    /// installation found on this computer; null leaves it to a system-wide Microsoft SQL Server Compact 4.0 SP1.</summary>
    public static string? SqlCeRuntimeFolder(string? preferred, LegacyInstallationSearch? search = null)
    {
        if (preferred != null && HasSqlCeRuntime(preferred)) return Path.GetFullPath(preferred);
        return Find(search).Select(i => i.ProgramFolder).FirstOrDefault(HasSqlCeRuntime);
    }

    /// <summary>Reads one program folder; null when it holds no program file of the original.</summary>
    public static LegacyInstallation? Inspect(string programFolder, LegacyInstallationSearch? search = null)
    {
        search ??= new();
        var folder = Path.GetFullPath(programFolder).TrimEnd(Path.DirectorySeparatorChar);
        var exe = Path.Combine(folder, ProgramFile);
        if (!Safe(() => File.Exists(exe))) return null;
        var version = Safe(() => FileVersionInfo.GetVersionInfo(exe).FileVersion);
        var settings = ReadSettings(Path.Combine(folder, ProgramFile + ".config"));
        var userConfig = NewestUserConfig(search.LocalAppData, version);
        if (userConfig != null) foreach (var (key, value) in ReadSettings(userConfig)) settings[key] = value;

        var catalogName = settings.GetValueOrDefault("defaultUserDatabase") is { Length: > 0 } configured ? configured.Trim() : DefaultCatalog;
        var catalog = Path.IsPathFullyQualified(catalogName) ? catalogName : Path.Combine(folder, catalogName);
        var cacheRoot = settings.GetValueOrDefault("CacheFolder") is { Length: > 0 } root && !root.Equals("DEFAULT", StringComparison.Ordinal) && Safe(() => Directory.Exists(root)) ? root.TrimEnd('\\', '/') : folder;
        return Build(folder, version, catalog, cacheRoot, userConfig) with { Language = Texts.FromOriginal(settings.GetValueOrDefault("Language")) };
    }

    /// <summary>A catalog file without a program next to it, for example a copy: its caches are looked for beside it.</summary>
    public static LegacyInstallation FromCatalog(string catalogPath)
    {
        var catalog = Path.GetFullPath(catalogPath);
        var folder = Path.GetDirectoryName(catalog) ?? throw new ArgumentException(T("The database needs a folder."));
        return Build(folder, null, catalog, folder, null);
    }

    private static LegacyInstallation Build(string folder, string? version, string catalog, string cacheRoot, string? userConfig)
    {
        var cache = Path.Combine(cacheRoot, "cache", Path.GetFileName(catalog));
        var tagImages = Path.Combine(folder, "tag_images");
        return new LegacyInstallation
        {
            ProgramFolder = folder, Version = version, CatalogPath = catalog, CatalogBytes = Safe(() => new FileInfo(catalog) is { Exists: true } info ? info.Length : 0),
            CacheFolder = cache, TagImageFolder = tagImages, UserConfigPath = userConfig,
            Folders =
            [
                Summarize(LegacyDataGroup.VideoThumbnails, Path.Combine(cache, "vcache"), name => VideoThumbnailName().IsMatch(name), false),
                Summarize(LegacyDataGroup.BookmarkImages, Path.Combine(cache, "bcache"), name => BookmarkImageName().IsMatch(name), false),
                Summarize(LegacyDataGroup.ClipPreviews, Path.Combine(cache, "ccache"), name => VideoThumbnailName().IsMatch(name), false),
                Summarize(LegacyDataGroup.TagImages, tagImages, name => TagImageExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase), true),
            ],
        };
    }

    private static LegacyFolderSummary Summarize(LegacyDataGroup group, string folder, Func<string, bool> accept, bool subfolders)
    {
        int files = 0; long bytes = 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = subfolders, MaxRecursionDepth = 1, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            if (Directory.Exists(folder))
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
                    if (accept(file.Name) && (!subfolders || !string.Equals(file.DirectoryName, folder, StringComparison.OrdinalIgnoreCase))) { files++; bytes += file.Length; }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new(group, folder, files, bytes);
    }

    private static IEnumerable<string> UninstallLogFolders(string folder)
    {
        var log = Path.Combine(folder, "uninstall.log");
        string[] lines;
        try { if (!File.Exists(log)) yield break; lines = File.ReadAllLines(log); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var line in lines)
        {
            var path = line.Trim().Trim('"');
            if (path.EndsWith("\\" + ProgramFile, StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(path)) yield return Path.GetDirectoryName(path)!;
        }
    }

    /// <summary>Settings are kept per program path and version under %LOCALAPPDATA%\EazyMojo; the newest one of the same
    /// version wins, otherwise the newest of any version.</summary>
    internal static string? NewestUserConfig(string localAppData, string? version)
    {
        var root = Path.Combine(localAppData, "EazyMojo");
        try
        {
            if (!Directory.Exists(root)) return null;
            var files = Directory.EnumerateDirectories(root, FolderName + ".exe_Url_*")
                .SelectMany(d => Directory.EnumerateDirectories(d)).Select(v => new FileInfo(Path.Combine(v, "user.config"))).Where(f => f.Exists).ToList();
            var same = files.Where(f => version != null && f.Directory!.Name.Equals(version, StringComparison.OrdinalIgnoreCase)).ToList();
            return (same.Count > 0 ? same : files).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>String settings of a .NET settings file (application defaults or user.config).</summary>
    internal static Dictionary<string, string> ReadSettings(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return result;
            var document = XDocument.Load(path);
            foreach (var setting in document.Descendants("setting"))
                if (setting.Attribute("name")?.Value is { } name && (string?)setting.Attribute("serializeAs") is null or "String")
                    result[name] = setting.Element("value")?.Value ?? "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException) { }
        return result;
    }

    private static T? Safe<T>(Func<T> read)
    {
        try { return read(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return default; }
    }
}
