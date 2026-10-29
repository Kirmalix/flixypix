using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace FlixyPix.Core;

/// <summary>Which parts of Kodi-style .nfo files are read, following the original's NFO settings
/// (<c>chkimportnfo_*</c>, <c>importnfodata_tags</c>, <c>importnfodata_description</c>).</summary>
public sealed record NfoPreferences
{
    public bool ImportTitle { get; init; } = true;
    public bool ImportTags { get; init; } = true;
    /// <summary>Elements whose values become tags, each in a tag category named after the element.</summary>
    public string TagElements { get; init; } = "director;genre;set;studio;year;actor";
    public bool ImportDescription { get; init; } = true;
    /// <summary>Elements written into the description, in this order; "plot" becomes the text below the facts.</summary>
    public string DescriptionElements { get; init; } = "title;year;episode;season;set;studio;country;language;mpaa;credits;director;rating;plot;actor";
    /// <summary>Also look in subfolders of the video's folder for its .nfo (original "Search sub-folders for NFO files").</summary>
    public bool SearchSubfolders { get; init; }
    /// <summary>A local picture in &lt;thumb&gt; becomes the video's thumbnail (original <c>chkimportnfo_thumbnail</c>).</summary>
    public bool ImportThumbnail { get; init; } = true;
    /// <summary>Local pictures in &lt;fanart&gt;&lt;thumb&gt; become artwork of the video (original <c>chkimportnfo_fanart</c>).</summary>
    public bool ImportFanart { get; init; } = true;

    public static IReadOnlyList<string> Split(string elements) => elements.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(e => e.ToLowerInvariant()).Distinct().ToArray();
}

/// <summary>A local picture named in an .nfo: the thumbnail (&lt;thumb&gt; below the root) or fanart (&lt;fanart&gt;&lt;thumb&gt;).</summary>
public sealed record NfoPicture(string Path, bool IsFanart, string Sha256);

/// <summary>What an .nfo file would change on one medium.</summary>
public sealed record NfoChange(Guid MediaId, string NfoPath, string TitleBefore, string? Title, string? Description, IReadOnlyDictionary<string, IReadOnlyList<string>> Tags, string DocumentSha256,
    IReadOnlyList<NfoPicture>? Pictures = null, DateOnly? ReleaseDate = null)
{
    public int TagCount => Tags.Values.Sum(t => t.Count);
    public IReadOnlyList<NfoPicture> Images => Pictures ?? [];
}
/// <param name="SkippedLinks">Pictures given as web addresses; FlixyPix downloads nothing, unlike the original.</param>
public sealed record NfoPlan(IReadOnlyList<NfoChange> Changes, int Checked, int WithoutNfo, IReadOnlyList<string> Problems, int SkippedLinks = 0);

public sealed partial class CatalogRepository
{
    public void SaveNfoPreferences(NfoPreferences nfo)
    {
        ArgumentNullException.ThrowIfNull(nfo);
        if (nfo.TagElements is null || nfo.DescriptionElements is null) throw new ArgumentException(T("The NFO element lists are missing."));
        SavePreferences(GetPreferences() with { Nfo = nfo });
    }

    /// <summary>German names for the usual Kodi elements; other elements keep their own name.</summary>
    public static string NfoLabel(string element) => element switch
    {
        "title" => T("Title"), "originaltitle" => T("Original title"), "year" => T("Year"), "episode" => T("Episode"), "season" => T("Season"), "set" => T("Set"), "studio" => T("Studio"),
        "country" => T("Country"), "language" => T("Language"), "mpaa" => T("Age rating"), "credits" => T("Screenplay"), "director" => T("Director"), "rating" => T("Rating"),
        "plot" => T("Plot"), "outline" => T("Outline"), "actor" => T("Performers"), "genre" => T("Genre"), "tag" => T("Keyword"), "showtitle" => T("Series"),
        _ => element.Length == 0 ? element : char.ToUpperInvariant(element[0]) + element[1..],
    };

    /// <summary>The .nfo of a media file: "&lt;name&gt;.nfo" beside it, then "movie.nfo" in its folder, then optionally "&lt;name&gt;.nfo" in subfolders.</summary>
    public static string? FindNfo(string mediaPath, bool subfolders)
    {
        var folder = Path.GetDirectoryName(mediaPath); if (folder == null || !Directory.Exists(folder)) return null;
        var name = Path.GetFileNameWithoutExtension(mediaPath) + ".nfo";
        foreach (var candidate in new[] { Path.Combine(folder, name), Path.Combine(folder, "movie.nfo") }) if (File.Exists(candidate)) return candidate;
        if (!subfolders) return null;
        try { return Directory.EnumerateFiles(folder, name, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).FirstOrDefault(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Element values of an .nfo (actors and sets by their name); null for a file that is no XML, such as a plain link.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? ReadNfo(string path)
    {
        XDocument document;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 4 * 1024 * 1024) return null;
            document = XDocument.Load(XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }));
        }
        catch (XmlException) { return null; }
        if (document.Root is not { } root) return null;
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in root.Elements())
        {
            var key = element.Name.LocalName.ToLowerInvariant();
            var value = (key is "actor" or "set" ? element.Element("name")?.Value ?? (element.HasElements ? null : element.Value) : element.HasElements ? null : element.Value)?.Trim();
            if (string.IsNullOrEmpty(value) || value.Length > 8000) continue;
            if (!values.TryGetValue(key, out var list)) values[key] = list = [];
            if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
        }
        return values.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Picture references of an .nfo in file order: &lt;thumb&gt; directly below the root and &lt;fanart&gt;&lt;thumb&gt;; actor pictures are left out.</summary>
    public static IReadOnlyList<(string Reference, bool IsFanart)> ReadNfoPictures(string path)
    {
        XDocument document;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 4 * 1024 * 1024) return [];
            document = XDocument.Load(XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }));
        }
        catch (XmlException) { return []; }
        if (document.Root is not { } root) return [];
        static bool Is(XElement element, string name) => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);
        var pictures = new List<(string, bool)>();
        foreach (var element in root.Elements())
        {
            if (Is(element, "thumb")) pictures.Add((element.Value.Trim(), false));
            else if (Is(element, "fanart")) pictures.AddRange(element.Elements().Where(e => Is(e, "thumb")).Select(e => (e.Value.Trim(), true)));
        }
        return pictures.Where(p => p.Item1.Length is > 0 and <= 2048).ToList();
    }

    /// <summary>A picture reference as a local file: absolute, or relative to the video's folder and then to the .nfo's folder
    /// (as the original did). Web addresses and anything that is no readable picture up to 32 MiB give null.</summary>
    public static string? ResolveNfoPicture(string reference, string mediaPath, string nfoPath)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out var uri) && !uri.IsFile) return null;
        IEnumerable<string> candidates = Path.IsPathFullyQualified(reference) ? [reference] : new[] { Path.GetDirectoryName(mediaPath), Path.GetDirectoryName(nfoPath) }
            .OfType<string>().Select(folder => Path.GetFullPath(Path.Combine(folder, reference)));
        var head = new byte[32];
        foreach (var candidate in candidates)
        {
            try
            {
                var info = new FileInfo(candidate);
                if (!info.Exists || info.Length is 0 or > MaximumAssetBytes) continue;
                using var stream = info.OpenRead(); var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                if (ImageExtension(head.AsSpan(0, read)) != null) return info.FullName;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return null;
    }

    private static DateOnly? ParseReleaseDate(string? value) =>
        value is { Length: >= 10 } && DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date : null;

    private static string FileSha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }

    /// <summary>Reads the .nfo files of the given media with the library's NFO settings; nothing is written.</summary>
    public NfoPlan PlanNfoImport(IReadOnlyCollection<Guid> mediaIds, CancellationToken token = default)
    {
        var settings = GetPreferences().Nfo;
        var tagElements = NfoPreferences.Split(settings.TagElements); var descriptionElements = NfoPreferences.Split(settings.DescriptionElements);
        var changes = new List<NfoChange>(); var problems = new List<string>(); int without = 0, checkedCount = 0, links = 0;
        foreach (var id in mediaIds.Distinct())
        {
            token.ThrowIfCancellationRequested();
            string? document;
            lock (gate) { ThrowIfDisposed(); using var command = Command("SELECT document FROM media WHERE id=$id", null, ("$id", id.ToString())); document = command.ExecuteScalar() as string; }
            if (document == null || JsonSerializer.Deserialize<MediaItem>(document, Json) is not { } item) continue;
            checkedCount++;
            if (FindNfo(item.Path, settings.SearchSubfolders) is not { } nfo) { without++; continue; }
            var values = ReadNfo(nfo);
            if (values == null) { problems.Add(Path.GetFileName(nfo) + T(": not a readable XML file")); continue; }
            string? Value(string key) => values.TryGetValue(key, out var list) ? list[0] : null;
            var title = settings.ImportTitle && Value("title") is { } t && t != item.Title ? t : null;
            string? description = null;
            if (settings.ImportDescription)
            {
                var facts = descriptionElements.Where(e => e != "plot" && values.ContainsKey(e)).Select(e => NfoLabel(e) + ": " + string.Join(", ", values[e]));
                var text = string.Join("\n", facts);
                if (descriptionElements.Contains("plot") && Value("plot") is { } plot) text = text.Length == 0 ? plot : text + "\n\n" + plot;
                if (text.Length > 0 && text != item.Description) description = text;
            }
            var tags = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (settings.ImportTags)
                foreach (var element in tagElements.Where(values.ContainsKey))
                {
                    var category = NfoLabel(element);
                    var present = item.TagCategories.GetValueOrDefault(category) ?? [];
                    var fresh = values[element].Where(v => v.Length <= 200 && !present.Contains(v, StringComparer.OrdinalIgnoreCase)).ToArray();
                    if (fresh.Length > 0) tags[category] = fresh;
                }
            var pictures = new List<NfoPicture>();
            if (settings.ImportThumbnail || settings.ImportFanart)
            {
                // Pictures the video already has (same bytes) are not added again.
                HashSet<string> known; lock (gate) known = ReadMediaAssetMetadata(item.Id).Select(a => a.Sha256).ToHashSet(StringComparer.Ordinal);
                foreach (var (reference, fanart) in ReadNfoPictures(nfo))
                {
                    if (fanart ? !settings.ImportFanart : !settings.ImportThumbnail || pictures.Any(p => !p.IsFanart)) continue;
                    if (Uri.TryCreate(reference, UriKind.Absolute, out var uri) && !uri.IsFile) { links++; continue; }
                    if (ResolveNfoPicture(reference, item.Path, nfo) is not { } file) { problems.Add(Path.GetFileName(nfo) + T(": image not found · ") + reference); continue; }
                    var hash = FileSha256(file);
                    if (known.Add(hash)) pictures.Add(new(file, fanart, hash));
                }
            }
            // Kodi writes the release date as <premiered> (films) or <aired> (episodes), always as yyyy-MM-dd.
            var released = new[] { Value("premiered"), Value("aired") }.Select(ParseReleaseDate).FirstOrDefault(d => d is not null);
            if (released == item.ReleaseDate) released = null;
            if (title == null && description == null && tags.Count == 0 && pictures.Count == 0 && released == null) continue;
            changes.Add(new(item.Id, nfo, item.Title, title, description, tags, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document))), pictures, released));
        }
        return new(changes, checkedCount, without, problems, links);
    }

    /// <summary>Applies the chosen changes in one transaction; a medium edited since the preview is skipped. Returns the number changed.
    /// Pictures are stored in the catalog; a picture changed since the preview is left out. A new thumbnail clears
    /// <see cref="MediaItem.ThumbnailPath"/>, so the app shows the stored picture (<c>thumbnail.assetId</c>) instead.</summary>
    public int ApplyNfoImport(IReadOnlyList<NfoChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        lock (gate)
        {
            ThrowIfDisposed(); using var transaction = connection.BeginTransaction(); var applied = 0;
            foreach (var change in changes)
            {
                using var command = Command("SELECT document FROM media WHERE id=$id", transaction, ("$id", change.MediaId.ToString()));
                if (command.ExecuteScalar() is not string document || Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document))) != change.DocumentSha256) continue;
                var item = JsonSerializer.Deserialize<MediaItem>(document, Json)!;
                var categories = item.TagCategories.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.OrdinalIgnoreCase);
                foreach (var (category, tags) in change.Tags) categories[category] = (categories.GetValueOrDefault(category) ?? []).Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var extra = new Dictionary<string, string?>(item.Extra) { ["nfo.source"] = change.NfoPath }; var thumbnailPath = item.ThumbnailPath;
                using var count = Command("SELECT COUNT(*) FROM media_assets WHERE media_id=$media", transaction, ("$media", item.Id.ToString()));
                var position = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                foreach (var picture in change.Images)
                {
                    byte[] data;
                    try { data = File.ReadAllBytes(picture.Path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
                    if (data.Length is 0 or > MaximumAssetBytes || Convert.ToHexStringLower(SHA256.HashData(data)) != picture.Sha256) continue;
                    var asset = new MediaAsset
                    {
                        MediaId = item.Id, Data = data, IsCustom = true, Position = position++, Kind = picture.IsFanart ? MediaAssetKind.Artwork : MediaAssetKind.Thumbnail,
                        Label = picture.IsFanart ? T("Fanart from NFO") : T("Thumbnail from NFO"), Extra = new() { ["nfo.source"] = picture.Path },
                    };
                    ValidateAsset(asset); SaveAsset(asset, transaction);
                    if (!picture.IsFanart) { extra["thumbnail.assetId"] = asset.Id.ToString(); thumbnailPath = null; }
                }
                UpsertCore(item with
                {
                    Title = change.Title ?? item.Title, Description = change.Description ?? item.Description, TagCategories = categories, ReleaseDate = change.ReleaseDate ?? item.ReleaseDate,
                    ThumbnailPath = thumbnailPath, Extra = extra,
                }, transaction);
                applied++;
            }
            transaction.Commit(); return applied;
        }
    }
}
