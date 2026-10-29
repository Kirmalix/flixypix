using System.Formats.Nrbf;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed record LegacyCombineEntry
{
    public int Order { get; init; }
    public Guid OriginalId { get; init; }
    public int? MovieId { get; init; }
    public string InputPath { get; init; } = "";
    public int StartSeconds { get; init; }
    public int EndSeconds { get; init; }
    public bool IsClip { get; init; }
    public string? Description { get; init; }
    public int? OriginalWidth { get; init; }
    public int? OriginalHeight { get; init; }
    public bool Crossfade { get; init; }
    public bool MuteAudio { get; init; }
    public List<string> BlockingIssues { get; init; } = [];
}

/// <summary>Data-only preservation carrier. Importing it does not create or run a media job.</summary>
public sealed record LegacyCombineList
{
    public Guid Id { get; init; }
    public string SourceId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? SourcePath { get; init; }
    public string PayloadSha256 { get; init; } = "";
    public string PayloadBase64 { get; init; } = "";
    public string? PlaintextSha256 { get; init; }
    public HistoricalProjectionStatus Status { get; init; } = HistoricalProjectionStatus.Opaque;
    public List<LegacyCombineEntry> Entries { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool IsExecutable => false;
}

public static class LegacyCombineListReader
{
    public static LegacyCombineList Read(string path, string sourceId)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 8 or > LegacyObjectReader.MaximumPayloadBytes)
            throw new InvalidDataException("Combine list must contain between 8 bytes and 32 MiB.");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        return Read(bytes, sourceId) with { Name = Path.GetFileName(path), SourcePath = Path.GetFullPath(path) };
    }

    public static LegacyCombineList Read(byte[] bytes, string sourceId) => LegacyObjectReader.ReadCombineList(bytes, sourceId);

    public static void ValidateProjection(LegacyCombineList draft) => _ = Verify(draft, refresh: false);

    /// <summary>Checks identity and payload hash, then projects the preserved payload again with the current reader,
    /// so lists stored by an older version gain newly translated options.</summary>
    public static LegacyCombineList Refresh(LegacyCombineList draft) => Verify(draft, refresh: true);

    private static LegacyCombineList Verify(LegacyCombineList draft, bool refresh)
    {
        ArgumentNullException.ThrowIfNull(draft); LegacyHistoricalReader.RequireIdentity(draft.SourceId, "combine-list");
        if (draft.PayloadBase64 is null || draft.PayloadBase64.Length > (long)LegacyObjectReader.MaximumPayloadBytes * 4 / 3 + 4 ||
            draft.Entries is null || draft.Entries.Count > 10_000) throw new InvalidDataException("Invalid bounded combine-list source.");
        byte[] bytes; try { bytes = Convert.FromBase64String(draft.PayloadBase64); } catch (FormatException error) { throw new InvalidDataException("Invalid combine-list payload encoding.", error); }
        if (bytes.Length is < 8 or > LegacyObjectReader.MaximumPayloadBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != draft.PayloadSha256 ||
            draft.Id != LegacyHistoricalReader.HistoricalId(draft.SourceId, "combine-list", draft.PayloadSha256)) throw new InvalidDataException("Combine-list identity or payload hash mismatch.");
        var reread = Read(bytes, draft.SourceId);
        // A changed status means an edited projection, not a newer reader; entries may gain translated options.
        if (refresh && reread.Status == draft.Status) return reread with { Name = draft.Name, SourcePath = draft.SourcePath };
        if (JsonSerializer.Serialize(reread.Entries) != JsonSerializer.Serialize(draft.Entries) || reread.Status != draft.Status || reread.PlaintextSha256 != draft.PlaintextSha256)
            throw new InvalidDataException("Combine-list projection differs from its preserved source payload.");
        return draft;
    }
}

public static partial class LegacyObjectReader
{
    private const string CombineItemType = "MyMediaViewer.frmVideoTools+myVideos";
    private static readonly HashSet<string> CombineMembers = new(StringComparer.Ordinal)
    {
        "_id", "_thumbnail", "_video", "_tooltype", "_newfilename", "_starttime", "_endtime", "_dimensions", "_quality", "_savefolder",
        "_isClip", "_clipDescription", "_clipid", "_crossfade", "_muteaudio", "_combinetags", "_copytags", "_guid",
        "_requiresCombinePreProcess", "_deleteOnPostProcess", "_useOriginalFile", "_scale_command", "_framerate_command", "_displayaspectratio_command", "_addToCollection"
    };

    internal static LegacyCombineList ReadCombineList(byte[] bytes, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(bytes); LegacyHistoricalReader.RequireIdentity(sourceId, "combine-list");
        if (bytes.Length is < 8 or > MaximumPayloadBytes) throw Invalid("Combine list must contain between 8 bytes and 32 MiB.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var draft = new LegacyCombineList
        {
            Id = LegacyHistoricalReader.HistoricalId(sourceId, "combine-list", hash), SourceId = sourceId, Name = "Legacy combine list",
            PayloadSha256 = hash, PayloadBase64 = Convert.ToBase64String(bytes)
        };
        try
        {
            // Reconstructed fixed file-format parameter, not the user's SQLCE password.
            using var cipher = DES.Create();
            cipher.Key = Convert.FromHexString("657A6670657A6670"); cipher.IV = cipher.Key;
            cipher.Mode = CipherMode.CBC; cipher.Padding = PaddingMode.PKCS7;
            using var decryptor = cipher.CreateDecryptor();
            var plain = decryptor.TransformFinalBlock(bytes, 0, bytes.Length);
            var decoded = ReadBytes(plain);
            draft = draft with { PlaintextSha256 = decoded.Sha256 };
            var dictionary = RequireClass(decoded.Root, "System.Collections.Generic.Dictionary`2", true);
            RequireGenericArguments(dictionary, "System.Guid", CombineItemType);
            var values = dictionary.GetSerializationRecord("KeyValuePairs") as SZArrayRecord<SerializationRecord>
                ?? throw Invalid("Combine dictionary has no KeyValuePairs array.");
            if (values.Rank != 1 || values.Lengths[0] > 10_000) throw Invalid("Combine list exceeds 10000 entries.");
            var entries = new List<LegacyCombineEntry>(); var ids = new HashSet<Guid>();
            foreach (var record in values.GetArray(allowNulls: false))
            {
                var pair = RequireClass(record, "System.Collections.Generic.KeyValuePair`2", true);
                RequireGenericArguments(pair, "System.Guid", CombineItemType);
                var id = ReadGuid(pair.GetClassRecord("key"));
                if (id == Guid.Empty || !ids.Add(id)) throw Invalid("Duplicate or empty combine entry ID.");
                var item = RequireClass(pair.GetClassRecord("value"), CombineItemType);
                var movie = RequireClass(item.GetClassRecord("_video"), "MyMediaViewer.movies");
                var folder = RequiredString(movie, "_fullpath"); var name = RequiredString(movie, "_filename");
                if (!Path.IsPathFullyQualified(folder) || string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name))
                    throw Invalid("Combine entry has no unambiguous absolute media path.");
                var problems = new List<string>();
                foreach (var member in item.MemberNames.Where(member => !CombineMembers.Contains(member))) problems.Add("Unknown combine member: " + member);
                var start = item.GetInt32("_starttime"); var end = item.GetInt32("_endtime");
                if (start < 0 || end <= start) problems.Add("Invalid or empty original time range.");
                var clip = item.GetBoolean("_isClip");
                foreach (var flag in new[] { "_combinetags", "_copytags", "_addToCollection" })
                    if (item.HasMember(flag) && item.GetBoolean(flag)) problems.Add("Original option has no complete translation: " + flag);
                foreach (var command in new[] { "_scale_command", "_framerate_command", "_displayaspectratio_command" })
                    if (item.HasMember(command) && !string.IsNullOrWhiteSpace(item.GetString(command))) problems.Add("Original transformation retained without execution: " + command);
                // The constructor creates _guid independently of the dictionary key; both need not be equal.
                if (item.HasMember("_tooltype") && EnumValue(item, "_tooltype", "MyMediaViewer.module2+VideoToolType") is not (0 or 3))
                    problems.Add("Unexpected original video-tool type.");
                int? Number(string member) { try { return movie.HasMember(member) ? movie.GetInt32(member) : null; } catch (InvalidOperationException) { return null; } }
                var originalWidth = Number("_width"); var originalHeight = Number("_height");
                if (item.HasMember("_dimensions"))
                {
                    var dimensions = RequireClass(item.GetClassRecord("_dimensions"), "System.Drawing.Rectangle");
                    if (dimensions.GetInt32("x") != 0 || dimensions.GetInt32("y") != 0 || originalWidth is null || originalHeight is null ||
                        dimensions.GetInt32("width") != originalWidth || dimensions.GetInt32("height") != originalHeight)
                        problems.Add("Original custom dimensions need a separately confirmed transformation.");
                }
                entries.Add(new()
                {
                    Order = entries.Count, OriginalId = id, MovieId = item.GetInt32("_id"), InputPath = Path.Combine(folder, name),
                    StartSeconds = start, EndSeconds = end, IsClip = clip, Description = item.HasMember("_clipDescription") ? item.GetString("_clipDescription") : null,
                    OriginalWidth = originalWidth, OriginalHeight = originalHeight, BlockingIssues = problems,
                    Crossfade = item.HasMember("_crossfade") && item.GetBoolean("_crossfade"), MuteAudio = item.HasMember("_muteaudio") && item.GetBoolean("_muteaudio")
                });
            }
            return draft with
            {
                Status = HistoricalProjectionStatus.Partial, Entries = entries,
                Warnings = ["Statically reconstructed DES/NRBF file contract; original runtime parity remains unverified. No queued operation was started.",
                    "Dictionary entry order is preserved. New normalized output uses an explicit modern H.264/AAC profile; historical commands are never executed."]
            };
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return draft with { Warnings = ["Opaque combine list retained: " + error.GetType().Name + ": " + error.Message] };
        }
    }
}

public sealed partial class CatalogRepository
{
    private void InitializeLegacyCombineListSchema(SqliteTransaction transaction)
    {
        using var command = Command("CREATE TABLE IF NOT EXISTS legacy_combine_lists(id TEXT PRIMARY KEY, document TEXT NOT NULL)", transaction);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<LegacyCombineList> GetLegacyCombineLists() => ReadHistoricalDocuments<LegacyCombineList>("legacy_combine_lists");

    public LegacyCombineList ImportLegacyCombineList(string path, string sourceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var draft = LegacyCombineListReader.Read(path, sourceId); ValidateLegacyCombineList(draft);
        lock (gate)
        {
            ThrowIfDisposed(); using var tx = connection.BeginTransaction(); InitializeLegacyCombineListSchema(tx);
            using var find = Command("SELECT document FROM legacy_combine_lists WHERE id=$id", tx, ("$id", draft.Id.ToString()));
            if (find.ExecuteScalar() is string existing) return JsonSerializer.Deserialize<LegacyCombineList>(existing, Json)!;
            SaveDocument("legacy_combine_lists", draft.Id, draft, tx); cancellationToken.ThrowIfCancellationRequested(); tx.Commit(); return draft;
        }
    }

    private void SavePortableLegacyCombineList(JsonElement value, SqliteTransaction transaction)
    {
        var draft = value.Deserialize<LegacyCombineList>(Json) ?? throw new InvalidDataException("Null combine-list draft.");
        ValidateLegacyCombineList(draft); SaveDocument("legacy_combine_lists", draft.Id, draft, transaction);
    }

    private static void ValidateLegacyCombineList(LegacyCombineList draft)
    {
        LegacyHistoricalReader.RequireIdentity(draft.SourceId, "combine-list");
        if (draft.Id == Guid.Empty || draft.Name is null || draft.Name.Length > 4096 || draft.PayloadBase64 is null ||
            draft.PayloadBase64.Length > (long)LegacyObjectReader.MaximumPayloadBytes * 4 / 3 + 4 || draft.Entries is null || draft.Entries.Count > 10_000 ||
            draft.Warnings is null || !Enum.IsDefined(draft.Status)) throw new InvalidDataException("Invalid bounded combine-list draft.");
        LegacyCombineListReader.ValidateProjection(draft);
    }
}
