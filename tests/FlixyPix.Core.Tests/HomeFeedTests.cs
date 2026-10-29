using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class HomeFeedTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-home-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogRepository catalog;
    public HomeFeedTests() { Directory.CreateDirectory(directory); catalog = new CatalogRepository(Path.Combine(directory, "catalog.db")); }
    public void Dispose() { catalog.Dispose(); SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }

    private MediaItem Video(string title, int rating = 0, string[]? tags = null, double position = 0, int addedDaysAgo = 1, Dictionary<string, List<string>>? categories = null, bool later = false) =>
        catalog.Upsert(new()
        {
            Path = Path.Combine(directory, title + ".mp4"), Title = title, Kind = MediaKind.Video, DurationSeconds = 1000, PlaybackPositionSeconds = position, Rating = rating,
            Tags = [.. tags ?? []], TagCategories = categories ?? [], AddedAt = Now.AddDays(-addedDaysAgo), WatchLater = later, ThumbnailPath = Path.Combine(directory, title + ".jpg"),
        });

    private HomeRow Row(HomeFeed feed, HomeRowKind kind) => feed.Rows.Single(r => r.Kind == kind);

    [Fact]
    public void PlaysAreCountedAndStartedVideosContinueNewestFirst()
    {
        var a = Video("A", position: 300); var b = Video("B", position: 500); Video("Fertig", position: 990); Video("Neu");
        catalog.RecordPlay(a.Id, Now.AddDays(-3)); catalog.RecordPlay(b.Id, Now.AddDays(-1)); catalog.RecordPlay(b.Id, Now.AddHours(-1));
        Assert.Equal((2L, Now.AddHours(-1)), (catalog.GetMedia(b.Id)!.PlayCount!.Value, catalog.GetMedia(b.Id)!.LastPlayedAt!.Value));
        var feed = catalog.BuildHomeFeed(Now);
        var row = Row(feed, HomeRowKind.ContinueWatching);
        Assert.Equal(["B", "A"], row.Cards.Select(c => c.Item.Title));
        Assert.Equal("bei 8:20", row.Cards[0].Reason);
        Assert.Equal("B", feed.Hero[0].Item.Title);
    }

    [Fact]
    public void SuggestionsFollowRecentTagsSkipWhatWasJustSeenAndVary()
    {
        var seen = Video("Gesehen", tags: ["Strand", "Sommer"]);
        catalog.RecordPlay(seen.Id, Now.AddDays(-1));
        for (var i = 0; i < 4; i++) Video("Strand " + i, rating: 6, tags: ["Strand"]);
        Video("Sommer", tags: ["Sommer", "Stadt"]); Video("Fremd", rating: 10, tags: ["Berg"]);
        var cards = Row(catalog.BuildHomeFeed(Now), HomeRowKind.ForYou).Cards;
        Assert.DoesNotContain(cards, c => c.Item.Title is "Gesehen" or "Fremd");
        Assert.Equal(2, cards.Count(c => c.Item.Title.StartsWith("Strand", StringComparison.Ordinal)));
        Assert.Contains(cards, c => c.Item.Title == "Sommer" && c.Reason == "Weil: Sommer");
    }

    [Fact]
    public void WithoutHistoryGoodUnseenMediaAreSuggestedAndRowsFollowThePreferences()
    {
        Video("Gut", rating: 8); Video("Mäßig", rating: 2); Video("Merkliste", later: true, addedDaysAgo: 30);
        var feed = catalog.BuildHomeFeed(Now);
        Assert.Equal(["Gut"], Row(feed, HomeRowKind.ForYou).Cards.Select(c => c.Item.Title));
        Assert.Equal(["Merkliste"], Row(feed, HomeRowKind.WatchLater).Cards.Select(c => c.Item.Title));
        Assert.Equal("Merkliste", Row(feed, HomeRowKind.RecentlyAdded).Cards.Last().Item.Title);
        Assert.DoesNotContain(feed.Rows, r => r.Kind == HomeRowKind.ContinueWatching);
        catalog.SavePreferences(catalog.GetPreferences() with { Home = new() { Rows = [HomeRowKind.WatchLater, HomeRowKind.RecentlyAdded] } });
        Assert.Equal([HomeRowKind.WatchLater, HomeRowKind.RecentlyAdded], catalog.BuildHomeFeed(Now).Rows.Select(r => r.Kind));
        // The random row stays the same over the day and changes the next day.
        for (var i = 0; i < 40; i++) Video("Zufall " + i);
        catalog.SavePreferences(catalog.GetPreferences() with { Home = new() { Rows = [HomeRowKind.RandomOfTheDay] } });
        var morning = Row(catalog.BuildHomeFeed(Now.AddHours(-3)), HomeRowKind.RandomOfTheDay).Cards.Select(c => c.Item.Id).ToList();
        Assert.Equal(morning, Row(catalog.BuildHomeFeed(Now.AddHours(3)), HomeRowKind.RandomOfTheDay).Cards.Select(c => c.Item.Id));
        Assert.NotEqual(morning, Row(catalog.BuildHomeFeed(Now.AddDays(1)), HomeRowKind.RandomOfTheDay).Cards.Select(c => c.Item.Id));
    }

    [Fact]
    public void PerformersStudiosPopularRediscoverAndTagRows()
    {
        Video("Jane 1", rating: 9, categories: new() { ["Darsteller"] = ["Jane"], ["Studios"] = ["Acme"] }, tags: ["Strand"]);
        Video("Jane 2", rating: 4, categories: new() { ["Darsteller"] = ["Jane"] }, tags: ["Strand"]);
        Video("John", rating: 7, categories: new() { ["Darsteller"] = ["John"] }, tags: ["Strand"]);
        var old = Video("Alt", rating: 10, tags: ["Strand"]); catalog.RecordPlay(old.Id, Now.AddYears(-1));
        catalog.UpsertVocabularyTag(new("John", "Darsteller", IsFavorite: true));
        var feed = catalog.BuildHomeFeed(Now);
        var performers = Row(feed, HomeRowKind.FavoritePerformers);
        Assert.Equal("Lieblingsdarsteller", performers.Title);
        Assert.Equal(["John", "Jane"], performers.Entities.Select(e => e.Entry.Tag));
        Assert.Equal("Jane 1", performers.Entities[1].Cover!.Title);
        Assert.Equal(["Acme"], Row(feed, HomeRowKind.Studios).Entities.Select(e => e.Entry.Tag));
        Assert.Equal(["Alt", "Jane 1", "John"], Row(feed, HomeRowKind.Popular).Cards.Select(c => c.Item.Title));
        Assert.Contains(Row(feed, HomeRowKind.Rediscover).Cards, c => c.Item.Title == "Alt" && c.Reason!.StartsWith("zuletzt", StringComparison.Ordinal));
        var tagRow = feed.Rows.First(r => r.Kind == HomeRowKind.MoreWithTag);
        Assert.Equal(("Mehr mit Strand", "Strand"), (tagRow.Title, tagRow.Tag));
        Assert.Equal(["Alt", "Jane 1", "John", "Jane 2"], tagRow.Cards.Select(c => c.Item.Title));
    }

    [Fact]
    public void OriginalHomeSectionsChooseTheirRows()
    {
        var path = Path.Combine(directory, "legacy.jsonl");
        File.WriteAllLines(path, [
            JsonSerializer.Serialize(new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('c', 64), sourceName = "fixture.sdf" }),
            JsonSerializer.Serialize(new { type = "schema", table = "configsettings", columns = new[] { new { name = "ID" }, new { name = "parameter" }, new { name = "value1" } } }),
            JsonSerializer.Serialize(new { type = "row", table = "configsettings", values = new { ID = 1, parameter = "homevideosections", value1 = "24" } }),
            JsonSerializer.Serialize(new { type = "footer", tables = 1, rows = 1, tableRows = new Dictionary<string, int> { ["configsettings"] = 1 } })]);
        Assert.Empty(catalog.ImportLegacyJsonl(path).Errors);
        var rows = catalog.GetPreferences().Home.Rows;
        Assert.DoesNotContain(HomeRowKind.ContinueWatching, rows); Assert.DoesNotContain(HomeRowKind.WatchLater, rows);
        Assert.Contains(HomeRowKind.RecentlyAdded, rows); Assert.Contains(HomeRowKind.FavoritePerformers, rows); Assert.Contains(HomeRowKind.ForYou, rows);
    }
}
