using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public enum HistoricalProjectionStatus { Complete, Partial, Opaque }

/// <summary>A reusable group of tags. Importing a preset never assigns tags to media.</summary>
public sealed record TagPreset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public MediaKind Kind { get; init; }
    public List<string> Tags { get; init; } = [];
    public Dictionary<string, List<string>> TagCategories { get; init; } = new();
    public HistoricalProjectionStatus Status { get; init; } = HistoricalProjectionStatus.Complete;
    public string? SourceId { get; init; }
    public List<string> LegacyRowKeys { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

/// <summary>Structural XML inventory only. No saved search is created or activated by this record.</summary>
public sealed record LegacySearchDraft
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string LegacyId { get; init; } = "";
    public int? SearchType { get; init; }
    public MediaKind? Kind { get; init; }
    public string? RootType { get; init; }
    public HistoricalProjectionStatus Status { get; init; } = HistoricalProjectionStatus.Opaque;
    public Dictionary<string, string> Fields { get; init; } = new();
    public string RawXml { get; init; } = "";
    public string RawXmlSha256 { get; init; } = "";
    public List<string> Warnings { get; init; } = [];
    public bool IsExecutable => false;
}

public sealed record TagPresetChange(Guid MediaId, string Title, IReadOnlyList<string> BeforeTags, IReadOnlyList<string> AfterTags);
public sealed record TagPresetPreview(TagPreset Preset, IReadOnlyList<TagPresetChange> Changes);
public sealed record HistoricalLegacyRow(string Table, JsonElement Values);
public sealed record HistoricalImportResult(int PresetRows, int SearchRows, int JobRows, IReadOnlyList<string> Warnings);

/// <summary>Readers accept data only; they never deserialize original types or execute historical commands.</summary>
public static partial class LegacyHistoricalReader
{
    public const int MaximumXmlCharacters = 1024 * 1024;
    internal static Guid HistoricalId(string sourceId, string type, string key) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(sourceId + ":" + type + ":" + key)).AsSpan(0, 16));
    internal static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static LegacySearchDraft ReadSearchSettings(string xml, string sourceId, string legacyId, int? searchType = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        RequireIdentity(sourceId, legacyId);
        if (xml.Length > MaximumXmlCharacters) throw new InvalidDataException("Historical search XML exceeds the 1 MiB-character limit; source row must remain raw.");
        var draft = new LegacySearchDraft
        {
            Id = HistoricalId(sourceId, "search_settings", legacyId), Name = "Legacy search " + legacyId,
            SourceId = sourceId, LegacyId = legacyId, SearchType = searchType, RawXml = xml, RawXmlSha256 = HashText(xml)
        };
        try
        {
            using var input = new StringReader(xml);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlCharacters, MaxCharactersFromEntities = 0
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root ?? throw new InvalidDataException("Historical search XML has no root element.");
            var name = root.Name.LocalName;
            if (root.Name.NamespaceName.Length != 0 || name is not ("SearchSettings_Image" or "SearchSettings_Video"))
                return draft with { RootType = root.Name.ToString(), Warnings = ["Unknown XML root retained without projection."] };
            var kind = name == "SearchSettings_Image" ? MediaKind.Image : MediaKind.Video;
            var warnings = new List<string> { "Original search UI semantics are not completely mapped; this draft is inactive and does not create an executable saved search." };
            var nodes = root.Descendants().Take(4097).ToArray();
            if (nodes.Length > 4096 || nodes.Any(node => node.Ancestors().Take(18).Count() > 16))
                throw new InvalidDataException("Historical search XML exceeds 4096 elements or 16 levels.");
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var child in root.Elements())
            {
                if (child.Name.NamespaceName.Length != 0 || child.HasElements)
                { warnings.Add("Complex or namespaced XML field retained in raw XML: " + child.Name); continue; }
                if (!fields.TryAdd(child.Name.LocalName, child.Value))
                    warnings.Add("Duplicate XML field retained in raw XML: " + child.Name.LocalName);
            }
            var expectedKind = searchType switch { 500 or 501 => MediaKind.Video, 502 or 503 => MediaKind.Image, _ => (MediaKind?)null };
            if (expectedKind.HasValue && expectedKind != kind) warnings.Add("Search type and XML root disagree; neither is silently reinterpreted.");
            if (searchType is not (500 or 501 or 502 or 503)) warnings.Add("Unknown historical search type retained.");
            return draft with { RootType = name, Kind = kind, Status = HistoricalProjectionStatus.Partial, Fields = fields, Warnings = warnings };
        }
        catch (XmlException error) { throw new InvalidDataException("Invalid historical search XML: " + error.Message, error); }
    }

    internal static void RequireIdentity(string sourceId, string legacyId)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 2048 || string.IsNullOrWhiteSpace(legacyId) || legacyId.Length > 2048)
            throw new ArgumentException("Historical data requires a bounded source identity and row key.");
    }
}

public sealed partial class CatalogRepository
{
    private void InitializeHistoricalSchema(SqliteTransaction transaction)
    {
        using var command = Command("""
            CREATE TABLE IF NOT EXISTS tag_presets(id TEXT PRIMARY KEY, document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS legacy_search_drafts(id TEXT PRIMARY KEY, document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS legacy_job_drafts(id TEXT PRIMARY KEY, document TEXT NOT NULL);
            """, transaction);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<TagPreset> GetTagPresets() => ReadHistoricalDocuments<TagPreset>("tag_presets");
    public IReadOnlyList<LegacySearchDraft> GetLegacySearchDrafts() => ReadHistoricalDocuments<LegacySearchDraft>("legacy_search_drafts");
    public IReadOnlyList<LegacyJobDraft> GetLegacyJobDrafts() => ReadHistoricalDocuments<LegacyJobDraft>("legacy_job_drafts")
        .OrderBy(draft => draft.SourceId, StringComparer.Ordinal).ThenBy(draft => draft.OriginalOrder ?? long.MaxValue).ThenBy(draft => draft.LegacyId, StringComparer.Ordinal).ToArray();
    private IReadOnlyList<T> ReadHistoricalDocuments<T>(string table)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var exists = Command("SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table", null, ("$table", table));
            return exists.ExecuteScalar() is null ? [] : ReadDocuments<T>(table);
        }
    }

    public TagPreset UpsertTagPreset(TagPreset preset)
    {
        ValidateTagPreset(preset);
        lock (gate)
        {
            ThrowIfDisposed(); using var transaction = connection.BeginTransaction(); InitializeHistoricalSchema(transaction);
            var normalized = NormalizeTagPreset(preset); SaveDocument("tag_presets", normalized.Id, normalized, transaction); transaction.Commit(); return normalized;
        }
    }

    public TagPresetPreview PreviewTagPreset(Guid presetId, IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);
        lock (gate)
        {
            ThrowIfDisposed(); using var transaction = connection.BeginTransaction();
            var preset = FindTagPreset(presetId, transaction); var changes = TagPresetChanges(preset, mediaIds, transaction, cancellationToken);
            transaction.Commit(); return new(preset, changes.Select(change => new TagPresetChange(change.Before.Id, change.Before.Title, change.Before.Tags, change.After.Tags)).ToArray());
        }
    }

    public int ApplyTagPreset(Guid presetId, IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);
        lock (gate)
        {
            ThrowIfDisposed(); using var transaction = connection.BeginTransaction(); var preset = FindTagPreset(presetId, transaction);
            var changes = TagPresetChanges(preset, mediaIds, transaction, cancellationToken); var count = 0;
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (JsonSerializer.Serialize(change.Before, Json) == JsonSerializer.Serialize(change.After, Json)) continue;
                UpsertCore(change.After, transaction); count++;
            }
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return count;
        }
    }

    private TagPreset FindTagPreset(Guid id, SqliteTransaction transaction)
    {
        using var command = Command("SELECT document FROM tag_presets WHERE id=$id", transaction, ("$id", id.ToString()));
        var preset = command.ExecuteScalar() is string raw ? JsonSerializer.Deserialize<TagPreset>(raw, Json) : null;
        if (preset is null) throw new ArgumentException("Tag preset not found.");
        ValidateTagPreset(preset);
        if (preset.Status == HistoricalProjectionStatus.Opaque) throw new InvalidOperationException("An opaque tag preset cannot be applied.");
        return preset;
    }

    private List<(MediaItem Before, MediaItem After)> TagPresetChanges(TagPreset preset, IReadOnlyCollection<Guid> mediaIds, SqliteTransaction transaction, CancellationToken token)
    {
        if (mediaIds.Count > 10_000) throw new ArgumentException("Select at most 10000 media items per preset operation.");
        var changes = new List<(MediaItem, MediaItem)>();
        foreach (var id in mediaIds.Distinct())
        {
            token.ThrowIfCancellationRequested();
            using var command = Command("SELECT document FROM media WHERE id=$id", transaction, ("$id", id.ToString()));
            var item = command.ExecuteScalar() is string raw ? JsonSerializer.Deserialize<MediaItem>(raw, Json) : null;
            if (item is null || item.Kind != preset.Kind) throw new ArgumentException("Every selected media item must exist and match the preset media kind.");
            var categories = item.TagCategories.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var group in preset.TagCategories) categories[group.Key] = categories.GetValueOrDefault(group.Key, []).Concat(group.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var tags = item.Tags.Concat(preset.Tags).Concat(categories.SelectMany(pair => pair.Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            changes.Add((item, item with { Tags = tags, TagCategories = categories }));
        }
        return changes;
    }

    private static TagPreset NormalizeTagPreset(TagPreset preset) => preset with
    {
        Name = preset.Name.Trim(), Tags = preset.Tags.Select(tag => tag.Trim()).Where(tag => tag.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        TagCategories = preset.TagCategories.ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Select(tag => tag.Trim()).Where(tag => tag.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase)
    };

    // Called by the same transaction as a global tag change; archival source rows and inactive drafts stay untouched.
    private void RewriteHistoricalTagPresets(string tag, string? replacement, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var exists = Command("SELECT 1 FROM sqlite_master WHERE name='tag_presets' AND type='table'", transaction);
        if (exists.ExecuteScalar() is null) return;
        List<string> Replace(IEnumerable<string> tags) => tags.Where(value => !Same(value, tag) || replacement != null)
            .Select(value => Same(value, tag) ? replacement! : value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var preset in ReadDocuments<TagPreset>("tag_presets", transaction))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!preset.Tags.Any(value => Same(value, tag)) && !preset.TagCategories.Any(pair => pair.Value.Any(value => Same(value, tag)))) continue;
            var changed = NormalizeTagPreset(preset with { Tags = Replace(preset.Tags), TagCategories = preset.TagCategories.ToDictionary(pair => pair.Key, pair => Replace(pair.Value), StringComparer.OrdinalIgnoreCase) });
            SaveDocument("tag_presets", changed.Id, changed, transaction);
        }
    }

    private void MoveHistoricalTagPresetCategory(string tag, string fromCategory, string toCategory, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var exists = Command("SELECT 1 FROM sqlite_master WHERE name='tag_presets' AND type='table'", transaction);
        if (exists.ExecuteScalar() is null) return;
        foreach (var preset in ReadDocuments<TagPreset>("tag_presets", transaction))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = preset.TagCategories.FirstOrDefault(pair => Same(pair.Key, fromCategory));
            if (group.Value is null || !group.Value.Any(value => Same(value, tag))) continue;
            var categories = preset.TagCategories.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            categories[group.Key] = group.Value.Where(value => !Same(value, tag)).ToList();
            if (categories[group.Key].Count == 0) categories.Remove(group.Key);
            categories[toCategory] = categories.GetValueOrDefault(toCategory, []).Append(tag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            SaveDocument("tag_presets", preset.Id, preset with { TagCategories = categories }, transaction);
        }
    }

    private static void ValidateTagPreset(TagPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Id == Guid.Empty || string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 4096 || preset.Kind is not (MediaKind.Video or MediaKind.Image) || !Enum.IsDefined(preset.Status) ||
            preset.Tags is null || preset.Tags.Count > 10_000 || preset.Tags.Any(tag => tag is null || tag.Length > 256) || preset.TagCategories is null || preset.TagCategories.Count > 256 ||
            preset.TagCategories.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 256 || pair.Value is null || pair.Value.Count > 10_000 || pair.Value.Any(tag => tag is null || tag.Length > 256)) ||
            preset.LegacyRowKeys is null || preset.LegacyRowKeys.Count > 10_000 || preset.LegacyRowKeys.Any(key => key is null || key.Length > 2048) || preset.Warnings is null)
            throw new ArgumentException("Invalid bounded tag preset.");
    }

    public HistoricalImportResult ImportHistoricalRecords(IEnumerable<HistoricalLegacyRow> rows, string sourceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows); LegacyHistoricalReader.RequireIdentity(sourceId, "source");
        lock (gate)
        {
            ThrowIfDisposed(); using var transaction = connection.BeginTransaction(); InitializeHistoricalSchema(transaction);
            var warnings = new List<string>(); var counts = new int[3];
            foreach (var row in rows) { cancellationToken.ThrowIfCancellationRequested(); ProjectHistoricalRecord(row.Table, row.Values, sourceId, transaction, warnings, counts); }
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return new(counts[0], counts[1], counts[2], warnings);
        }
    }

    private void ImportHistoricalData(Func<string, IEnumerable<JsonElement>> rows, string sourceId, SqliteTransaction transaction, List<string> warnings, CancellationToken cancellationToken = default)
    {
        InitializeHistoricalSchema(transaction); var counts = new int[3];
        foreach (var table in new[] { "keyword", "search_settings", "cliptovideo_queue" })
            foreach (var values in rows(table)) { cancellationToken.ThrowIfCancellationRequested(); ProjectHistoricalRecord(table, values, sourceId, transaction, warnings, counts); }
    }

    private void ProjectHistoricalRecord(string table, JsonElement values, string sourceId, SqliteTransaction transaction, List<string> warnings, int[] counts)
    {
        try
        {
            if (table.Equals("keyword", StringComparison.OrdinalIgnoreCase) && Number(values, "type") is 4 or 5)
            {
                var name = Text(values, "keyword"); var key = Text(values, "ID");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("Tag preset has no name or row key.");
                var kind = Number(values, "type") == 4 ? MediaKind.Video : MediaKind.Image;
                var id = LegacyHistoricalReader.HistoricalId(sourceId, "tag-preset:" + kind, name.ToUpperInvariant());
                using var find = Command("SELECT document FROM tag_presets WHERE id=$id", transaction, ("$id", id.ToString()));
                var preset = find.ExecuteScalar() is string raw ? JsonSerializer.Deserialize<TagPreset>(raw, Json)! : new TagPreset { Id = id, Name = name, Kind = kind, SourceId = sourceId };
                var tags = Text(values, "keytags") ?? ""; var reasons = preset.Warnings.ToList();
                var decoded = Array.Empty<string>();
                if (tags.Length != 0)
                {
                    if (tags.Length < 2 || tags[0] != '"' || tags[^1] != '"' || (decoded = tags[1..^1].Split("\" \"", StringSplitOptions.RemoveEmptyEntries)).Any(tag => tag.Contains('"')))
                    { reasons.Add("Malformed original keytags retained in the source record; this row contributes no tags."); decoded = []; }
                }
                // Category identity is not encoded in this keyword row; do not infer it from tag text.
                preset = NormalizeTagPreset(preset with { Tags = preset.Tags.Concat(decoded).ToList(), LegacyRowKeys = preset.LegacyRowKeys.Append(key).Distinct().ToList(),
                    Status = reasons.Count == 0 ? HistoricalProjectionStatus.Complete : HistoricalProjectionStatus.Partial, Warnings = reasons.Distinct().ToList() });
                ValidateTagPreset(preset); SaveDocument("tag_presets", id, preset, transaction); counts[0]++;
                AuditLegacy("keyword", key, "partially-projected", preset.Status == HistoricalProjectionStatus.Complete ? "tag-preset" : "tag-preset-partial", transaction, id);
            }
            else if (table.Equals("search_settings", StringComparison.OrdinalIgnoreCase))
            {
                var key = Text(values, "ID") ?? throw new InvalidDataException("Search draft has no row key."); var xml = Text(values, "xml") ?? "";
                var type = Number(values, "searchtype"); var searchType = type is >= int.MinValue and <= int.MaxValue && type % 1 == 0 ? (int?)type.Value : null;
                LegacySearchDraft draft;
                try { draft = LegacyHistoricalReader.ReadSearchSettings(xml, sourceId, key, searchType); }
                catch (InvalidDataException error)
                {
                    draft = new() { Id = LegacyHistoricalReader.HistoricalId(sourceId, "search_settings", key), Name = "Legacy search " + key, SourceId = sourceId, LegacyId = key,
                        SearchType = searchType, RawXml = xml.Length <= LegacyHistoricalReader.MaximumXmlCharacters ? xml : "", RawXmlSha256 = LegacyHistoricalReader.HashText(xml),
                        Warnings = [error.Message, "Original source record remains authoritative; no executable search was created."] };
                }
                SaveDocument("legacy_search_drafts", draft.Id, draft, transaction); counts[1]++;
                AuditLegacy("search_settings", key, draft.Status == HistoricalProjectionStatus.Opaque ? "raw-retained" : "partially-projected", "inactive-search-draft", transaction, draft.Id);
                warnings.Add("search_settings/" + key + ": preserved as an inactive XML draft; original search semantics are not completely projected.");
            }
            else if (table.Equals("cliptovideo_queue", StringComparison.OrdinalIgnoreCase))
            {
                var draft = LegacyHistoricalReader.ReadJobDraft(values, sourceId); SaveDocument("legacy_job_drafts", draft.Id, draft, transaction); counts[2]++;
                AuditLegacy("cliptovideo_queue", draft.LegacyId, "partially-projected", "inactive-queue-draft", transaction, draft.Id);
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        { warnings.Add(table + "/" + (Text(values, "ID") ?? "?") + ": " + error.Message + " Historical raw row must be retained."); }
    }

    // Portable document kinds are explicit; no caller-supplied table name reaches SQL.
    private void SaveHistoricalPortableDocument(string kind, JsonElement value, SqliteTransaction transaction)
    {
        switch (kind.ToLowerInvariant())
        {
            case "tagpresets":
                var preset = value.Deserialize<TagPreset>(Json) ?? throw new InvalidDataException("Null tag preset."); ValidateTagPreset(preset);
                SaveDocument("tag_presets", preset.Id, NormalizeTagPreset(preset), transaction); break;
            case "legacysearchdrafts":
                var search = value.Deserialize<LegacySearchDraft>(Json) ?? throw new InvalidDataException("Null search draft."); ValidateSearchDraft(search);
                SaveDocument("legacy_search_drafts", search.Id, search, transaction); break;
            case "legacyjobdrafts":
                var job = value.Deserialize<LegacyJobDraft>(Json) ?? throw new InvalidDataException("Null job draft."); LegacyHistoricalReader.ValidateJobDraft(job);
                SaveDocument("legacy_job_drafts", job.Id, job, transaction); break;
            default: throw new ArgumentException("Unknown historical document kind.");
        }
    }

    private static void ValidateSearchDraft(LegacySearchDraft draft)
    {
        LegacyHistoricalReader.RequireIdentity(draft.SourceId, draft.LegacyId);
        if (draft.Id == Guid.Empty || draft.Name is null || draft.Name.Length > 4096 || !Enum.IsDefined(draft.Status) || draft.Kind.HasValue && !Enum.IsDefined(draft.Kind.Value) ||
            draft.RawXml is null || draft.RawXml.Length > LegacyHistoricalReader.MaximumXmlCharacters || draft.Fields is null || draft.Fields.Count > 4096 ||
            draft.Fields.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 4096 || pair.Value is null || pair.Value.Length > LegacyHistoricalReader.MaximumXmlCharacters) || draft.Warnings is null ||
            draft.RawXmlSha256 is null || draft.RawXmlSha256.Length != 64 || !draft.RawXmlSha256.All(Uri.IsHexDigit) || draft.RawXml.Length != 0 && !string.Equals(LegacyHistoricalReader.HashText(draft.RawXml), draft.RawXmlSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid bounded historical search draft or XML hash.");
    }
}
