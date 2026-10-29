using System.Text;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class CatalogCheckTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-check-" + Guid.NewGuid());
    private string Library => Path.Combine(root, "library");
    private string CatalogPath => Path.Combine(Library, "catalog.db");
    public CatalogCheckTests() { Directory.CreateDirectory(Library); Directory.CreateDirectory(Path.Combine(root, "media")); }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

    private string MediaFile(string name)
    {
        var path = Path.Combine(root, "media", name); File.WriteAllText(path, name); return path;
    }

    private string OldFile(string folder, string name)
    {
        var path = Path.Combine(Library, folder, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1)); return path;
    }

    private void Tamper(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={CatalogPath};Pooling=False");
        connection.Open();
        connection.CreateCollation("EFP_ORDINAL", (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=OFF; " + sql; command.ExecuteNonQuery();
    }

    [Fact]
    public void HealthyCatalogHasNoProblemsAndTheCheckIsRecorded()
    {
        using var catalog = new CatalogRepository(CatalogPath);
        var thumbnail = OldFile("thumbnails", "used.jpg");
        catalog.Upsert(new MediaItem { Path = MediaFile("a.mp4"), Kind = MediaKind.Video, Tags = ["Urlaub"], ThumbnailPath = thumbnail });
        catalog.Upsert(new MediaItem { Path = MediaFile("b.jpg"), Kind = MediaKind.Image, TagCategories = new() { ["Ort"] = ["Strand"] } });
        catalog.PrepareSearchIndex();
        var report = catalog.CheckCatalog(Library);
        Assert.Equal(2, report.Media);
        Assert.Equal(0, report.Issues);
        Assert.All(report.Findings, finding => Assert.Equal(0, finding.Count));
        Assert.Equal(0, catalog.GetMaintenanceLog().LastCheckIssues);
    }

    [Fact]
    public void FindsAndRepairsBrokenIndexesOrphansAndMissingFiles()
    {
        Guid video, gone;
        using (var catalog = new CatalogRepository(CatalogPath))
        {
            video = catalog.Upsert(new MediaItem { Path = MediaFile("a.mp4"), Kind = MediaKind.Video, Tags = ["Urlaub", "Strand"] }).Id;
            gone = catalog.Upsert(new MediaItem { Path = MediaFile("gone.mp4"), Kind = MediaKind.Video }).Id;
            catalog.UpsertPlaylist(new Playlist { Name = "Sommer", MediaIds = [video, gone], Entries = [new(video), new(gone)] });
            catalog.PrepareSearchIndex();
        }
        File.Delete(Path.Combine(root, "media", "gone.mp4"));
        Tamper($"""
            DELETE FROM media_tags WHERE tag='Strand';
            DELETE FROM media_search;
            INSERT INTO bookmarks(id,media_id,document) VALUES('{Guid.NewGuid()}','{Guid.NewGuid()}','{"{}"}');
            UPDATE media SET path_key='WRONG' WHERE id='{video}';
            """);
        var unused = OldFile("hover-cache", Guid.NewGuid().ToString("N") + "-3-0.jpg");
        var kept = OldFile("hover-cache", video.ToString("N") + "-3-0.jpg");
        var leftover = OldFile("thumbnails", "x.jpg.tmp");
        var fresh = Path.Combine(Library, "thumbnails", "fresh.jpg"); File.WriteAllBytes(fresh, [1]);

        using (var catalog = new CatalogRepository(CatalogPath))
        {
            var report = catalog.CheckCatalog(Library);
            Assert.Empty(report[CatalogFindingKind.Integrity].Examples);
            Assert.Equal(1, report[CatalogFindingKind.OrphanRows].Count);
            Assert.Equal(1, report[CatalogFindingKind.QueryTable].Count);
            Assert.Equal(2, report[CatalogFindingKind.SearchIndex].Count);
            Assert.Equal(1, report[CatalogFindingKind.PathKeys].Count);
            Assert.Equal([Path.Combine(root, "media", "gone.mp4")], report[CatalogFindingKind.MissingFiles].Examples);
            Assert.Equal(2, report[CatalogFindingKind.OrphanFiles].Count); // the fresh file may still be written
            Assert.Equal(1, report[CatalogFindingKind.MissingThumbnails].Count);
            Assert.Equal(report.Issues, catalog.GetMaintenanceLog().LastCheckIssues);

            var all = report.Findings.Where(f => f.Repairable).Select(f => f.Kind).ToHashSet();
            var result = catalog.RepairCatalog(report, all, Path.Combine(Library, "backups"));
            Assert.True(File.Exists(result.BackupPath));
            Assert.Equal(1, result.Repaired[CatalogFindingKind.MissingFiles]);
            Assert.Null(catalog.GetMedia(gone));
            Assert.Equal([video], catalog.GetPlaylists().Single().MediaIds);
            Assert.False(File.Exists(unused)); Assert.False(File.Exists(leftover)); Assert.True(File.Exists(kept)); Assert.True(File.Exists(fresh));
            Assert.Single(catalog.Search(text: "a.mp4"));
            Assert.Single(catalog.Search(tag: "Strand"));

            var again = catalog.CheckCatalog(Library);
            Assert.Equal(1, again.Issues); // only the missing thumbnail, which "Medien vorbereiten" makes
            Assert.Equal(1, again[CatalogFindingKind.MissingThumbnails].Count);
        }
        // The copy before the repair still has the removed medium.
        using var copy = new CatalogRepository(Directory.GetFiles(Path.Combine(Library, "backups"), "*.db").Single());
        Assert.NotNull(copy.GetMedia(gone));
    }

    [Fact]
    public void MergesEntriesOfOneFileWrittenInTwoUnicodeForms()
    {
        var composed = MediaFile("Café.jpg");
        var decomposed = composed.Normalize(NormalizationForm.FormD);
        Assert.NotEqual(composed, decomposed);
        using var catalog = new CatalogRepository(CatalogPath);
        var first = catalog.Upsert(new MediaItem { Path = composed, Kind = MediaKind.Image, Tags = ["Paris"], AddedAt = DateTimeOffset.UtcNow.AddDays(-2) });
        var second = catalog.Upsert(new MediaItem { Path = decomposed, Kind = MediaKind.Image, Tags = ["Kaffee"], Rating = 4, IsFavorite = true });
        catalog.UpsertBookmark(new Bookmark { MediaId = second.Id, PositionSeconds = 2 });
        catalog.UpsertPlaylist(new Playlist { Name = "Reise", MediaIds = [second.Id], Entries = [new(second.Id)] });
        var report = catalog.CheckCatalog(Library);
        Assert.Equal(1, report[CatalogFindingKind.SamePath].Count);
        catalog.RepairCatalog(report, new HashSet<CatalogFindingKind> { CatalogFindingKind.SamePath }, Path.Combine(Library, "backups"));
        Assert.Null(catalog.GetMedia(second.Id));
        var merged = catalog.GetMedia(first.Id)!;
        Assert.Equal(["Paris", "Kaffee"], merged.Tags); Assert.Equal(4, merged.Rating); Assert.True(merged.IsFavorite);
        Assert.Equal(first.Id, catalog.GetBookmarks(first.Id).Single().MediaId);
        Assert.Equal([first.Id], catalog.GetPlaylists().Single().MediaIds);
        Assert.Equal(0, catalog.CheckCatalog(Library)[CatalogFindingKind.SamePath].Count);
    }

    [Fact]
    public void RebuildSearchIndexKeepsSearchesWorking()
    {
        using var catalog = new CatalogRepository(CatalogPath);
        catalog.Upsert(new MediaItem { Path = MediaFile("Sonnenuntergang.jpg"), Kind = MediaKind.Image, Tags = ["Abend"] });
        Assert.Equal(1, catalog.RebuildSearchIndex());
        Assert.Equal(0, catalog.PendingSearchIndexRows());
        Assert.Single(catalog.Search(text: "untergang"));
        Assert.Single(catalog.Search(tag: "Abend"));
    }
}
