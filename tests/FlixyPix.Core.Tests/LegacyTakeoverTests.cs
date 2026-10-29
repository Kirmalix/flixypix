using System.Security.Cryptography;
using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Takeover of a synthetic installation of the original: discovery, cache pictures, tag pictures and settings groups.</summary>
public sealed class LegacyTakeoverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-takeover-" + Guid.NewGuid().ToString("N"));
    private string Program => Path.Combine(root, "Programme", LegacyInstallations.FolderName);
    private string AppData => Path.Combine(root, "AppData");
    private string Cache => Path.Combine(Program, "cache", "mmvdb.sdf");
    private string Thumbnails => Path.Combine(root, "library", "thumbnails");
    private LegacyInstallationSearch Search => new() { ProgramFolders = [Program], LocalAppData = AppData };

    public LegacyTakeoverTests() => CreateInstallation();
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }

    private static byte[] Jpeg(byte marker) => [0xFF, 0xD8, 0xFF, 0xE0, marker, 1, 2, 3];
    private static void Write(string path, byte[] data) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, data); }
    private static void Settings(string path, params (string Name, string Value)[] settings) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path,
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><userSettings><MyMediaViewer.My.MySettings>" +
        string.Concat(settings.Select(s => $"<setting name=\"{s.Name}\" serializeAs=\"String\"><value>{s.Value}</value></setting>")) +
        "<setting name=\"databases\" serializeAs=\"Xml\"><value><ArrayOfString><string>default|mmvdb-alt.sdf</string></ArrayOfString></value></setting>" +
        "</MyMediaViewer.My.MySettings></userSettings></configuration>"); }

    private void CreateInstallation()
    {
        Write(Path.Combine(Program, LegacyInstallations.ProgramFile), [0x4D, 0x5A]);
        Settings(Path.Combine(Program, LegacyInstallations.ProgramFile + ".config"), ("defaultUserDatabase", "mmvdb.sdf"), ("CacheFolder", "DEFAULT"));
        Write(Path.Combine(Program, "mmvdb.sdf"), new byte[2048]);
        Write(Path.Combine(Program, "mmvdb-alt.sdf"), new byte[1024]);
        Settings(Path.Combine(AppData, "EazyMojo", LegacyInstallations.FolderName + ".exe_Url_test", "4.8.4.0", "user.config"), ("defaultUserDatabase", "mmvdb.sdf"), ("Language", "German"));
        Write(Path.Combine(Cache, "vcache", "1.jpg"), Jpeg(1));
        Write(Path.Combine(Cache, "vcache", "2.jpg"), Jpeg(2));
        Write(Path.Combine(Cache, "vcache", "99.jpg"), Jpeg(99));    // deleted medium: orphan
        Write(Path.Combine(Cache, "vcache", "notes.txt"), [1]);
        Write(Path.Combine(Cache, "bcache", "1_10.jpg"), Jpeg(10));
        Write(Path.Combine(Cache, "bcache", "2_11.jpg"), Jpeg(11)); // bookmark 11 belongs to video 1: not guessed
        Write(Path.Combine(Cache, "ccache", "5.jpg"), Jpeg(5));
        Write(Path.Combine(Cache, "ccache", "thumb_5.jpg"), Jpeg(6)); // resized copy, not a separate picture
        Write(Path.Combine(Program, "tag_images", "Genre", "Action.png"), [137, 80, 78, 71, 13, 10, 26, 10, 1]);
    }

    private static void SqlCeFiles(string folder) { foreach (var file in LegacyInstallations.SqlCeRuntimeFiles) Write(Path.Combine(folder, file), [0x4D, 0x5A]); }

    [Fact]
    public void TheSqlCeRuntimeComesFromTheChosenFolderOrAFoundInstallation()
    {
        Assert.False(LegacyInstallations.HasSqlCeRuntime(Program));
        Assert.Null(LegacyInstallations.SqlCeRuntimeFolder(Program, Search));

        SqlCeFiles(Program);
        Assert.True(LegacyInstallations.HasSqlCeRuntime(Program));
        var elsewhere = Path.Combine(root, "runtime");
        Directory.CreateDirectory(elsewhere);
        Assert.Equal(Program, LegacyInstallations.SqlCeRuntimeFolder(elsewhere, Search));

        SqlCeFiles(elsewhere);
        Assert.Equal(elsewhere, LegacyInstallations.SqlCeRuntimeFolder(elsewhere, Search));
        File.Delete(Path.Combine(Program, "sqlceme40.dll"));
        Assert.False(LegacyInstallations.HasSqlCeRuntime(Program));
        Assert.Null(LegacyInstallations.SqlCeRuntimeFolder(null, Search));
    }

    private string Exchange()
    {
        var tables = new Dictionary<string, object[]>
        {
            ["movies"] = [new { ID = 1, fullpath = @"U:\Filme\", filename = "eins.mp4", title = "Eins" }, new { ID = 2, fullpath = @"U:\Filme\", filename = "zwei.mp4", title = "Zwei" },
                new { ID = 3, fullpath = @"U:\Filme\", filename = "drei.mp4", title = "Drei" }],
            ["videobookmarks"] = [new { ID = 10, videoid = 1, bookmarktime = 30, description = "A" }, new { ID = 11, videoid = 1, bookmarktime = 60, description = "B" },
                new { ID = 12, videoid = 3, bookmarktime = 5, description = "C" }],
            ["videoclips"] = [new { ID = 5, videoid = 2, starttime = 10, endtime = 20, description = "Clip" }],
            ["configsettings"] = [new { ID = 101, parameter = "fullscreen", value1 = "True" }, new { ID = 102, parameter = "nhotkeysmallffwd", value1 = "15" },
                new { ID = 103, parameter = "videofiletypes", value1 = "mp4;mkv" }, new { ID = 104, parameter = "searchhiddenfolders", value1 = "True" },
                new { ID = 105, parameter = "customvideoplayer", value1 = @"C:\player.exe" }],
        };
        var records = new List<object> { new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('a', 64) } };
        foreach (var (table, rows) in tables)
        {
            records.Add(new { type = "schema", table, columns = rows[0].GetType().GetProperties().Select(p => new { name = p.Name }) });
            records.AddRange(rows.Select(row => (object)new { type = "row", table, values = row }));
        }
        records.Add(new { type = "footer", rows = tables.Values.Sum(r => r.Length), tables = tables.Count, tableRows = tables.ToDictionary(t => t.Key, t => t.Value.Length) });
        var path = Path.Combine(root, "legacy.jsonl"); File.WriteAllLines(path, records.Select(r => JsonSerializer.Serialize(r))); return path;
    }

    private (CatalogRepository Catalog, string Batch) Migrate(bool adoptSettings = false)
    {
        var catalog = new CatalogRepository(Path.Combine(root, "library", "catalog.db"));
        var result = catalog.ImportLegacyJsonl(Exchange(), new LegacyImportOptions { SourceId = "takeover-test", AdoptAppSettings = adoptSettings });
        Assert.Empty(result.Errors);
        catalog.UpsertVocabularyTag(new("Action", "Genre"));
        return (catalog, result.Migration!.Batch);
    }

    private static MediaItem Media(CatalogRepository catalog, string legacyId) => catalog.QueryPage(new MediaQuery { Limit = 10 }).Items.Single(m => m.LegacyId == legacyId);
    private string SourceHashes() => string.Join(",", Directory.EnumerateFiles(Program, "*", SearchOption.AllDirectories).Order().Select(f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));
    private static LegacyTakeoverChoice Everything(params LegacySettingGroup[] settings) => new(Enum.GetValues<LegacyDataGroup>(), settings);

    [Fact]
    public void FindsInstallationWithCatalogCacheAndUserSettings()
    {
        var other = Path.Combine(root, "Alt", LegacyInstallations.FolderName);
        Write(Path.Combine(other, LegacyInstallations.ProgramFile), [0x4D, 0x5A]);
        File.WriteAllLines(Path.Combine(Program, "uninstall.log"), [Path.Combine(other, "graphics"), Path.Combine(other, LegacyInstallations.ProgramFile)]);
        var found = LegacyInstallations.Find(Search);
        Assert.Equal(2, found.Count);
        var installation = found[0];
        Assert.Equal(Path.Combine(Program, "mmvdb.sdf"), installation.CatalogPath);
        Assert.True(installation.HasCatalog);
        Assert.Equal(Cache, installation.CacheFolder);
        Assert.EndsWith("user.config", installation.UserConfigPath);
        // The original's interface language (user.config "Language") is offered to FlixyPix.
        Assert.Equal("de", installation.Language);
        Assert.Equal((3, 2, 1, 1), (installation.Folder(LegacyDataGroup.VideoThumbnails)!.Files, installation.Folder(LegacyDataGroup.BookmarkImages)!.Files,
            installation.Folder(LegacyDataGroup.ClipPreviews)!.Files, installation.Folder(LegacyDataGroup.TagImages)!.Files));
        Assert.False(found[1].HasCatalog);
    }

    [Fact]
    public void CacheFolderSettingMovesTheCacheRootOnlyWhenItExists()
    {
        var elsewhere = Path.Combine(root, "Cache"); Directory.CreateDirectory(elsewhere);
        var config = Directory.EnumerateFiles(AppData, "user.config", SearchOption.AllDirectories).Single();
        Settings(config, ("defaultUserDatabase", "mmvdb.sdf"), ("CacheFolder", elsewhere + "\\"));
        Assert.Equal(Path.Combine(elsewhere, "cache", "mmvdb.sdf"), LegacyInstallations.Inspect(Program, Search)!.CacheFolder);
        Settings(config, ("CacheFolder", Path.Combine(root, "missing")));
        Assert.Equal(Cache, LegacyInstallations.Inspect(Program, Search)!.CacheFolder);
        Assert.Null(LegacyInstallations.Inspect(root, Search));
    }

    [Fact]
    public void PlanMatchesFilesWithMigratedMediaBookmarksAndClips()
    {
        var (catalog, batch) = Migrate(); using var _ = catalog;
        var plan = catalog.PlanLegacyTakeover(LegacyInstallations.Inspect(Program, Search)!, batch);
        Assert.Equal(new LegacyTakeoverGroup(LegacyDataGroup.VideoThumbnails, 3, 24, 2, 0, 0, 1, 1), plan.Group(LegacyDataGroup.VideoThumbnails));
        Assert.Equal(new LegacyTakeoverGroup(LegacyDataGroup.BookmarkImages, 2, 16, 1, 0, 0, 1, 2), plan.Group(LegacyDataGroup.BookmarkImages));
        Assert.Equal(new LegacyTakeoverGroup(LegacyDataGroup.ClipPreviews, 1, 8, 1, 0, 0, 0, 0), plan.Group(LegacyDataGroup.ClipPreviews));
        Assert.Equal(1, plan.Group(LegacyDataGroup.TagImages)!.Adopt);
        Assert.Equal([LegacySettingGroup.Player, LegacySettingGroup.FileTypes, LegacySettingGroup.General], plan.Settings.Groups);
        Assert.Equal(2, plan.Settings.Differences(LegacySettingGroup.Player));
        Assert.Equal(0, plan.Settings.Conflicts(LegacySettingGroup.Player));
    }

    [Fact]
    public void TakeoverCopiesChosenGroupsOnceAndKeepsTheInstallationUntouched()
    {
        var (catalog, batch) = Migrate(); using var _ = catalog;
        var before = SourceHashes();
        var installation = LegacyInstallations.Inspect(Program, Search)!;
        var report = catalog.ApplyLegacyTakeover(catalog.PlanLegacyTakeover(installation, batch), Everything(LegacySettingGroup.Player, LegacySettingGroup.General), Thumbnails);
        Assert.Equal(before, SourceHashes());
        Assert.Equal([2, 1, 1, 1], report.Data.Select(r => r.Adopted));
        Assert.Equal(1, report.Data.Single(r => r.Group == LegacyDataGroup.VideoThumbnails).Orphans);

        var first = Media(catalog, "movies:1");
        Assert.StartsWith(Thumbnails, first.ThumbnailPath);
        Assert.Equal(Jpeg(1), File.ReadAllBytes(first.ThumbnailPath!));
        Assert.Equal(CatalogRepository.LegacyOrigin, first.Extra[CatalogRepository.ThumbnailOriginKey]);
        Assert.Null(Media(catalog, "movies:3").ThumbnailPath);
        var bookmark = catalog.GetBookmarks(first.Id).Single(b => b.Title == "A");
        Assert.Equal(Jpeg(10), catalog.GetMediaAssets(first.Id).Single(a => a.Id == bookmark.PreviewAssetId).Data);
        Assert.Null(catalog.GetBookmarks(first.Id).Single(b => b.Title == "B").PreviewAssetId);
        var second = Media(catalog, "movies:2");
        Assert.Equal(Jpeg(5), catalog.GetMediaAssets(second.Id).Single(a => a.Id == catalog.GetScenes(second.Id).Single().PreviewAssetId).Data);
        Assert.Single(catalog.GetTagAssetSummaries());

        var preferences = catalog.GetPreferences();
        Assert.True(preferences.Playback.AlwaysFullscreen); Assert.Equal(15, preferences.Playback.SmallJumpForwardSeconds);
        Assert.True(preferences.General.IncludeHiddenFolders);
        Assert.Equal(new PreviewPreferences().VideoExtensions, preferences.Previews.VideoExtensions); // file types were not chosen
        var rows = catalog.GetMigrationRows(batch, 0, 100).Where(r => r.Table == "configsettings").ToDictionary(r => r.Key!, r => r.Status);
        Assert.Equal(("partially-projected", "raw-retained", "raw-retained"), (rows["101"], rows["103"], rows["105"]));

        // A second run finds everything in place and copies nothing.
        var files = Directory.GetFiles(Thumbnails).Length;
        var again = catalog.PlanLegacyTakeover(installation, batch);
        Assert.Equal((0, 2), (again.Group(LegacyDataGroup.VideoThumbnails)!.Adopt, again.Group(LegacyDataGroup.VideoThumbnails)!.AlreadyAdopted));
        Assert.Equal(1, again.Group(LegacyDataGroup.BookmarkImages)!.AlreadyAdopted);
        var repeat = catalog.ApplyLegacyTakeover(again, Everything(), Thumbnails);
        Assert.Equal(0, repeat.Data.Where(r => r.Group != LegacyDataGroup.TagImages).Sum(r => r.Adopted));
        Assert.Equal(files, Directory.GetFiles(Thumbnails).Length);
        Assert.Single(catalog.GetTagAssetSummaries());
    }

    [Fact]
    public void FailureAfterCopyingLeavesNoHalfTakeover()
    {
        var (catalog, batch) = Migrate(); using var _ = catalog;
        var plan = catalog.PlanLegacyTakeover(LegacyInstallations.Inspect(Program, Search)!, batch);
        // The settings change after the preview: the takeover must stop after copying and roll everything back.
        catalog.SavePlaybackPreferences(catalog.GetPreferences().Playback with { LargeJumpForwardSeconds = 45 });
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyLegacyTakeover(plan, Everything(LegacySettingGroup.Player), Thumbnails));
        Assert.Empty(Directory.GetFiles(Thumbnails));
        Assert.Null(Media(catalog, "movies:1").ThumbnailPath);
        Assert.Empty(catalog.GetMediaAssets(Media(catalog, "movies:1").Id));
        Assert.Empty(catalog.GetTagAssetSummaries());

        Assert.Throws<OperationCanceledException>(() => catalog.ApplyLegacyTakeover(plan, Everything(), Thumbnails, token: new CancellationToken(true)));
        Assert.Empty(Directory.GetFiles(Thumbnails));
    }

    [Fact]
    public void ChangedSourceFileStopsTheTakeover()
    {
        var (catalog, batch) = Migrate(); using var _ = catalog;
        var plan = catalog.PlanLegacyTakeover(LegacyInstallations.Inspect(Program, Search)!, batch);
        File.WriteAllBytes(Path.Combine(Cache, "vcache", "2.jpg"), Jpeg(7).Concat(Jpeg(7)).ToArray());
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyLegacyTakeover(plan, Everything(), Thumbnails));
        Assert.Empty(Directory.GetFiles(Thumbnails));
        Assert.Null(Media(catalog, "movies:1").ThumbnailPath);
    }

    [Fact]
    public void OwnThumbnailsAreKeptUnlessReplacementIsChosen()
    {
        var (catalog, batch) = Migrate(); using var _ = catalog;
        Directory.CreateDirectory(Thumbnails);
        var own = Path.Combine(Thumbnails, "own.jpg"); File.WriteAllBytes(own, Jpeg(42));
        catalog.Upsert(Media(catalog, "movies:1") with { ThumbnailPath = own });
        var installation = LegacyInstallations.Inspect(Program, Search)!;
        var plan = catalog.PlanLegacyTakeover(installation, batch);
        Assert.Equal((1, 1), (plan.Group(LegacyDataGroup.VideoThumbnails)!.Adopt, plan.Group(LegacyDataGroup.VideoThumbnails)!.Kept));
        catalog.ApplyLegacyTakeover(plan, new([LegacyDataGroup.VideoThumbnails], []), Thumbnails);
        Assert.Equal(own, Media(catalog, "movies:1").ThumbnailPath);

        catalog.ApplyLegacyTakeover(catalog.PlanLegacyTakeover(installation, batch, replaceThumbnails: true), new([LegacyDataGroup.VideoThumbnails], []), Thumbnails);
        Assert.Equal(Jpeg(1), File.ReadAllBytes(Media(catalog, "movies:1").ThumbnailPath!));
        Assert.False(File.Exists(own)); // replaced generated thumbnail inside the library folder is removed
    }

    [Fact]
    public void SettingsCanBeTakenOverLaterAndMarkConflicts()
    {
        var (catalog, batch) = Migrate(); using var _ = catalog;
        catalog.SavePlaybackPreferences(catalog.GetPreferences().Playback with { SmallJumpForwardSeconds = 20 });
        var plan = catalog.PlanLegacySettings(batch);
        var jump = plan.Changes.Single(c => c.Label == "Kleiner Sprung vor");
        Assert.Equal(("15", "15 s", "20 s", true), (jump.Original, jump.Proposed, jump.Current, jump.Conflict));
        Assert.False(plan.Changes.Single(c => c.Label == "Immer im Vollbild abspielen").Conflict);
        Assert.Equal(3, catalog.ApplyLegacySettings(plan, [LegacySettingGroup.Player, LegacySettingGroup.FileTypes]));
        Assert.Equal([".mp4", ".mkv"], catalog.GetPreferences().Previews.VideoExtensions);
        Assert.False(catalog.GetPreferences().General.IncludeHiddenFolders);
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyLegacySettings(plan, [LegacySettingGroup.General]));

        // A migration that already adopted everything shows no differences.
        using var adopted = new CatalogRepository(Path.Combine(root, "adopted.db"));
        var result = adopted.ImportLegacyJsonl(Exchange(), new LegacyImportOptions { SourceId = "adopted" });
        Assert.DoesNotContain(adopted.PlanLegacySettings(result.Migration!.Batch).Changes, c => c.Differs);
    }
}
