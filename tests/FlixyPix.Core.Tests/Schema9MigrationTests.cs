using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class Schema9MigrationTests
{
    [Fact]
    public void Schema8CatalogRetainsCommittedWalAndReceivesConsistentBackupBeforeUpgrade()
    {
        var root = Path.Combine(Path.GetTempPath(), "flixypix-schema9-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "catalog.db"); Guid id;
            using (var catalog = new CatalogRepository(path)) id = catalog.Upsert(new() { Path = @"U:\offline\preserved.mp4", Kind = MediaKind.Video, Rating = 9 }).Id;
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); source.Open();
            source.CreateCollation("EFP_ORDINAL", (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
            using (var prepare = source.CreateCommand())
            {
                prepare.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; DROP TABLE historical_content_folders; DROP TABLE historical_parser_definitions; DROP TABLE legacy_combine_lists; PRAGMA user_version=8;";
                prepare.ExecuteNonQuery();
            }
            using (var update = source.CreateCommand()) { update.CommandText = "UPDATE media SET document=json_set(document,'$.description','Committed WAL description')"; Assert.Equal(1, update.ExecuteNonQuery()); }
            using var upgraded = new CatalogRepository(path);
            Assert.Equal("Committed WAL description", upgraded.GetMedia(id)!.Description); Assert.Equal(9, upgraded.GetMedia(id)!.Rating);
            Assert.Contains("before-v16-v8", upgraded.MigrationBackupPath!);
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = upgraded.MigrationBackupPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); backup.Open();
            using var version = backup.CreateCommand(); version.CommandText = "PRAGMA user_version"; Assert.Equal(8L, version.ExecuteScalar());
            using var data = backup.CreateCommand(); data.CommandText = "SELECT document FROM media";
            using var document = JsonDocument.Parse((string)data.ExecuteScalar()!); Assert.Equal("Committed WAL description", document.RootElement.GetProperty("description").GetString());
            Assert.Empty(upgraded.GetHistoricalContentFolders()); Assert.Empty(upgraded.GetHistoricalParserDefinitions()); Assert.Empty(upgraded.GetLegacyCombineLists()); Assert.True(upgraded.CheckIntegrity().IsValid);
        }
        finally { Directory.Delete(root, true); }
    }
}
