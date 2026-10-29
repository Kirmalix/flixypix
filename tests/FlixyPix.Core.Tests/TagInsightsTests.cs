using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class TagInsightsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-tags-" + Guid.NewGuid().ToString("N"));
    private CatalogRepository Open() => new(Path.Combine(directory, "catalog.db"));
    private MediaItem Item(string path, params string[] tags) => new() { Path = Path.Combine(directory, path), Title = Path.GetFileNameWithoutExtension(path), Kind = MediaKind.Image, Tags = tags.ToList() };

    public TagInsightsTests() => Directory.CreateDirectory(directory);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }

    [Fact]
    public void UsageCountsIgnoreCaseAndFollowWrites()
    {
        using var catalog = Open();
        var first = catalog.Upsert(Item("a/1.jpg", "See", "Natur"));
        catalog.Upsert(Item("a/2.jpg", "see"));
        var usage = catalog.GetTagUsage();
        Assert.Equal(("See", 2), (usage[0].Tag, usage[0].MediaCount));
        Assert.Equal(1, Assert.Single(usage, u => u.Tag == "Natur").MediaCount);
        catalog.Delete(first.Id);
        Assert.DoesNotContain(catalog.GetTagUsage(), u => u.Tag == "Natur");
    }

    [Fact]
    public void RequiredAndExcludedTagsCombine()
    {
        using var catalog = Open();
        var both = catalog.Upsert(Item("a/1.jpg", "Natur", "See"));
        catalog.Upsert(Item("a/2.jpg", "Natur"));
        var categorized = catalog.Upsert(Item("a/3.jpg", "Natur") with { TagCategories = new() { ["Orte"] = ["See"] } });
        Assert.Equal(2, catalog.QueryPage(new() { RequiredTags = ["natur", "SEE"] }).TotalCount);
        Assert.Equal([both.Id, categorized.Id], catalog.QueryPage(new() { RequiredTags = ["Natur", "See"], Sort = MediaSort.Path }).Items.Select(i => i.Id));
        Assert.Single(catalog.QueryPage(new() { RequiredTags = ["Natur"], ExcludedTags = ["see"] }).Items);
        Assert.Equal(0, catalog.QueryPage(new() { ExcludedTags = ["Natur"] }).TotalCount);
    }

    [Fact]
    public void BulkTagUpdateAddsOnceAndRemovesFromCategories()
    {
        using var catalog = Open();
        var one = catalog.Upsert(Item("a/1.jpg", "Natur") with { TagCategories = new() { ["Orte"] = ["See", "Berg"] } });
        var two = catalog.Upsert(Item("a/2.jpg", "natur", "See"));
        var untouched = catalog.Upsert(Item("a/3.jpg", "See"));
        Assert.Equal(2, catalog.UpdateTags([one.Id, two.Id], ["Natur", "Urlaub"], ["see"]));
        var updated = catalog.GetMedia(one.Id)!;
        // Categorized tags are also kept as flat tags; removal clears both.
        Assert.Equal(["Natur", "Berg", "Urlaub"], updated.Tags);
        Assert.Equal(["Berg"], updated.TagCategories["Orte"]);
        Assert.Equal(["natur", "Urlaub"], catalog.GetMedia(two.Id)!.Tags);
        Assert.Equal(["See"], catalog.GetMedia(untouched.Id)!.Tags);
        Assert.Equal(0, catalog.UpdateTags([one.Id], ["Urlaub"], []));
        Assert.Throws<ArgumentException>(() => catalog.UpdateTags([one.Id], ["X"], ["x"]));
    }

    [Fact]
    public void SuggestionsExplainCoOccurrenceFolderAndNameEvidence()
    {
        using var catalog = Open();
        for (var i = 0; i < 4; i++) catalog.Upsert(Item($"reise/{i}.jpg", "Strand", "Meer", i < 3 ? "Sonne" : "Wolken"));
        var target = catalog.Upsert(Item("reise/neu.jpg", "Strand"));
        catalog.SavePreferences(catalog.GetPreferences() with { TagVocabulary = [new("Sommerfest", "Anlässe")] });
        var named = catalog.Upsert(Item("Sommerfest 2019/bild.jpg"));
        var suggestions = catalog.SuggestTags([target.Id]);
        var meer = Assert.Single(suggestions, s => s.Tag == "Meer");
        Assert.Equal(TagSuggestionSource.CoOccurrence, meer.Source);
        Assert.Contains("80 %", meer.Reason.Replace(' ', ' '));
        Assert.Equal("Meer", suggestions[0].Tag);
        Assert.DoesNotContain(suggestions, s => s.Tag == "Wolken"); // seen together only once
        Assert.DoesNotContain(suggestions, s => s.Tag == "Strand");
        var byName = Assert.Single(catalog.SuggestTags([named.Id]), s => s.Tag == "Sommerfest");
        Assert.Equal(TagSuggestionSource.Name, byName.Source);
        var untagged = catalog.Upsert(Item("reise/leer.jpg"));
        Assert.Contains(catalog.SuggestTags([untagged.Id]), s => s.Tag == "Strand" && s.Source == TagSuggestionSource.Folder);
    }

    [Fact]
    public void MultiSelectionSuggestsTagsMissingOnSomeMedia()
    {
        using var catalog = Open();
        var a = catalog.Upsert(Item("x/a.jpg", "Familie", "Garten"));
        var b = catalog.Upsert(Item("y/b.jpg", "Familie"));
        catalog.Upsert(Item("z/c.jpg", "Familie", "Garten"));
        var suggestions = catalog.SuggestTags([a.Id, b.Id]);
        Assert.Contains(suggestions, s => s.Tag == "Garten");
        Assert.DoesNotContain(suggestions, s => s.Tag == "Familie");
    }

    /// <summary>Tag edits and simple field edits update the facet and tag-statistics caches in place; the result must equal a full recount.</summary>
    [Fact]
    public void CachesCarriedAcrossEditsEqualAFullRecount()
    {
        using var catalog = Open();
        var media = new List<MediaItem>();
        for (var i = 0; i < 6; i++) media.Add(catalog.Upsert(Item($"reise/{i}.jpg", "Strand", "Meer", i % 2 == 0 ? "Sonne" : "Wolken") with { Category = "Urlaub" }));
        media.Add(catalog.Upsert(Item("haus/a.jpg", "Garten") with { TagCategories = new() { ["Orte"] = ["Terrasse"] } }));
        media.Add(catalog.Upsert(Item("haus/b.jpg")));
        catalog.SavePreferences(catalog.GetPreferences() with { TagVocabulary = [new("Sommerfest", "Anlässe")] });
        static string Snapshot(CatalogRepository c, IReadOnlyList<MediaItem> m) => System.Text.Json.JsonSerializer.Serialize(new
        {
            facets = c.GetFacets(), usage = c.GetTagUsage(), combinations = c.GetTagCombinations(null, 50),
            suggestions = m.Select(i => c.SuggestTags([i.Id], 20)), extending = c.GetTagCombinations(["Meer"], 50),
        });
        Snapshot(catalog, media); // fill the caches
        var facets = catalog.GetFacets();

        catalog.UpdateTags([media[0].Id, media[7].Id], ["Neu", "Sommerfest"], ["Sonne"]);   // new tags, a medium gains its first tags
        catalog.UpdateTags([media[6].Id], [], ["Terrasse", "Garten"]);                     // categorized tag and the last tags of a medium
        catalog.UpdateTags(media.Take(3).Select(m => m.Id).ToArray(), [], ["Wolken"]);
        catalog.Upsert(catalog.GetMedia(media[1].Id)! with { Rating = 8, IsFavorite = true });
        Assert.Same(facets.Folders, catalog.GetFacets().Folders); // folders and categories were not rescanned
        var carried = catalog.GetFacets();
        Assert.True(catalog.PrepareSearchIndex() > 0);
        Assert.Same(carried, catalog.GetFacets()); // indexing for the quick search changes no media
        using (var fresh = Open()) Assert.Equal(Snapshot(fresh, media), Snapshot(catalog, media));

        // A changed path or category is recounted in full.
        catalog.Upsert(catalog.GetMedia(media[2].Id)! with { Path = Path.Combine(directory, "neu", "2.jpg"), Category = "Ausflug" });
        Assert.Contains(catalog.GetFacets().Categories, c => c == "Ausflug");
        using (var fresh = Open()) Assert.Equal(Snapshot(fresh, media), Snapshot(catalog, media));
    }

    [Fact]
    public void CombinationsPreferLargerTypicalSetsAndExtendContext()
    {
        using var catalog = Open();
        for (var i = 0; i < 6; i++) catalog.Upsert(Item($"a/{i}.jpg", "Urlaub", "Strand", "Meer"));
        for (var i = 0; i < 4; i++) catalog.Upsert(Item($"b/{i}.jpg", "Familie", "Geburtstag"));
        catalog.Upsert(Item("c/0.jpg", "Urlaub", "Berge"));
        var combinations = catalog.GetTagCombinations();
        Assert.Equal(["Meer", "Strand", "Urlaub"], combinations[0].Tags.Order());
        Assert.Equal(6, combinations[0].MediaCount);
        Assert.DoesNotContain(combinations, c => c.Tags.Count == 2 && c.Tags.Contains("Strand"));
        Assert.Contains(combinations, c => c.Tags.Order().SequenceEqual(["Familie", "Geburtstag"]));
        var extending = catalog.GetTagCombinations(["meer"]);
        Assert.Equal(["Meer", "Strand", "Urlaub"], Assert.Single(extending).Tags.Order());
        Assert.Empty(catalog.GetTagCombinations(["Urlaub", "Strand", "Meer", "Familie", "Geburtstag"]));
    }
}
