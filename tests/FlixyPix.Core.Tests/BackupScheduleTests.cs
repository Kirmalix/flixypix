using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class BackupScheduleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-schedule-" + Guid.NewGuid());
    public BackupScheduleTests() => Directory.CreateDirectory(root);
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

    [Fact]
    public async Task AutomaticBackupsAreDueVerifiedAndRotated()
    {
        var library = Path.Combine(root, "library");
        using var catalog = new CatalogRepository(Path.Combine(library, "catalog.db"));
        catalog.Upsert(new MediaItem { Path = @"U:\a.mp4", Kind = MediaKind.Video });
        Assert.False(catalog.GetBackupSchedule().IsDue(catalog.GetMaintenanceLog(), DateTimeOffset.Now));
        catalog.SaveBackupSchedule(new BackupSchedule(IntervalDays: 7, Keep: 2));
        Assert.True(catalog.GetBackupSchedule().IsDue(catalog.GetMaintenanceLog(), DateTimeOffset.Now));

        var folder = Path.Combine(library, "backups"); Directory.CreateDirectory(folder);
        foreach (var day in new[] { 1, 2, 3 })
        {
            var old = BackupRotation.NewAutomaticPath(folder, new DateTime(2026, 1, day));
            File.WriteAllText(old, "old"); File.SetLastWriteTime(old, new DateTime(2026, 1, day));
        }
        File.WriteAllText(Path.Combine(folder, "eigene.flixypixbackup"), "mine");
        var result = await catalog.CreateAutomaticBackupAsync(library);
        Assert.False(catalog.GetBackupSchedule().IsDue(catalog.GetMaintenanceLog(), DateTimeOffset.Now));
        Assert.True(catalog.GetBackupSchedule().IsDue(catalog.GetMaintenanceLog(), DateTimeOffset.Now.AddDays(7)));
        var kept = BackupRotation.List(folder);
        Assert.Equal([result.PackagePath, BackupRotation.NewAutomaticPath(folder, new DateTime(2026, 1, 3))], kept.Select(b => b.Path));
        Assert.True(File.Exists(Path.Combine(folder, "eigene.flixypixbackup"))); // not named by FlixyPix

        var check = await CatalogRepository.VerifyBackupAsync(result.PackagePath);
        Assert.Equal((1, 1), (check.Media, check.Files));
        var broken = Path.Combine(root, "broken.flixypixbackup");
        var bytes = File.ReadAllBytes(result.PackagePath); bytes[bytes.Length / 3] ^= 0xFF; File.WriteAllBytes(broken, bytes);
        await Assert.ThrowsAnyAsync<Exception>(() => CatalogRepository.VerifyBackupAsync(broken));
    }
}
