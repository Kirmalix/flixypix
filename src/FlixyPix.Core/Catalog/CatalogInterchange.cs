using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace FlixyPix.Core;

public sealed record NfoMetadata
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public int? Rating { get; init; }
    public double? DurationSeconds { get; init; }
    public List<string> Tags { get; init; } = [];
    public List<string> Genres { get; init; } = [];
    public string? Category { get; init; }
    public string? OriginalXml { get; init; }
}

/// <summary>Readable reconstruction CSV and a minimal Kodi-style local XML NFO subset.</summary>
public static class CatalogInterchange
{
    private const long NfoLimit = 8 * 1024 * 1024;
    private static readonly string[] Columns = ["format", "version", "id", "path", "kind", "title", "tags", "rating", "description", "width", "height", "durationSeconds", "fileSize", "json"];

    public static void ExportCsv(IEnumerable<MediaItem> items, string path)
    {
        ArgumentNullException.ThrowIfNull(items);
        AtomicNewFile(path, writer =>
        {
            WriteRow(writer, Columns);
            foreach (var item in items)
                WriteRow(writer, ["flixypix-csv", "1", item.Id.ToString(), item.Path, item.Kind.ToString(), item.Title,
                    JsonSerializer.Serialize(item.Tags), item.Rating.ToString(CultureInfo.InvariantCulture), item.Description ?? "",
                    Invariant(item.Width), Invariant(item.Height), Invariant(item.DurationSeconds), Invariant(item.FileSize),
                    JsonSerializer.Serialize(item, CatalogRepository.Json)]);
        });
    }

    public static ImportResult ImportCsv(CatalogRepository repository, string path)
    {
        ArgumentNullException.ThrowIfNull(repository);
        try
        {
            var rows = ParseCsv(File.ReadAllText(path));
            if (rows.Count == 0 || !rows[0].SequenceEqual(Columns, StringComparer.Ordinal))
                return new ImportResult { Errors = ["CSV header does not match flixypix-csv version 1."] };
            var items = new List<MediaItem>();
            for (var index = 1; index < rows.Count; index++)
            {
                var row = rows[index];
                try
                {
                    if (row.Length != Columns.Length || !ProductIdentity.IsFormat(row[0],"flixypix-csv") || row[1] != "1") throw new FormatException("Wrong column count or unsupported format/version.");
                    if (!Guid.TryParse(row[2], out var id) || id == Guid.Empty || !Enum.TryParse<MediaKind>(row[4], true, out var kind) || !Enum.IsDefined(kind))
                        throw new FormatException("Invalid media id or kind.");
                    var original = string.IsNullOrWhiteSpace(row[13]) ? new MediaItem { Path = row[3] }
                        : JsonSerializer.Deserialize<MediaItem>(row[13], CatalogRepository.Json) ?? throw new FormatException("Invalid JSON metadata cell.");
                    var tags = JsonSerializer.Deserialize<List<string>>(row[6]) ?? throw new FormatException("Tags must be a JSON array.");
                    var rating = int.Parse(row[7], NumberStyles.Integer, CultureInfo.InvariantCulture);
                    items.Add(original with
                    {
                        Id = id, Path = row[3], Kind = kind, Title = row[5], Tags = tags, Rating = rating,
                        Description = row[8] == (original.Description ?? "") ? original.Description : row[8],
                        Width = OptionalInt(row[9]), Height = OptionalInt(row[10]), DurationSeconds = OptionalDouble(row[11]), FileSize = OptionalLong(row[12])
                    });
                }
                catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or ArgumentException)
                { return new ImportResult { Errors = [$"CSV record {index + 1}: {ex.Message}"] }; }
            }
            return repository.ImportItemsAtomically(items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        { return new ImportResult { Errors = [ex.Message] }; }
    }

    public static NfoMetadata ReadNfo(string path)
    {
        if (new FileInfo(path).Length > NfoLimit) throw new InvalidDataException("NFO exceeds the 8 MiB input limit.");
        var bytes = File.ReadAllBytes(path);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var byteStream = new MemoryStream(bytes, writable: false);
        using var encodingReader = XmlReader.Create(byteStream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = NfoLimit, MaxCharactersFromEntities = 0 });
        var encodedDocument = XDocument.Load(encodingReader, LoadOptions.PreserveWhitespace);
        var encoding = Encoding.GetEncoding(encodedDocument.Declaration?.Encoding ?? "utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        using var textReader = new StreamReader(new MemoryStream(bytes, writable: false), encoding, detectEncodingFromByteOrderMarks: true);
        var original = textReader.ReadToEnd();
        var document = ParseNfo(original);
        var root = document.Root!;
        string? Value(string name) => root.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value;
        var genres = root.Elements().Where(x => x.Name.LocalName == "genre").Select(x => x.Value).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var tags = root.Elements().Where(x => x.Name.LocalName == "tag").Select(x => x.Value).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ratingText = Value("userrating") ?? Value("rating");
        var ratingMax = 10d;
        if (ratingText is null)
        {
            var ratings = root.Elements().FirstOrDefault(x => x.Name.LocalName == "ratings")?.Elements().Where(x => x.Name.LocalName == "rating").ToArray() ?? [];
            var rating = ratings.FirstOrDefault(x => string.Equals((string?)x.Attribute("default"), "true", StringComparison.OrdinalIgnoreCase)) ?? ratings.FirstOrDefault();
            ratingText = rating?.Elements().FirstOrDefault(x => x.Name.LocalName == "value")?.Value;
            if (rating?.Attribute("max") is { } maximum) ratingMax = RequiredNonnegative(maximum.Value, "rating max");
        }
        int? personalRating = null;
        if (!string.IsNullOrWhiteSpace(ratingText))
        {
            var value = RequiredNonnegative(ratingText, "rating");
            if (ratingMax <= 0 || value > ratingMax) throw new InvalidDataException("NFO rating exceeds its maximum.");
            personalRating = (int)Math.Round(value / ratingMax * 10, MidpointRounding.AwayFromZero);
        }
        double? seconds = null;
        if (Value("runtime") is { Length: > 0 } runtime) seconds = RequiredNonnegative(runtime, "runtime") * 60;
        else if (root.Descendants().FirstOrDefault(x => x.Name.LocalName == "durationinseconds") is { } duration)
            seconds = RequiredNonnegative(duration.Value, "durationinseconds");
        if (seconds is { } number && !double.IsFinite(number)) throw new InvalidDataException("NFO duration is not finite.");
        return new NfoMetadata
        {
            Title = Value("title"), Description = Value("plot"), Rating = personalRating, DurationSeconds = seconds,
            Tags = tags, Genres = genres, Category = Value("category") ?? genres.FirstOrDefault(), OriginalXml = original
        };
    }

    public static MediaItem ApplyNfo(MediaItem item, NfoMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Tags is null || metadata.Genres is null || metadata.Rating is < 0 or > 10 ||
            metadata.DurationSeconds is { } duration && (!double.IsFinite(duration) || duration < 0)) throw new ArgumentException("Invalid NFO metadata.", nameof(metadata));
        var extra = new Dictionary<string, string?>(item.Extra);
        if (metadata.OriginalXml is not null) extra["nfo.originalXml"] = metadata.OriginalXml;
        var categories = item.TagCategories.ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        if (metadata.Genres.Count != 0)
            categories["Genre"] = (categories.GetValueOrDefault("Genre") ?? []).Concat(metadata.Genres).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return item with
        {
            Title = metadata.Title ?? item.Title, Description = metadata.Description ?? item.Description,
            Rating = metadata.Rating ?? item.Rating, DurationSeconds = metadata.DurationSeconds ?? item.DurationSeconds,
            Tags = item.Tags.Concat(metadata.Tags).Concat(metadata.Genres).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Category = metadata.Category ?? item.Category, TagCategories = categories, Extra = extra
        };
    }

    /// <param name="roles">Role of each tag category (<see cref="TagRoles.Of"/>); with it, performers become &lt;actor&gt; and
    /// studios &lt;studio&gt;, as Kodi and other players expect.</param>
    public static void WriteNfo(MediaItem item, string path, Func<string, TagRole>? roles = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var document = item.Extra.TryGetValue("nfo.originalXml", out var original) && original is not null ? ParseNfo(original) : new XDocument(new XElement("movie"));
        var root = document.Root!;
        void Set(string name, string? value)
        {
            root.Elements().Where(x => x.Name.LocalName == name).Remove();
            if (value is not null) root.Add(new XElement(root.Name.Namespace + name, value));
        }
        Set("title", item.Title);
        Set("plot", item.Description);
        Set("userrating", item.Rating.ToString(CultureInfo.InvariantCulture));
        Set("runtime", item.DurationSeconds is { } duration ? (duration / 60).ToString("R", CultureInfo.InvariantCulture) : null);
        root.Elements().Where(x => x.Name.LocalName is "tag" or "genre" or "category").Remove();
        foreach (var tag in item.Tags) root.Add(new XElement(root.Name.Namespace + "tag", tag));
        var genres = item.TagCategories.GetValueOrDefault("Genre") ?? (item.Category is null ? [] : new List<string> { item.Category });
        foreach (var genre in genres) root.Add(new XElement(root.Name.Namespace + "genre", genre));
        if (item.Category is not null) root.Add(new XElement(root.Name.Namespace + "category", item.Category));
        if (item.ReleaseDate is { } released)
        {
            Set("premiered", TagProfile.IsoDate(released));
            Set("year", released.Year.ToString(CultureInfo.InvariantCulture));
        }
        if (roles is not null)
        {
            string[] Of(TagRole role) => item.TagCategories.Where(p => roles(p.Key) == role).SelectMany(p => p.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var (performers, studios) = (Of(TagRole.Performer), Of(TagRole.Studio));
            if (performers.Length > 0)
            {
                root.Elements().Where(x => x.Name.LocalName == "actor").Remove();
                foreach (var performer in performers) root.Add(new XElement(root.Name.Namespace + "actor", new XElement(root.Name.Namespace + "name", performer)));
            }
            if (studios.Length > 0)
            {
                root.Elements().Where(x => x.Name.LocalName == "studio").Remove();
                foreach (var studio in studios) root.Add(new XElement(root.Name.Namespace + "studio", studio));
            }
        }
        AtomicNewFile(path, writer => document.Save(writer, SaveOptions.None));
    }

    private static XDocument ParseNfo(string xml)
    {
        if (Encoding.UTF8.GetByteCount(xml) > NfoLimit) throw new InvalidDataException("NFO exceeds the 8 MiB input limit.");
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = NfoLimit, MaxCharactersFromEntities = 0
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (document.Root?.Name.LocalName is not ("movie" or "episodedetails" or "musicvideo" or "tvshow"))
            throw new InvalidDataException("Expected movie, episodedetails, musicvideo or tvshow NFO root.");
        return document;
    }

    private static double RequiredNonnegative(string value, string field)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < 0)
            throw new InvalidDataException($"NFO {field} must be a finite nonnegative number.");
        return number;
    }

    private static string Invariant<T>(T? value) where T : struct, IFormattable => value?.ToString(null, CultureInfo.InvariantCulture) ?? "";
    private static int? OptionalInt(string value) => value.Length == 0 ? null : int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static long? OptionalLong(string value) => value.Length == 0 ? null : long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static double? OptionalDouble(string value) => value.Length == 0 ? null : double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static void AtomicNewFile(string path, Action<TextWriter> write)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        FileAccessPolicy.Current.EnsureWritable(fullPath);
        if (File.Exists(fullPath)) throw new IOException("Export destination already exists; select a new file to preserve the existing source.");
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false)))
                write(writer);
            FileAccessPolicy.Current.EnsureWritable(fullPath); File.Move(temporary, fullPath, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteRow(TextWriter writer, IEnumerable<string> fields)
    {
        writer.Write(string.Join(",", fields.Select(value => "\"" + value.Replace("\"", "\"\"") + "\"")));
        writer.Write("\r\n");
    }

    private static List<string[]> ParseCsv(string input)
    {
        var records = new List<string[]>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var afterQuote = false;
        var started = false;
        for (var index = 0; index < input.Length; index++)
        {
            var character = input[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < input.Length && input[index + 1] == '"') { field.Append('"'); index++; }
                    else { quoted = false; afterQuote = true; }
                }
                else field.Append(character);
                continue;
            }
            if (character == ',' || character is '\r' or '\n')
            {
                record.Add(field.ToString()); field.Clear(); afterQuote = false; started = false;
                if (character != ',')
                {
                    if (character == '\r' && index + 1 < input.Length && input[index + 1] == '\n') index++;
                    records.Add(record.ToArray()); record.Clear();
                }
                continue;
            }
            if (afterQuote) throw new FormatException("Unexpected characters after a closing CSV quote.");
            if (character == '"')
            {
                if (started) throw new FormatException("Unexpected quote in an unquoted CSV field.");
                quoted = true; started = true;
            }
            else { field.Append(character); started = true; }
        }
        if (quoted) throw new FormatException("CSV ends inside a quoted field.");
        if (started || afterQuote || field.Length != 0 || record.Count != 0) { record.Add(field.ToString()); records.Add(record.ToArray()); }
        return records;
    }
}
