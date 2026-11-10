using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>What keeps a large library quick must give the same answers as reading everything anew.</summary>
public sealed class LargeLibraryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-large-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(directory, "catalog.db");
    private readonly CatalogRepository repository;
    private MediaItem Item(string name, MediaKind kind = MediaKind.Image, string[]? tags = null, Dictionary<string, List<string>>? groups = null) => new()
    {
        Path = Path.Combine(directory, "media", name), Title = name, Kind = kind, Tags = [.. tags ?? []], TagCategories = groups ?? new(),
    };

    public LargeLibraryTests()
    {
        Directory.CreateDirectory(directory);
        repository = new CatalogRepository(Database);
    }

    public void Dispose()
    {
        repository.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    [Fact]
    public void SettingsStayTheSameObjectUntilTheyChange()
    {
        var first = repository.GetPreferences();
        Assert.Same(first, repository.GetPreferences());
        repository.SaveViewState(new ViewState { View = "Video" });
        Assert.Same(first, repository.GetPreferences());
        var saved = repository.SavePreferences(first with { ImageFavoriteMinimumRating = 3 });
        Assert.Equal(3, repository.GetPreferences().ImageFavoriteMinimumRating);
        Assert.Equal(saved.ImageFavoriteMinimumRating, repository.GetPreferences().ImageFavoriteMinimumRating);
    }

    [Fact]
    public void SettingsDoNotWaitForALongRead()
    {
        var known = repository.GetPreferences();
        repository.SavePreferences(known with { ImageFavoriteMinimumRating = 3 });
        var current = repository.GetPreferences();
        var holding = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        var gate = typeof(CatalogRepository).GetField("gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(repository)!;
        var holder = new Thread(() => { lock (gate) { holding.Set(); release.Wait(); } });
        holder.Start(); holding.Wait();
        try { Assert.Same(current, repository.GetPreferences()); }
        finally { release.Set(); holder.Join(); }
    }

    [Fact]
    public void ListsAreKnownOnlyWhileTheyAreCurrent()
    {
        var one = repository.Upsert(Item("one.jpg", tags: ["See"]));
        Assert.False(repository.BrowsingKnown);
        _ = repository.GetFacets(); _ = repository.GetTagUsage(); _ = repository.GetNavigationCounts();
        Assert.True(repository.BrowsingKnown);
        repository.UpdateTags([one.Id], ["Berg"], []);
        Assert.True(repository.BrowsingKnown);
        repository.Upsert(Item("two.jpg"));
        Assert.False(repository.BrowsingKnown);
    }

    [Fact]
    public void IndexingForTheSearchKeepsTheLists()
    {
        var one = repository.Upsert(Item("one.jpg", tags: ["See"]));
        _ = repository.GetFacets(); _ = repository.GetTagUsage(); _ = repository.GetNavigationCounts();
        repository.UpdateTags([one.Id], ["Berg"], []);
        Assert.True(repository.BrowsingKnown);
        Assert.NotEqual(0, repository.PendingSearchIndexRows());
        repository.PrepareSearchIndex();
        Assert.True(repository.BrowsingKnown);
        repository.UpdateTags([one.Id], ["Wald"], []);
        Assert.Equal(1, repository.QueryPage(new() { Text = "wald" }).TotalCount); // indexes in the search's own transaction
        Assert.True(repository.BrowsingKnown);
    }

    [Fact]
    public void SavingSettingsKeepsTheListsAndTheSuggestions()
    {
        var one = repository.Upsert(Item("one.jpg", tags: ["See"]));
        var facets = repository.GetFacets(); _ = repository.GetTagUsage(); _ = repository.GetNavigationCounts();
        repository.SavePreferences(repository.GetPreferences() with { ImageFavoriteMinimumRating = 3 });
        Assert.True(repository.BrowsingKnown);
        Assert.Same(facets, repository.GetFacets());
        var field = typeof(CatalogRepository).GetField("cachedTagStatistics", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        repository.PrepareTagSuggestions();
        var statistics = field.GetValue(repository);
        Assert.NotNull(statistics);
        // A tag only the vocabulary knows is offered at once, with its role, and no longer once it is gone; no path is read for it.
        repository.SavePreferences(repository.GetPreferences() with { TagVocabulary = [new TagVocabularyEntry("one", "People")] });
        var offered = Assert.Single(repository.SuggestTags([one.Id], 10), suggestion => suggestion.Tag == "one");
        Assert.Equal(TagRoles.Of(repository.GetPreferences(), "People"), offered.Role);
        repository.SavePreferences(repository.GetPreferences() with { TagVocabulary = [] });
        Assert.DoesNotContain(repository.SuggestTags([one.Id], 10), suggestion => suggestion.Tag == "one");
        Assert.Same(statistics, field.GetValue(repository));
    }

    [Fact]
    public void SuggestionsKeptAcrossSettingsAnswerAsIfBuiltAnew()
    {
        var items = Enumerable.Range(0, 6).Select(i => repository.Upsert(Item($"Anna Berg {i}.jpg", tags: i % 2 == 0 ? ["See", "Berg"] : ["See"]))).ToArray();
        repository.PrepareTagSuggestions();
        var vocabulary = new[] { new TagVocabularyEntry("Anna", "People"), new TagVocabularyEntry("Wald", "Studio") with { Aliases = ["Berg"] } };
        repository.SavePreferences(repository.GetPreferences() with { TagVocabulary = [.. vocabulary] });
        var kept = items.Select(item => repository.SuggestTags([item.Id], 10)).ToArray();
        using var fresh = new CatalogRepository(Database);
        var built = items.Select(item => fresh.SuggestTags([item.Id], 10)).ToArray();
        Assert.Equal(built.Length, kept.Length);
        for (var i = 0; i < built.Length; i++) Assert.Equal(built[i], kept[i]);
    }

    [Fact]
    public void WatchLaterAnswersWithAndWithoutKeptMedia()
    {
        var later = repository.Upsert(Item("later.jpg") with { WatchLater = true });
        var other = repository.Upsert(Item("other.jpg"));
        Assert.Equal([later.Id], repository.QueryPage(new() { WatchLater = true }).Items.Select(i => i.Id));
        Assert.Equal([later.Id], repository.QueryPage(new() { WatchLater = true, Sort = MediaSort.AddedAt, Descending = true }).Items.Select(i => i.Id));
        Assert.Equal(2, repository.QueryPage(new() { WatchLater = true, KeepIds = [other.Id] }).TotalCount);
        Assert.Equal(2, repository.QueryPage(new() { WatchLater = true, KeepIds = [other.Id] }).Items.Count);
        Assert.Equal([other.Id], repository.QueryPage(new() { WatchLater = false }).Items.Select(i => i.Id));
    }

    [Fact]
    public void SettingsWrittenByAnotherConnectionAreSeen()
    {
        repository.SavePreferences(repository.GetPreferences() with { ImageFavoriteMinimumRating = 3 });
        Assert.Equal(3, repository.GetPreferences().ImageFavoriteMinimumRating);
        using (var other = new CatalogRepository(Database)) other.SavePreferences(other.GetPreferences() with { ImageFavoriteMinimumRating = 5 });
        Assert.Equal(5, repository.GetPreferences().ImageFavoriteMinimumRating);
    }

    [Fact]
    public void SettingsOfAChangeTakenBackDoNotStay()
    {
        repository.UpsertVocabularyTag(new TagVocabularyEntry("Anna", "People"));
        Assert.Single(repository.GetPreferences().TagVocabulary);
        // A preview runs the change in a transaction that is rolled back.
        repository.PreviewTagChange("Anna", null);
        Assert.Single(repository.GetPreferences().TagVocabulary);
        Assert.Equal("Anna", repository.GetPreferences().TagVocabulary[0].Tag);
    }

    /// <summary>The lists a second connection reads from the file, to compare with those carried along.</summary>
    private (CatalogFacets Facets, IReadOnlyList<TagUsage> Usage, NavigationCounts Navigation) Fresh()
    {
        using var other = new CatalogRepository(Database);
        return (other.GetFacets(), other.GetTagUsage(), other.GetNavigationCounts());
    }

    private void AssertSameAsFresh()
    {
        var fresh = Fresh();
        var facets = repository.GetFacets();
        Assert.Equal(fresh.Facets.Tags, facets.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(fresh.Facets.TagCategories, facets.TagCategories, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(fresh.Facets.CategorizedTags.Count, facets.CategorizedTags.Count);
        foreach (var (category, tags) in fresh.Facets.CategorizedTags) Assert.Equal(tags, facets.CategorizedTags[category], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(fresh.Facets.TagCategoryCounts!.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Value), facets.TagCategoryCounts!.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Value));
        Assert.Equal(fresh.Usage.Select(u => (u.Tag.ToUpperInvariant(), u.MediaCount)).Order(), repository.GetTagUsage().Select(u => (u.Tag.ToUpperInvariant(), u.MediaCount)).Order());
        Assert.Equal(fresh.Navigation, repository.GetNavigationCounts());
    }

    [Fact]
    public void ATagEditKeepsTheListsWithoutReadingThemAgain()
    {
        var one = repository.Upsert(Item("one.jpg", tags: ["See", "Berg"], groups: new() { ["People"] = ["Anna"] }));
        var two = repository.Upsert(Item("two.jpg", tags: ["see"], groups: new() { ["People"] = ["Anna", "Tom"], ["Places"] = ["Rom"] }));
        var three = repository.Upsert(Item("three.jpg", MediaKind.Audio));
        _ = repository.GetFacets(); _ = repository.GetTagUsage(); _ = repository.GetNavigationCounts();

        // A new tag; one still in use elsewhere; the last use of a tag.
        repository.UpdateTags([three.Id], ["Neu"], []); AssertSameAsFresh();
        repository.UpdateTags([one.Id], [], ["See"]); AssertSameAsFresh();
        Assert.Contains(repository.GetFacets().Tags, tag => tag.Equals("see", StringComparison.OrdinalIgnoreCase));
        repository.UpdateTags([two.Id], [], ["see"]); AssertSameAsFresh();
        Assert.DoesNotContain(repository.GetFacets().Tags, tag => tag.Equals("see", StringComparison.OrdinalIgnoreCase));

        // Categories: a tag leaves one of two media, then a category empties, then a new one arrives.
        repository.ApplyImmediateEdit([one.Id], ImmediateEditFields.Tags, item => item with { TagCategories = new() }); AssertSameAsFresh();
        repository.ApplyImmediateEdit([two.Id], ImmediateEditFields.Tags, item => item with { TagCategories = new() { ["People"] = ["Tom"] } }); AssertSameAsFresh();
        Assert.DoesNotContain("Places", repository.GetFacets().TagCategories);
        repository.ApplyImmediateEdit([three.Id, one.Id], ImmediateEditFields.Tags, item => item with { TagCategories = new() { ["Orte"] = ["Rom", "Oslo"] } }); AssertSameAsFresh();
        Assert.Equal(2, repository.GetFacets().TagCategoryCounts!["Orte"]);
    }

    [Fact]
    public void AReviewEditMovesTheCountsOfTheSidebar()
    {
        var one = repository.Upsert(Item("one.jpg"));
        _ = repository.Upsert(Item("two.mp3", MediaKind.Audio));
        _ = repository.GetNavigationCounts();
        repository.ApplyImmediateEdit([one.Id], ImmediateEditFields.Rating, item => item with { ReviewState = ReviewState.NeedsAttention, Rating = 4 });
        Assert.Equal(Fresh().Navigation, repository.GetNavigationCounts());
    }

    [Fact]
    public void ListsKeptForTheNextStartAreThoseReadAnew()
    {
        var one = repository.Upsert(Item("one.jpg", tags: ["See"], groups: new() { ["People"] = ["Anna"] }));
        repository.Upsert(Item("two.mp3", MediaKind.Audio, groups: new() { ["people"] = ["Tom"] }));
        Assert.False(repository.LoadBrowsingLists());
        KeepLists();
        using var again = new CatalogRepository(Database);
        Assert.True(again.LoadBrowsingLists());
        var kept = again.GetFacets();
        Assert.Same(kept, again.GetFacets());
        var fresh = Fresh();
        Assert.Equal(fresh.Facets.MediaCount, kept.MediaCount);
        Assert.Equal(fresh.Facets.Tags, kept.Tags);
        Assert.Equal(fresh.Facets.Folders, kept.Folders);
        Assert.Equal(fresh.Facets.FolderCounts, kept.FolderCounts);
        Assert.Equal(fresh.Facets.TagCategoryCounts, kept.TagCategoryCounts);
        Assert.Equal(fresh.Usage, again.GetTagUsage());
        Assert.Equal(fresh.Navigation, again.GetNavigationCounts());
        Assert.True(kept.CategorizedTags.ContainsKey("PEOPLE")); // Compared without regard to case, as when read anew.
    }

    private void KeepLists()
    {
        _ = repository.GetFacets(); _ = repository.GetTagUsage(); _ = repository.GetNavigationCounts();
        repository.SaveBrowsingLists();
    }

    [Fact]
    public void WhatDoesNotAffectTheListsKeepsThem()
    {
        var one = repository.Upsert(Item("one.jpg", tags: ["See"]));
        KeepLists();
        repository.ApplyImmediateEdit([one.Id], ImmediateEditFields.Rating, item => item with { Rating = 4 });
        repository.SaveViewState(new ViewState { View = "Video" });
        repository.UpdatePlaybackPosition(one.Id, 12);
        Assert.True(repository.LoadBrowsingLists());
    }

    [Fact]
    public void EverythingThatAffectsTheListsMakesThemOld()
    {
        var one = repository.Upsert(Item("one.jpg", tags: ["See"]));
        void Check(Action change)
        {
            KeepLists();
            Assert.True(repository.LoadBrowsingLists());
            change();
            Assert.False(repository.LoadBrowsingLists());
        }
        Check(() => repository.UpdateTags([one.Id], ["Berg"], []));
        Check(() => repository.ApplyImmediateEdit([one.Id], ImmediateEditFields.Tags, item => item with { TagCategories = new() { ["People"] = ["Anna"] } }));
        Check(() => repository.Upsert(repository.GetMedia(one.Id)! with { Category = "Natur" }));
        Check(() => repository.Upsert(repository.GetMedia(one.Id)! with { ReviewState = ReviewState.Reviewed }));
        Check(() => repository.Upsert(Item("two.jpg")));
        Check(() => repository.Delete(one.Id));
        // Another SQLite writer needs no knowledge of the lists.
        var three = repository.Upsert(Item("three.jpg"));
        KeepLists();
        using (var sql = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            sql.Open(); CatalogRepository.AddOrdinalCollation(sql);
            using var command = sql.CreateCommand();
            command.CommandText = "UPDATE media SET document=json_set(document,'$.path','X:/moved.jpg') WHERE id=$id"; command.Parameters.AddWithValue("$id", three.Id.ToString());
            command.ExecuteNonQuery();
        }
        Assert.False(repository.LoadBrowsingLists());
    }

    [Fact]
    public void DamagedKeptListsAreReadAnew()
    {
        repository.Upsert(Item("one.jpg"));
        KeepLists();
        using (var sql = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            sql.Open();
            using var command = sql.CreateCommand(); command.CommandText = "UPDATE browsing_lists SET document='{not json'"; command.ExecuteNonQuery();
        }
        Assert.False(repository.LoadBrowsingLists());
        Assert.Equal(1, repository.GetFacets().MediaCount);
    }

    [Theory]
    [InlineData(MediaSort.Title, false)] [InlineData(MediaSort.Title, true)] [InlineData(MediaSort.AddedAt, true)] [InlineData(MediaSort.Rating, true)]
    [InlineData(MediaSort.Duration, false)] [InlineData(MediaSort.FileDate, true)] [InlineData(MediaSort.OCount, true)] [InlineData(MediaSort.Bitrate, true)]
    public void PagesKeepTheOrderInEveryFilter(MediaSort sort, bool descending)
    {
        for (var i = 0; i < 40; i++)
            repository.Upsert(new MediaItem { Path = Path.Combine(directory, "media", $"{i}.jpg"), Title = $"t{i % 7}", Kind = i % 3 == 0 ? MediaKind.Video : MediaKind.Image, Rating = i % 11, DurationSeconds = i % 5, WatchLater = i % 4 == 0, IsFavorite = i % 9 == 0, OCount = i % 3, AddedAt = DateTimeOffset.UtcNow.AddHours(-(i % 6)) });
        foreach (var filter in new MediaQuery[] { new(), new() { Kind = MediaKind.Video }, new() { Kind = MediaKind.Image }, new() { WatchLater = true }, new() { IsFavorite = true }, new() { MinimumRating = 6 } })
        {
            var query = filter with { Sort = sort, Descending = descending };
            var whole = repository.QueryPage(query with { Limit = 1000 });
            var pieces = repository.QueryPage(query with { Limit = 5 }).Items.Concat(repository.QueryItems(query with { Limit = 995, Offset = 5 }));
            Assert.Equal(whole.Items.Select(i => i.Id), pieces.Select(i => i.Id));
            Assert.Equal(whole.TotalCount, repository.CountMedia(query));
            Assert.Equal(whole.Items.Select(i => i.Id).Order(), repository.Search().Where(i => Matches(i, filter)).Select(i => i.Id).Order());
        }
        bool Matches(MediaItem item, MediaQuery filter) =>
            (filter.Kind is null || item.Kind == filter.Kind) && (filter.WatchLater is null || item.WatchLater == filter.WatchLater) && item.Rating >= filter.MinimumRating
            && (filter.IsFavorite is null || repository.IsFavorite(item));
    }

    [Fact]
    public void ItemsWithoutCountingAreThoseOfThePage()
    {
        foreach (var name in new[] { "c.jpg", "a.jpg", "b.jpg" }) repository.Upsert(Item(name));
        var query = new MediaQuery { Offset = 1, Limit = 1 };
        Assert.Equal(repository.QueryPage(query).Items.Select(i => i.Id), repository.QueryItems(query).Select(i => i.Id));
    }

    [Fact]
    public void AMediumIsFoundAtItsPlace()
    {
        foreach (var name in new[] { "c.jpg", "a.jpg", "b.jpg" }) repository.Upsert(Item(name));
        var query = new MediaQuery();
        var second = repository.QueryPage(query with { Offset = 1, Limit = 1 }).Items[0];
        Assert.True(repository.IsAtPosition(query, 1, second.Id));
        Assert.False(repository.IsAtPosition(query, 0, second.Id));
        Assert.False(repository.IsAtPosition(query, 7, second.Id));
    }

    [Fact]
    public void FolderCheckNotesFirstStatesOnlyInTheFoldersAndLeavesMediaAlone()
    {
        var inside = Path.Combine(directory, "watched"); var outside = Path.Combine(directory, "other");
        Directory.CreateDirectory(inside); Directory.CreateDirectory(outside);
        var file = Path.Combine(inside, "a.jpg"); var elsewhere = Path.Combine(outside, "b.jpg");
        File.WriteAllBytes(file, [1, 2, 3]); File.WriteAllBytes(elsewhere, [1, 2, 3]);
        repository.Upsert(new MediaItem { Path = file, Title = "a", Kind = MediaKind.Image });
        repository.Upsert(new MediaItem { Path = elsewhere, Title = "b", Kind = MediaKind.Image });
        Assert.Empty(repository.PlanFolderSync([new LibraryFolder { Path = inside }]).Changes);
        // The entries themselves stay as they were: rewriting them would also queue them for the search index again.
        Assert.Null(repository.GetMediaByPath(file)!.FileModifiedAtUtc);
        Assert.Null(repository.GetMediaByPath(file)!.FileSize);
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(1));
        File.SetLastWriteTimeUtc(elsewhere, File.GetLastWriteTimeUtc(elsewhere).AddMinutes(1));
        Assert.Equal(file, Assert.Single(repository.PlanFolderSync([new LibraryFolder { Path = inside }]).Changes).Path);
        // A folder checked for the first time takes what it finds as its starting point.
        Assert.Empty(repository.PlanFolderSync([new LibraryFolder { Path = outside }]).Changes);
    }

    [Fact]
    public void FolderCheckStillListsWhatLiesInAFolder()
    {
        var inside = Path.Combine(directory, "watched"); Directory.CreateDirectory(inside);
        var kept = Path.Combine(inside, "a.jpg"); var gone = Path.Combine(inside, "gone.jpg");
        File.WriteAllBytes(kept, [1, 2, 3]);
        repository.Upsert(new MediaItem { Path = kept, Title = "a", Kind = MediaKind.Image });
        repository.Upsert(new MediaItem { Path = gone, Title = "gone", Kind = MediaKind.Image });
        var plan = repository.PlanFolderSync([new LibraryFolder { Path = inside }]);
        Assert.Contains(plan.Changes, change => change.Kind == FolderChangeKind.Missing && change.Path == gone);
        Assert.DoesNotContain(plan.Changes, change => change.Path == kept && change.Kind is FolderChangeKind.New or FolderChangeKind.Missing);
    }
}
