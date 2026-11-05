using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>Shared connection access for catalog modules. The repository owns its lifetime and the admission gate.</summary>
internal sealed class CatalogDatabase(SqliteConnection connection, object gate, Func<bool> isDisposed)
{
    private readonly CatalogCancellation cancellation = new(connection, gate);
    internal IDisposable Cancellation(CancellationToken token) => cancellation.Enter(token);
    internal T Run<T>(CancellationToken token, Func<T> action)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var scope = Cancellation(token);
            try { return action(); }
            catch (SqliteException error) when (error.SqliteErrorCode == 9 && token.IsCancellationRequested)
            { throw new OperationCanceledException("Catalog operation cancelled.", error, token); }
        }
    }
    internal SqliteConnection Connection => connection;
    internal object Gate => gate;
    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(isDisposed(), this);
    internal (long Changes, long DataVersion) ChangeStamp()
    {
        using var changes = Command("SELECT total_changes()", null);
        using var version = Command("PRAGMA data_version", null);
        return (Convert.ToInt64(changes.ExecuteScalar()), Convert.ToInt64(version.ExecuteScalar()));
    }
    internal SqliteCommand Command(string sql, SqliteTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
}
