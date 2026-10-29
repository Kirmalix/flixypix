using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class CatalogMaintenanceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-maintenance-" + Guid.NewGuid());
    public CatalogMaintenanceTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void StatisticsCountMediaByKindTagsAndCollections()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var video = catalog.Upsert(new MediaItem { Path = @"U:\a.mp4", Kind = MediaKind.Video, Tags = ["Urlaub", "Strand"] });
        catalog.Upsert(new MediaItem { Path = @"U:\b.jpg", Kind = MediaKind.Image, Tags = ["urlaub"] });
        catalog.Upsert(new MediaItem { Path = @"U:\c.jpg", Kind = MediaKind.Image });
        catalog.UpsertPlaylist(new Playlist { Name = "Sommer", MediaIds = [video.Id] });
        catalog.UpsertBookmark(new Bookmark { MediaId = video.Id, PositionSeconds = 3 });
        var statistics = catalog.GetStatistics();
        Assert.Equal((1, 2, 0, 3), (statistics.Videos, statistics.Images, statistics.Audio, statistics.Media));
        Assert.Equal(2, statistics.Tags); // "Urlaub" and "urlaub" are one tag.
        Assert.Equal((1, 1, 0), (statistics.Collections, statistics.Bookmarks, statistics.Scenes));
        Assert.True(statistics.DatabaseBytes > 0);
    }

    [Fact]
    public async Task BackupAndCheckAreRecordedAndKeptInTheCatalog()
    {
        var path = Path.Combine(root, "catalog.db");
        using (var catalog = new CatalogRepository(path))
        {
            Assert.Null(catalog.GetMaintenanceLog().LastBackupAt);
            catalog.Upsert(new MediaItem { Path = @"U:\a.mp4", Kind = MediaKind.Video });
            var package = Path.Combine(root, "backup.flixypixbackup");
            await catalog.CreateBackupAsync(package, includeExternalMedia: false);
            var log = catalog.GetMaintenanceLog();
            Assert.Equal(Path.GetFullPath(package), log.LastBackupPath); Assert.Equal(new FileInfo(package).Length, log.LastBackupBytes);
            Assert.True(log.LastBackupAt > DateTimeOffset.Now.AddMinutes(-1));
            catalog.RecordCheck(2);
        }
        using var reopened = new CatalogRepository(path);
        var kept = reopened.GetMaintenanceLog();
        Assert.Equal(2, kept.LastCheckIssues); Assert.NotNull(kept.LastCheckAt); Assert.NotNull(kept.LastBackupAt);
    }
}
