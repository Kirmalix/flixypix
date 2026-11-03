using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>The catalog's side of the <see cref="AssetStore"/>: media graphics, tag images and the original's binary values
/// keep only "sha256" and "byteLength" in their rows, the bytes lie in files beside the catalog. A file is written before
/// the row that uses it; a rolled-back or interrupted write therefore leaves at most an unused file, never a row without
/// bytes. Rows that stop using bytes note them in a temporary table (triggers, so cascades count too); those files go when
/// nothing uses them any more: on closing, compacting and in the catalog check.</summary>
public sealed partial class CatalogRepository
{
    private AssetStore assets = null!;

    /// <summary>Folder of the picture files beside the catalog.</summary>
    public string AssetDirectory => assets.Directory;
    /// <summary>Whether the pictures of this library are stored encrypted (only in an encrypted library).</summary>
    public bool EncryptsPictures => assets.Encrypts;

    /// <summary>Encrypts the picture files of an encrypted library, or stores them as plain image files again.</summary>
    public void SetPictureEncryption(bool encrypt, IProgress<CatalogOpenProgress>? progress = null, CancellationToken token = default)
    {
        lock (gate) { ThrowIfDisposed(); assets.SetEncryptFiles(encrypt, progress, token); }
    }

    private void InitializeAssetSchema(SqliteTransaction? transaction)
    {
        using var command = Command("""
            CREATE INDEX IF NOT EXISTS media_assets_hash ON media_assets(json_extract(document,'$.sha256'));
            CREATE TEMP TABLE IF NOT EXISTS released_assets(sha256 TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TRIGGER IF NOT EXISTS release_media_asset AFTER DELETE ON main.media_assets
                BEGIN INSERT OR IGNORE INTO released_assets SELECT json_extract(OLD.document,'$.sha256') WHERE json_extract(OLD.document,'$.sha256') IS NOT NULL; END;
            CREATE TEMP TRIGGER IF NOT EXISTS replace_media_asset AFTER UPDATE OF document ON main.media_assets
                WHEN json_extract(OLD.document,'$.sha256') IS NOT json_extract(NEW.document,'$.sha256')
                BEGIN INSERT OR IGNORE INTO released_assets SELECT json_extract(OLD.document,'$.sha256') WHERE json_extract(OLD.document,'$.sha256') IS NOT NULL; END;
            CREATE TEMP TRIGGER IF NOT EXISTS release_tag_asset AFTER DELETE ON main.tag_assets
                BEGIN INSERT OR IGNORE INTO released_assets SELECT json_extract(OLD.document,'$.sha256') WHERE json_extract(OLD.document,'$.sha256') IS NOT NULL; END;
            CREATE TEMP TRIGGER IF NOT EXISTS release_legacy_blob AFTER DELETE ON main.legacy_blobs
                BEGIN INSERT OR IGNORE INTO released_assets VALUES(OLD.sha256); END;
            """, transaction);
        command.ExecuteNonQuery();
    }

    /// <summary>Bytes of a row, or empty when their file is missing or damaged (the catalog check reports it).</summary>
    private byte[] LoadAssetBytes(string? sha256, long length)
    {
        if (string.IsNullOrEmpty(sha256)) return [];
        try { return assets.Read(sha256, length) ?? []; }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The stored form of a document: the same JSON without its bytes.</summary>
    private static string WithoutData<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, Json)!.AsObject(); node.Remove("data");
        return node.ToJsonString(Json);
    }

    /// <summary>A stored document with its bytes inlined again, for the portable export.</summary>
    private void WriteWithData(Utf8JsonWriter writer, string document)
    {
        var node = JsonNode.Parse(document)!.AsObject();
        var bytes = LoadAssetBytes(node["sha256"]?.GetValue<string>(), node["byteLength"]?.GetValue<long>() ?? 0);
        if (bytes.Length == 0 && (node["byteLength"]?.GetValue<long>() ?? 0) > 0) throw new InvalidDataException(T("A picture file is missing or damaged; run the catalog check."));
        node["data"] = Convert.ToBase64String(bytes); node.WriteTo(writer);
    }

    private const string ReferencedAssetsSql = """
        SELECT json_extract(document,'$.sha256') FROM media_assets WHERE json_extract(document,'$.sha256') IS NOT NULL
        UNION SELECT json_extract(document,'$.sha256') FROM tag_assets WHERE json_extract(document,'$.sha256') IS NOT NULL
        UNION SELECT sha256 FROM legacy_blobs
        """;

    /// <summary>Every SHA-256 whose bytes the catalog uses.</summary>
    internal List<string> ReferencedAssetHashes()
    {
        lock (gate)
        {
            ThrowIfDisposed(); var hashes = new List<string>();
            using var command = Command(ReferencedAssetsSql, null); using var reader = command.ExecuteReader();
            while (reader.Read()) hashes.Add(reader.GetString(0));
            return hashes;
        }
    }

    /// <summary>Deletes the files of bytes that rows stopped using since the last time, if nothing else uses them.
    /// Runs outside transactions only.</summary>
    private void FlushReleasedAssets()
    {
        var released = new List<string>();
        using (var select = Command("SELECT sha256 FROM temp.released_assets", null))
        using (var reader = select.ExecuteReader()) while (reader.Read()) released.Add(reader.GetString(0));
        if (released.Count == 0) return;
        foreach (var sha256 in released)
        {
            using var used = Command("""
                SELECT EXISTS(SELECT 1 FROM media_assets WHERE json_extract(document,'$.sha256')=$hash)
                    OR EXISTS(SELECT 1 FROM tag_assets WHERE json_extract(document,'$.sha256')=$hash)
                    OR EXISTS(SELECT 1 FROM legacy_blobs WHERE sha256=$hash)
                """, null, ("$hash", sha256));
            if (Convert.ToInt64(used.ExecuteScalar()) == 0)
                try { assets.Delete(sha256); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        using var clear = Command("DELETE FROM temp.released_assets", null); clear.ExecuteNonQuery();
    }

    /// <summary>Deletes picture files that no row uses (left by interrupted or rolled-back writes). Returns number and size.</summary>
    public (int Files, long Bytes) RemoveUnusedAssetFiles(CancellationToken token = default)
    {
        // Inside the lock, so no picture can be written between reading the references and deleting.
        lock (gate) { ThrowIfDisposed(); FlushReleasedAssets(); return assets.Collect(ReferencedAssetHashes(), token); }
    }
}
