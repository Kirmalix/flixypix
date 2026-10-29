using System.Globalization;
using System.Text;

namespace FlixyPix.Core;

/// <summary>Extended M3U playlists (original: "Export list to .m3u playlist"). Paths are absolute; the text is UTF-8,
/// which players expect for <c>.m3u8</c> and accept for <c>.m3u</c>. Media files are only listed, never touched.</summary>
public static class M3uPlaylists
{
    public static string Build(IEnumerable<MediaItem> items)
    {
        var text = new StringBuilder("#EXTM3U\n");
        foreach (var item in items)
        {
            var seconds = item.DurationSeconds is > 0 and var duration ? ((long)Math.Round(duration)).ToString(CultureInfo.InvariantCulture) : "-1";
            var title = (string.IsNullOrWhiteSpace(item.Title) ? Path.GetFileNameWithoutExtension(item.Path) : item.Title).ReplaceLineEndings(" ").Trim();
            text.Append("#EXTINF:").Append(seconds).Append(',').Append(title).Append('\n').Append(Path.GetFullPath(item.Path)).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>Writes atomically through a temporary file; an existing playlist is replaced only when complete.</summary>
    public static void Write(string path, IEnumerable<MediaItem> items)
    {
        var target = Path.GetFullPath(path);
        FileAccessPolicy.Current.EnsureWritable(target);
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, Build(items), new UTF8Encoding(false));
        File.Move(temporary, target, true);
    }

    /// <summary>The local files of a playlist in order: comments and web addresses are skipped, <c>file:</c> addresses and
    /// paths relative to the playlist are resolved.</summary>
    public static IReadOnlyList<string> Read(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var files = new List<string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim().TrimStart((char)0xFEFF);
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.Scheme != Uri.UriSchemeFile && uri.Scheme.Length > 1) continue;
            try { files.Add(uri is { IsFile: true } ? uri.LocalPath : Path.GetFullPath(Path.Combine(folder, line))); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return files;
    }
}

public sealed record PlaylistImport(Playlist? Collection, int Entries, IReadOnlyList<string> NotInCatalog);

public sealed partial class CatalogRepository
{
    /// <summary>Makes a collection named after the playlist from the entries that are in the catalog, in their order.
    /// No collection is made when none is.</summary>
    public PlaylistImport ImportM3u(string path)
    {
        var files = M3uPlaylists.Read(path);
        var ids = new List<Guid>(); var missing = new List<string>();
        foreach (var file in files)
            if (GetMediaByPath(file) is { } item) { if (!ids.Contains(item.Id)) ids.Add(item.Id); }
            else missing.Add(file);
        if (ids.Count == 0) return new(null, files.Count, missing);
        var name = Path.GetFileNameWithoutExtension(path);
        var collection = UpsertPlaylist(new Playlist { Name = string.IsNullOrWhiteSpace(name) ? "M3U" : name, MediaIds = ids, Entries = ids.Select(id => new PlaylistEntry(id)).ToList() });
        return new(collection, files.Count, missing);
    }
}
