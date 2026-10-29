using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    private ImportResult ProjectLegacy(Func<string, IEnumerable<JsonElement>> rows, string source,
        SqliteTransaction transaction, List<string> warnings)
    {
            var errors = new List<string>();
            var imported = 0;
            var skipped = 0;
            var mapped = new LegacyMediaMap(this,transaction);
            foreach (var table in new[] { "movies", "pictures" })
            foreach (var values in rows(table))
            {
                try
                {
                    var id = Text(values, "ID");
                    var directory = Text(values, "fullpath");
                    var filename = Text(values, "filename");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(filename))
                        throw new ArgumentException("Missing ID, fullpath or filename; raw row retained.");
                    // Original splitFileAndPath stores a trailing separator in fullpath, plus filename separately.
                    var fullPath = System.IO.Path.Combine(directory, filename);
                    var kind = table.Equals("pictures", StringComparison.OrdinalIgnoreCase) ? MediaKind.Image : MediaKind.Video;
                    var rating = Number(values, "rating") ?? 0;
                    if (rating is < 0 or > 10 || rating % 1 != 0)
                    {
                        warnings.Add($"{table}/{id}: original rating {rating} retained in legacy values; outside supported integer 0–10 range.");
                        rating = 0;
                    }
                    var item = new MediaItem
                    {
                        Id = StableLegacyId(source, ":" + table.ToLowerInvariant() + ":" + id),
                        LegacyId = table + ":" + id,
                        Path = fullPath,
                        Title = Text(values, "title") is { Length: > 0 } title ? title : System.IO.Path.GetFileNameWithoutExtension(filename),
                        Kind = kind,
                        Width = PositiveInteger(values, "width"),
                        Height = PositiveInteger(values, "height"),
                        FileSize = NonnegativeSize(values),
                        DurationSeconds = kind == MediaKind.Video && Number(values, "length") is >= 0 and { } length ? length : null,
                        PlaybackPositionSeconds = kind == MediaKind.Video && Number(values, "lastposition") is >= 0 and { } position ? position : 0,
                        Rating = (int)rating,
                        PlayCount=NonnegativeLegacyInteger(values,"plays"), ViewCount=NonnegativeLegacyInteger(values,"views"),
                        OriginalAddedAt=Text(values,"addedDate"), OriginalFileDate=Text(values,"filedate"),
                        // The original row stays verbatim in legacy_records (found via migration_rows); no copy per medium.
                        Extra = new Dictionary<string, string?> { ["legacy.table"] = table }
                    };
                    using(var lookup=Command("SELECT target_id FROM legacy_mappings WHERE source_id=$source AND legacy_key=$key",transaction,("$source",legacySession!.SourceId),("$key",table+":"+id)))
                    if(lookup.ExecuteScalar() is string priorId)item=item with { Id=Guid.Parse(priorId) };
                    var saved = UpsertCore(item, transaction);
                    AuditLegacy(table,id,"partially-projected","media-supported-fields",transaction,saved.Id);
                    mapped[table + ":" + id] = saved;
                    imported++;
                }
                catch (Exception ex) when (ex is ArgumentException or SqliteException or OverflowException)
                { errors.Add($"{table}: {ex.Message}"); AuditLegacy(table,Text(values,"ID"),"invalid","invalid-media",transaction); skipped++; }
            }

            // Proven keyword association: type 2 = movies, type 3 = pictures; remoteID is the item ID.
            // Do not treat searchcriteria vocabulary as media associations.
            foreach (var values in rows("keyword"))
            {
                var kind = Number(values, "type");
                var table = kind == 2 ? "movies" : kind == 3 ? "pictures" : null;
                var remote = Text(values, "remoteID");
                var tags = Text(values, "keytags");
                if (table is null || remote is null || string.IsNullOrWhiteSpace(tags) || !mapped.TryGetValue(table + ":" + remote, out var item)) continue;
                if (tags.Length < 2 || tags[0] != '"' || tags[^1] != '"')
                {
                    warnings.Add($"keyword/{Text(values, "ID")}: unrecognized keytags encoding retained verbatim.");
                    continue;
                }
                var decoded = tags[1..^1].Split("\" \"", StringSplitOptions.RemoveEmptyEntries);
                if (decoded.Any(x => x.Contains('"')))
                {
                    warnings.Add($"keyword/{Text(values, "ID")}: ambiguous embedded quote in keytags retained verbatim.");
                    continue;
                }
                var updated = UpsertCore(item with { Tags = item.Tags.Concat(decoded).ToList() }, transaction);
                mapped[table + ":" + remote] = updated;
                AuditLegacy("keyword",Text(values,"ID"),"partially-projected","media-tags",transaction,updated.Id);
            }
            ImportLegacyCategories(rows, mapped, transaction, warnings);
            ImportLegacyAppSettings(rows, transaction, warnings);
            ImportLegacyCollections(rows, source, mapped, transaction, errors, warnings);
            ImportLegacyAssets(rows, source, mapped, transaction, warnings);
            ImportLegacyOrganization(rows,transaction,warnings);
            ImportLegacyTagRoles(rows,transaction);
            ImportLegacyPlayerTransforms(rows,mapped,transaction,warnings);
            warnings.Add("Legacy schema, header, rows and footer are retained in portable export; unsupported settings, image slideshow parameters, serialized rules and content path behavior are not interpreted.");
            return new ImportResult { Imported = imported, Skipped = skipped, Errors = errors, Warnings = warnings };
    }

    private static long? NonnegativeLegacyInteger(JsonElement values,string field)=>long.TryParse(Text(values,field),NumberStyles.Integer,CultureInfo.InvariantCulture,out var result)&&result>=0?result:null;
    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    private static JsonElement Property(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }
    private static string? Text(JsonElement value, string name)
    {
        var property = Property(value, name);
        if (property.ValueKind == JsonValueKind.Null || property.ValueKind == JsonValueKind.Undefined) return null;
        if (property.ValueKind == JsonValueKind.String) return property.GetString();
        if (property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return property.ToString();
        foreach (var type in new[] { "$datetime", "$guid", "$decimal" })
        {
            var unwrapped = Property(property, type);
            if (unwrapped.ValueKind == JsonValueKind.String) return unwrapped.GetString();
        }
        return null;
    }
    private static double? Number(JsonElement value, string name) =>
        double.TryParse(Text(value, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : null;
    private static int? PositiveInteger(JsonElement value, string name) => Number(value, name) is > 0 and <= int.MaxValue and { } number ? (int)number : null;
    private static long? NonnegativeSize(JsonElement values) => Number(values, "filesize") is >= 0 and < long.MaxValue and { } number ? (long)number : null;
}
