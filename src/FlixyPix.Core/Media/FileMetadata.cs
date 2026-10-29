using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace FlixyPix.Core;

/// <summary>Descriptive metadata of one media file, from an XMP sidecar or from the file itself (EXIF, IPTC, XMP,
/// Windows properties). <see cref="Stars"/> is 0–5 as in XMP; <see cref="Keywords"/> are flat,
/// <see cref="HierarchicalKeywords"/> are paths such as "Personen|Familie|Anna".</summary>
public sealed record FileMetadata
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public int? Stars { get; init; }
    public DateTimeOffset? TakenAt { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public IReadOnlyList<string> HierarchicalKeywords { get; init; } = [];

    public bool IsEmpty => Title is null && Description is null && Stars is null && TakenAt is null && Keywords.Count == 0 && HierarchicalKeywords.Count == 0;

    /// <summary>This metadata with its gaps filled from <paramref name="other"/>; keywords of both are kept.</summary>
    public FileMetadata Or(FileMetadata? other) => other is null ? this : new()
    {
        Title = Title ?? other.Title, Description = Description ?? other.Description, Stars = Stars ?? other.Stars, TakenAt = TakenAt ?? other.TakenAt,
        Keywords = Keywords.Concat(other.Keywords).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        HierarchicalKeywords = HierarchicalKeywords.Concat(other.HierarchicalKeywords).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
    };

    /// <summary>Tags by category. A hierarchical keyword "Kategorie|…|Tag" becomes the tag in the category of its top level;
    /// flat keywords go under the empty category, except those that only repeat a level of a hierarchical keyword.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TagsByCategory()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var levels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string category, string tag)
        {
            if (tag.Length > 200) return;
            if (!result.TryGetValue(category, out var list)) result[category] = list = [];
            if (!list.Contains(tag, StringComparer.OrdinalIgnoreCase)) list.Add(tag);
        }
        foreach (var path in HierarchicalKeywords)
        {
            var parts = path.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            levels.UnionWith(parts);
            if (parts.Length == 1) Add("", parts[0]);
            else if (parts.Length > 1 && parts[0].Length <= 200) Add(parts[0], parts[^1]);
        }
        foreach (var keyword in Keywords) if (!levels.Contains(keyword)) Add("", keyword);
        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A trimmed text, or null for empty texts and the placeholders cameras write ("OLYMPUS DIGITAL CAMERA").</summary>
    public static string? Clean(string? text)
    {
        var value = text?.Replace("\0", "").Trim();
        return string.IsNullOrEmpty(value) || value.Length > 8000 || Placeholders.Contains(value) ? null : value;
    }

    /// <summary>Keywords from a text that may hold several separated by semicolons (Windows "Markierungen", EXIF XPKeywords).</summary>
    public static IEnumerable<string> SplitKeywords(string? text) =>
        (text ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(Clean).OfType<string>();

    /// <summary>Dates as XMP (ISO 8601) and EXIF ("2024:07:14 18:30:00") write them; dates without a day are ignored.</summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        var value = text?.Replace("\0", "").Trim();
        if (string.IsNullOrEmpty(value) || value.Length < 10) return null;
        if (DateTime.TryParseExact(value, ["yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm", "yyyy:MM:dd"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var exif))
            return new DateTimeOffset(exif);
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var iso) && iso.Year > 1800) return iso;
        return null;
    }

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "OLYMPUS DIGITAL CAMERA", "SONY DSC", "DIGITAL CAMERA", "KONICA MINOLTA DIGITAL CAMERA", "DCIM", "default", "Untitled", "Created with GIMP",
    };
}

/// <summary>Reads the metadata embedded in a media file; null when the file has none or its kind is not supported.</summary>
public interface IFileMetadataReader
{
    FileMetadata? Read(string path, MediaKind kind);
}

/// <summary>XMP sidecar files (<c>Bild.jpg.xmp</c>, as darktable and digiKam write them, or <c>Bild.xmp</c>, as Lightroom does).
/// FlixyPix reads them and writes title, description, rating and tags into them; the media file itself is never touched.</summary>
public static class XmpSidecar
{
    private const long MaximumBytes = 4 * 1024 * 1024;
    private static readonly XNamespace Meta = "adobe:ns:meta/", Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#",
        Dc = "http://purl.org/dc/elements/1.1/", Xmp = "http://ns.adobe.com/xap/1.0/", Lr = "http://ns.adobe.com/lightroom/1.0/",
        Photoshop = "http://ns.adobe.com/photoshop/1.0/", Exif = "http://ns.adobe.com/exif/1.0/";

    /// <summary>The existing sidecar of a media file: "&lt;Datei&gt;.xmp", otherwise "&lt;Name&gt;.xmp" when no other file
    /// in the folder has the same name; null when there is none.</summary>
    public static string? Find(string mediaPath)
    {
        var full = mediaPath + ".xmp";
        if (File.Exists(full)) return full;
        var stem = Path.ChangeExtension(mediaPath, ".xmp");
        if (!File.Exists(stem) || Path.GetDirectoryName(mediaPath) is not { } folder) return null;
        try
        {
            var namesakes = Directory.EnumerateFiles(folder, Path.GetFileNameWithoutExtension(mediaPath) + ".*")
                .Count(f => !f.EndsWith(".xmp", StringComparison.OrdinalIgnoreCase));
            return namesakes <= 1 ? stem : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Where FlixyPix writes the sidecar: into the existing one, otherwise "&lt;Datei&gt;.xmp".</summary>
    public static string PathFor(string mediaPath) => Find(mediaPath) ?? mediaPath + ".xmp";

    /// <summary>Reads a sidecar; null when it is no XMP document.</summary>
    public static FileMetadata? Read(string sidecarPath) => Load(ReadText(sidecarPath)) is { } document ? Parse(document) : null;

    /// <summary>Reads an XMP packet, for example the one embedded in an image; null when it is no XMP document.</summary>
    public static FileMetadata? Parse(string xml) => Load(xml) is { } document ? Parse(document) : null;

    private static FileMetadata Parse(XDocument document)
    {
        var descriptions = document.Descendants(Rdf + "Description").ToArray();
        IEnumerable<string> Values(XNamespace ns, string name)
        {
            foreach (var description in descriptions)
            {
                if (description.Attribute(ns + name) is { } attribute) yield return attribute.Value;
                foreach (var element in description.Elements(ns + name))
                {
                    if (Container(element) is not { } container) { if (!element.HasElements) yield return element.Value; continue; }
                    var items = container.Elements(Rdf + "li").ToList();
                    if (container.Name == Rdf + "Alt") { if (DefaultItem(items) is { } item) yield return item.Value; }
                    else foreach (var item in items) yield return item.Value;
                }
            }
        }
        string? First(XNamespace ns, string name) => Values(ns, name).Select(FileMetadata.Clean).FirstOrDefault(v => v != null);
        int? stars = null;
        if (First(Xmp, "Rating") is { } rating && double.TryParse(rating, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 5)
            stars = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        return new()
        {
            Title = First(Dc, "title"), Description = First(Dc, "description"), Stars = stars,
            TakenAt = new[] { Values(Exif, "DateTimeOriginal"), Values(Photoshop, "DateCreated"), Values(Xmp, "CreateDate") }
                .SelectMany(v => v).Select(FileMetadata.ParseDate).FirstOrDefault(d => d != null),
            Keywords = Values(Dc, "subject").SelectMany(FileMetadata.SplitKeywords).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            HierarchicalKeywords = Values(Lr, "hierarchicalSubject").Select(FileMetadata.Clean).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    /// <summary>The sidecar text after merging the medium into <paramref name="existing"/> (null for a new sidecar).
    /// Tags are added to the keywords already there; title, description and rating replace the default-language value
    /// only when the medium has one. Everything else in the sidecar stays as it was.</summary>
    public static string Merge(string? existing, MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var document = existing is null ? NewDocument() : Load(existing) ?? throw new InvalidDataException(T("The existing sidecar is not an XMP document."));
        var rdf = document.Descendants(Rdf + "RDF").FirstOrDefault() ?? throw new InvalidDataException(T("The existing sidecar is not an XMP document."));
        var descriptions = rdf.Elements(Rdf + "Description").ToList();
        var target = descriptions.FirstOrDefault();
        if (target is null) { target = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", "")); rdf.Add(target); descriptions.Add(target); }

        var flat = item.Tags.Concat(item.TagCategories.Values.SelectMany(t => t));
        var hierarchical = item.TagCategories.Where(p => p.Key.Length > 0).SelectMany(p => p.Value.Select(tag => p.Key + "|" + tag));
        SetBag(descriptions, target, Dc, "subject", flat);
        SetBag(descriptions, target, Lr, "hierarchicalSubject", hierarchical);
        if (FileMetadata.Clean(item.Title) is { } title) SetAlt(descriptions, target, Dc, "title", title);
        if (FileMetadata.Clean(item.Description) is { } description) SetAlt(descriptions, target, Dc, "description", description);
        if (item.Rating > 0)
        {
            Declare(target, "xmp", Xmp);
            Replace(descriptions, target, Xmp, "Rating", _ => new XElement(Xmp + "Rating", Math.Clamp((int)Math.Round(item.Rating / 2.0, MidpointRounding.AwayFromZero), 1, 5).ToString(CultureInfo.InvariantCulture)));
        }
        return Serialize(document);
    }

    /// <summary>Writes the medium's title, description, rating and tags into its sidecar, atomically through a temporary file.
    /// Returns false when the sidecar already said the same.</summary>
    public static bool Write(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var target = Path.GetFullPath(PathFor(item.Path));
        FileAccessPolicy.Current.EnsureWritable(target);
        var existing = File.Exists(target) ? ReadText(target) : null;
        var text = Merge(existing, item);
        if (existing != null && Load(existing) is { } before && Serialize(before) == text) return false;
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, new System.Text.UTF8Encoding(false)); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }

    private static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new InvalidDataException(T("The sidecar is larger than 4 MB."));
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static XDocument? Load(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true });
            var document = XDocument.Load(reader);
            return document.Descendants(Rdf + "RDF").Any() ? document : null;
        }
        catch (XmlException) { return null; }
    }

    private static XDocument NewDocument() => new(new XElement(Meta + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", Meta.NamespaceName), new XAttribute(Meta + "xmptk", ProductIdentity.Name),
        new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName),
            new XElement(Rdf + "Description", new XAttribute(Rdf + "about", "")))));

    private static string Serialize(XDocument document)
    {
        var text = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = " ", NewLineChars = "\n" })) document.Save(writer);
        return text + "\n";
    }

    private static XElement? Container(XElement element) => element.Elements().FirstOrDefault(c => c.Name == Rdf + "Bag" || c.Name == Rdf + "Seq" || c.Name == Rdf + "Alt");
    private static XElement? DefaultItem(IReadOnlyList<XElement> items) => items.FirstOrDefault(li => (string?)li.Attribute(XNamespace.Xml + "lang") == "x-default") ?? items.FirstOrDefault();

    private static void Declare(XElement target, string prefix, XNamespace ns)
    {
        if (target.GetPrefixOfNamespace(ns) != null) return;
        var free = prefix; for (var n = 1; target.GetNamespaceOfPrefix(free) != null; n++) free = prefix + n;
        target.SetAttributeValue(XNamespace.Xmlns + free, ns.NamespaceName);
    }

    /// <summary>Replaces the property wherever it stands by what <paramref name="build"/> makes of its old values; the new
    /// element takes the place of the first old one, so a repeated export leaves the sidecar as it is.</summary>
    private static void Replace(IReadOnlyList<XElement> descriptions, XElement target, XNamespace ns, string name, Func<List<string>, XElement?> build)
    {
        var values = new List<string>();
        var place = descriptions.SelectMany(d => d.Elements(ns + name)).FirstOrDefault();
        var marker = new XComment("");
        if (place != null) place.AddBeforeSelf(marker); else target.Add(marker);
        foreach (var description in descriptions)
        {
            if (description.Attribute(ns + name) is { } attribute) { values.AddRange(FileMetadata.SplitKeywords(attribute.Value)); attribute.Remove(); }
            foreach (var element in description.Elements(ns + name).ToList())
            {
                values.AddRange(Container(element) is { } container ? container.Elements(Rdf + "li").Select(li => li.Value) : [element.Value]);
                element.Remove();
            }
        }
        if (build(values) is { } replacement) marker.AddBeforeSelf(replacement);
        marker.Remove();
    }

    private static void SetBag(IReadOnlyList<XElement> descriptions, XElement target, XNamespace ns, string name, IEnumerable<string> added)
    {
        Declare(target, ns == Dc ? "dc" : "lr", ns);
        Replace(descriptions, target, ns, name, old =>
        {
            var values = old.Concat(added).Select(FileMetadata.Clean).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return values.Count == 0 ? null : new XElement(ns + name, new XElement(Rdf + "Bag", values.Select(v => new XElement(Rdf + "li", v))));
        });
    }

    private static void SetAlt(IReadOnlyList<XElement> descriptions, XElement target, XNamespace ns, string name, string value)
    {
        var existing = descriptions.SelectMany(d => d.Elements(ns + name)).FirstOrDefault(e => Container(e)?.Name == Rdf + "Alt");
        if (existing != null)
        {
            var alt = Container(existing)!;
            var item = alt.Elements(Rdf + "li").FirstOrDefault(li => (string?)li.Attribute(XNamespace.Xml + "lang") == "x-default");
            if (item is null) alt.AddFirst(item = new XElement(Rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default")));
            item.Value = value;
            foreach (var description in descriptions) description.Attribute(ns + name)?.Remove();
            return;
        }
        Declare(target, "dc", ns);
        Replace(descriptions, target, ns, name, _ => new XElement(ns + name, new XElement(Rdf + "Alt", new XElement(Rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"), value))));
    }
}
