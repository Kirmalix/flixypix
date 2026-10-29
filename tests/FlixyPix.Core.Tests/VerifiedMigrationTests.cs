using System.Security.Cryptography;
using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class VerifiedMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-audit-test-" + Guid.NewGuid().ToString("N"));
    public VerifiedMigrationTests() => Directory.CreateDirectory(root);
    private string Exchange()
    {
        object[] entries = [
            new { type = "header", format = "flixypix-legacy", version = 2, sourceSha256 = new string('a', 64) },
            new { type = "schema", table = "movies", columns = new[] { new { name = "ID" }, new { name = "fullpath" }, new { name = "filename" }, new { name = "rating" } } },
            new { type = "row", table = "movies", values = new { ID = 1, fullpath = @"U:\offline", filename = "never-read.mp4", rating = 7 } },
            new { type = "footer", tables = 1, rows = 1, tableRows = new Dictionary<string, int> { ["movies"] = 1 }, blobCount = 0, blobBytes = 0 }
        ];
        var source = Path.Combine(root, "catalog.jsonl"); File.WriteAllLines(source, entries.Select(value => JsonSerializer.Serialize(value))); return source;
    }
    [Fact] public async Task OfflineCatalogAuditsRepeatPortableAndBackupWithoutMediaFiles()
    {
        var source = Exchange(); var before = SHA256.HashData(File.ReadAllBytes(source)); var target = Path.Combine(root, "verified");
        var report = await VerifiedMigration.RunAsync(source, target, "synthetic-source");
        Assert.True(report.Passed); Assert.True(report.RepeatIsNoOp); Assert.True(report.PortableRoundTripVerified); Assert.True(report.BackupRestoreVerified);
        Assert.Equal(1, report.MediaCount); Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.True(File.Exists(Path.Combine(target, "audit-result.json")));
        using var catalog = new CatalogRepository(Path.Combine(target, "Library", "catalog.db"));
        var item = Assert.Single(catalog.QueryPage(new()).Items); Assert.Equal(@"U:\offline\never-read.mp4", item.Path); Assert.Equal(7, item.Rating);
    }
    [Fact] public async Task ExistingDestinationAndCancellationNeverPublishPartialLibrary()
    {
        var source = Exchange(); var target = Path.Combine(root, "existing"); Directory.CreateDirectory(target); var sentinel = Path.Combine(target, "keep.txt"); File.WriteAllText(sentinel, "keep");
        await Assert.ThrowsAsync<IOException>(() => VerifiedMigration.RunAsync(source, target, "source")); Assert.Equal("keep", File.ReadAllText(sentinel));
        var cancelled = Path.Combine(root, "cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VerifiedMigration.RunAsync(source, cancelled, "source", new CancellationToken(true)));
        Assert.False(Directory.Exists(cancelled));
    }
    public void Dispose() => Directory.Delete(root, true);
}
