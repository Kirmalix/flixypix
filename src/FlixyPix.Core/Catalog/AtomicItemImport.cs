using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    internal ImportResult ImportItemsAtomically(IReadOnlyList<MediaItem> items)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var imported = 0;
            try
            {
                foreach (var item in items) { UpsertCore(item, transaction); imported++; }
                transaction.Commit();
                return new ImportResult { Imported = imported };
            }
            catch (Exception ex) when (ex is ArgumentException or SqliteException)
            {
                transaction.Rollback();
                cachedMedia = null;
                return new ImportResult { Errors = [$"CSV import rolled back: {ex.Message}"] };
            }
        }
    }
}
