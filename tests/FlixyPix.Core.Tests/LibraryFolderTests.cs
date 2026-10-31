using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Library folders: new files are offered, moved and renamed files relinked, vanished files only reported,
/// and every option changes what the check finds.</summary>
public sealed class LibraryFolderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-library-folders-" + Guid.NewGuid().ToString("N"));
    private string Media => Path.Combine(root, "media");
    public LibraryFolderTests() => Directory.CreateDirectory(root);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
    private CatalogRepository Open() => new(Path.Combine(root, "catalog.db"));
    private string Fixture(string relative, int bytes = 32)
    {
        var path = Path.Combine(Media, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = new byte[bytes]; Random.Shared.NextBytes(content); File.WriteAllBytes(path, content); return path;
    }
    private static LibraryFolder[] Watch(CatalogRepository catalog, params LibraryFolder[] folders) => catalog.SaveLibraryFolders(folders).ToArray();
    private static string Names(IEnumerable<FolderChange> changes, FolderChangeKind kind) =>
        string.Join(",", changes.Where(c => c.Kind == kind).Select(c => Path.GetFileName(c.Path)).Order(StringComparer.Ordinal));
    private static FolderSyncResult ApplyAll(CatalogRepository catalog, FolderSyncPlan plan) => catalog.ApplyFolderSync(plan, plan.Actionable);

    [Fact]
    public void NewFilesAreOfferedAndImportedWithTheFolderCategoryOnlyAfterApply()
    {
        Fixture("a.jpg"); Fixture("trip/b.mp4"); Fixture("notes.txt");
        using var catalog = Open();
        Watch(catalog, new LibraryFolder { Path = Media + Path.DirectorySeparatorChar, Category = " Urlaub " });
        var plan = catalog.PlanFolderSync();
        Assert.Equal("a.jpg,b.mp4", Names(plan.Changes, FolderChangeKind.New));
        Assert.Empty(catalog.Search());
        var result = ApplyAll(catalog, plan);
        Assert.Equal(2, result.Imported); Assert.Empty(result.Skipped);
        Assert.All(catalog.Search(), item => Assert.Equal("Urlaub", item.Category));
        Assert.Contains(catalog.Search(), item => item.Title == "b" && item.Kind == MediaKind.Video && item.FileSize == 32);
        Assert.Empty(catalog.PlanFolderSync().Changes);
    }

    [Fact]
    public void KindSubfolderAndTypeListOptionsNarrowTheCheck()
    {
        Fixture("a.jpg"); Fixture("b.mp4"); Fixture("deep/c.jpg"); Fixture("d.png");
        using var catalog = Open();
        catalog.SavePreviewPreferences(catalog.GetPreferences().Previews with { ImageExtensions = [".jpg"] });
        Watch(catalog, new LibraryFolder { Path = Media, Kind = MediaKind.Image, IncludeSubfolders = false });
        Assert.Equal("a.jpg", Names(catalog.PlanFolderSync().Changes, FolderChangeKind.New));
        Watch(catalog, new LibraryFolder { Path = Media, Kind = MediaKind.Image });
        Assert.Equal("a.jpg,c.jpg", Names(catalog.PlanFolderSync().Changes, FolderChangeKind.New));
        Watch(catalog, new LibraryFolder { Path = Media });
        Assert.Equal("a.jpg,b.mp4,c.jpg", Names(catalog.PlanFolderSync().Changes, FolderChangeKind.New));
    }

    [Fact]
    public void FilesOutsideTheFilterOrInHiddenFoldersAreNotReportedMissing()
    {
        var movie = Fixture("b.mp4"); var hidden = Fixture("secret/c.jpg");
        File.SetAttributes(Path.GetDirectoryName(hidden)!, FileAttributes.Directory | FileAttributes.Hidden);
        using var catalog = Open();
        catalog.Upsert(new MediaItem { Path = movie, Kind = MediaKind.Video }); catalog.Upsert(new MediaItem { Path = hidden, Kind = MediaKind.Image });
        Watch(catalog, new LibraryFolder { Path = Media, Kind = MediaKind.Image });
        Assert.Empty(catalog.PlanFolderSync().Changes);
    }

    [Fact]
    public void MovedFileKeepsItsCatalogEntryTagsAndTitle()
    {
        var before = Fixture("inbox/clip.mp4");
        using var catalog = Open();
        var item = catalog.Upsert(new MediaItem { Path = before, Kind = MediaKind.Video, Title = "Mein Titel", Tags = ["Strand"], Rating = 8, FileSize = 32 });
        Watch(catalog, new LibraryFolder { Path = Media });
        Directory.CreateDirectory(Path.Combine(Media, "sorted"));
        var after = Path.Combine(Media, "sorted", "clip.mp4"); File.Move(before, after);
        var plan = catalog.PlanFolderSync();
        var change = Assert.Single(plan.Changes);
        Assert.Equal(FolderChangeKind.Moved, change.Kind); Assert.Equal(item.Id, change.MediaId); Assert.Equal(before, change.PreviousPath);
        Assert.Equal(1, ApplyAll(catalog, plan).Relinked);
        var moved = Assert.Single(catalog.Search());
        Assert.Equal((item.Id, after, "Mein Titel", 8), (moved.Id, moved.Path, moved.Title, moved.Rating));
        Assert.Equal(["Strand"], moved.Tags); Assert.Equal(before, moved.Extra["folders.previousPath"]);
    }

    [Fact]
    public void LargeUniqueRenameIsRelinkedAndUnchangedTitleFollowsButSmallRenameIsNot()
    {
        var large = Fixture("old-name.mp4", (int)CatalogRepository.RenameMinimumBytes); var small = Fixture("small.jpg");
        using var catalog = Open();
        var video = catalog.Upsert(new MediaItem { Path = large, Kind = MediaKind.Video, Title = "old-name", FileSize = CatalogRepository.RenameMinimumBytes });
        catalog.Upsert(new MediaItem { Path = small, Kind = MediaKind.Image, FileSize = 32 });
        Watch(catalog, new LibraryFolder { Path = Media });
        var renamed = Path.Combine(Media, "new-name.mp4"); File.Move(large, renamed); File.Move(small, Path.Combine(Media, "tiny.jpg"));
        var plan = catalog.PlanFolderSync();
        Assert.Equal("new-name.mp4", Names(plan.Changes, FolderChangeKind.Renamed));
        Assert.Equal("tiny.jpg", Names(plan.Changes, FolderChangeKind.New));
        Assert.Equal("small.jpg", Names(plan.Changes, FolderChangeKind.Missing));
        var result = catalog.ApplyFolderSync(plan, plan.Changes.Where(c => c.Kind == FolderChangeKind.Renamed).ToList());
        Assert.Equal((0, 1), (result.Imported, result.Relinked));
        var relinked = catalog.GetMedia(video.Id)!;
        Assert.Equal((renamed, "new-name"), (relinked.Path, relinked.Title));
    }

    [Fact]
    public void AmbiguousMovesStayNewAndMissing()
    {
        var first = Fixture("a/same.jpg"); var second = Fixture("b/same.jpg");
        using var catalog = Open();
        catalog.Upsert(new MediaItem { Path = first, Kind = MediaKind.Image, FileSize = 32 }); catalog.Upsert(new MediaItem { Path = second, Kind = MediaKind.Image, FileSize = 32 });
        Watch(catalog, new LibraryFolder { Path = Media });
        Directory.CreateDirectory(Path.Combine(Media, "c")); Directory.CreateDirectory(Path.Combine(Media, "d"));
        File.Move(first, Path.Combine(Media, "c", "same.jpg")); File.Move(second, Path.Combine(Media, "d", "same.jpg"));
        var plan = catalog.PlanFolderSync();
        Assert.Equal(0, plan.Count(FolderChangeKind.Moved)); Assert.Equal(2, plan.Count(FolderChangeKind.New)); Assert.Equal(2, plan.Count(FolderChangeKind.Missing));
    }

    [Fact]
    public void MissingFilesAreOnlyReportedAndUnreachableFoldersReportNothing()
    {
        var gone = Fixture("gone.jpg");
        using var catalog = Open();
        var item = catalog.Upsert(new MediaItem { Path = gone, Kind = MediaKind.Image });
        var offline = Path.Combine(root, "unplugged-drive");
        catalog.Upsert(new MediaItem { Path = Path.Combine(offline, "x.jpg"), Kind = MediaKind.Image });
        Watch(catalog, new LibraryFolder { Path = Media }, new LibraryFolder { Path = offline });
        File.Delete(gone);
        var plan = catalog.PlanFolderSync();
        var missing = Assert.Single(plan.Changes);
        Assert.Equal((FolderChangeKind.Missing, item.Id), (missing.Kind, missing.MediaId));
        Assert.Equal(offline, Assert.Single(plan.Unreachable).Path);
        Assert.Empty(plan.Actionable);
        Assert.Throws<ArgumentException>(() => catalog.ApplyFolderSync(plan, [missing with { Path = "other" }]));
        catalog.ApplyFolderSync(plan, plan.Changes);
        Assert.Equal(2, catalog.Search().Count);
    }

    [Fact]
    public void DeclinedAndRemovedFilesAreNotOfferedAgain()
    {
        Fixture("keep.jpg"); Fixture("skip.jpg"); var removed = Fixture("removed.jpg");
        using var catalog = Open();
        var item = catalog.Upsert(new MediaItem { Path = removed, Kind = MediaKind.Image });
        Watch(catalog, new LibraryFolder { Path = Media });
        var plan = catalog.PlanFolderSync();
        var result = catalog.ApplyFolderSync(plan, plan.Changes.Where(c => c.Path.EndsWith("keep.jpg", StringComparison.Ordinal)).ToList(), ignoreDeclined: true);
        Assert.Equal((1, 1), (result.Imported, result.Ignored));
        catalog.Delete(item.Id);
        Assert.Equal(1, catalog.IgnoreInLibraryFolders([removed, Path.Combine(root, "elsewhere.jpg")]));
        Assert.Equal(0, catalog.IgnoreInLibraryFolders([removed]));
        Assert.Empty(catalog.PlanFolderSync().Changes);
        Assert.Equal(2, Assert.Single(catalog.GetLibraryFolders()).IgnoredPaths.Count);
    }

    [Fact]
    public void ApplyChecksEachChangeAgainAndSkipsWhatNoLongerHolds()
    {
        var vanished = Fixture("vanished.jpg"); Fixture("raced.jpg"); var moved = Fixture("from/clip.mp4");
        using var catalog = Open();
        var item = catalog.Upsert(new MediaItem { Path = moved, Kind = MediaKind.Video, FileSize = 32 });
        Watch(catalog, new LibraryFolder { Path = Media });
        Directory.CreateDirectory(Path.Combine(Media, "to")); File.Move(moved, Path.Combine(Media, "to", "clip.mp4"));
        var plan = catalog.PlanFolderSync();
        File.Delete(vanished);
        catalog.Upsert(new MediaItem { Path = Path.Combine(Media, "raced.jpg"), Kind = MediaKind.Image });
        catalog.Upsert(catalog.GetMedia(item.Id)! with { Path = Path.Combine(root, "relinked-by-hand.mp4") });
        var result = ApplyAll(catalog, plan);
        Assert.Equal((0, 0, 3), (result.Imported, result.Relinked, result.Skipped.Count));
        Assert.Equal(Path.Combine(root, "relinked-by-hand.mp4"), catalog.GetMedia(item.Id)!.Path);
    }

    [Fact]
    public void NestedFoldersUseTheMostSpecificCategoryAndPathsMustBeUnique()
    {
        Fixture("top.jpg"); Fixture("holiday/beach.jpg");
        using var catalog = Open();
        Watch(catalog, new LibraryFolder { Path = Media, Category = "Alle" }, new LibraryFolder { Path = Path.Combine(Media, "holiday"), Category = "Urlaub" });
        ApplyAll(catalog, catalog.PlanFolderSync());
        Assert.Equal("Urlaub", catalog.Search().Single(i => i.Title == "beach").Category);
        Assert.Equal("Alle", catalog.Search().Single(i => i.Title == "top").Category);
        Assert.Throws<ArgumentException>(() => catalog.SaveLibraryFolders([new LibraryFolder { Path = Media }, new LibraryFolder { Path = Media + Path.DirectorySeparatorChar }]));
        Assert.Throws<ArgumentException>(() => catalog.SaveLibraryFolders([new LibraryFolder { Path = "relative" }]));
    }

    [Fact]
    public void WorkFoldersOfTheLibraryAreNeverOfferedButMediaBesideTheCatalogAre()
    {
        // The other tests keep the catalog above their media folder; this one puts the library inside it.
        Fixture("photo.jpg"); Fixture("Library/thumbnails/x.jpg"); Fixture("Library/hover-cache/1/y.jpg"); Fixture("Library/cover.jpg");
        using var catalog = new CatalogRepository(Path.Combine(Media, "Library", "catalog.db"));
        catalog.SaveLibraryFolders([new LibraryFolder { Path = Media }]);
        Assert.Equal("cover.jpg,photo.jpg", Names(catalog.PlanFolderSync().Changes, FolderChangeKind.New));
        catalog.SaveLibraryFolders([new LibraryFolder { Path = Path.Combine(Media, "Library", "thumbnails") }]);
        Assert.Empty(catalog.PlanFolderSync().Changes);
    }

    [Fact]
    public void LibraryFoldersTravelWithThePortableCatalog()
    {
        using var catalog = Open();
        Watch(catalog, new LibraryFolder { Path = Media, Kind = MediaKind.Video, Watch = false, Category = "Filme", IgnoredPaths = [Path.Combine(Media, "x.mp4")] });
        var export = Path.Combine(root, "export.json"); catalog.Export(export);
        using var other = new CatalogRepository(Path.Combine(root, "other.db"));
        other.ImportJson(export);
        var folder = Assert.Single(other.GetLibraryFolders());
        Assert.Equal((Media, MediaKind.Video, false, "Filme", 1), (folder.Path, folder.Kind!.Value, folder.Watch, folder.Category!, folder.IgnoredPaths.Count));
    }
    [Fact]
    public void ModificationUsesSizeAndTimeAndPreservesPersonalData()
    {
        var path = Fixture("change.jpg"); using var catalog = Open();
        Watch(catalog, new LibraryFolder { Path = Media }); ApplyAll(catalog, catalog.PlanFolderSync());
        var item = catalog.GetMediaByPath(path)!; catalog.Upsert(item with { Tags = ["Keep"], Rating = 8 });
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        var plan = catalog.PlanFolderSync(); Assert.Equal(FolderChangeKind.Modified, Assert.Single(plan.Changes).Kind);
        Assert.Equal(1, ApplyAll(catalog, plan).Updated);
        Assert.Equal(["Keep"], catalog.GetMedia(item.Id)!.Tags); Assert.Equal(8, catalog.GetMedia(item.Id)!.Rating);
        Assert.Empty(catalog.PlanFolderSync().Changes);
    }
    [Fact]
    public void ChangedAfterPlanningIsSkippedAndReportSurvivesReopen()
    {
        var path = Fixture("race.jpg"); using (var catalog = Open()) {
        Watch(catalog, new LibraryFolder { Path = Media }); var plan = catalog.PlanFolderSync();
        File.AppendAllText(path, "changed"); var result = ApplyAll(catalog, plan);
        Assert.Single(result.Skipped); Assert.Empty(catalog.Search()); catalog.SaveFolderSyncReport(plan, result); }
        using var reopened = Open(); Assert.Single(reopened.GetFolderSyncReport()!.Result.Skipped);
    }
    [Fact]
    public void MissingVersionRemovalKeepsTheMedium()
    {
        var main = Fixture("main.mp4"); var version = Fixture("other.mp4"); using var catalog = Open();
        var item = catalog.Upsert(new MediaItem { Path = main, Kind = MediaKind.Video, Files = [new MediaFile { Path = version, FileSize = 32 }] });
        Watch(catalog, new LibraryFolder { Path = Media }); File.Delete(version);
        var plan = catalog.PlanFolderSync(); Assert.Equal(version, Assert.Single(plan.Changes).Path);
        Assert.Equal(1, catalog.RemoveMissingFolderFiles(plan, plan.Changes)); Assert.Empty(catalog.GetMedia(item.Id)!.Files);
    }
    [Fact]
    public void CancellationRollsBackTheEntireApply()
    {
        Fixture("cancel.jpg"); using var catalog = Open(); Watch(catalog, new LibraryFolder { Path = Media });
        var plan = catalog.PlanFolderSync(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => catalog.ApplyFolderSync(plan, plan.Actionable, cancellationToken: cancel.Token));
        Assert.Empty(catalog.Search());
    }

    [Fact]
    public void VersionModificationAndRenameKeepTheOwner()
    {
        var main = Fixture("primary.mp4"); var other = Fixture("version.mp4", (int)CatalogRepository.RenameMinimumBytes);
        using var catalog = Open(); Watch(catalog, new LibraryFolder { Path = Media });
        var item = catalog.Upsert(new MediaItem { Path = main, Kind = MediaKind.Video, Files = [new MediaFile { Path = other, FileSize = CatalogRepository.RenameMinimumBytes }] });
        catalog.InitializeFolderReferences(catalog.GetLibraryFolders());
        File.SetLastWriteTimeUtc(other, File.GetLastWriteTimeUtc(other).AddHours(1));
        var changed = catalog.PlanFolderSync(); Assert.Equal(FolderChangeKind.Modified, Assert.Single(changed.Changes).Kind);
        Assert.Equal(1, ApplyAll(catalog, changed).Updated);
        Assert.Empty(catalog.PlanFolderSync().Changes);
        var renamed = Path.Combine(Media, "renamed.mp4"); File.Move(other,renamed);
        Assert.Equal(1, ApplyAll(catalog,catalog.PlanFolderSync()).Relinked);
        Assert.Equal(renamed, Assert.Single(catalog.GetMedia(item.Id)!.Files).Path);
    }
    [Fact]
    public void FirstReferenceDoesNotHideAnExistingSizeChange()
    {
        var path = Fixture("baseline.jpg"); using var catalog = Open(); Watch(catalog,new LibraryFolder { Path = Media });
        catalog.Upsert(new MediaItem { Path = path, Kind = MediaKind.Image, FileSize = 1 });
        catalog.InitializeFolderReferences(catalog.GetLibraryFolders());
        Assert.Equal(FolderChangeKind.Modified,Assert.Single(catalog.PlanFolderSync().Changes).Kind);
    }
    [Fact]
    public void BusyFileIsDeferredAndOfflineRemovalDoesNothing()
    {
        var path = Fixture("busy.jpg"); using var catalog = Open(); Watch(catalog,new LibraryFolder { Path = Media });
        var plan = catalog.PlanFolderSync();
        using(var writer = new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.Read)) Assert.Single(ApplyAll(catalog,plan).Skipped);
        ApplyAll(catalog,catalog.PlanFolderSync()); File.Delete(path); var missing = catalog.PlanFolderSync();
        Directory.Delete(Media); Assert.Equal(0,catalog.RemoveMissingFolderFiles(missing,missing.Changes)); Assert.Single(catalog.Search());
    }

    private sealed class SyncProgress(Action<FolderSyncProgress> report) : IProgress<FolderSyncProgress>
    { public void Report(FolderSyncProgress step) => report(step); }
    [Fact]
    public void LargeScanReportsProgressAndCancelsWithoutCatalogWrites()
    {
        for(int n = 0; n < 1024; n++) Fixture($"bulk/{n}.jpg");
        using var catalog = Open(); Watch(catalog,new LibraryFolder { Path = Media });
        using var cancel = new CancellationTokenSource(); int observed = 0;
        var progress = new SyncProgress(step => { observed = step.Done; if(step.Done >= 128) cancel.Cancel(); });
        Assert.Throws<OperationCanceledException>(() => catalog.PlanFolderSync(cancellationToken:cancel.Token, progress:progress));
        Assert.True(observed >= 128); Assert.Empty(catalog.Search());
    }

}
