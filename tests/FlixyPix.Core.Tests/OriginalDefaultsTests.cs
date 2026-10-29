using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class OriginalDefaultsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-defaults-" + Guid.NewGuid());
    public OriginalDefaultsTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    private static JsonElement Row(object values) => JsonSerializer.SerializeToElement(values);

    [Fact]
    public void FingerprintMatchesTheGeneratorAndIgnoresIdCaseAndEmptyColumns()
    {
        // Same value as scripts/original/first-run-defaults.py fingerprint() for this row.
        var row = Row(new Dictionary<string, object?> { ["ID"] = 7, ["parameter"] = "Ä", ["value1"] = "1", ["blobobject"] = null, ["n"] = 2.5, ["x"] = new object?[] { true, null, new Dictionary<string, int> { ["B"] = 1 } } });
        Assert.Equal("48b0d9cf2c834aad82e1677c2f9eee43b58397cbd323b8aa86521042e77fe26d", OriginalDefaults.Fingerprint(row));
        Assert.Equal(OriginalDefaults.Fingerprint(Row(new { parameter = "a", value1 = "1" })), OriginalDefaults.Fingerprint(Row(new { id = 3, PARAMETER = "a", value1 = "1", blobobject = (string?)null })));
        Assert.NotEqual(OriginalDefaults.Fingerprint(Row(new { parameter = "a", value1 = "1" })), OriginalDefaults.Fingerprint(Row(new { parameter = "a", value1 = "2" })));
    }

    [Fact]
    public void SettingsWithoutIdCategoriesAndWatchLaterAreReportedAsTakenOver()
    {
        object[] records =
        [
            new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('e', 64) },
            new { type = "schema", table = "movies", columns = new[] { new { name = "ID" }, new { name = "fullpath" }, new { name = "filename" }, new { name = "categoryid" } } },
            new { type = "row", table = "movies", values = new { ID = 5, fullpath = @"U:\clips", filename = "a.mp4", categoryid = 2 } },
            new { type = "schema", table = "categories", columns = new[] { new { name = "ID" }, new { name = "category" }, new { name = "mtype" } } },
            new { type = "row", table = "categories", values = new { ID = 2, category = "Reisen", mtype = 0 } },
            new { type = "row", table = "categories", values = new { ID = 3, category = "Unbenutzt", mtype = 0 } },
            new { type = "schema", table = "videowatchlater", columns = new[] { new { name = "ID" }, new { name = "videoid" } } },
            new { type = "row", table = "videowatchlater", values = new { ID = 1, videoid = 5 } },
            new { type = "schema", table = "configsettings", columns = new[] { new { name = "parameter" }, new { name = "value1" } } },
            new { type = "row", table = "configsettings", values = new { parameter = "favrating", value1 = "4" } },
            new { type = "footer", tables = 4, rows = 5, tableRows = new Dictionary<string, int> { ["movies"] = 1, ["categories"] = 2, ["videowatchlater"] = 1, ["configsettings"] = 1 } },
        ];
        var path = Path.Combine(root, "taken-over.jsonl"); File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(record)));
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var result = catalog.ImportLegacyJsonl(path);
        Assert.Empty(result.Errors);
        var video = Assert.Single(catalog.Search()); Assert.True(video.WatchLater); Assert.Equal("Reisen", video.Category);
        var rows = catalog.GetMigrationRows(result.Migration!.Batch).ToDictionary(row => row.Table + ":" + row.Key);
        Assert.Equal(("partially-projected", "app-setting"), (rows["configsettings:favrating"].Status, rows["configsettings:favrating"].Code));
        Assert.Equal("watch-later", rows["videowatchlater:1"].Code); Assert.Contains(video.Id.ToString(), rows["videowatchlater:1"].TargetIds);
        Assert.Equal("media-category", rows["categories:2"].Code);
        Assert.Equal("raw-retained", rows["categories:3"].Status);
    }

    [Fact]
    public void BuiltinReferenceCoversTheFirstRunTables()
    {
        var defaults = OriginalDefaults.Builtin;
        Assert.StartsWith("run-", defaults.Reference);
        foreach (var table in new[] { "configsettings", "properties_domain", "properties_main", "display_settings", "customplayersettings", "videosettings", "searchcriteria" })
            Assert.Contains(table, defaults.Tables);
        Assert.Null(defaults.Classify("movies", Row(new { ID = 1 })));
    }

    [Fact]
    public void RawRowsEqualToDefaultsAreReportedAsDefaultsAndChangedSettingsAsChanged()
    {
        var standard = new { parameter = "thumbsize", value1 = "160" };
        var property = new { ID = 4, property_id = 1, property_text = "Blond" };
        var defaults = new OriginalDefaults(new Dictionary<string, IEnumerable<string>>
        {
            ["configsettings"] = [OriginalDefaults.Fingerprint(Row(standard)), OriginalDefaults.Fingerprint(Row(new { parameter = "sortkey", value1 = "1" }))],
            ["properties_domain"] = [OriginalDefaults.Fingerprint(Row(property))],
        }, ["thumbsize", "sortkey"]);
        object[] records =
        [
            new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('d', 64) },
            new { type = "schema", table = "configsettings", columns = new[] { new { name = "ID" }, new { name = "parameter" }, new { name = "value1" } } },
            new { type = "row", table = "configsettings", values = new { ID = 1, standard.parameter, standard.value1 } },
            new { type = "row", table = "configsettings", values = new { ID = 2, parameter = "sortkey", value1 = "5" } },
            new { type = "row", table = "configsettings", values = new { ID = 3, parameter = "unknownkey", value1 = "x" } },
            new { type = "schema", table = "properties_domain", columns = new[] { new { name = "ID" }, new { name = "property_id" }, new { name = "property_text" } } },
            new { type = "row", table = "properties_domain", values = new { ID = 99, property.property_id, property.property_text } },
            new { type = "row", table = "properties_domain", values = new { ID = 100, property_id = 1, property_text = "Own value" } },
            new { type = "footer", tables = 2, rows = 5, tableRows = new Dictionary<string, int> { ["configsettings"] = 3, ["properties_domain"] = 2 } },
        ];
        var path = Path.Combine(root, "defaults.jsonl"); File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(record)));
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var result = catalog.ImportLegacyJsonl(path, new LegacyImportOptions { OriginalDefaults = defaults, AdoptAppSettings = false });
        Assert.Empty(result.Errors);
        var codes = catalog.GetMigrationRows(result.Migration!.Batch).ToDictionary(row => row.Table + ":" + row.Key, row => (row.Status, row.Code));
        Assert.Equal(("raw-retained", "original-default"), codes["configsettings:1"]);
        Assert.Equal(("raw-retained", "changed-original-setting"), codes["configsettings:2"]);
        Assert.Equal("preserved-unverified-projection", codes["configsettings:3"].Code);
        Assert.Equal("original-default", codes["properties_domain:99"].Code);
        Assert.Equal("preserved-unverified-projection", codes["properties_domain:100"].Code);
        Assert.Equal(1, result.Migration.Tables.Single(table => table.Table == "properties_domain").OriginalDefaults);
        Assert.Equal(1, result.Migration.Tables.Single(table => table.Table == "configsettings").OriginalDefaults);
    }
}
