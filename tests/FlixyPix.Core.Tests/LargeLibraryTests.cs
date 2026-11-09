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
    public void FolderReferencesReachOnlyMediaInTheFolders()
    {
        var inside = Path.Combine(directory, "watched"); var outside = Path.Combine(directory, "other");
        Directory.CreateDirectory(inside); Directory.CreateDirectory(outside);
        var file = Path.Combine(inside, "a.jpg"); var elsewhere = Path.Combine(outside, "b.jpg");
        File.WriteAllBytes(file, [1, 2, 3]); File.WriteAllBytes(elsewhere, [1, 2, 3]);
        repository.Upsert(new MediaItem { Path = file, Title = "a", Kind = MediaKind.Image });
        repository.Upsert(new MediaItem { Path = elsewhere, Title = "b", Kind = MediaKind.Image });
        repository.InitializeFolderReferences([new LibraryFolder { Path = inside }]);
        Assert.NotNull(repository.GetMediaByPath(file)!.FileModifiedAtUtc);
        Assert.Equal(3, repository.GetMediaByPath(file)!.FileSize);
        Assert.Null(repository.GetMediaByPath(elsewhere)!.FileModifiedAtUtc);
        // Without folders nothing happens, and a second pass changes nothing.
        repository.InitializeFolderReferences([]);
        var stamp = repository.GetMediaByPath(file)!.FileModifiedAtUtc;
        repository.InitializeFolderReferences([new LibraryFolder { Path = inside }]);
        Assert.Equal(stamp, repository.GetMediaByPath(file)!.FileModifiedAtUtc);
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
