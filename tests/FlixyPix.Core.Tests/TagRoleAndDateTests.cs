using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class TagRoleAndDateTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-roles-" + Guid.NewGuid().ToString("N"));
    public TagRoleAndDateTests() => Directory.CreateDirectory(directory);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }
    private string PathFor(string name) => Path.Combine(directory, name);
    private static string Header => JsonSerializer.Serialize(new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('b', 64), sourceName = "fixture.sdf" });
    private static string Schema(string table, params string[] columns) => JsonSerializer.Serialize(new { type = "schema", table,
        columns = columns.Select((name, ordinal) => new { name, dataType = "nvarchar", nullable = true, ordinal }) });
    private static string Row(string table, object values) => JsonSerializer.Serialize(new { type = "row", table, values });
    private static string Footer(Dictionary<string, int> counts) => JsonSerializer.Serialize(new { type = "footer", tables = counts.Count, rows = counts.Values.Sum(), tableRows = counts });
    private static Dictionary<string, string> Date(string value) => new() { ["$datetime"] = value };

    [Theory]
    [InlineData("Actors", TagRole.Performer)]
    [InlineData("darsteller", TagRole.Performer)]
    [InlineData("Models (Adult)", TagRole.Performer)]
    [InlineData("Studios", TagRole.Studio)]
    [InlineData("Label", TagRole.Studio)]
    [InlineData("Genre", TagRole.Tag)]
    [InlineData("Acting", TagRole.Tag)]
    public void CategoriesWithoutExplicitRoleFollowTheirName(string category, TagRole expected) => Assert.Equal(expected, TagRoles.Default(category));

    [Fact]
    public void ExplicitRolesAreStoredOnlyWhenTheyDifferFromTheDefault()
    {
        using var catalog = new CatalogRepository(PathFor("catalog.db"));
        catalog.Upsert(new() { Path = PathFor("a.mp4"), Kind = MediaKind.Video, TagCategories = new() { ["Stars"] = ["Jane"], ["Studios"] = ["Acme"] } });
        catalog.SetTagRole("Stars", TagRole.Performer);
        catalog.SetTagRole("studios", TagRole.Studio);
        Assert.Equal(TagRole.Performer, catalog.GetTagRole("STARS"));
        Assert.Equal([new TagCategoryRole("Stars", TagRole.Performer)], catalog.GetPreferences().TagCategoryRoles);
        var categories = catalog.GetTagCategories();
        Assert.Equal(["Stars", "Studios"], categories.Select(c => c.Category));
        Assert.Equal([(TagRole.Performer, true), (TagRole.Studio, false)], categories.Select(c => (c.Role, c.IsExplicit)));
        catalog.SetTagRole("Stars", TagRole.Tag);
        Assert.Empty(catalog.GetPreferences().TagCategoryRoles);
        Assert.Throws<ArgumentException>(() => catalog.SavePreferences(catalog.GetPreferences() with
        {
            TagCategoryRoles = [new("Stars", TagRole.Performer), new("stars", TagRole.Studio)]
        }));
    }

    [Fact]
    public void OriginalCastCategoriesBecomePerformersAndUnlistedDefaultsBecomeTags()
    {
        var path = PathFor("legacy.jsonl");
        File.WriteAllLines(path, [Header,
            Schema("movies", "ID", "fullpath", "filename"), Row("movies", new { ID = 1, fullpath = directory + Path.DirectorySeparatorChar, filename = "Film.mp4" }),
            Schema("keyword", "ID", "type", "remoteID", "keyword", "keytags"),
            Row("keyword", new { ID = 2, type = 2, remoteID = 1, keyword = "Film.mp4", keytags = "\"Jane\" \"Acme\"" }),
            Schema("searchcriteria", "ID", "movie", "category", "value1"),
            Row("searchcriteria", new { ID = 3, movie = "SEARCHTAGINFO", category = "Stars", value1 = "Jane" }),
            Row("searchcriteria", new { ID = 4, movie = "SEARCHTAGINFO", category = "Actors", value1 = "Not a person" }),
            Row("searchcriteria", new { ID = 5, movie = "SEARCHTAGINFO", category = "Studios", value1 = "Acme" }),
            Schema("configsettings", "ID", "parameter", "value1"), Row("configsettings", new { ID = 6, parameter = "tagcategorycast", value1 = "stars:darsteller" }),
            Footer(new() { ["movies"] = 1, ["keyword"] = 1, ["searchcriteria"] = 3, ["configsettings"] = 1 })]);
        using var catalog = new CatalogRepository(PathFor("catalog.db"));
        var result = catalog.ImportLegacyJsonl(path);
        Assert.Empty(result.Errors);
        Assert.Equal(TagRole.Performer, catalog.GetTagRole("Stars"));
        Assert.Equal(TagRole.Tag, catalog.GetTagRole("Actors"));
        Assert.Equal(TagRole.Studio, catalog.GetTagRole("Studios"));
        Assert.Equal(TagRole.Performer, catalog.GetTagRole("Darsteller"));
        Assert.Contains(catalog.GetMigrationRows(result.Migration!.Batch, 0, 100), r => r.Code == "tag-roles");

        // A library migrated before roles existed takes them over later from the retained rows.
        catalog.SavePreferences(catalog.GetPreferences() with { TagCategoryRoles = [] });
        Assert.Equal(TagRole.Performer, catalog.GetTagRole("Actors"));
        Assert.True(catalog.AdoptLegacyTagRoles(result.Migration!.Batch));
        Assert.Equal(TagRole.Tag, catalog.GetTagRole("Actors"));
        Assert.Equal(TagRole.Performer, catalog.GetTagRole("Stars"));
    }

    [Fact]
    public async Task FileDateAndImportDateComeFromTheOriginalAndDriveSearchAndSorting()
    {
        var path = PathFor("legacy.jsonl");
        File.WriteAllLines(path, [Header,
            Schema("movies", "ID", "fullpath", "filename", "filedate", "addedDate"),
            Row("movies", new { ID = 1, fullpath = directory + Path.DirectorySeparatorChar, filename = "Old.mp4", filedate = Date("2009-05-01T12:00:00.1234567"), addedDate = Date("2020-01-02T08:00:00") }),
            Row("movies", new { ID = 2, fullpath = directory + Path.DirectorySeparatorChar, filename = "New.mp4", filedate = Date("2021-07-01T12:00:00"), addedDate = Date("2012-03-04T08:00:00") }),
            Footer(new() { ["movies"] = 2 })]);
        using var catalog = new CatalogRepository(PathFor("catalog.db"));
        Assert.Empty(catalog.ImportLegacyJsonl(path).Errors);
        var scanned = PathFor("Scanned.mp4"); File.WriteAllBytes(scanned, [1, 2, 3]);
        File.SetLastWriteTimeUtc(scanned, new DateTime(2005, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(1, (await new MediaScanner(catalog).ScanAsync([scanned])).Imported);
        var item = catalog.GetMediaByPath(scanned)!;
        Assert.Equal(new DateTimeOffset(2005, 1, 1, 0, 0, 0, TimeSpan.Zero), item.FileCreatedAt);

        string[] Titles(SearchField field, SearchComparison comparison, DateTimeOffset date) => catalog.QueryPage(new()
        {
            Expression = new() { Condition = new() { Field = field, Comparison = comparison, Date = date } }, Sort = MediaSort.Title
        }).Items.Select(i => i.Title).ToArray();
        Assert.Equal(["Old", "Scanned"], Titles(SearchField.FileDate, SearchComparison.LessThan, new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(["New"], Titles(SearchField.AddedAt, SearchComparison.LessThan, new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(["New", "Old", "Scanned"], catalog.QueryPage(new() { Sort = MediaSort.AddedAt }).Items.Select(i => i.Title));
        Assert.Equal(["Scanned", "Old", "New"], catalog.QueryPage(new() { Sort = MediaSort.FileDate }).Items.Select(i => i.Title));
        Assert.Equal(0, catalog.GetPosition(new() { Sort = MediaSort.FileDate }, item.Id));
    }

    [Fact]
    public void Schema9LibraryGainsTheDateColumnsWithBackupAndAWorkingTextIndex()
    {
        var path = PathFor("catalog.db");
        Guid id;
        using (var catalog = new CatalogRepository(path))
        {
            id = catalog.Upsert(new() { Path = PathFor("Ferien Sommer.mp4"), Kind = MediaKind.Video, FileCreatedAt = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero) }).Id;
            catalog.PrepareSearchIndex();
        }
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            old.Open();
            old.CreateCollation("EFP_ORDINAL", (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
            using var downgrade = old.CreateCommand();
            downgrade.CommandText = """
                DROP INDEX media_query_imported_page;
                DROP TRIGGER media_query_insert; DROP TRIGGER media_query_update;
                ALTER TABLE media_query DROP COLUMN file_date; ALTER TABLE media_query DROP COLUMN imported;
                PRAGMA user_version=9;
                """;
            downgrade.ExecuteNonQuery();
        }
        using var upgraded = new CatalogRepository(path);
        Assert.Contains("before-v17-v9", upgraded.MigrationBackupPath!);
        var before2002 = new SearchExpression { Condition = new() { Field = SearchField.FileDate, Comparison = SearchComparison.LessThan, Date = new DateTimeOffset(2002, 1, 1, 0, 0, 0, TimeSpan.Zero) } };
        Assert.Equal(id, Assert.Single(upgraded.QueryPage(new() { Expression = before2002 }).Items).Id);
        Assert.Equal(id, Assert.Single(upgraded.QueryPage(new() { Text = "sommer" }).Items).Id);
        upgraded.Upsert(new() { Path = PathFor("Neu.mp4"), Kind = MediaKind.Video, FileCreatedAt = new DateTimeOffset(2000, 6, 1, 0, 0, 0, TimeSpan.Zero) });
        Assert.Equal(2, upgraded.QueryPage(new() { Expression = before2002 }).TotalCount);
        Assert.True(upgraded.CheckIntegrity().IsValid);
    }
}
