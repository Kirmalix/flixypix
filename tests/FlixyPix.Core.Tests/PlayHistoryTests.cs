using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>O counter, playback history and the watched share: counting, undo, the switch, deleting, sorting and searching,
/// merging, and the upgrade of a version 11 library.</summary>
public sealed class PlayHistoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-history-" + Guid.NewGuid().ToString("N"));
    private readonly string path;
    private CatalogRepository catalog;

    public PlayHistoryTests()
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "catalog.db");
        catalog = new CatalogRepository(path);
    }

    public void Dispose() { catalog.Dispose(); SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }

    private MediaItem Add(string title, long? plays = null, MediaKind kind = MediaKind.Video) => catalog.Upsert(new MediaItem
    {
        Path = Path.Combine(directory, title + (kind == MediaKind.Image ? ".jpg" : ".mp4")), Title = title, Kind = kind, DurationSeconds = kind == MediaKind.Image ? null : 600, PlayCount = plays,
    });

    private IReadOnlyList<string> Titles(MediaQuery query) => catalog.QueryPage(query with { Limit = 100 }).Items.Select(m => m.Title).ToList();

    [Fact]
    public void OCounterCountsTakesBackAndNotesWhen()
    {
        var video = Add("Clip");
        var when = new DateTimeOffset(2026, 10, 28, 21, 15, 0, TimeSpan.Zero);
        catalog.AddO(video.Id, when); catalog.AddO(video.Id, when.AddMinutes(5));
        Assert.Equal(2, catalog.GetMedia(video.Id)!.OCount);
        var history = catalog.GetHistory(video.Id);
        Assert.Equal([when.AddMinutes(5), when], history.Select(e => e.At));
        Assert.All(history, e => Assert.Equal(HistoryKind.O, e.Kind));

        Assert.Equal(1, catalog.UndoO(video.Id)!.OCount);
        Assert.Equal([when], catalog.GetHistory(video.Id).Select(e => e.At));
        catalog.UndoO(video.Id);
        // Below zero is not possible.
        Assert.Equal(0, catalog.UndoO(video.Id)!.OCount);
        Assert.Empty(catalog.GetHistory(video.Id));
    }

    [Fact]
    public void PlaybackCountsAsWatchedFromTheShareInTheSettings()
    {
        var video = Add("Film");
        catalog.RecordPlay(video.Id, DateTimeOffset.UtcNow);
        var entry = catalog.BeginPlayback(video.Id, DateTimeOffset.UtcNow);
        Assert.NotNull(entry);
        Assert.Equal(0, catalog.FinishPlayback(video.Id, entry, 120, .2)!.WatchCount);
        catalog.RecordPlay(video.Id, DateTimeOffset.UtcNow);
        Assert.Equal(1, catalog.FinishPlayback(video.Id, catalog.BeginPlayback(video.Id, DateTimeOffset.UtcNow), 560, .93)!.WatchCount);
        var history = catalog.GetHistory(video.Id);
        Assert.Equal([.93, .2], history.Select(e => e.Share));
        Assert.Equal([560d, 120d], history.Select(e => e.Seconds));

        // At 50 % the short playback would have counted.
        catalog.SaveHistoryPreferences(new HistoryPreferences { WatchedShare = .5 });
        catalog.RecordPlay(video.Id, DateTimeOffset.UtcNow);
        Assert.Equal(2, catalog.FinishPlayback(video.Id, catalog.BeginPlayback(video.Id, DateTimeOffset.UtcNow), 300, .5)!.WatchCount);
        Assert.Throws<ArgumentException>(() => catalog.SaveHistoryPreferences(new HistoryPreferences { WatchedShare = .05 }));
        Assert.Throws<ArgumentException>(() => catalog.SaveHistoryPreferences(new HistoryPreferences { WatchedShare = 1.2 }));
    }

    [Fact]
    public void PlaysBeforeTheHistoryStayWatched()
    {
        var old = Add("Alt", plays: 3);
        var unseen = Add("Neu");
        Assert.Equal(["Alt"], Titles(new MediaQuery { Expression = new MediaFilter { Watched = true }.ToExpression(DateTimeOffset.UtcNow) }));
        // A short playback now keeps the three earlier ones as watched.
        catalog.RecordPlay(old.Id, DateTimeOffset.UtcNow);
        Assert.Equal(3, catalog.FinishPlayback(old.Id, catalog.BeginPlayback(old.Id, DateTimeOffset.UtcNow), 10, .02)!.WatchCount);
        // Started but hardly seen does not count as watched.
        catalog.RecordPlay(unseen.Id, DateTimeOffset.UtcNow);
        catalog.FinishPlayback(unseen.Id, catalog.BeginPlayback(unseen.Id, DateTimeOffset.UtcNow), 10, .02);
        Assert.Equal(["Alt"], Titles(new MediaQuery { Expression = new MediaFilter { Watched = true }.ToExpression(DateTimeOffset.UtcNow) }));
        Assert.Equal(["Neu"], Titles(new MediaQuery { Expression = new MediaFilter { Watched = false }.ToExpression(DateTimeOffset.UtcNow) }));
    }

    [Fact]
    public void SwitchedOffHistoryNotesNothingButStillCounts()
    {
        var video = Add("Still");
        var image = Add("Bild", kind: MediaKind.Image);
        Assert.Null(catalog.BeginPlayback(image.Id, DateTimeOffset.UtcNow));
        catalog.SaveHistoryPreferences(new HistoryPreferences { Enabled = false });
        Assert.Null(catalog.BeginPlayback(video.Id, DateTimeOffset.UtcNow));
        catalog.RecordPlay(video.Id, DateTimeOffset.UtcNow);
        Assert.Equal(1, catalog.FinishPlayback(video.Id, null, 590, .98)!.WatchCount);
        Assert.Equal(1, catalog.AddO(video.Id, DateTimeOffset.UtcNow)!.OCount);
        Assert.Equal(0, catalog.CountHistory());
    }

    [Fact]
    public void DeletingTheHistoryKeepsTheCounters()
    {
        var first = Add("Eins"); var second = Add("Zwei");
        catalog.AddO(first.Id, DateTimeOffset.UtcNow); catalog.AddO(second.Id, DateTimeOffset.UtcNow);
        catalog.FinishPlayback(first.Id, catalog.BeginPlayback(first.Id, DateTimeOffset.UtcNow), 600, 1);
        Assert.Equal(2, catalog.ClearHistory(first.Id));
        Assert.Equal(1, catalog.CountHistory());
        Assert.Equal(1, catalog.ClearHistory());
        Assert.Equal(0, catalog.CountHistory());
        Assert.Equal(1, catalog.GetMedia(first.Id)!.OCount);
        // Deleting a medium takes its history along.
        catalog.AddO(second.Id, DateTimeOffset.UtcNow);
        catalog.Delete(second.Id);
        Assert.Equal(0, catalog.CountHistory());
    }

    [Fact]
    public void SortsAndSearchesByTheOCounter()
    {
        var none = Add("Null"); var one = Add("Eins"); var three = Add("Drei");
        catalog.AddO(one.Id, DateTimeOffset.UtcNow);
        for (var i = 0; i < 3; i++) catalog.AddO(three.Id, DateTimeOffset.UtcNow);
        var (sort, descending) = ViewState.SortFor(8);
        Assert.Equal(["Drei", "Eins", "Null"], Titles(new MediaQuery { Sort = sort, Descending = descending }));
        Assert.Equal(1, catalog.GetPosition(new MediaQuery { Sort = sort, Descending = descending }, one.Id));
        var atLeastTwo = SearchExpression.Where(new() { Field = SearchField.OCount, Comparison = SearchComparison.GreaterOrEqual, Number = 2 });
        Assert.Equal(["Drei"], Titles(new MediaQuery { Expression = atLeastTwo }));
        var watched = SearchExpression.Where(new() { Field = SearchField.WatchCount, Comparison = SearchComparison.GreaterThan, Number = 0 });
        Assert.Empty(Titles(new MediaQuery { Expression = watched }));
        Assert.NotNull(none);
    }

    [Fact]
    public void MergingDuplicatesAddsCountersAndMovesTheHistory()
    {
        var keep = Add("Behalten", plays: 2); var other = Add("Doppelt", plays: 1);
        catalog.AddO(keep.Id, DateTimeOffset.UtcNow); catalog.AddO(other.Id, DateTimeOffset.UtcNow); catalog.AddO(other.Id, DateTimeOffset.UtcNow);
        var merged = catalog.MergeDuplicateMetadata(keep.Id, [other.Id]);
        Assert.Equal(3, merged.OCount);
        // Without history yet the plays count as watched: 2 + 1.
        Assert.Equal(3, merged.WatchCount ?? merged.PlayCount);
        Assert.Equal(3, catalog.GetHistory(keep.Id).Count);
        catalog.Delete(other.Id);
        Assert.Equal(3, catalog.CountHistory());
    }

    [Fact]
    public void CoverageCountsWatchedPartsNotSeeksOrPauses()
    {
        var coverage = new PlaybackCoverage(100);
        for (var second = 0.0; second <= 30; second += .2) coverage.Observe(second);
        // A seek is not watched time.
        coverage.Observe(70);
        for (var second = 70.2; second <= 80; second += .2) coverage.Observe(second);
        // Rewatching a part adds time but no new share.
        coverage.Observe(10);
        for (var second = 10.2; second <= 20; second += .2) coverage.Observe(second);
        Assert.InRange(coverage.WatchedSeconds, 49.5, 50.5);
        Assert.InRange(coverage.Share, .39, .42);
        // Fast playback reports bigger steps; close to the end the rest counts.
        coverage.Observe(80);
        for (var second = 84.0; second <= 98; second += 4) coverage.Observe(second, speed: 2);
        Assert.InRange(coverage.Share, .59, .62);
        Assert.Equal(0, new PlaybackCoverage(null).Share);
    }

    [Fact]
    public void UpgradesAVersion11Library()
    {
        var video = catalog.Upsert(Add("Alt") with { OCount = 4 });
        catalog.Dispose(); SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            connection.CreateCollation("EFP_ORDINAL", (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
            using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP INDEX media_query_o_page; DROP TRIGGER media_query_insert; DROP TRIGGER media_query_update;
                ALTER TABLE media_query DROP COLUMN o_count; DROP TABLE play_history; PRAGMA user_version=11;
                """;
            downgrade.ExecuteNonQuery();
        }
        catalog = new CatalogRepository(path);
        Assert.Contains("before-v16-v11", catalog.MigrationBackupPath!);
        Assert.Equal(["Alt"], Titles(new MediaQuery { Expression = SearchExpression.Where(new() { Field = SearchField.OCount, Comparison = SearchComparison.Equals, Number = 4 }) }));
        Assert.Equal(5, catalog.AddO(video.Id, DateTimeOffset.UtcNow)!.OCount);
        Assert.Equal(1, catalog.CountHistory());
    }
}
