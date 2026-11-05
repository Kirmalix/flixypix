using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    private MediaItem? ReadImportedPath(string path, SqliteTransaction transaction)
    {
        using var command = Command("SELECT document FROM media WHERE path_key=$path", transaction, ("$path", PathKey(path)));
        return command.ExecuteScalar() is string json ? System.Text.Json.JsonSerializer.Deserialize<MediaItem>(json, Json) : null;
    }

    internal ImportResult ImportItemsAtomically(IReadOnlyList<MediaItem> items)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var imported = 0;
            try
            {
                foreach (var item in items)
                {
                    var existing = ReadMediaDocument(item.Id, transaction) ?? ReadImportedPath(item.Path, transaction);
                    UpsertCore(item with { ReviewState = existing?.ReviewState ?? (item.ReviewState == ReviewState.Unspecified ? ReviewState.Unreviewed : item.ReviewState) }, transaction); imported++;
                }
                transaction.Commit();
                return new ImportResult { Imported = imported };
            }
            catch (Exception ex) when (ex is ArgumentException or SqliteException)
            {
                transaction.Rollback();

                return new ImportResult { Errors = [$"CSV import rolled back: {ex.Message}"] };
            }
        }
    }
}
