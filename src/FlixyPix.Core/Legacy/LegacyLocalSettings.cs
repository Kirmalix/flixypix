using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>Preserved content-folder definition. Refresh flags are historical data, never an automatic scan request.</summary>
public sealed record HistoricalContentFolder
{
    public Guid Id { get; init; }
    public string SourceId { get; init; } = "";
    public string LegacyId { get; init; } = "";
    public string Path { get; init; } = "";
    public MediaKind? Kind { get; init; }
    public string? CategoryLegacyId { get; init; }
    public string? Category { get; init; }
    public bool? OriginalAlwaysRefresh { get; init; }
    public bool? OriginalRefreshOnce { get; init; }
    public string RawJson { get; init; } = "";
    public string RawSha256 { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool IsExecutable => false;
}

public sealed record HistoricalFolderCategoryChange(Guid MediaId, string Path, string? CurrentCategory, string? ProposedCategory,
    string CurrentMediaSha256, IReadOnlyList<Guid> MatchingFolderIds);

public sealed record HistoricalFolderCategoryPreview(IReadOnlyList<Guid> MediaIds, string? SourceId, Guid? FolderId,
    bool ReplaceExisting, string FolderSetSha256, IReadOnlyList<HistoricalFolderCategoryChange> Changes,
    IReadOnlyList<string> Blockers, IReadOnlyList<string> Notes)
{
    public bool CanApply => Blockers.Count == 0;
}

public sealed partial class CatalogRepository
{
    private void InitializeHistoricalLocalSettingsSchema(SqliteTransaction transaction)
    {
        using var command = Command("CREATE TABLE IF NOT EXISTS historical_content_folders(id TEXT PRIMARY KEY,document TEXT NOT NULL)", transaction);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<HistoricalContentFolder> GetHistoricalContentFolders(string? sourceId = null)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return ReadDocuments<HistoricalContentFolder>("historical_content_folders")
                .Where(folder => sourceId is null || folder.SourceId == sourceId).OrderBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase).ThenBy(folder => folder.Id).ToArray();
        }
    }

    private void ImportHistoricalContentFolders(Func<string, IEnumerable<JsonElement>> rows, string sourceId, SqliteTransaction transaction,
        List<string> warnings, CancellationToken cancellationToken)
    {
        InitializeHistoricalLocalSettingsSchema(transaction);
        var categories = new Dictionary<string, List<(MediaKind? Kind, string? Category)>>(StringComparer.Ordinal);
        foreach (var values in rows("categories"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Text(values, "ID") is not { } id) continue;
            if (!categories.TryGetValue(id, out var matches)) categories[id] = matches = [];
            matches.Add((HistoricalFolderKind(values), Text(values, "category")));
        }
        var ordinal = 0;
        foreach (var values in rows("paths"))
        {
            cancellationToken.ThrowIfCancellationRequested(); ordinal++;
            var key = Text(values, "ID") ?? "ordinal:" + ordinal;
            try
            {
                var reasons = new List<string>();
                var kind = HistoricalFolderKind(values);
                var path = Text(values, "path") ?? "";
                var categoryId = Text(values, "categoryid");
                string? category = null;
                if (kind is null) reasons.Add("Unknown original paths.mtype; folder remains inactive.");
                if (!TryHistoricalDirectory(path, out _)) reasons.Add("Invalid or non-absolute original folder path; bytes remain preserved.");
                if (categoryId is not null && categories.TryGetValue(categoryId, out var matches))
                {
                    var names = matches.Where(match => match.Kind == kind && match.Category is { Length: > 0 }).Select(match => match.Category!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (names.Length == 1 && matches.All(match => match.Kind == kind)) category = names[0];
                    else reasons.Add("Ambiguous or wrong-kind original category reference.");
                }
                else reasons.Add("Original category reference is unresolved.");
                var always = HistoricalFolderFlag(values, "refresh"); var once = HistoricalFolderFlag(values, "refreshonce");
                if (always is null) reasons.Add("Missing or invalid original refresh flag retained without assuming false.");
                if (once is null) reasons.Add("Missing or invalid original refreshonce flag retained without assuming false.");
                var raw = values.GetRawText();
                var folder = new HistoricalContentFolder { Id = LegacyHistoricalReader.HistoricalId(sourceId, "paths", key), SourceId = sourceId,
                    LegacyId = key, Path = path, Kind = kind, CategoryLegacyId = categoryId, Category = category,
                    OriginalAlwaysRefresh = always, OriginalRefreshOnce = once, RawJson = raw,
                    RawSha256 = LegacyHistoricalReader.HashText(raw), Warnings = reasons };
                ValidateHistoricalContentFolder(folder);
                SaveDocument("historical_content_folders", folder.Id, folder, transaction);
                AuditLegacy("paths", Text(values, "ID"), "partially-projected", "inactive-content-folder", transaction, folder.Id);
                warnings.AddRange(reasons.Select(reason => "paths/" + key + ": " + reason));
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException)
            { warnings.Add("paths/" + key + ": content folder retained in original records: " + error.Message); }
        }
    }

    private void SavePortableHistoricalContentFolder(JsonElement value, SqliteTransaction transaction)
    {
        var folder = value.Deserialize<HistoricalContentFolder>(Json) ?? throw new InvalidDataException("Null historical content folder.");
        ValidateHistoricalContentFolder(folder);
        SaveDocument("historical_content_folders", folder.Id, folder, transaction);
    }

    private static MediaKind? HistoricalFolderKind(JsonElement values) => Number(values, "mtype") switch { 0 => MediaKind.Video, 1 => MediaKind.Image, _ => null };

    private static bool? HistoricalFolderFlag(JsonElement values, string name)
    {
        var value = Property(values, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true, JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) && number is 0 or 1 => number == 1,
            _ => null
        };
    }

    private static void ValidateHistoricalContentFolder(HistoricalContentFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder); LegacyHistoricalReader.RequireIdentity(folder.SourceId, folder.LegacyId);
        if (folder.Id == Guid.Empty || folder.Path is null || folder.Path.Length > 32768 || folder.Kind is { } kind && kind is not (MediaKind.Video or MediaKind.Image) ||
            folder.Category?.Length > 4096 || folder.CategoryLegacyId?.Length > 2048 || folder.RawJson is null || folder.RawJson.Length is < 2 or > 1024 * 1024 ||
            folder.RawSha256 != LegacyHistoricalReader.HashText(folder.RawJson) || folder.Warnings is null || folder.Warnings.Count > 100 || folder.Warnings.Any(warning => warning is null || warning.Length > 32768))
            throw new InvalidDataException("Invalid bounded historical content-folder definition or checksum.");
        using var raw = JsonDocument.Parse(folder.RawJson);
        if (raw.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Historical content folder requires its original object row.");
        if (folder.Id != LegacyHistoricalReader.HistoricalId(folder.SourceId, "paths", folder.LegacyId) ||
            folder.Path != (Text(raw.RootElement, "path") ?? "") || folder.Kind != HistoricalFolderKind(raw.RootElement) ||
            folder.CategoryLegacyId != Text(raw.RootElement, "categoryid") || folder.OriginalAlwaysRefresh != HistoricalFolderFlag(raw.RootElement, "refresh") ||
            folder.OriginalRefreshOnce != HistoricalFolderFlag(raw.RootElement, "refreshonce"))
            throw new InvalidDataException("Historical content-folder projection does not match its preserved row.");
    }

    // Pure lexical operation: never probe a drive, follow a link or enumerate a source folder.
    private static bool TryHistoricalDirectory(string path, out string directory)
    {
        directory = "";
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal)) return false;
            directory = System.IO.Path.GetFullPath(path);
            if (!System.IO.Path.EndsInDirectorySeparator(directory)) directory += System.IO.Path.DirectorySeparatorChar;
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return false; }
    }

    public HistoricalFolderCategoryPreview PreviewHistoricalContentFolderCategories(IReadOnlyCollection<Guid> mediaIds, string? sourceId = null,
        Guid? folderId = null, bool replaceExisting = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);
        lock (gate)
        {
            ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            var preview = BuildHistoricalContentFolderCategoryPreview(mediaIds, sourceId, folderId, replaceExisting, transaction, cancellationToken);
            transaction.Commit(); return preview;
        }
    }

    public int ApplyHistoricalContentFolderCategories(HistoricalFolderCategoryPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        lock (gate)
        {
            ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            var current = BuildHistoricalContentFolderCategoryPreview(preview.MediaIds, preview.SourceId, preview.FolderId, preview.ReplaceExisting, transaction, cancellationToken);
            if (!preview.CanApply || !current.CanApply || JsonSerializer.Serialize(preview, Json) != JsonSerializer.Serialize(current, Json))
                throw new InvalidOperationException("Historical folder/category preview is incomplete or changed; preview it again.");
            var changed = 0;
            foreach (var change in current.Changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.CurrentCategory == change.ProposedCategory) continue;
                var item = ReadHistoricalFolderMedia(change.MediaId, transaction).Item;
                UpsertCore(item with { Category = change.ProposedCategory }, transaction); changed++;
            }
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return changed;
        }
    }

    private HistoricalFolderCategoryPreview BuildHistoricalContentFolderCategoryPreview(IReadOnlyCollection<Guid> mediaIds, string? sourceId,
        Guid? folderId, bool replaceExisting, SqliteTransaction transaction, CancellationToken token)
    {
        if (mediaIds.Count is < 1 or > 10_000 || mediaIds.Any(id => id == Guid.Empty)) throw new ArgumentException("Select 1–10000 valid media IDs.");
        var ids = mediaIds.Distinct().Order().ToArray(); var blockers = new List<string>();
        var folders = ReadDocuments<HistoricalContentFolder>("historical_content_folders", transaction)
            .Where(folder => sourceId is null || folder.SourceId == sourceId).Where(folder => folderId is null || folder.Id == folderId).OrderBy(folder => folder.Id).ToArray();
        if (folderId is not null && folders.Length == 0) blockers.Add("Selected historical content folder does not exist in this source scope.");
        var folderHash = LegacyHistoricalReader.HashText(JsonSerializer.Serialize(folders, Json));
        var changes = new List<HistoricalFolderCategoryChange>();
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            var (raw, media) = ReadHistoricalFolderMedia(id, transaction);
            var parent = System.IO.Path.GetDirectoryName(media.Path) ?? "";
            TryHistoricalDirectory(parent, out var directory);
            var matches = folders.Where(folder => folder.Kind == media.Kind && directory.Length != 0 && TryHistoricalDirectory(folder.Path, out var prefix) && directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            string? proposed = media.Category;
            if (matches.Length > 0)
            {
                var categories = matches.Select(folder => folder.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (categories.Length != 1 || string.IsNullOrWhiteSpace(categories[0])) blockers.Add(id + ": matching folders have unresolved or conflicting categories; select one proven folder explicitly.");
                else if (!replaceExisting && !string.IsNullOrWhiteSpace(media.Category) && !string.Equals(media.Category, categories[0], StringComparison.OrdinalIgnoreCase))
                    blockers.Add(id + ": existing category differs; explicit replacement requires a new preview.");
                else proposed = categories[0];
            }
            changes.Add(new(id, media.Path, media.Category, proposed, LegacyHistoricalReader.HashText(raw), matches.Select(folder => folder.Id).ToArray()));
        }
        return new(ids, sourceId, folderId, replaceExisting, folderHash, changes, blockers,
            ["Catalog metadata only: no source files, scans or original refresh commands are accessed.",
                "Original prefix queries do not establish a longest-prefix winner; conflicting categories require explicit selection."]);
    }

    private (string Raw, MediaItem Item) ReadHistoricalFolderMedia(Guid id, SqliteTransaction transaction)
    {
        using var command = Command("SELECT document FROM media WHERE id=$id", transaction, ("$id", id.ToString()));
        var raw = command.ExecuteScalar() as string ?? throw new KeyNotFoundException("Selected media does not exist.");
        return (raw, JsonSerializer.Deserialize<MediaItem>(raw, Json) ?? throw new InvalidDataException("Invalid selected media document."));
    }
}
