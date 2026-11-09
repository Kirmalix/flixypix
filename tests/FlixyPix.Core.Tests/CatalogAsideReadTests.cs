using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>The reads a start leaves for later go another way than the usual calls; they must give the same answers.</summary>
public sealed class CatalogAsideReadTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-aside-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogRepository repository;

    public CatalogAsideReadTests()
    {
        Directory.CreateDirectory(directory);
        repository = new CatalogRepository(Path.Combine(directory, "catalog.db"));
        MediaItem Item(string name, MediaKind kind, params string[] tags) => new()
        {
            Path = Path.Combine(directory, "one", name), Title = name, Kind = kind, Tags = [.. tags], IsFavorite = name.StartsWith('f'),
            WatchLater = name.StartsWith('w'), Category = "Natur",
        };
        repository.Upsert(Item("f1.jpg", MediaKind.Image, "See", "Berg"));
        repository.Upsert(Item("w2.jpg", MediaKind.Image, "see"));
        repository.Upsert(Item("a3.mp4", MediaKind.Video, "Berg", " "));
        repository.Upsert(new MediaItem { Path = Path.Combine(directory, "two", "a4.mp4"), Title = "a4", Kind = MediaKind.Video });
    }

    public void Dispose()
    {
        repository.Dispose();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    [Fact]
    public void CountingGivesTheNumberOfThePage()
    {
        foreach (var query in new MediaQuery[] { new() { Limit = 1 }, new() { Kind = MediaKind.Image, Limit = 1 }, new() { WatchLater = true, Limit = 1 }, new() { Kind = MediaKind.Audio, Limit = 1 } })
            Assert.Equal(repository.QueryPage(query).TotalCount, repository.CountMedia(query));
    }

    [Fact]
    public void QuickFacetsKnowTheNumberAndNoListsUntilTheyAreRead()
    {
        var quick = repository.QuickFacets();
        Assert.Equal(4, quick.MediaCount);
        Assert.Empty(quick.Folders); Assert.Empty(quick.Tags);
        var full = repository.GetFacets();
        Assert.Same(full, repository.QuickFacets());
    }

    [Fact]
    public void ReadingBesideGivesTheSameListsAndKeepsThem()
    {
        Assert.True(repository.LoadBrowsingAside());
        var aside = repository.GetFacets();
        Assert.Same(aside, repository.GetFacets());
        var usage = repository.GetTagUsage(); var navigation = repository.GetNavigationCounts();
        using var other = new CatalogRepository(Path.Combine(directory, "catalog.db"));
        var usual = other.GetFacets();
        Assert.Equal(usual.MediaCount, aside.MediaCount);
        Assert.Equal(usual.Tags, aside.Tags);
        Assert.Equal(usual.Folders, aside.Folders);
        Assert.Equal(usual.Categories, aside.Categories);
        Assert.Equal(usual.FolderCounts, aside.FolderCounts);
        Assert.Equal(other.GetNavigationCounts(), navigation);
        Assert.Equal(other.GetTagUsage(), usage);
    }

    [Fact]
    public void AChangeAfterwardsIsSeen()
    {
        repository.LoadBrowsingAside();
        repository.Upsert(new MediaItem { Path = Path.Combine(directory, "three", "n.jpg"), Title = "n", Kind = MediaKind.Image, Tags = ["Neu"] });
        Assert.Equal(5, repository.GetFacets().MediaCount);
        Assert.Contains(repository.GetTagUsage(), usage => usage.Tag == "Neu");
        Assert.Contains(repository.GetFacets().Folders, folder => folder.EndsWith("three", StringComparison.Ordinal));
    }

    [Fact]
    public void SavingTheViewMeanwhileKeepsWhatWasRead()
    {
        Assert.True(repository.LoadBrowsingAside(default, () => repository.SaveViewState(new ViewState { View = "Video" })));
        var kept = repository.QuickFacets();
        Assert.Equal(4, kept.MediaCount);
        Assert.NotEmpty(kept.Folders);
        Assert.Equal("Video", repository.GetViewState()?.View);
    }

    [Fact]
    public void AChangeWhileReadingBesideIsNotKept()
    {
        Assert.False(repository.LoadBrowsingAside(default, () => repository.Upsert(new MediaItem { Path = Path.Combine(directory, "three", "n.jpg"), Title = "n", Kind = MediaKind.Image, Tags = ["Neu"] })));
        Assert.Equal(5, repository.GetFacets().MediaCount);
        Assert.Contains(repository.GetTagUsage(), usage => usage.Tag == "Neu");
    }

    [Fact]
    public void TagUsageIsTheSameWithAndWithoutTheStatistics()
    {
        var alone = repository.GetTagUsage();
        Assert.Equal(2, alone.Single(usage => usage.Tag.Equals("See", StringComparison.OrdinalIgnoreCase)).MediaCount);
        Assert.Equal(2, alone.Single(usage => usage.Tag == "Berg").MediaCount);
        Assert.DoesNotContain(alone, usage => string.IsNullOrWhiteSpace(usage.Tag));
        var item = repository.Search().First(media => media.Title == "w2.jpg");
        repository.PrepareTagSuggestions();
        var prepared = repository.SuggestTags([item.Id], 10);
        Assert.Equal(alone, repository.GetTagUsage());
        Assert.Contains(prepared, suggestion => suggestion.Tag == "Berg");
    }

    [Fact]
    public void ASavedSearchCountsItsHitsBesideTheConnection()
    {
        Assert.Equal((1L, 0L), repository.CountSavedSearch(new SavedSearch { IsFavorite = true }, DateTimeOffset.Now));
        Assert.Equal((1L, 0L), repository.CountSavedSearch(new SavedSearch { WatchLater = true }, DateTimeOffset.Now));
        Assert.Equal((4L, 4L), repository.CountSavedSearch(new SavedSearch { LastOpenedAt = DateTimeOffset.Now.AddDays(-1) }, DateTimeOffset.Now));
    }
}
