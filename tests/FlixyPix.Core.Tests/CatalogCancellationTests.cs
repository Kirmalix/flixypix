using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class CatalogCancellationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-cancellation-" + Guid.NewGuid().ToString("N"));
    public CatalogCancellationTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedWriteRollsBackAndBoundaryReportsCancellation(bool encrypted)
    {
        using var key = new CatalogKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        using var connection = new SqliteConnection(CatalogEncryption.ConnectionString(Path.Combine(root, "test.db"), encrypted ? key : null).ToString());
        connection.Open();
        var database = new CatalogDatabase(connection, new object(), () => false);
        using var stop = new CancellationTokenSource();
        var calls = 0;
        connection.CreateFunction("test_cancel", () => { if (++calls == 10) stop.Cancel(); return 1; });
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE results(value INTEGER)";
            create.ExecuteNonQuery();
        }
        var error = Assert.Throws<OperationCanceledException>(() => database.Run(stop.Token, () =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = database.Command("WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1000000) INSERT INTO results SELECT test_cancel() FROM n", transaction);
            command.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }));
        Assert.Equal(stop.Token, error.CancellationToken);
        Assert.InRange(calls, 10, 1000);
        database.Run(CancellationToken.None, () =>
        {
            using var count = database.Command("SELECT count(*) FROM results", null);
            Assert.Equal(0L, count.ExecuteScalar());
            using var insert = database.Command("INSERT INTO results VALUES(42)", null);
            Assert.Equal(1, insert.ExecuteNonQuery());
            return true;
        });
    }

    [Fact]
    public void CancelsRunningSqlAndRestoresOuterScopeAndConnection()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var gate = new object();
        var cancellation = new CatalogCancellation(connection, gate);
        using var stop = new CancellationTokenSource();
        var calls = 0;
        // Cancellation originates inside a running statement, without scheduling or timing assumptions.
        connection.CreateFunction("test_cancel", () => { if (++calls == 10) stop.Cancel(); return 1; });
        lock (gate)
        {
            using (cancellation.Enter(stop.Token))
            {
                using (cancellation.Enter(CancellationToken.None))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1000000) SELECT sum(test_cancel()) FROM n";
                    var error = Assert.Throws<SqliteException>(() => command.ExecuteScalar());
                    Assert.Equal(9, error.SqliteErrorCode);
                    Assert.InRange(calls, 10, 1000);
                }
                using var again = connection.CreateCommand();
                again.CommandText = "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<10000) SELECT sum(x) FROM n";
                Assert.Equal(9, Assert.Throws<SqliteException>(() => again.ExecuteScalar()).SqliteErrorCode);
            }
            using var healthy = connection.CreateCommand();
            healthy.CommandText = "SELECT 42";
            Assert.Equal(42L, healthy.ExecuteScalar());
        }
    }

    [Fact]
    public void RequiresGateAndRejectsPrecancelledToken()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var gate = new object();
        var cancellation = new CatalogCancellation(connection, gate);
        Assert.Throws<InvalidOperationException>(() => cancellation.Enter(default));
        lock (gate) Assert.Throws<OperationCanceledException>(() => cancellation.Enter(new CancellationToken(true)));
    }

    [Fact]
    public void NavigationOverloadsRespectCancellationAndKeepCatalogUsable()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "navigation.db"));
        var item = catalog.Upsert(new() { Path = Path.Combine(root, "image.jpg"), Kind = MediaKind.Image });
        var playlist = new Playlist { MediaIds = [item.Id] };
        var token = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => catalog.GetPosition(new(), item.Id, token));
        Assert.Throws<OperationCanceledException>(() => catalog.QueryPlaylistPage(playlist, new(), true, null, token));
        Assert.Equal(0, catalog.GetPosition(new(), item.Id));
        Assert.Single(catalog.QueryPlaylistPage(playlist, new()).Page.Items);
    }
}
