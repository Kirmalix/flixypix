using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class MediaFilterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-filter-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogRepository catalog;

    public MediaFilterTests()
    {
        Directory.CreateDirectory(directory);
        catalog = new CatalogRepository(Path.Combine(directory, "catalog.db"));
        MediaItem Video(string title, double minutes, int width, int height, int years, int importedDaysAgo, long plays, params (string Category, string Tag)[] tags) => new()
        {
            Path = Path.Combine(directory, title + ".mp4"), Title = title, Kind = MediaKind.Video, DurationSeconds = minutes * 60, Width = width, Height = height,
            FileSize = (long)(minutes * 10 * 1024 * 1024), FileCreatedAt = Now.AddYears(-years), AddedAt = Now.AddDays(-importedDaysAgo), PlayCount = plays,
            Tags = tags.Where(t => t.Category.Length == 0).Select(t => t.Tag).ToList(),
            TagCategories = tags.Where(t => t.Category.Length > 0).GroupBy(t => t.Category).ToDictionary(g => g.Key, g => g.Select(t => t.Tag).ToList()),
        };
        catalog.Upsert(Video("Kurz", 4, 1280, 720, 0, 2, 0, ("Darsteller", "Jane"), ("Studios", "Acme")));
        catalog.Upsert(Video("Mittel", 25, 1920, 1080, 3, 40, 2, ("Darsteller", "Jane"), ("Darsteller", "John"), ("", "Strand")));
        catalog.Upsert(Video("Lang", 95, 3840, 2160, 6, 400, 0, ("Darsteller", "John"), ("Studios", "Bolt")));
        catalog.Upsert(Video("Alt", 50, 640, 480, 10, 1000, 1, ("Studios", "Acme"), ("", "Strand")));
    }
    public void Dispose() { catalog.Dispose(); SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }

    private string[] Titles(MediaFilter filter, ViewState? state = null) =>
        catalog.QueryPage(((state ?? new ViewState()) with { Filter = filter }).ToQuery(Now)).Items.Select(i => i.Title).Order(StringComparer.Ordinal).ToArray();

    private static TagFacet Performer(string name) => new("Darsteller", name, TagRole.Performer);
    private static TagFacet Studio(string name) => new("Studios", name, TagRole.Studio);

    [Fact]
    public void PerformersAndStudiosCombineAllOrAnyAndExclude()
    {
        Assert.Equal(["Mittel"], Titles(new() { Include = [Performer("Jane"), Performer("John")] }));
        Assert.Equal(["Kurz", "Lang", "Mittel"], Titles(new() { Include = [Performer("Jane"), Performer("John")], AnyOf = [TagRole.Performer] }));
        // Groups combine with AND: performers (any) and a studio.
        Assert.Equal(["Kurz"], Titles(new() { Include = [Performer("Jane"), Performer("John"), Studio("Acme")], AnyOf = [TagRole.Performer] }));
        Assert.Equal(["Alt", "Kurz"], Titles(new() { Exclude = [Performer("John")] }));
        Assert.Equal(["Alt"], Titles(new() { Exclude = [Performer("John"), Performer("Jane")] }));
        Assert.Equal(["Alt", "Mittel"], Titles(new() { Include = [new("", "Strand", TagRole.Tag)] }));
    }

    [Fact]
    public void RangesResolutionAndWatchedStateFilter()
    {
        Assert.Equal(["Mittel"], Titles(new() { MinMinutes = 5, MaxMinutes = 30 }));
        Assert.Equal(["Alt", "Lang"], Titles(new() { MinMinutes = 45 }));
        Assert.Equal(["Kurz"], Titles(new() { MaxMegabytes = 100 }));
        Assert.Equal(["Lang", "Mittel"], Titles(new() { Resolution = ResolutionClass.FullHD }));
        Assert.Equal(["Lang"], Titles(new() { Resolution = ResolutionClass.UHD }));
        Assert.Equal(["Kurz", "Lang"], Titles(new() { Watched = false }));
        Assert.Equal(["Alt", "Mittel"], Titles(new() { Watched = true }));
    }

    [Fact]
    public void FileDateAndImportDateUseRelativeAndFixedWindows()
    {
        Assert.Equal(["Kurz"], Titles(new() { FileDate = new(DateWindow.LastYear) }));
        Assert.Equal(["Alt", "Lang"], Titles(new() { FileDate = new(DateWindow.OlderThan5Years) }));
        Assert.Equal(["Kurz", "Mittel"], Titles(new() { Imported = new(DateWindow.LastYear) }));
        Assert.Equal(["Kurz"], Titles(new() { Imported = new(DateWindow.Last7Days) }));
        Assert.Equal(["Alt", "Lang"], Titles(new() { Imported = new(DateWindow.OlderThanYear) }));
        // A fixed "to" date includes that whole day.
        var day = new DateTimeOffset(Now.AddYears(-3).Date, TimeSpan.Zero);
        Assert.Equal(["Mittel"], Titles(new() { FileDate = new(DateWindow.Custom, day, day) }));
        Assert.Equal(["Alt", "Lang", "Mittel"], Titles(new() { FileDate = new(DateWindow.Custom, null, day) }));
        Assert.True(new MediaFilter().IsEmpty); Assert.False(new MediaFilter { FileDate = new(DateWindow.Custom, day) }.IsEmpty);
        Assert.True(new MediaFilter { FileDate = new(DateWindow.Custom) }.IsEmpty);
    }

    [Fact]
    public void FilterDescribesItselfForChips()
    {
        var filter = new MediaFilter { Include = [Performer("Jane")], Exclude = [Studio("Bolt")], MinMinutes = 5, MaxMinutes = 30, Resolution = ResolutionClass.UHD,
            FileDate = new(DateWindow.OlderThan2Years), Watched = false };
        Assert.Equal(["Jane", "ohne Bolt", "5–30 min", "ab 4K", "Datei älter als 2 Jahre", "nie gesehen"], filter.Describe());
    }

    [Fact]
    public void ViewStateCombinesTagModesAndSortChoices()
    {
        var state = new ViewState { RequiredTags = ["Strand"], View = "Video", SortOrder = 7 };
        Assert.Equal(["Alt", "Mittel"], Titles(new(), state));
        Assert.Equal(["Alt", "Kurz", "Mittel"], Titles(new(), state with { RequiredTags = ["Strand", "Jane"], TagsMatchAny = true }));
        Assert.Equal(["Mittel"], Titles(new(), state with { RequiredTags = ["Strand", "Jane"] }));
        Assert.Equal(["Kurz", "Mittel", "Lang", "Alt"], catalog.QueryPage((state with { RequiredTags = [] }).ToQuery(Now)).Items.Select(i => i.Title));
        Assert.Equal((MediaSort.FileDate, false), ViewState.SortFor(6));
        Assert.Equal((MediaSort.Title, false), ViewState.SortFor(5));
    }

    [Fact]
    public void SavedSearchesKeepTheWholeViewCountNewMediaAndFollowRenames()
    {
        var state = new ViewState { View = "Video", Filter = new() { Include = [Performer("Jane")], MaxMinutes = 30 }, SortOrder = 3 };
        var search = catalog.UpsertSavedSearch(new SavedSearch { Name = "Jane kurz", State = state, Glyph = "" });
        Assert.Equal((2L, 0L), catalog.CountSavedSearch(search, Now));
        catalog.MarkSavedSearchOpened(search.Id, Now.AddDays(-5));
        search = catalog.GetSavedSearches().Single();
        Assert.Equal((2L, 1L), catalog.CountSavedSearch(search, Now));
        Assert.Equal(["Kurz", "Mittel"], catalog.SearchSaved(search.Id).Select(i => i.Title).Order(StringComparer.Ordinal));
        // Older searches without a view still work.
        var legacy = catalog.UpsertSavedSearch(new SavedSearch { Name = "Alt", Tag = "Strand", MinimumRating = 0, Order = -1 });
        Assert.Equal(["Alt", "Jane kurz"], catalog.GetSavedSearches().Select(s => s.Name));
        Assert.Equal((2L, 0L), catalog.CountSavedSearch(legacy, Now));
        // Renaming and deleting tags reach the saved view.
        catalog.ChangeTag("Jane", "Jane Doe");
        Assert.Equal("Jane Doe", catalog.GetSavedSearches().Single(s => s.Name == "Jane kurz").State!.Filter.Include.Single().Tag);
        Assert.Equal(2, catalog.SearchSaved(search.Id).Count);
        catalog.DeleteTags(["Jane Doe"]);
        Assert.Empty(catalog.SearchSaved(search.Id));
        Assert.Throws<ArgumentException>(() => catalog.UpsertSavedSearch(new SavedSearch { Name = "kaputt", State = new ViewState { Filter = null! } }));
    }
}
