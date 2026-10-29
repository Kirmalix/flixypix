using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    private static readonly HashSet<string> PortableArrays = new(StringComparer.Ordinal)
    { "items", "playlists", "scenes", "savedsearches", "filenametagrules", "bookmarks", "mediaassets", "collectionsets", "legacyrecords", "migrationstate",
     "legacyblobs", "tagpresets", "legacysearchdrafts", "legacyjobdrafts", "legacyparserdrafts", "tagassets", "historicalparserdefinitions",
     "legacycombinelists", "historicalcontentfolders" };

    private void StagePortable(string path, int maximumEntityBytes, SqliteTransaction transaction, CancellationToken cancellationToken, string? secret)
    {
        using (var create = Command("""
            CREATE TEMP TABLE portable_stage(section TEXT NOT NULL,ordinal INTEGER NOT NULL,document TEXT NOT NULL,PRIMARY KEY(section,ordinal));
            CREATE TEMP TABLE portable_id_map(original TEXT PRIMARY KEY,mapped TEXT NOT NULL);
            CREATE TEMP TABLE portable_legacy_rows(group_id INTEGER NOT NULL,ordinal INTEGER NOT NULL,record TEXT NOT NULL,PRIMARY KEY(group_id,ordinal));
            CREATE TEMP TABLE portable_legacy_batches(group_id INTEGER PRIMARY KEY,batch TEXT NOT NULL);
            """, transaction)) create.ExecuteNonQuery();
        using var input = new PortableJsonInput(path, maximumEntityBytes, cancellationToken, secret);
        if (input.ReadToken().Type != JsonTokenType.StartObject) throw new InvalidDataException("Expected a portable catalog JSON object.");
        string? format = null; int? version = null;
        while (true)
        {
            var token = input.ReadToken();
            if (token.Type == JsonTokenType.EndObject) break;
            if (token.Type != JsonTokenType.PropertyName) throw new InvalidDataException("Expected a portable catalog property name.");
            var section = token.Text!.ToLowerInvariant();
            if (PortableArrays.Contains(section))
            {
                // Preserve System.Text.Json's last-property-wins behavior without accumulating duplicate sections.
                using (var clear = Command("DELETE FROM temp.portable_stage WHERE section=$section", transaction, ("$section", section))) clear.ExecuteNonQuery();
                if (input.ReadToken().Type != JsonTokenType.StartArray) throw new InvalidDataException($"Portable catalog '{section}' must be an array, not null.");
                var ordinal = 0;
                while (input.ReadValueOrEndArray() is { } value)
                {
                    using (value) SavePortableStage(section, ordinal++, value.RootElement.GetRawText(), transaction);
                }
            }
            else
            {
                using var value = input.ReadValue();
                if (section == "format") format = value.RootElement.ValueKind == JsonValueKind.String ? value.RootElement.GetString() : null;
                else if (section == "version") version = value.RootElement.ValueKind == JsonValueKind.Number && value.RootElement.TryGetInt32(out var number) ? number : null;
                else if (section == "preferences")
                {
                    using (var clear = Command("DELETE FROM temp.portable_stage WHERE section='preferences'", transaction)) clear.ExecuteNonQuery();
                    if (value.RootElement.ValueKind != JsonValueKind.Null) SavePortableStage(section, 0, value.RootElement.GetRawText(), transaction);
                }
                // Unknown top-level properties are permitted, bounded and structurally validated, as before.
            }
        }
        input.RequireEnd();
        if (!ProductIdentity.IsFormat(format!,"flixypix-catalog") || version is not (1 or 2 or 3 or 4)) throw new InvalidDataException("Unsupported or missing portable catalog format/version (expected version 1, 2, 3 or 4).");
    }

    private void SavePortableStage(string section, int ordinal, string json, SqliteTransaction transaction)
    {
        using var insert = Command("INSERT INTO temp.portable_stage VALUES($section,$ordinal,$json)", transaction,
            ("$section", section), ("$ordinal", ordinal), ("$json", json));
        insert.ExecuteNonQuery();
    }
    private IEnumerable<T> ReadStagedPortable<T>(string section, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = Command("SELECT document FROM temp.portable_stage WHERE section=$section ORDER BY ordinal", transaction, ("$section", section));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!;
        }
    }
    private void SavePortableIdMap(Guid original, Guid mapped, SqliteTransaction transaction)
    {
        using var command = Command("INSERT INTO temp.portable_id_map VALUES($original,$mapped) ON CONFLICT(original) DO UPDATE SET mapped=excluded.mapped", transaction,
            ("$original", original.ToString()), ("$mapped", mapped.ToString())); command.ExecuteNonQuery();
    }
    private Guid MapPortableId(Guid original, SqliteTransaction transaction)
    {
        using var command = Command("SELECT mapped FROM temp.portable_id_map WHERE original=$original", transaction, ("$original", original.ToString()));
        return command.ExecuteScalar() is string mapped ? Guid.Parse(mapped) : original;
    }
    private static void AddPortableError(List<string> errors, string message)
    {
        if (errors.Count >= 100) throw new InvalidDataException("Import stopped after 100 entity errors; no changes were retained.");
        errors.Add(message);
    }

    private void ImportStagedPortableLegacy(SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var group = 0; var ordinal = 0;
        foreach (var raw in ReadStagedPortable<string>("legacyrecords", transaction, cancellationToken))
        {
            if (raw is null) throw new InvalidDataException("Legacy record cannot be null.");
            using var parsed = JsonDocument.Parse(raw);
            var type = Text(parsed.RootElement, "type");
            if (type == "header" && ordinal != 0) Finish();
            if (ordinal == 0) hash.AppendData("["u8); else hash.AppendData(","u8);
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(raw)));
            using (var insert = Command("INSERT INTO temp.portable_legacy_rows VALUES($group,$ordinal,$raw)", transaction,
                ("$group", group), ("$ordinal", ordinal++), ("$raw", raw))) insert.ExecuteNonQuery();
            if (type == "footer") Finish();
        }
        if (ordinal != 0) Finish();
        using var save = Command("""
            INSERT OR IGNORE INTO legacy_records(batch,ordinal,record)
            SELECT b.batch,r.ordinal,r.record FROM temp.portable_legacy_rows r JOIN temp.portable_legacy_batches b ON b.group_id=r.group_id
            ORDER BY r.group_id,r.ordinal
            """, transaction); save.ExecuteNonQuery();

        void Finish()
        {
            hash.AppendData("]"u8);
            var batch = Convert.ToHexString(hash.GetHashAndReset());
            using var insert = Command("INSERT INTO temp.portable_legacy_batches VALUES($group,$batch)", transaction, ("$group", group), ("$batch", batch));
            insert.ExecuteNonQuery(); group++; ordinal = 0;
        }
    }

    private void ValidatePortablePreviewReferences(SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "scenes", "bookmarks" })
        {
            using var command = Command($"SELECT media_id,json_extract(document,'$.previewAssetId') FROM {table} WHERE json_extract(document,'$.previewAssetId') IS NOT NULL", transaction);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsurePreviewOwnership(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), transaction);
            }
        }
    }

    /// <summary>Incremental UTF-8 JSON reader retaining no more than one bounded array entity.</summary>
    private sealed class PortableJsonInput : IDisposable
    {
        private readonly Stream stream;
        private readonly int limit;
        private readonly CancellationToken cancellation;
        private byte[] buffer;
        private int start, end;
        private bool final;
        private JsonReaderState state;
        public PortableJsonInput(string path, int limit, CancellationToken cancellation, string? secret = null)
        {
            Stream file;
            if (EncryptedContainer.IsContainer(path))
            {
                if (secret is null) throw new CatalogLockedException(T("The export file is encrypted. Enter the password or recovery key of the exporting library."));
                file = EncryptedContainer.OpenRead(path, EncryptedContainer.PortableContent, secret);
            }
            else file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            try
            {
                // Match the old text reader's BOM support without allocating a transcoded whole document.
                Span<byte> prefix = stackalloc byte[4]; var prefixLength = file.Read(prefix); file.Position = 0;
                Encoding? encoding = null; var bom = 0;
                if (prefixLength >= 4 && prefix.SequenceEqual(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); bom = 4; }
                else if (prefixLength >= 4 && prefix.SequenceEqual(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, false, true); bom = 4; }
                else if (prefixLength >= 3 && prefix[..3].SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf })) bom = 3;
                else if (prefixLength >= 2 && prefix[..2].SequenceEqual(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); bom = 2; }
                else if (prefixLength >= 2 && prefix[..2].SequenceEqual(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); bom = 2; }
                file.Position = bom;
                stream = encoding is null ? file : Encoding.CreateTranscodingStream(file, encoding, new UTF8Encoding(false, true), leaveOpen: false);
            }
            catch { file.Dispose(); throw; }
            this.limit = limit; this.cancellation = cancellation;
            buffer = new byte[Math.Min(64 * 1024, limit + 16)];
        }
        public (JsonTokenType Type, string? Text) ReadToken()
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var reader = new Utf8JsonReader(buffer.AsSpan(start, end - start), final, state);
                if (reader.Read())
                {
                    var token = (reader.TokenType, reader.TokenType == JsonTokenType.PropertyName ? reader.GetString() : null);
                    Consume(reader); return token;
                }
                Consume(reader);
                if (final) throw new InvalidDataException("Unexpected end of portable catalog JSON.");
                Fill();
            }
        }
        public JsonDocument ReadValue() => ReadValueCore(false) ?? throw new InvalidDataException("Expected a JSON value.");
        public JsonDocument? ReadValueOrEndArray() => ReadValueCore(true);
        private JsonDocument? ReadValueCore(bool allowEndArray)
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var reader = new Utf8JsonReader(buffer.AsSpan(start, end - start), final, state);
                if (!reader.Read())
                {
                    Consume(reader);
                    if (final) throw new InvalidDataException("Truncated portable catalog value.");
                    Fill(); continue;
                }
                if (allowEndArray && reader.TokenType == JsonTokenType.EndArray) { Consume(reader); return null; }
                var valueStart = reader.TokenStartIndex;
                if (JsonDocument.TryParseValue(ref reader, out var document))
                {
                    if (reader.BytesConsumed - valueStart > limit) { document.Dispose(); throw new InvalidDataException($"Portable entity exceeds the {limit}-byte limit."); }
                    Consume(reader); return document;
                }
                if (final) throw new InvalidDataException("Truncated portable catalog value.");
                // The original state still points before the value; preserve its bytes while filling.
                Fill();
            }
        }
        public void RequireEnd()
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var reader = new Utf8JsonReader(buffer.AsSpan(start, end - start), final, state);
                if (reader.Read()) throw new InvalidDataException("Unexpected data after portable catalog object.");
                Consume(reader);
                if (final) return;
                Fill();
            }
        }
        private void Consume(Utf8JsonReader reader) { start += checked((int)reader.BytesConsumed); state = reader.CurrentState; }
        private void Fill()
        {
            cancellation.ThrowIfCancellationRequested();
            if (start != 0) { buffer.AsSpan(start, end - start).CopyTo(buffer); end -= start; start = 0; }
            if (end == buffer.Length)
            {
                if (buffer.Length >= limit + 16) throw new InvalidDataException($"Portable entity exceeds the {limit}-byte limit.");
                Array.Resize(ref buffer, Math.Min(limit + 16, checked(buffer.Length * 2)));
            }
            var count = stream.Read(buffer, end, buffer.Length - end); end += count; final = count == 0;
        }
        public void Dispose() => stream.Dispose();
    }
}
