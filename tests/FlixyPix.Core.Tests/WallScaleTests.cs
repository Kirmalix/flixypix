using System.Diagnostics;
using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class WallScaleTests
{
    [Fact, Trait("Category", "Slow")]
    public void HundredThousandVideosAndHalfMillionMarksStayPaged()
    {
        var root = Path.Combine(Path.GetTempPath(), "flixypix-wall-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
            using (var seed = new SqliteConnection("Data Source=" + catalog.DatabasePath))
            {
                seed.Open(); CatalogRepository.AddOrdinalCollation(seed);
                using var transaction = seed.BeginTransaction();
                using var media = seed.CreateCommand(); media.Transaction = transaction;
                media.CommandText = "INSERT INTO media(id,path_key,document) VALUES($id,$path,$json)";
                var mediaId = media.Parameters.Add("$id", SqliteType.Text); var path = media.Parameters.Add("$path", SqliteType.Text); var document = media.Parameters.Add("$json", SqliteType.Text);
                media.Prepare(); var ids = new string[100_000];
                for (var index = 0; index < 101_000; index++)
                {
                    var id = Guid.NewGuid().ToString(); if (index < ids.Length) ids[index] = id;
                    var item = new MediaItem { Id = Guid.Parse(id), Path = Path.Combine(root, index + ".mp4"), Kind = index < ids.Length ? MediaKind.Video : MediaKind.Image,
                        Title = "Video " + index, Description = "Benchmark description", AddedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), Tags = ["Performer" + index % 100] };
                    mediaId.Value = id; path.Value = item.Path.ToUpperInvariant(); document.Value = JsonSerializer.Serialize(item, CatalogRepository.Json); media.ExecuteNonQuery();
                }
                using var scenes = seed.CreateCommand(); using var bookmarks = seed.CreateCommand();
                foreach (var (command, table) in new[] { (scenes, "scenes"), (bookmarks, "bookmarks") })
                {
                    command.Transaction = transaction; command.CommandText = $"INSERT INTO {table}(id,media_id,document) VALUES($id,$media,$json)";
                    command.Parameters.Add("$id", SqliteType.Text); command.Parameters.Add("$media", SqliteType.Text); command.Parameters.Add("$json", SqliteType.Text); command.Prepare();
                }
                for (var index = 0; index < 500_000; index++)
                {
                    var id = Guid.NewGuid(); var owner = ids[index / 5]; var position = index % 5;
                    var command = index % 2 == 0 ? scenes : bookmarks;
                    command.Parameters["$id"].Value = id.ToString(); command.Parameters["$media"].Value = owner;
                    command.Parameters["$json"].Value = index % 2 == 0
                        ? JsonSerializer.Serialize(new Scene { Id = id, MediaId = Guid.Parse(owner), Title = "Mark " + index, StartSeconds = position, EndSeconds = position + .5, Tags = ["Class" + index % 10] }, CatalogRepository.Json)
                        : JsonSerializer.Serialize(new Bookmark { Id = id, MediaId = Guid.Parse(owner), Title = "Mark " + index, PositionSeconds = position, Tags = ["Class" + index % 10] }, CatalogRepository.Json);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            var watch = Stopwatch.StartNew(); var first = catalog.QueryWallPage(new()); var firstMs = watch.Elapsed.TotalMilliseconds;
            Assert.Equal(500_000, first.TotalCount); Assert.Equal(120, first.Items.Count);
            var samples = new List<double>();
            for (var run = 0; run < 20; run++)
            {
                var filtered = run % 2 == 1; watch.Restart();
                var page = catalog.QueryWallPage(filtered ? new(Tags: ["Class3", "Performer0"]) : new(), filtered ? 0 : run * 20000);
                samples.Add(watch.Elapsed.TotalMilliseconds);
                Assert.Equal(filtered ? 1000 : 500_000, page.TotalCount); Assert.InRange(page.Items.Count, 1, 120);
            }
            var textSeries = new List<object>(); var textBudgets = new List<(string Name, double First, double P95)>();
            foreach (var scenario in new (string Name, WallQuery Query, int Count)[] {
                ("rare", new(Text: "Mark 499999"), 1),
                ("common", new(Text: "Benchmark description"), 500000),
                ("missing", new(Text: "no-such-wall-result"), 0),
                ("text-tags", new(Text: "Mark", Tags: ["Class3", "Performer0"]), 1000) })
            {
                var times = new List<double>();
                for (var run = 0; run < 21; run++)
                {
                    watch.Restart(); var page = catalog.QueryWallPage(scenario.Query); times.Add(watch.Elapsed.TotalMilliseconds);
                    Assert.Equal(scenario.Count, page.TotalCount); Assert.Equal(Math.Min(120, scenario.Count), page.Items.Count);
                }
                var sorted = times.Skip(1).Order().ToArray(); var percentile = sorted[18];
                textSeries.Add(new { scenario.Name, FirstMs = times[0], P95Ms = percentile, SamplesMs = times.Skip(1).ToArray() });
                textBudgets.Add((scenario.Name, times[0], percentile));
            }
            var memory = new List<object>();
            for (var run = 0; run <= 100; run++)
            {
                var page = catalog.QueryWallPage(new(), run * 4000); Assert.Equal(120, page.Items.Count);
                if (run % 20 == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); using var process = Process.GetCurrentProcess(); memory.Add(new { Page = run, ManagedBytes = GC.GetTotalMemory(false), PrivateBytes = process.PrivateMemorySize64 }); }
            }
            var ordered = samples.Order().ToArray(); var p95 = ordered[(int)Math.Ceiling(ordered.Length * .95) - 1];
            var project = new DirectoryInfo(AppContext.BaseDirectory);
            while (project != null && !File.Exists(Path.Combine(project.FullName, "FlixyPix.slnx"))) project = project.Parent;
            if (project != null)
            {
                var report = Path.Combine(project.FullName, "artifacts", "test-results", "wall-500k-benchmark.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                File.WriteAllText(report, JsonSerializer.Serialize(new { Videos = 100_000, Images = 1000, Marks = 500_000, FirstMs = firstMs, P95Ms = p95, SamplesMs = samples, TextSeries = textSeries, MemorySeries = memory }, new JsonSerializerOptions { WriteIndented = true }));
            }
            foreach (var budget in textBudgets) { Assert.True(budget.First < 2000, $"{budget.Name} first {budget.First:F0}ms"); Assert.True(budget.P95 < 1000, $"{budget.Name} p95 {budget.P95:F0}ms"); }
            Assert.True(firstMs < 2000, $"First wall page took {firstMs:F0} ms.");
            Assert.True(p95 < 1000, $"Warm page/filter p95 took {p95:F0} ms.");
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
