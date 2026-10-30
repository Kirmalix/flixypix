using System.Diagnostics;
using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace FlixyPix.Core.Tests;

public sealed class QueryTests(ITestOutputHelper output) : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-query-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(directory, "catalog.db");
    private CatalogRepository Open() => new(Database);
    private MediaItem Item(string name, string title) => new() { Path = Path.Combine(directory, name), Title = title, Kind = MediaKind.Image };

    [Fact]
    public void CachedFacetsFollowOwnAndForeignWrites()
    {
        using var catalog = Open();
        var item = catalog.Upsert(Item("one/a.jpg", "Eins") with { Tags = ["Alt"] });
        var cached = catalog.GetFacets();
        Assert.Same(cached, catalog.GetFacets());
        catalog.Upsert(item with { Tags = ["Neu"], Category = "Reise" });
        var afterOwnWrite = catalog.GetFacets();
        Assert.NotSame(cached, afterOwnWrite);
        Assert.Equal(["Neu"], afterOwnWrite.Tags); Assert.Equal(["Reise"], afterOwnWrite.Categories);
        using (var other = Open()) other.Upsert(Item("two/b.jpg", "Zwei") with { Tags = ["Fremd"] });
        var afterForeignWrite = catalog.GetFacets();
        Assert.Equal(2, afterForeignWrite.MediaCount); Assert.Contains("Fremd", afterForeignWrite.Tags);
        catalog.Delete(item.Id);
        Assert.Equal(["Fremd"], catalog.GetFacets().Tags);
    }

    [Fact]
    public void FacetsCountMediaPerFolderCategoryAndTagCategory()
    {
        using var catalog = Open();
        catalog.Upsert(Item("one/a.jpg", "A") with { Category = "Reise", TagCategories = new() { ["Orte"] = ["Rom", "Wien"] } });
        catalog.Upsert(Item("one/b.jpg", "B") with { Category = "Reise", TagCategories = new() { ["Orte"] = ["Rom"], ["Personen"] = ["Anna"] } });
        catalog.Upsert(Item("one/sub/c.jpg", "C") with { Category = "Familie" });
        catalog.Upsert(Item("two/d.jpg", "D"));
        var facets = catalog.GetFacets();
        Assert.Equal(2, facets.FolderCounts![Path.Combine(directory, "one")]); Assert.Equal(1, facets.FolderCounts[Path.Combine(directory, "one", "sub")]);
        Assert.Equal(1, facets.FolderCounts[Path.Combine(directory, "two")]); Assert.Equal(facets.Folders.Count, facets.FolderCounts.Count);
        Assert.Equal(2, facets.CategoryCounts!["Reise"]); Assert.Equal(1, facets.CategoryCounts["Familie"]); Assert.Equal(["Familie", "Reise"], facets.Categories);
        // A medium with two tags of one category counts once.
        Assert.Equal(2, facets.TagCategoryCounts!["Orte"]); Assert.Equal(1, facets.TagCategoryCounts["Personen"]);
    }

    [Fact]
    public void PagedUnicodeQueriesMatchCompatibilitySearchAndFacets()
    {
        using var catalog = Open();
        var alpha = catalog.Upsert(Item("one/a.jpg", "Ärger Σ") with { Tags = ["Straße", "Привет"], TagCategories = new() { ["Orte"] = ["München"] }, Category = "Bilder", Rating = 8 });
        var beta = catalog.Upsert(Item("two/b.jpg", "äRGER σ") with { Tags = ["STRASSE"], Category = "Videos", WatchLater = true, Kind = MediaKind.Video });
        var queries = new[] { new MediaQuery { Text = "ärGER" }, new MediaQuery { Tag = "straße" }, new MediaQuery { Text = "ПРИВЕТ münchen" },
            new MediaQuery { TagCategory = "orte", Tag = "MÜNCHEN" }, new MediaQuery { IsFavorite = true }, new MediaQuery { WatchLater = false }, new MediaQuery { Category = "bilder" } };
        foreach (var query in queries)
        {
            var actual = catalog.QueryPage(query);
            var legacy = catalog.Search(query.Text, query.Tag, query.MinimumRating, query.IsFavorite, query.WatchLater, query.Category, query.TagCategory);
            Assert.Equal(legacy.Select(x => x.Id), actual.Items.Select(x => x.Id));
            Assert.Equal(legacy.Count, actual.TotalCount);
        }
        Assert.Equal(alpha.Id, Assert.Single(catalog.QueryPage(new() { Folder = Path.Combine(directory, "one") }).Items).Id);
        Assert.Equal(2, catalog.QueryPage(new() { Folder = directory }).TotalCount);
        Assert.Empty(catalog.QueryPage(new() { Folder = directory, IncludeSubfolders = false }).Items);
        var first = catalog.QueryPage(new() { Limit = 1 });
        var second = catalog.QueryPage(new() { Limit = 1, Offset = 1 });
        Assert.Equal(2, first.TotalCount); Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
        var facets = catalog.GetFacets();
        Assert.Equal(2, facets.MediaCount); Assert.Contains("München", facets.CategorizedTags["orte"]);
        Assert.Equal(2, facets.Folders.Count); Assert.Equal(2, facets.Categories.Count);
        Assert.Contains("Straße", facets.Tags); Assert.Contains("STRASSE", facets.Tags);
        Assert.Equal(alpha.Id, catalog.GetMedia(alpha.Id)!.Id);
        Assert.Equal(alpha.Tags, catalog.GetMedia(alpha.Id)!.Tags);
        Assert.Null(catalog.GetMedia(Guid.NewGuid()));
        catalog.Delete(alpha.Id);
        Assert.Equal(1, catalog.GetFacets().MediaCount); Assert.Empty(catalog.GetFacets().CategorizedTags);
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.QueryPage(new() { Limit = 0 }));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => catalog.QueryPage(new(), cancelled.Token));
    }

    [Fact]
    public void SchemaSixMigrationBacksUpCommittedWalAndPreservesDocuments()
    {
        Directory.CreateDirectory(directory);
        using var original = new SqliteConnection($"Data Source={Database};Pooling=False"); original.Open();
        using (var schema = original.CreateCommand())
        {
            schema.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE media(id TEXT PRIMARY KEY,path_key TEXT NOT NULL UNIQUE,document TEXT NOT NULL); PRAGMA user_version=6;";
            schema.ExecuteNonQuery();
        }
        var item = Item("old.jpg", "WAL München");
        using (var insert = original.CreateCommand())
        {
            insert.CommandText = "INSERT INTO media VALUES($id,$path,$json)";
            insert.Parameters.AddWithValue("$id", item.Id.ToString()); insert.Parameters.AddWithValue("$path", item.Path.ToUpperInvariant());
            insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            insert.ExecuteNonQuery();
        }
        Assert.True(File.Exists(Database + "-wal"));
        using var migrated = Open();
        Assert.NotNull(migrated.MigrationBackupPath);
        using var backup = new SqliteConnection($"Data Source={migrated.MigrationBackupPath};Mode=ReadOnly;Pooling=False"); backup.Open();
        using var version = backup.CreateCommand(); version.CommandText = "PRAGMA user_version"; Assert.Equal(6L, version.ExecuteScalar());
        using var count = backup.CreateCommand(); count.CommandText = "SELECT count(*) FROM media"; Assert.Equal(1L, count.ExecuteScalar());
        Assert.Equal(item.Id, Assert.Single(migrated.QueryPage(new() { Text = "münchen" }).Items).Id);
        using var verify = original.CreateCommand(); verify.CommandText = "PRAGMA user_version"; Assert.Equal(17L, verify.ExecuteScalar());
    }

    [Fact]
    public void FailedMigrationRollsBackSchemaAndRetainsRecoverableBackup()
    {
        Directory.CreateDirectory(directory);
        using (var source = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            source.Open(); using var command = source.CreateCommand();
            command.CommandText = "CREATE TABLE media(id TEXT PRIMARY KEY,path_key TEXT NOT NULL UNIQUE,document TEXT NOT NULL); INSERT INTO media VALUES('broken','broken','not-json'); PRAGMA user_version=6;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => Open());
        Assert.Single(Directory.GetFiles(directory, "*.bak"));
        using var verify = new SqliteConnection($"Data Source={Database};Pooling=False"); verify.Open();
        using var version = verify.CreateCommand(); version.CommandText = "PRAGMA user_version"; Assert.Equal(6L, version.ExecuteScalar());
        using var table = verify.CreateCommand(); table.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='media_query'"; Assert.Equal(0L, table.ExecuteScalar());
    }

    [Fact, Trait("Category", "Slow")]
    public void HundredThousandItemsStayPagedAndMeetQueryBudgets()
    {
        using var catalog = Open();
        using (var seed = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            seed.Open(); seed.CreateCollation("EFP_ORDINAL", StringComparer.OrdinalIgnoreCase.Compare);
            using var transaction = seed.BeginTransaction();
            using var insert = seed.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO media(id,path_key,document) VALUES($id,$path,$json)";
            var id = insert.Parameters.Add("$id", SqliteType.Text); var path = insert.Parameters.Add("$path", SqliteType.Text); var json = insert.Parameters.Add("$json", SqliteType.Text);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            insert.Prepare();
            for (var index = 0; index < 100_000; index++)
            {
                var item = Item($"folder-{index % 50}/image-{index:000000}.jpg", $"München {index:000000}") with
                { Tags = ["Natur", "Gruppe" + index % 10], Rating = index % 11, Category = index % 2 == 0 ? "Fotos" : "Auswahl", Kind = MediaKind.Image };
                id.Value = item.Id.ToString(); path.Value = item.Path.ToUpperInvariant(); json.Value = JsonSerializer.Serialize(item, options); insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        var watch = Stopwatch.StartNew();
        var first = catalog.QueryPage(new());
        var firstMs = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(100_000, first.TotalCount); Assert.Equal(120, first.Items.Count);
        watch.Restart(); var searched = catalog.QueryPage(new() { Text = "münchen 0999", Offset = 10, Limit = 20 }); var searchMs = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(120, searched.TotalCount); Assert.Equal(20, searched.Items.Count);
        watch.Restart(); var facets = catalog.GetFacets(); var facetsMs = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(100_000, facets.MediaCount); Assert.Equal(50, facets.Folders.Count);
        watch.Restart(); var last = catalog.QueryPage(new() { Offset = 99_960 }); var warmMs = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(40, last.Items.Count);
        var samples = new List<(string Kind, double Milliseconds)>();
        for (var iteration = 0; iteration < 10; iteration++)
        {
            watch.Restart(); var page = catalog.QueryPage(new() { Offset = iteration * 9970 });
            samples.Add(("page", watch.Elapsed.TotalMilliseconds)); Assert.Equal(100_000, page.TotalCount);
            watch.Restart(); var search = catalog.QueryPage(new() { Text = "m\u00fcnchen " + (9990 + iteration).ToString("000000"), Limit = 20 });
            samples.Add(("search", watch.Elapsed.TotalMilliseconds)); Assert.True(search.TotalCount >= 1);
        }
        static double P95(IEnumerable<double> values)
        {
            var ordered = values.Order().ToArray(); return ordered[(int)Math.Ceiling(ordered.Length * .95) - 1];
        }
        var pageP95 = P95(samples.Where(x => x.Kind == "page").Select(x => x.Milliseconds));
        var searchP95 = P95(samples.Where(x => x.Kind == "search").Select(x => x.Milliseconds));
        // The same searches once the trigram index is built, as the desktop does in the background after start and imports.
        watch.Restart(); Assert.True(catalog.PrepareSearchIndex() > 0); var indexMs = watch.Elapsed.TotalMilliseconds;
        for (var iteration = 0; iteration < 10; iteration++)
        {
            watch.Restart(); var search = catalog.QueryPage(new() { Text = "münchen " + (9990 + iteration).ToString("000000"), Limit = 20 });
            samples.Add(("indexed", watch.Elapsed.TotalMilliseconds)); Assert.Equal(1, search.TotalCount);
        }
        var indexedP95 = P95(samples.Where(x => x.Kind == "indexed").Select(x => x.Milliseconds));
        // The start page: every row from limited queries, also at this size.
        watch.Restart(); var home = catalog.BuildHomeFeed(DateTimeOffset.Now); var homeMs = watch.Elapsed.TotalMilliseconds;
        Assert.Contains(home.Rows, r => r.Kind == HomeRowKind.RecentlyAdded && r.Cards.Count == 20);
        // Statistics: half of the library becomes Full HD videos with plays, plus 20,000 playbacks in the history.
        using (var videos = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            videos.Open(); videos.CreateCollation("EFP_ORDINAL", StringComparer.OrdinalIgnoreCase.Compare);
            using var change = videos.CreateCommand();
            change.CommandText = """
                UPDATE media SET document=json_set(document,'$.kind','Video','$.width',1920,'$.height',1080,'$.durationSeconds',600,'$.playCount',rowid%5) WHERE rowid%2=0;
                INSERT INTO play_history(media_id,kind,at,seconds,share) SELECT id, 0, julianday('now') - rowid%180, 300, .5 FROM media WHERE rowid%2=0 AND rowid<=40000;
                """;
            change.ExecuteNonQuery();
        }
        var statisticsMs = new List<double>();
        for (var run = 0; run < 5; run++) { watch.Restart(); var statistics = catalog.BuildStatistics(DateTimeOffset.Now); statisticsMs.Add(watch.Elapsed.TotalMilliseconds); Assert.Equal(100_000, statistics.Media); Assert.Equal(50_000, statistics.Kinds.Single(k => k.View == "Video").Count); }
        var statisticsWarm = statisticsMs.Skip(1).Max();
        output.WriteLine($"statistics: cold={statisticsMs[0]:F0}ms; warm max={statisticsWarm:F0}ms; parts={string.Join(", ", catalog.BuildStatistics(DateTimeOffset.Now).Timings.Select(t => $"{t.Key} {t.Value:F0}"))}");
        output.WriteLine($"100000 media: first={firstMs:F1}ms; search={searchMs:F1}ms; facets={facetsMs:F1}ms; warm deep page={warmMs:F1}ms; warm 10-page p95={pageP95:F1}ms; warm 10-search p95={searchP95:F1}ms; index build={indexMs:F0}ms; indexed 10-search p95={indexedP95:F1}ms");
        var report = new
        {
            RecordedAtUtc = DateTimeOffset.UtcNow, Environment.MachineName, Environment.ProcessorCount,
            OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            MediaCount = 100_000, firstMs, searchMs, facetsMs, warmMs, pageP95, searchP95, indexMs, indexedP95, homeMs, statisticsMs, HomeTimings = catalog.LastHomeTimings, Samples = samples.Select(x => new { x.Kind, x.Milliseconds }).ToArray()
        };
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "FlixyPix.slnx"))) root = root.Parent;
        if (root is not null)
        {
            var reportDirectory = Path.Combine(root.FullName, "artifacts", "test-results"); Directory.CreateDirectory(reportDirectory);
            File.WriteAllText(Path.Combine(reportDirectory, "catalog-100k-benchmark.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(statisticsWarm < 1000, $"Statistics took {statisticsWarm:F0} ms warm.");
        Assert.True(firstMs < 2000, $"First page {firstMs:F1}ms exceeded 2000ms");
        Assert.True(searchMs < 1000 && warmMs < 1000 && pageP95 < 1000 && searchP95 < 1000,
            $"Warm query budget: search={searchMs:F1}, deep page={warmMs:F1}, page p95={pageP95:F1}, search p95={searchP95:F1}ms");
        // A selective search on the built index must be clearly faster than the row-by-row comparison.
        Assert.True(indexedP95 < 250 && indexMs < 10000, $"Indexed search p95 {indexedP95:F1}ms, index build {indexMs:F0}ms");
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void PortableVersionsRoundTripThroughStreamingExport(int version)
    {
        using var catalog = Open();
        catalog.Upsert(Item("portable.jpg", "Portable"));
        var export = Path.Combine(directory, "catalog.json"); catalog.Export(export);
        var content = File.ReadAllText(export); Assert.Contains("\"version\": 4", content);
        File.WriteAllText(export, content.Replace("\"version\": 4", $"\"version\": {version}"));
        using var target = new CatalogRepository(Path.Combine(directory, "target.db"));
        Assert.Empty(target.ImportJson(export).Errors); Assert.Single(target.QueryPage(new()).Items);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
