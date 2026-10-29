using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>Schema 16: smaller documents. All documents are stored without indentation, media documents also without
/// properties that are null (SQLite reads a missing property as null as well). A medium taken over from the original no
/// longer carries a copy of the original's row in Extra["legacy.values"]: legacy_records keeps that row verbatim (found via
/// migration_rows), and the two dates the query table needs move to <see cref="MediaItem.OriginalAddedAt"/> and
/// <see cref="MediaItem.OriginalFileDate"/>. On a real takeover catalog the media documents shrank from 221 to 84 MB.</summary>
public sealed partial class CatalogRepository
{
    private void CompactDocuments(SqliteTransaction transaction, IProgress<CatalogOpenProgress>? progress, CancellationToken token)
    {
        var ids = new List<string>();
        using (var select = Command("SELECT id FROM media", transaction))
        using (var reader = select.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
        progress?.Report(new(CatalogOpenPhase.Entries, 0, ids.Count)); var done = 0;
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            if (++done % 500 == 0 || done == ids.Count) progress?.Report(new(CatalogOpenPhase.Entries, done, ids.Count));
            string document;
            using (var read = Command("SELECT document FROM media WHERE id=$id", transaction, ("$id", id))) document = (string)read.ExecuteScalar()!;
            var item = JsonSerializer.Deserialize<MediaItem>(document, Json) ?? throw new InvalidDataException("Invalid media document.");
            if (item.Extra.Remove("legacy.values", out var raw) && raw is not null)
            {
                try
                {
                    using var values = JsonDocument.Parse(raw);
                    item = item with { OriginalAddedAt = item.OriginalAddedAt ?? Text(values.RootElement, "addedDate"), OriginalFileDate = item.OriginalFileDate ?? Text(values.RootElement, "filedate") };
                }
                catch (JsonException) { } // a value set by hand rather than by the takeover; the row in legacy_records stays
            }
            item.Extra.Remove("legacy.sourceSha256");
            using var update = Command("UPDATE media SET document=$json WHERE id=$id", transaction, ("$json", JsonSerializer.Serialize(item, MediaJson)), ("$id", id));
            update.ExecuteNonQuery();
        }
        using (var assets = Command("""UPDATE media_assets SET document=json_remove(document,'$.extra."legacy.sourceSha256"') WHERE json_extract(document,'$.extra."legacy.sourceSha256"') IS NOT NULL""", transaction))
            assets.ExecuteNonQuery();
        // json() returns the same document without whitespace.
        var tables = new List<string>();
        using (var select = Command("SELECT m.name FROM sqlite_master m JOIN pragma_table_info(m.name) c ON c.name='document' WHERE m.type='table' AND m.name<>'media'", transaction))
        using (var reader = select.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
        foreach (var table in tables) { using var minify = Command($"UPDATE \"{table}\" SET document=json(document) WHERE json_valid(document)", transaction); minify.ExecuteNonQuery(); }
    }
}
