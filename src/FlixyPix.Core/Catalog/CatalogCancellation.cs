using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>Owns SQLite's single progress callback while the catalog gate is held.</summary>
internal sealed class CatalogCancellation(SqliteConnection connection, object gate)
{
    private readonly object sync = gate;
    private Scope? current;
    private readonly SQLitePCL.delegate_progress progress = state => ((Scope)state).IsCancelled ? 1 : 0;

    internal IDisposable Enter(CancellationToken token)
    {
        if (!Monitor.IsEntered(sync)) throw new InvalidOperationException("The catalog gate must be held.");
        token.ThrowIfCancellationRequested();
        var scope = new Scope(this, current, token);
        current = scope;
        Install();
        return scope;
    }

    private void Install() => SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, current is null ? 0 : 1000,
        current is null ? null! : progress, current!);

    private sealed class Scope(CatalogCancellation owner, Scope? parent, CancellationToken token) : IDisposable
    {
        internal bool IsCancelled => token.IsCancellationRequested || parent?.IsCancelled == true;
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            if (!Monitor.IsEntered(owner.sync) || !ReferenceEquals(owner.current, this))
                throw new InvalidOperationException("Cancellation scopes must leave in reverse order under the catalog gate.");
            owner.current = parent;
            owner.Install();
            disposed = true;
        }
    }
}
