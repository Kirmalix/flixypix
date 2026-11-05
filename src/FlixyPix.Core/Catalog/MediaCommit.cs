using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    /// <summary>Publish cache changes only after the database transaction has committed.</summary>
    private void CommitMediaChanges(SqliteTransaction transaction, (long Changes, long DataVersion) before,
        IReadOnlyList<(MediaItem Old, MediaItem New)> changes)
    {
        transaction.Commit();
        if (changes.Count > 0) CarryCachesAcross(before, changes);
    }
}
