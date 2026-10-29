using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    public ImportResult ImportLegacyJsonl(string path) => ImportLegacyJsonl(path, new LegacyImportOptions());

    /// <summary>Stages validated records in temporary disk-backed SQLite storage, never the entire stream in RAM.</summary>
    public ImportResult ImportLegacyJsonl(string path, int maximumRecordCharacters, CancellationToken cancellationToken = default)
        => ImportLegacyJsonl(path,new LegacyImportOptions { MaximumRecordCharacters=maximumRecordCharacters },cancellationToken);

    public ImportResult ImportLegacyJsonl(string path,LegacyImportOptions options,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var maximumRecordCharacters=options.MaximumRecordCharacters;
        if(!Enum.IsDefined(options.ConflictPolicy))throw new ArgumentException("Invalid conflict policy.");
        if (maximumRecordCharacters < 1024) throw new ArgumentOutOfRangeException(nameof(maximumRecordCharacters), "Record limit must be at least 1024 characters.");
        lock (gate)
        {
            ThrowIfDisposed();
            using (var configure = Command("PRAGMA temp_store=FILE", null)) configure.ExecuteNonQuery();
            InitializeAssetSchema(null); // changing temp_store drops the temporary tables and triggers
            using var transaction = connection.BeginTransaction();
            try
            {
                using (var create = Command("""
                    CREATE TEMP TABLE legacy_stage(ordinal INTEGER PRIMARY KEY, record_type TEXT, table_name TEXT, record TEXT NOT NULL);
                    CREATE INDEX temp.legacy_stage_table ON legacy_stage(record_type,table_name COLLATE NOCASE);
                    CREATE TEMP TABLE legacy_blob_refs(sha256 TEXT PRIMARY KEY,length INTEGER NOT NULL);
                    """, transaction)) create.ExecuteNonQuery();
                var warnings = new List<string>();
                string HashExport(){using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return Convert.ToHexStringLower(SHA256.HashData(input));}
                var exportHash=HashExport();
                var (source, batch, counts, schemas) = StageLegacy(path, maximumRecordCharacters, transaction, warnings, cancellationToken);
                if(exportHash!=HashExport())throw new InvalidDataException("The exchange changed during import.");
                using(var existing=Command("SELECT report FROM legacy_imports WHERE batch=$batch",transaction,("$batch",batch)))
                if(existing.ExecuteScalar() is string prior)
                {
                    var report=JsonSerializer.Deserialize<MigrationReport>(prior,Json)! with { AlreadyImported=true };
                    transaction.Rollback();return new ImportResult { Migration=report,Skipped=(int)Math.Min(int.MaxValue,report.Rows),Warnings=["Identical migration batch already imported; existing edits were retained."] };
                }
                BeginLegacyProjection(source,batch,options,transaction,cancellationToken);
                using(var audit=Command("""
                    INSERT INTO migration_rows(batch,ordinal,table_name,legacy_key,status,code,target_ids)
                    SELECT $batch,ordinal,table_name,coalesce(CAST(json_extract(record,'$.values.ID') AS TEXT),CAST(json_extract(record,'$.values.id') AS TEXT),
                    CASE WHEN lower(table_name)='configsettings' THEN CAST(json_extract(record,'$.values.parameter') AS TEXT) END),
                    'raw-retained','preserved-unverified-projection','[]' FROM temp.legacy_stage WHERE record_type='row'
                    """,transaction,("$batch",batch)))audit.ExecuteNonQuery();
                using (var save = Command("INSERT OR IGNORE INTO legacy_records(batch,ordinal,record) SELECT $batch,ordinal,record FROM temp.legacy_stage ORDER BY ordinal",
                    transaction, ("$batch", batch))) save.ExecuteNonQuery();
                var result = ProjectLegacy(table => ReadStagedLegacyRows(table, transaction, cancellationToken), source, transaction, warnings);
                ImportHistoricalData(table=>ReadStagedLegacyRows(table,transaction,cancellationToken),legacySession!.SourceId,transaction,warnings,cancellationToken);
                ImportLegacyParserSettings(table=>ReadStagedLegacyRows(table,transaction,cancellationToken),legacySession!.SourceId,transaction,warnings,cancellationToken);
                ImportHistoricalContentFolders(table=>ReadStagedLegacyRows(table,transaction,cancellationToken),legacySession!.SourceId,transaction,warnings,cancellationToken);
                MarkOriginalDefaults(options.OriginalDefaults,batch,transaction);
                var migration=FinishLegacyReport(source,batch,exportHash,transaction);
                foreach (var (table, count) in counts)
                    if (table is not ("movies" or "pictures"))
                        warnings.Add($"{table}: {count} original rows retained verbatim; only verified fields projected.");
                using (var drop = Command("DROP TABLE temp.legacy_stage; DROP TABLE temp.legacy_blob_refs; DROP TABLE temp.legacy_media_map; DROP TABLE temp.legacy_protected_entities;", transaction)) drop.ExecuteNonQuery();
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
                return result with { Migration=migration, Compatibility=LegacyCompatibility.Assess(schemas) };
            }
            catch (OperationCanceledException)
            {
                transaction.Rollback(); cachedMedia = null; throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or SqliteException)
            {
                transaction.Rollback();
                cachedMedia = null;
                return new ImportResult { Errors = [ex.Message] };
            }
            finally { legacySession=null; }
        }
    }

    private (string Source, string Batch, Dictionary<string, int> Counts, Dictionary<string, HashSet<string>> Schemas) StageLegacy(string path, int limit,
        SqliteTransaction transaction, List<string> warnings, CancellationToken cancellationToken)
    {
        var schemas = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var tableRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rowCount = 0;
        var recordCount = 0;
        var footerSeen = false;
        var source = "";
        var version=1;
        using var input = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("["u8);
        foreach (var (lineNumber, raw) in ReadBoundedLines(input, limit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            using var parsed = JsonDocument.Parse(raw);
            var value = parsed.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Line {lineNumber}: expected a JSON object.");
            var type = Text(value, "type");
            var table = Text(value, "table");
            if (recordCount == 0)
            {
                if (type != "header" || !ProductIdentity.IsFormat(Text(value, "format")!,"flixypix-legacy") || Number(value, "version") is not (1 or 2))
                    throw new InvalidDataException("Expected flixypix-legacy version 1 or 2 header.");
                version=(int)Number(value,"version")!;
                source = (Text(value, "sourceSha256") ?? "").ToLowerInvariant();
                if (source.Length != 64 || !source.All(Uri.IsHexDigit)) throw new InvalidDataException("Header is missing a valid source SHA-256.");
            }
            else if (footerSeen) throw new InvalidDataException($"Line {lineNumber}: unexpected record after footer.");
            else if (type == "header") throw new InvalidDataException($"Line {lineNumber}: duplicate stream header.");
            else if (type == "row")
            {
                var values = Property(value, "values");
                if (string.IsNullOrWhiteSpace(table) || values.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"Line {lineNumber}: invalid table row.");
                if (!schemas.TryGetValue(table, out var columns)) throw new InvalidDataException($"Line {lineNumber}: row has no preceding schema for '{table}'.");
                var names = values.EnumerateObject().Select(x => x.Name).ToList();
                if (names.Count != columns.Count || !columns.SetEquals(names)) throw new InvalidDataException($"Line {lineNumber}: row/schema columns do not match for '{table}'.");
                tableRows[table]++;
                rowCount++;
                foreach(var property in values.EnumerateObject())
                {
                    var reference=Property(property.Value,"$binaryRef");
                    if(reference.ValueKind==JsonValueKind.Undefined)continue;
                    if(version!=2)throw new InvalidDataException("Binary references require legacy exchange version 2.");
                    StageLegacyBlob(reference,System.IO.Path.GetFullPath(path)+".blobs",transaction,cancellationToken);
                }
            }
            else if (type == "schema")
            {
                var columnArray = Property(value, "columns");
                if (string.IsNullOrWhiteSpace(table) || columnArray.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"Line {lineNumber}: invalid schema record.");
                var names = columnArray.EnumerateArray().Select(x => Text(x, "name")).ToList();
                if (names.Count == 0 || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count || schemas.ContainsKey(table))
                    throw new InvalidDataException($"Line {lineNumber}: empty, duplicate or invalid schema for '{table}'.");
                schemas.Add(table, new HashSet<string>(names!, StringComparer.OrdinalIgnoreCase));
                tableRows.Add(table, 0);
            }
            else if (type == "footer")
            {
                if (Number(value, "rows") != rowCount || Number(value, "tables") != schemas.Count) throw new InvalidDataException("Footer row/table count does not match the stream.");
                var counts = Property(value, "tableRows");
                if (counts.ValueKind != JsonValueKind.Object || counts.EnumerateObject().Count() != schemas.Count) throw new InvalidDataException("Footer per-table counts are missing or inconsistent.");
                foreach (var (name, count) in tableRows)
                    if (Number(counts, name) != count) throw new InvalidDataException($"Footer row count for '{name}' does not match the stream.");
                if(version==2)
                {
                    using var blobs=Command("SELECT count(*),coalesce(sum(length),0) FROM temp.legacy_blob_refs",transaction);using var reader=blobs.ExecuteReader();reader.Read();
                    if(Number(value,"blobCount")!=reader.GetInt64(0)||Number(value,"blobBytes")!=reader.GetInt64(1))throw new InvalidDataException("Footer binary counts do not match the exchange.");
                }
                footerSeen = true;
            }
            else warnings.Add($"Unmapped record type '{type}' retained verbatim (line {lineNumber}).");
            if (recordCount != 0) hash.AppendData(","u8);
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(raw)));
            using var insert = Command("INSERT INTO temp.legacy_stage(ordinal,record_type,table_name,record) VALUES($ordinal,$type,$table,$raw)", transaction,
                ("$ordinal", recordCount++), ("$type", (object?)type ?? DBNull.Value), ("$table", (object?)table ?? DBNull.Value), ("$raw", raw));
            insert.ExecuteNonQuery();
        }
        if (!footerSeen) throw new InvalidDataException("Legacy stream has no footer; export may be incomplete.");
        // Lookups compare table_name COLLATE NOCASE; a BINARY index is unusable for them and turned the
        // per-media description lookup into a full scan of the stage (quadratic on real catalogs).
        using(var index=Command("""
            CREATE INDEX temp.legacy_description_lookup ON legacy_stage(table_name COLLATE NOCASE,CAST(json_extract(record,'$.values.mtype') AS TEXT),CAST(json_extract(record,'$.values.remoteid') AS TEXT));
            CREATE INDEX temp.legacy_playlist_lookup ON legacy_stage(table_name COLLATE NOCASE,json_extract(record,'$.values.description'));
            """,transaction))index.ExecuteNonQuery();
        hash.AppendData("]"u8);
        return (source, Convert.ToHexString(hash.GetHashAndReset()), tableRows, schemas);
    }

    private IEnumerable<JsonElement> ReadStagedLegacyRows(string table, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = Command("SELECT record FROM temp.legacy_stage WHERE record_type='row' AND table_name=$table COLLATE NOCASE ORDER BY ordinal", transaction, ("$table", table));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var parsed = JsonDocument.Parse(reader.GetString(0));
            yield return Property(parsed.RootElement, "values").Clone();
        }
    }

    private IEnumerable<JsonElement> ReadLegacyPlaylistRows(string name,SqliteTransaction transaction)
    {
        using var command=Command("SELECT record FROM temp.legacy_stage WHERE table_name='videoplaylists' COLLATE NOCASE AND json_extract(record,'$.values.description')=$name ORDER BY coalesce(json_extract(record,'$.values.position'),1e100),ordinal",transaction,("$name",name));
        using var reader=command.ExecuteReader();while(reader.Read()){legacySession!.Token.ThrowIfCancellationRequested();using var parsed=JsonDocument.Parse(reader.GetString(0));yield return Property(parsed.RootElement,"values").Clone();}
    }
    private string? LegacyDescription(string type,string remote,SqliteTransaction tx,List<string> warnings)
    {
        using var command=Command("SELECT DISTINCT json_extract(record,'$.values.description') FROM temp.legacy_stage WHERE table_name='descriptions' COLLATE NOCASE AND CAST(json_extract(record,'$.values.mtype') AS TEXT)=$type AND CAST(json_extract(record,'$.values.remoteid') AS TEXT)=$remote LIMIT 2",tx,("$type",type),("$remote",remote));
        using var reader=command.ExecuteReader();if(!reader.Read())return null;var value=reader.IsDBNull(0)?null:reader.GetString(0);if(!reader.Read())return value;warnings.Add($"descriptions/{type}:{remote}: conflicting descriptions preserved raw.");return null;
    }

    private static IEnumerable<(int LineNumber, string Raw)> ReadBoundedLines(TextReader input, int limit)
    {
        var buffer = new char[8192];
        var line = new StringBuilder();
        var lineNumber = 1;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            var start = 0;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != '\n') continue;
                Append(start, index - start);
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                yield return (lineNumber++, line.ToString());
                line.Clear();
                start = index + 1;
            }
            Append(start, read - start);
        }
        if (line.Length != 0)
        {
            if (line[^1] == '\r') line.Length--;
            yield return (lineNumber, line.ToString());
        }
        void Append(int start, int count)
        {
            if ((long)line.Length + count > limit)
                throw new InvalidDataException($"Legacy record at line {lineNumber} exceeds the configured {limit:N0}-character limit. Increase maximumRecordCharacters explicitly for larger artwork records; no data were imported.");
            line.Append(buffer, start, count);
        }
    }
}
