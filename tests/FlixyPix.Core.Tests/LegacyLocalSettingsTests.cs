using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class LegacyLocalSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-local-settings-" + Guid.NewGuid().ToString("N"));
    public LegacyLocalSettingsTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private CatalogRepository Open(string name = "catalog.db") => new(Path.Combine(root, name));
    private static object Category(int id = 1, string name = "Videos", int kind = 0) => new { ID = id, category = name, mtype = kind };
    private static object Folder(int id = 1, string path = @"U:\offline", int category = 1, int kind = 0, object? always = null, object? once = null) =>
        new { ID = id, path, categoryid = category, mtype = kind, refresh = always ?? false, refreshonce = once ?? true };
    private ImportResult Import(CatalogRepository catalog, object[] folders, object[]? categories = null, string sourceId = "source", bool replace = false, object[]? obsolete = null)
    {
        var tables = new Dictionary<string, object[]> { ["categories"] = categories ?? [Category()], ["paths"] = folders };
        if (obsolete is not null) tables["content_paths"] = obsolete;
        var records = new List<object> { new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('a', 64), sourceName = "synthetic.sdf" } };
        foreach (var (table, rows) in tables)
        {
            var columns = rows.SelectMany(row => JsonSerializer.SerializeToElement(row).EnumerateObject().Select(property => property.Name)).DefaultIfEmpty("ID").Distinct().Select(name => new { name }).ToArray();
            records.Add(new { type = "schema", table, columns });
            records.AddRange(rows.Select(row => new { type = "row", table, values = row }));
        }
        records.Add(new { type = "footer", tables = tables.Count, rows = tables.Values.Sum(rows => rows.Length), tableRows = tables.ToDictionary(pair => pair.Key, pair => pair.Value.Length) });
        var path = Path.Combine(root, Guid.NewGuid() + ".jsonl"); File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(record)));
        return catalog.ImportLegacyJsonl(path, new LegacyImportOptions { SourceId = sourceId,
            ConflictPolicy = replace ? LegacyConflictPolicy.ReplaceFromSource : LegacyConflictPolicy.PreserveExisting });
    }
    private static MediaItem Media(string path = @"U:\offline\movie.mp4", string? category = null) => new()
        { Path = path, Kind = MediaKind.Video, Category = category, Title = "Local title", Rating = 9, Tags = ["Existing"], PlaybackPositionSeconds = 37 };

    [Fact]
    public void MigrationPreservesActualFolderCategoryRefreshFlagsAndRawBytesWithoutMediaAccess()
    {
        using var catalog = Open(); var result = Import(catalog, [Folder(always: true)]);
        Assert.Empty(result.Errors); var folder = Assert.Single(catalog.GetHistoricalContentFolders());
        Assert.Equal(@"U:\offline", folder.Path); Assert.Equal(MediaKind.Video, folder.Kind); Assert.Equal("Videos", folder.Category);
        Assert.True(folder.OriginalAlwaysRefresh); Assert.True(folder.OriginalRefreshOnce); Assert.False(folder.IsExecutable);
        Assert.Equal(64, folder.RawSha256.Length); Assert.Contains("refreshonce", folder.RawJson);
        Assert.Empty(catalog.Search());
        var audit = catalog.GetMigrationRows(result.Migration!.Batch).Single(row => row.Table == "paths");
        Assert.Contains(folder.Id.ToString(), audit.TargetIds);
        Assert.Empty(Import(catalog, [Folder(always: true)]).Errors); Assert.Equal(folder.Id, Assert.Single(catalog.GetHistoricalContentFolders()).Id);
    }

    [Fact]
    public void OriginalContentFoldersAreSuggestedAsLibraryFoldersOnceWithoutTouchingTheirPaths()
    {
        using var catalog = Open();
        Assert.Empty(Import(catalog, [Folder(always: true), Folder(id: 2, path: @"U:\pictures", kind: 1, always: false), Folder(id: 3, path: "relative", always: true)],
            [Category(), Category(2, "Fotos", 1)]).Errors);
        var suggested = catalog.SuggestLibraryFoldersFromOriginal().OrderBy(f => f.Path).ToList();
        Assert.Equal([@"U:\offline", @"U:\pictures"], suggested.Select(f => f.Path));
        Assert.Equal((MediaKind.Video, "Videos", true), (suggested[0].Kind!.Value, suggested[0].Category!, suggested[0].Watch));
        Assert.False(suggested[1].Watch);
        catalog.SaveLibraryFolders([suggested[0]]);
        Assert.Equal(@"U:\pictures", Assert.Single(catalog.SuggestLibraryFoldersFromOriginal()).Path);
    }

    [Fact]
    public void MissingWrongKindCategoriesAndInvalidFlagsRemainInspectableWithoutInventedDefaults()
    {
        using var catalog = Open();
        Assert.Empty(Import(catalog, [Folder(category: 9, always: "unknown"), Folder(id: 2, category: 1, kind: 1)], [Category()]).Errors);
        var folders = catalog.GetHistoricalContentFolders();
        Assert.All(folders, folder => Assert.Null(folder.Category)); Assert.Null(folders.Single(folder => folder.LegacyId == "1").OriginalAlwaysRefresh);
        Assert.All(folders, folder => Assert.NotEmpty(folder.Warnings));
        var media = catalog.Upsert(Media()); var preview = catalog.PreviewHistoricalContentFolderCategories([media.Id]);
        Assert.False(preview.CanApply); Assert.Throws<InvalidOperationException>(() => catalog.ApplyHistoricalContentFolderCategories(preview));
        Assert.Null(catalog.GetMedia(media.Id)!.Category);
    }

    [Fact]
    public void PreviewAndApplyChangeOnlySelectedCatalogCategoryAndRetainOtherData()
    {
        using var catalog = Open(); Assert.Empty(Import(catalog, [Folder()]).Errors);
        var media = catalog.Upsert(Media()); var sibling = catalog.Upsert(Media(@"U:\offline2\movie.mp4"));
        var image = catalog.Upsert(Media(@"U:\offline\image.jpg") with { Kind = MediaKind.Image });
        var preview = catalog.PreviewHistoricalContentFolderCategories([media.Id, sibling.Id, image.Id]);
        Assert.True(preview.CanApply); Assert.Null(catalog.GetMedia(media.Id)!.Category);
        Assert.Equal(1, catalog.ApplyHistoricalContentFolderCategories(preview));
        var changed = catalog.GetMedia(media.Id)!;
        Assert.Equal("Videos", changed.Category); Assert.Equal(media.Title, changed.Title); Assert.Equal(media.Rating, changed.Rating);
        Assert.Equal(media.Tags, changed.Tags); Assert.Equal(media.PlaybackPositionSeconds, changed.PlaybackPositionSeconds);
        Assert.Null(catalog.GetMedia(sibling.Id)!.Category); Assert.Null(catalog.GetMedia(image.Id)!.Category);
        Assert.True(Assert.Single(catalog.GetHistoricalContentFolders()).OriginalRefreshOnce);
    }

    [Fact]
    public void NestedRootsWithDifferentCategoriesBlockUntilExplicitFolderSelection()
    {
        using var catalog = Open();
        Assert.Empty(Import(catalog, [Folder(), Folder(id: 2, path: @"U:\offline\special", category: 2)], [Category(), Category(2, "Special")]).Errors);
        var media = catalog.Upsert(Media(@"U:\offline\special\movie.mp4"));
        var ambiguous = catalog.PreviewHistoricalContentFolderCategories([media.Id]);
        Assert.False(ambiguous.CanApply); Assert.Contains(ambiguous.Blockers, text => text.Contains("conflicting"));
        Assert.Equal(2, Assert.Single(ambiguous.Changes).MatchingFolderIds.Count);
        var selected = catalog.GetHistoricalContentFolders().Single(folder => folder.LegacyId == "2");
        var explicitChoice = catalog.PreviewHistoricalContentFolderCategories([media.Id], folderId: selected.Id);
        Assert.True(explicitChoice.CanApply); Assert.Equal(1, catalog.ApplyHistoricalContentFolderCategories(explicitChoice));
        Assert.Equal("Special", catalog.GetMedia(media.Id)!.Category);
    }

    [Fact]
    public void ExistingCategoriesRequireExplicitReplacementPreview()
    {
        using var catalog = Open(); Import(catalog, [Folder()]); var media = catalog.Upsert(Media(category: "My category"));
        var preserved = catalog.PreviewHistoricalContentFolderCategories([media.Id]); Assert.False(preserved.CanApply);
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyHistoricalContentFolderCategories(preserved));
        var replacement = catalog.PreviewHistoricalContentFolderCategories([media.Id], replaceExisting: true);
        Assert.Equal("My category", Assert.Single(replacement.Changes).CurrentCategory);
        Assert.Equal(1, catalog.ApplyHistoricalContentFolderCategories(replacement)); Assert.Equal("Videos", catalog.GetMedia(media.Id)!.Category);
    }

    [Fact]
    public void ChangedMediaFolderSourceAndForgedChangesInvalidatePreview()
    {
        using var catalog = Open(); Import(catalog, [Folder()]); var media = catalog.Upsert(Media());
        var preview = catalog.PreviewHistoricalContentFolderCategories([media.Id]);
        var forged = preview with { Changes = [preview.Changes[0] with { ProposedCategory = "Unexpected" }] };
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyHistoricalContentFolderCategories(forged));
        catalog.Upsert(media with { Rating = 5 });
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyHistoricalContentFolderCategories(preview));
        var fresh = catalog.PreviewHistoricalContentFolderCategories([media.Id]);
        Assert.Empty(Import(catalog, [Folder()], [Category(name: "Changed")], replace: true).Errors);
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyHistoricalContentFolderCategories(fresh));
        Assert.Null(catalog.GetMedia(media.Id)!.Category); Assert.Equal(5, catalog.GetMedia(media.Id)!.Rating);
    }

    [Fact]
    public void SourceScopeSeparatesEqualLegacyFolderIdsAndCancellationIsAtomic()
    {
        using var catalog = Open(); Import(catalog, [Folder()], sourceId: "A"); Import(catalog, [Folder()], [Category(name: "Other")], sourceId: "B");
        var folders = catalog.GetHistoricalContentFolders(); Assert.Equal(2, folders.Count); Assert.NotEqual(folders[0].Id, folders[1].Id);
        var media = catalog.Upsert(Media()); Assert.False(catalog.PreviewHistoricalContentFolderCategories([media.Id]).CanApply);
        var scoped = catalog.PreviewHistoricalContentFolderCategories([media.Id], "A"); Assert.True(scoped.CanApply);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => catalog.ApplyHistoricalContentFolderCategories(scoped, canceled.Token)); Assert.Null(catalog.GetMedia(media.Id)!.Category);
        Assert.Throws<OperationCanceledException>(() => catalog.PreviewHistoricalContentFolderCategories([media.Id], cancellationToken: canceled.Token));
    }

    [Fact]
    public void NewSnapshotPreservesExistingFolderDefinitionUntilExplicitReplacement()
    {
        using var catalog = Open(); Import(catalog, [Folder()]);
        var original = Assert.Single(catalog.GetHistoricalContentFolders());
        Assert.Empty(Import(catalog, [Folder(path: @"U:\changed")]).Errors);
        Assert.Equal(original.Path, Assert.Single(catalog.GetHistoricalContentFolders()).Path);
        Assert.Empty(Import(catalog, [Folder(path: @"U:\another")], replace: true).Errors);
        Assert.Equal(@"U:\another", Assert.Single(catalog.GetHistoricalContentFolders()).Path);
    }

    [Fact]
    public void ObsoleteContentPathsArePreservedButNotMistakenForCurrentPathsTable()
    {
        using var catalog = Open(); Assert.Empty(Import(catalog, [], obsolete: [new { ID = 1, path = @"U:\old", type = "P", category = "Old" }]).Errors);
        Assert.Empty(catalog.GetHistoricalContentFolders());
        var path = Path.Combine(root, "preserved.json"); catalog.Export(path);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Contains(json.RootElement.GetProperty("legacyRecords").EnumerateArray(), record => record.GetString()!.Contains("content_paths"));
    }

    [Fact]
    public async Task PortableAndBackupRetainInactiveFoldersAndTheirOriginalRefreshFlags()
    {
        using var catalog = Open(); Import(catalog, [Folder(always: true)]);
        var original = Assert.Single(catalog.GetHistoricalContentFolders());
        var path = Path.Combine(root, "portable.json"); catalog.Export(path);
        using (var copy = Open("copy.db"))
        {
            Assert.Empty(copy.ImportJson(path).Errors); var folder = Assert.Single(copy.GetHistoricalContentFolders());
            Assert.Equal(original.Id, folder.Id); Assert.Equal(original.RawJson, folder.RawJson); Assert.True(folder.OriginalAlwaysRefresh); Assert.False(folder.IsExecutable);
        }
        var backup = Path.Combine(root, "folder.flixypixbackup"); await catalog.CreateBackupAsync(backup, false);
        var restored = await CatalogRepository.RestoreBackupAsync(backup, Path.Combine(root, "restored"));
        using var restoredCatalog = new CatalogRepository(restored.DatabasePath);
        Assert.Equal(original.RawSha256, Assert.Single(restoredCatalog.GetHistoricalContentFolders()).RawSha256);
    }
}
