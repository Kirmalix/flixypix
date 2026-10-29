using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>The statistics count the library by kind, resolution, file type, import month, performers, studios and tags,
/// the most watched and the playbacks per week; every bar leads to a query that finds exactly its media.</summary>
public sealed class StatisticsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-stats-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogRepository catalog;
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public StatisticsTests()
    {
        Directory.CreateDirectory(directory);
        catalog = new CatalogRepository(Path.Combine(directory, "catalog.db"));
    }

    public void Dispose() { catalog.Dispose(); SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }

    private MediaItem Add(string name, MediaKind kind, int? width = null, int? height = null, double? seconds = null, long size = 1000, DateTimeOffset? added = null,
        long? plays = null, Dictionary<string, List<string>>? categories = null, string[]? tags = null) => catalog.Upsert(new MediaItem
    {
        Path = Path.Combine(directory, name), Title = Path.GetFileNameWithoutExtension(name), Kind = kind, Width = width, Height = height, DurationSeconds = seconds,
        FileSize = size, AddedAt = added ?? Now, PlayCount = plays, TagCategories = categories ?? new(), Tags = [.. tags ?? []],
    });

    private IReadOnlyList<string> Titles(SearchExpression? expression) =>
        catalog.QueryPage(new MediaQuery { Expression = expression, Limit = 100 }).Items.Select(m => m.Title).OrderBy(t => t, StringComparer.Ordinal).ToList();

    [Fact]
    public void CountsTheLibraryAndEachBarFindsItsMedia()
    {
        Add("uhd.mp4", MediaKind.Video, 3840, 2160, 1800, 4000, Now.AddMonths(-1), plays: 3, categories: new() { ["Darsteller"] = ["Jane Doe"], ["Studio"] = ["Acme"] });
        Add("hoch.MKV", MediaKind.Video, 1080, 1920, 1800, 2000, Now, plays: 1, categories: new() { ["Darsteller"] = ["Jane Doe"] });
        Add("alt.avi", MediaKind.Video, 640, 480, 3600, 1000, Now.AddYears(-3));
        Add("ohne.mp4", MediaKind.Video, seconds: 600);
        Add("bild.jpg", MediaKind.Image, 800, 600, tags: ["Strand"]);
        Add("ton.mp3", MediaKind.Audio, seconds: 1800);

        var stats = catalog.BuildStatistics(Now);
        Assert.Equal(6, stats.Media);
        Assert.Equal(10_000, stats.Bytes);
        Assert.Equal(2.6667, stats.Hours, 3);
        Assert.Equal([("Video", 4L), ("Image", 1L), ("Audio", 1L)], stats.Kinds.Select(k => (k.View, k.Count)));
        // Portrait 1080 × 1920 counts as 1080p by its shorter side.
        Assert.Equal([("4K", 1L), ("1080p", 1L), ("SD", 1L), (Texts.T("unknown"), 1L)], stats.Resolutions.Select(r => (r.Label, r.Count)));
        Assert.Equal(["hoch"], Titles(stats.Resolutions[1].Expression));
        Assert.Equal(["uhd"], Titles(stats.Resolutions[0].Expression));
        Assert.Equal(["ohne"], Titles(stats.Resolutions[3].Expression));
        Assert.Equal(("MP4", 2L), (stats.FileTypes[0].Label, stats.FileTypes[0].Count));
        Assert.Equal(["hoch"], Titles(stats.FileTypes.Single(f => f.Label == "MKV").Expression));

        Assert.Equal(24, stats.ImportsPerMonth.Count);
        Assert.Equal([1L, 4L], stats.ImportsPerMonth.TakeLast(2).Select(m => m.Count));
        Assert.Equal(["uhd"], Titles(stats.ImportsPerMonth[^2].Expression));

        Assert.Equal(("Jane Doe", 2L), (stats.Performers.Single().Label, stats.Performers.Single().Count));
        Assert.Equal(["hoch", "uhd"], Titles(stats.Performers[0].Expression));
        Assert.Equal(["uhd"], Titles(stats.Studios.Single().Expression));
        Assert.Equal(["bild"], Titles(stats.Tags.Single(t => t.Label == "Strand").Expression));

        Assert.Equal(["uhd", "hoch"], stats.MostWatched.Select(m => m.Label));
        Assert.Equal(3, stats.MostWatched[0].Count);
        Assert.NotNull(stats.MostWatched[0].MediaId);
        Assert.Equal(3, stats.NeverWatched);
    }

    [Fact]
    public void CountsPlaybacksPerWeekFromTheHistory()
    {
        var video = Add("film.mp4", MediaKind.Video, 1920, 1080, 600);
        catalog.FinishPlayback(video.Id, catalog.BeginPlayback(video.Id, Now), 600, 1);
        catalog.FinishPlayback(video.Id, catalog.BeginPlayback(video.Id, Now.AddDays(-7)), 300, .5);
        catalog.BeginPlayback(video.Id, Now.AddDays(-400));
        catalog.AddO(video.Id, Now); catalog.AddO(video.Id, Now);
        var stats = catalog.BuildStatistics(Now);
        Assert.Equal(26, stats.PlaysPerWeek.Count);
        Assert.Equal([1L, 1L], stats.PlaysPerWeek.TakeLast(2).Select(w => w.Count));
        Assert.Equal(2, stats.PlaysPerWeek.Sum(w => w.Count));
        Assert.Equal(3, stats.Plays);
        Assert.Equal(.25, stats.WatchedHours, 3);
        Assert.Equal(2, stats.OTotal);
    }
}
