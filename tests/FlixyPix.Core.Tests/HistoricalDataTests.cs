using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class HistoricalDataTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-historical-" + Guid.NewGuid());
    public HistoricalDataTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private CatalogRepository Open() => new(Path.Combine(root, "catalog.db"));
    private static HistoricalLegacyRow Row(string table, object values) => new(table, JsonSerializer.SerializeToElement(values));

    [Fact]
    public void SearchXmlRetainsUnknownAndComplexFieldsWithoutActivatingSearch()
    {
        const string xml = "<SearchSettings_Image><ChkPictureWidth>true</ChkPictureWidth><NPictureWidth>640</NPictureWidth><FutureOption>yes</FutureOption><PropertiesValueList><item>x</item></PropertiesValueList></SearchSettings_Image>";
        var draft = LegacyHistoricalReader.ReadSearchSettings(xml, "source", "42", 502);
        Assert.False(draft.IsExecutable); Assert.Equal(HistoricalProjectionStatus.Partial, draft.Status);
        Assert.Equal(MediaKind.Image, draft.Kind); Assert.Equal("640", draft.Fields["NPictureWidth"]); Assert.Equal("yes", draft.Fields["FutureOption"]);
        Assert.DoesNotContain("PropertiesValueList", draft.Fields.Keys); Assert.Contains(draft.Warnings, warning => warning.Contains("Complex"));
        Assert.Equal(xml, draft.RawXml); Assert.Equal(64, draft.RawXmlSha256.Length);
        Assert.Equal(draft.Id, LegacyHistoricalReader.ReadSearchSettings(xml, "source", "42", 502).Id);
        Assert.NotEqual(draft.Id, LegacyHistoricalReader.ReadSearchSettings(xml, "other-source", "42", 502).Id);
    }

    [Fact]
    public void SearchXmlRejectsDtdAndSizeAndKeepsUnknownRootsOpaque()
    {
        Assert.Throws<InvalidDataException>(() => LegacyHistoricalReader.ReadSearchSettings("<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///C:/unread'>]><SearchSettings_Image>&a;</SearchSettings_Image>", "s", "1"));
        Assert.Throws<InvalidDataException>(() => LegacyHistoricalReader.ReadSearchSettings(new string('x', LegacyHistoricalReader.MaximumXmlCharacters + 1), "s", "1"));
        var opaque = LegacyHistoricalReader.ReadSearchSettings("<Unknown><Field>value</Field></Unknown>", "s", "1");
        Assert.Equal(HistoricalProjectionStatus.Opaque, opaque.Status); Assert.Empty(opaque.Fields); Assert.False(opaque.IsExecutable); Assert.Contains("Unknown", opaque.RawXml);
    }

    [Fact]
    public void SearchDraftsWithInvalidXmlAreRetainedAndRemainInactiveAfterRepeatImport()
    {
        using var catalog = Open();
        HistoricalLegacyRow[] rows = [Row("search_settings", new { ID = 1, searchtype = 500, xml = "<broken" }), Row("search_settings", new { ID = 2, searchtype = 500, xml = "<SearchSettings_Image><ChkPictureWidth>true</ChkPictureWidth></SearchSettings_Image>" })];
        var result = catalog.ImportHistoricalRecords(rows, "source"); Assert.Equal(2, result.SearchRows);
        catalog.ImportHistoricalRecords(rows, "source");
        var drafts = catalog.GetLegacySearchDrafts(); Assert.Equal(2, drafts.Count); Assert.All(drafts, draft => Assert.False(draft.IsExecutable)); Assert.Empty(catalog.GetSavedSearches());
        Assert.Equal(HistoricalProjectionStatus.Opaque, drafts.Single(draft => draft.LegacyId == "1").Status);
        Assert.Equal("<broken", drafts.Single(draft => draft.LegacyId == "1").RawXml);
        Assert.Contains(drafts.Single(draft => draft.LegacyId == "2").Warnings, warning => warning.Contains("disagree"));
    }

    [Fact]
    public void TagPresetsImportSeparatelyAndApplyOnlyAfterAnExplicitAdditiveOperation()
    {
        using var catalog = Open(); var video = catalog.Upsert(new() { Path = Path.Combine(root, "movie.mp4"), Kind = MediaKind.Video, Tags = ["Existing"] });
        HistoricalLegacyRow[] rows = [Row("keyword", new { ID = 1, type = 4, keyword = "Group", keytags = "\"People\" \"Place\"" }),
            Row("keyword", new { ID = 2, type = 4, keyword = "Group", keytags = "\"people\" \"Extra\"" }), Row("keyword", new { ID = 3, type = 5, keyword = "Group", keytags = "\"Pictures\"" })];
        var result = catalog.ImportHistoricalRecords(rows, "source"); Assert.Equal(3, result.PresetRows);
        catalog.ImportHistoricalRecords(rows, "source"); var presets = catalog.GetTagPresets(); Assert.Equal(2, presets.Count);
        var preset = presets.Single(p => p.Kind == MediaKind.Video); Assert.Equal(new[] { "People", "Place", "Extra" }, preset.Tags); Assert.Equal(2, preset.LegacyRowKeys.Count);
        Assert.Equal(new[] { "Existing" }, catalog.GetMedia(video.Id)!.Tags);
        var preview = catalog.PreviewTagPreset(preset.Id, [video.Id]); Assert.Equal(new[] { "Existing", "People", "Place", "Extra" }, Assert.Single(preview.Changes).AfterTags);
        Assert.Equal(new[] { "Existing" }, catalog.GetMedia(video.Id)!.Tags);
        Assert.Equal(1, catalog.ApplyTagPreset(preset.Id, [video.Id])); Assert.Equal(0, catalog.ApplyTagPreset(preset.Id, [video.Id]));
        Assert.Equal(new[] { "Existing", "People", "Place", "Extra" }, catalog.GetMedia(video.Id)!.Tags);
    }

    [Fact]
    public void PresetApplicationRejectsMixedKindsAndCancelsWithoutPartialChanges()
    {
        using var catalog = Open(); var video = catalog.Upsert(new() { Path = Path.Combine(root, "movie.mp4"), Kind = MediaKind.Video });
        var image = catalog.Upsert(new() { Path = Path.Combine(root, "image.jpg"), Kind = MediaKind.Image });
        var preset = catalog.UpsertTagPreset(new() { Name = "Video", Kind = MediaKind.Video, Tags = ["Added"], TagCategories = new() { ["Places"] = ["City"] } });
        Assert.Throws<ArgumentException>(() => catalog.ApplyTagPreset(preset.Id, [video.Id, image.Id])); Assert.Empty(catalog.GetMedia(video.Id)!.Tags);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => catalog.ApplyTagPreset(preset.Id, [video.Id], canceled.Token)); Assert.Empty(catalog.GetMedia(video.Id)!.Tags);
        Assert.Equal(1, catalog.ApplyTagPreset(preset.Id, [video.Id])); Assert.Equal(new[] { "City" }, catalog.GetMedia(video.Id)!.TagCategories["Places"]);
    }

    [Fact]
    public void MalformedPresetRowDoesNotInventTagsAndUnknownKeywordKindsDoNotBecomePresets()
    {
        using var catalog = Open();
        catalog.ImportHistoricalRecords([Row("keyword", new { ID = 1, type = 4, keyword = "Ambiguous", keytags = "not quoted" }), Row("keyword", new { ID = 2, type = 2, keyword = "Media", keytags = "\"Ignored\"" })], "source");
        var preset = Assert.Single(catalog.GetTagPresets()); Assert.Empty(preset.Tags); Assert.Equal(HistoricalProjectionStatus.Partial, preset.Status); Assert.NotEmpty(preset.Warnings);
    }

    [Fact]
    public void OriginalQueueCommandsAreOnlyDataAndOrderingAndOfflinePathsSurvive()
    {
        using var catalog = Open();
        var command = "cmd.exe /c echo must-never-run > C:\\unexpected.txt";
        var values = new { ID = 13, sourcefile = "U:\\offline\\source.mp4", outputfile = "U:\\offline\\target.mp4", videotoclipdestinationfile = "D:\\clips", txtcliptovideoname = "clip", totalseconds = 12.5, cmd = command };
        var row = Row("cliptovideo_queue", values); catalog.ImportHistoricalRecords([row], "source"); catalog.ImportHistoricalRecords([row], "source");
        var draft = Assert.Single(catalog.GetLegacyJobDrafts()); Assert.False(draft.IsExecutable); Assert.Equal(13, draft.OriginalOrder); Assert.Equal(12.5, draft.DurationSeconds);
        Assert.Equal(command, draft.OriginalCommand); Assert.Equal("U:\\offline\\source.mp4", draft.InputPath); Assert.Contains("cmd.exe", draft.RawJson);
        Assert.Empty(catalog.Search()); Assert.Equal(HistoricalProjectionStatus.Partial, draft.Status);
        var hostileJson = JsonSerializer.Serialize(draft).Replace("\"IsExecutable\":false", "\"IsExecutable\":true");
        Assert.False(JsonSerializer.Deserialize<LegacyJobDraft>(hostileJson)!.IsExecutable);
    }

    [Fact]
    public void InvalidQueueDurationIsPreservedWithoutInventingAValidDuration()
    {
        var draft = LegacyHistoricalReader.ReadJobDraft(JsonSerializer.SerializeToElement(new { ID = 3, outputfile = "old.mp4", totalseconds = "NaN", cmd = "unknown command" }), "source");
        Assert.Null(draft.DurationSeconds); Assert.Contains("NaN", draft.RawJson); Assert.Contains(draft.Warnings, warning => warning.Contains("Invalid")); Assert.False(draft.IsExecutable);
    }

    [Fact]
    public async Task HistoricalDocumentsSurvivePortableExportAndFullBackup()
    {
        using var catalog = Open();
        catalog.ImportHistoricalRecords([
            Row("keyword", new { ID = 1, type = 4, keyword = "Videos", keytags = "\"People\"" }),
            Row("search_settings", new { ID = 2, searchtype = 502, xml = "<SearchSettings_Image><FutureOption>yes</FutureOption></SearchSettings_Image>" }),
            Row("cliptovideo_queue", new { ID = 3, sourcefile = "U:\\offline.mp4", outputfile = "C:\\old-output.mp4", totalseconds = 4, cmd = "legacy command" })], "source");
        var json = Path.Combine(root, "portable.json"); catalog.Export(json);
        using (var copy = new CatalogRepository(Path.Combine(root, "portable-copy.db")))
        {
            Assert.Empty(copy.ImportJson(json).Errors);
            Assert.Equal("People", Assert.Single(Assert.Single(copy.GetTagPresets()).Tags));
            Assert.Equal("yes", Assert.Single(copy.GetLegacySearchDrafts()).Fields["FutureOption"]);
            Assert.Equal("legacy command", Assert.Single(copy.GetLegacyJobDrafts()).OriginalCommand);
            Assert.False(Assert.Single(copy.GetLegacySearchDrafts()).IsExecutable);
        }
        var backup = Path.Combine(root, "historical.flixypixbackup"); await catalog.CreateBackupAsync(backup, false);
        var restored = await CatalogRepository.RestoreBackupAsync(backup, Path.Combine(root, "restored"));
        using var restoredCatalog = new CatalogRepository(restored.DatabasePath);
        Assert.Equal(Assert.Single(catalog.GetTagPresets()).Id, Assert.Single(restoredCatalog.GetTagPresets()).Id);
        Assert.Equal(Assert.Single(catalog.GetLegacySearchDrafts()).RawXmlSha256, Assert.Single(restoredCatalog.GetLegacySearchDrafts()).RawXmlSha256);
        Assert.Equal("legacy command", Assert.Single(restoredCatalog.GetLegacyJobDrafts()).OriginalCommand);
    }

    [Fact]
    public void RegularLegacyImportExplainsHistoricalRowsInItsMigrationReport()
    {
        using var catalog = Open();
        object Schema(string table, params string[] fields) => new { type = "schema", table, columns = fields.Select(name => new { name }) };
        object RowRecord(string table, object values) => new { type = "row", table, values };
        object[] records = [new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('d', 64) },
            Schema("keyword", "ID", "type", "keyword", "keytags"), RowRecord("keyword", new { ID = 1, type = 4, keyword = "Group", keytags = "\"People\"" }),
            Schema("search_settings", "ID", "searchtype", "xml"), RowRecord("search_settings", new { ID = 2, searchtype = 502, xml = "<SearchSettings_Image/>" }),
            Schema("cliptovideo_queue", "ID", "outputfile", "cmd", "totalseconds"), RowRecord("cliptovideo_queue", new { ID = 3, outputfile = "C:\\old.mp4", cmd = "old command", totalseconds = 3 }),
            new { type = "footer", tables = 3, rows = 3, tableRows = new Dictionary<string, int> { ["keyword"] = 1, ["search_settings"] = 1, ["cliptovideo_queue"] = 1 } }];
        var path = Path.Combine(root, "historical.jsonl"); File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(record)));
        var result = catalog.ImportLegacyJsonl(path); Assert.Empty(result.Errors); Assert.NotNull(result.Migration);
        var rows = catalog.GetMigrationRows(result.Migration!.Batch); Assert.Equal(3, rows.Count);
        Assert.All(rows, row => { Assert.Equal("partially-projected", row.Status); Assert.Single(row.TargetIds); Assert.NotEqual("preserved-unverified-projection", row.Code); });
        Assert.Single(catalog.GetTagPresets()); Assert.Single(catalog.GetLegacySearchDrafts()); Assert.Single(catalog.GetLegacyJobDrafts());
    }

    [Fact]
    public void GlobalTagChangesAndCategoryMovesKeepModernPresetsConsistent()
    {
        using var catalog = Open();
        var preset = catalog.UpsertTagPreset(new() { Name = "People", Kind = MediaKind.Image, Tags = ["Alex", "Existing"], TagCategories = new() { ["People"] = ["Alex"], ["Places"] = ["Alex"] } });
        catalog.ChangeTag("Alex", "Jordan"); var renamed = Assert.Single(catalog.GetTagPresets());
        Assert.DoesNotContain("Alex", renamed.Tags); Assert.Contains("Jordan", renamed.Tags); Assert.Equal(new[] { "Jordan" }, renamed.TagCategories["People"]);
        catalog.MoveTagCategory("Jordan", "People", "Performers"); var moved = Assert.Single(catalog.GetTagPresets());
        Assert.False(moved.TagCategories.ContainsKey("People")); Assert.Equal(new[] { "Jordan" }, moved.TagCategories["Performers"]); Assert.Equal(new[] { "Jordan" }, moved.TagCategories["Places"]);
        catalog.ChangeTag("Jordan", null); var deleted = Assert.Single(catalog.GetTagPresets()); Assert.Equal(new[] { "Existing" }, deleted.Tags); Assert.All(deleted.TagCategories.Values, tags => Assert.Empty(tags));
    }
}
