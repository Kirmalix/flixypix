using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class StructuredSearchTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-expression-" + Guid.NewGuid().ToString("N"));
    private CatalogRepository Open(string name = "catalog.db") => new(Path.Combine(directory, name));
    private static SearchExpression Text(SearchField field, SearchComparison comparison, string value) => SearchExpression.Where(new() { Field = field, Comparison = comparison, Text = value });
    private static SearchExpression Number(SearchField field, SearchComparison comparison, double value) => SearchExpression.Where(new() { Field = field, Comparison = comparison, Number = value });
    private static SearchExpression Presence(SearchField field, bool known) => SearchExpression.Where(new() { Field = field, Comparison = known ? SearchComparison.IsKnown : SearchComparison.IsUnknown });
    private MediaItem Item(string title, MediaKind kind = MediaKind.Image) => new() { Path = Path.Combine(directory, title + ".jpg"), Title = title, Kind = kind };

    [Fact]
    public void NestedGroupsCombineTypedConditionsAndCompatibilityFilters()
    {
        using var catalog = Open();
        var match = catalog.Upsert(Item("München Sommer") with { Tags = ["Urlaub"], Rating = 9, Width = 4000, Height = 3000, FileSize = 123456,
            Description = "Abend am See", DurationSeconds = 30.5, PlaybackPositionSeconds = 12.25, Category = "Fotos", WatchLater = true });
        catalog.Upsert(Item("München Winter") with { Tags = ["Arbeit"], Rating = 9, Width = 1000, Height = 500 });
        catalog.Upsert(Item("Paris") with { Tags = ["Urlaub"], Rating = 4, Width = 4000 });
        var expression = SearchExpression.All(
            SearchExpression.Any(Text(SearchField.Title, SearchComparison.Contains, "mÜNCHEN"), Text(SearchField.Title, SearchComparison.Equals, "Paris")),
            SearchExpression.Not(Text(SearchField.Tag, SearchComparison.Equals, "ARBEIT")),
            Number(SearchField.Rating, SearchComparison.GreaterOrEqual, 8), Number(SearchField.Width, SearchComparison.GreaterThan, 2000),
            Number(SearchField.Height, SearchComparison.GreaterOrEqual, 3000), Number(SearchField.FileSize, SearchComparison.LessThan, 200000),
            Number(SearchField.Duration, SearchComparison.Equals, 30.5), Number(SearchField.PlaybackPosition, SearchComparison.GreaterThan, 12),
            Text(SearchField.Description, SearchComparison.EndsWith, "SEE"), Text(SearchField.Category, SearchComparison.StartsWith, "fo"),
            Text(SearchField.Kind, SearchComparison.Equals, "image"),
            SearchExpression.Where(new() { Field = SearchField.WatchLater, Comparison = SearchComparison.Equals, Flag = true }),
            SearchExpression.Where(new() { Field = SearchField.Favorite, Comparison = SearchComparison.Equals, Flag = true }));
        Assert.Equal(match.Id, Assert.Single(catalog.QueryPage(new() { Expression = expression, Text = "Sommer" }).Items).Id);
        Assert.Empty(catalog.QueryPage(new() { Expression = expression, MinimumRating = 10 }).Items);
        Assert.Equal(0, catalog.GetPosition(new() { Expression = expression }, match.Id));
    }

    [Fact]
    public void SetNegationAndMissingScalarsHaveExplicitSemantics()
    {
        using var catalog = Open();
        var tagged = catalog.Upsert(Item("Tagged") with { Tags = ["People", "Travel"], TagCategories = new() { ["Thema"] = ["Travel"] }, DurationSeconds = 5 });
        var empty = catalog.Upsert(Item("Empty"));
        var other = catalog.Upsert(Item("Other") with { Tags = ["Nature"] });
        var noPeople = catalog.QueryPage(new() { Expression = Text(SearchField.Tag, SearchComparison.NotEquals, "people") });
        Assert.Equal(new[] { empty.Id, other.Id }, noPeople.Items.Select(x => x.Id));
        Assert.Equal(tagged.Id, Assert.Single(catalog.QueryPage(new() { Expression = Text(SearchField.TagCategory, SearchComparison.Equals, "THEMA") }).Items).Id);
        Assert.Equal(2, catalog.QueryPage(new() { Expression = Presence(SearchField.Duration, false) }).TotalCount);
        Assert.Equal(0, catalog.QueryPage(new() { Expression = Number(SearchField.Duration, SearchComparison.NotEquals, 5) }).TotalCount);
        Assert.Equal(2, catalog.QueryPage(new() { Expression = SearchExpression.Not(Number(SearchField.Duration, SearchComparison.Equals, 5)) }).TotalCount);
        Assert.Equal(1, catalog.QueryPage(new() { Expression = Presence(SearchField.Tag, false) }).TotalCount);
        Assert.Equal(2, catalog.QueryPage(new() { Expression = Presence(SearchField.Tag, true) }).TotalCount);
    }

    [Fact]
    public void UnicodeLiteralValuesCannotBecomeSqlOrWildcardsAndDatesCompareInstants()
    {
        using var catalog = Open();
        var item = catalog.Upsert(Item("Straße_100% ' OR 1=1 --") with { AddedAt = new DateTimeOffset(2025, 3, 1, 10, 0, 0, TimeSpan.FromHours(2)) });
        catalog.Upsert(Item("Other") with { AddedAt = new DateTimeOffset(2025, 3, 2, 0, 0, 0, TimeSpan.Zero) });
        Assert.Equal(item.Id, Assert.Single(catalog.QueryPage(new() { Expression = Text(SearchField.Title, SearchComparison.Contains, "100% ' OR 1=1 --") }).Items).Id);
        Assert.Equal(item.Id, Assert.Single(catalog.QueryPage(new() { Expression = Text(SearchField.Path, SearchComparison.EndsWith, ".JPG") , Text = "straße" }).Items).Id);
        Assert.Empty(catalog.QueryPage(new() { Expression = Text(SearchField.Title, SearchComparison.Contains, "strasse") }).Items);
        var instant = SearchExpression.Where(new() { Field = SearchField.AddedAt, Comparison = SearchComparison.Equals, Date = new DateTimeOffset(2025, 3, 1, 8, 0, 0, TimeSpan.Zero) });
        Assert.Equal(item.Id, Assert.Single(catalog.QueryPage(new() { Expression = instant }).Items).Id);
        var later = SearchExpression.Where(new() { Field = SearchField.AddedAt, Comparison = SearchComparison.GreaterThan, Date = new DateTimeOffset(2025, 3, 1, 9, 0, 0, TimeSpan.Zero) });
        Assert.Equal("Other", Assert.Single(catalog.QueryPage(new() { Expression = later }).Items).Title);
    }

    [Fact]
    public void SavedExpressionSurvivesReopenPortableBackupAndInvalidImportRollsBack()
    {
        Guid searchId;
        var backup = Path.Combine(directory, "backup.json");
        using (var catalog = Open())
        {
            catalog.Upsert(Item("Portrait") with { Tags = ["Person"], Rating = 8 });
            catalog.Upsert(Item("Landscape") with { Rating = 5 });
            var search = catalog.UpsertSavedSearch(new() { Name = "People", Expression = SearchExpression.All(Text(SearchField.Tag, SearchComparison.Equals, "Person"), Number(SearchField.Rating, SearchComparison.GreaterOrEqual, 7)) });
            searchId = search.Id; catalog.Export(backup);
        }
        using (var reopened = Open()) Assert.Equal("Portrait", Assert.Single(reopened.SearchSaved(searchId)).Title);
        using var imported = Open("imported.db"); Assert.Empty(imported.ImportJson(backup).Errors);
        Assert.Equal("Portrait", Assert.Single(imported.SearchSaved(searchId)).Title);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var bad = new CatalogDocument { Items = [Item("MustRollback")], SavedSearches = [new() { Name = "Bad", Expression = SearchExpression.Not(null!) }] };
        File.WriteAllText(backup, JsonSerializer.Serialize(bad, options));
        Assert.NotEmpty(imported.ImportJson(backup).Errors); Assert.Equal(2, imported.QueryPage(new()).TotalCount);
    }

    [Fact]
    public void InvalidExpressionsAreRejectedBeforeSqlOrSavedWrites()
    {
        using var catalog = Open();
        var invalid = new List<SearchExpression>
        {
            SearchExpression.All(), new() { Operator = SearchOperator.Not, Children = [Presence(SearchField.Title, true), Presence(SearchField.Title, false)] },
            new() { Condition = new() { Field = (SearchField)999, Comparison = SearchComparison.Equals, Text = "x" } },
            Number(SearchField.Rating, SearchComparison.Contains, 5), Number(SearchField.Rating, SearchComparison.Equals, 11),
            Number(SearchField.Width, SearchComparison.Equals, double.NaN),
            SearchExpression.Where(new() { Field = SearchField.Title, Comparison = SearchComparison.Equals, Text = "x", Number = 1 }),
            Text(SearchField.Kind, SearchComparison.Contains, "Image"), Text(SearchField.Kind, SearchComparison.Equals, "Executable"),
            SearchExpression.Where(new() { Field = SearchField.WatchLater, Comparison = SearchComparison.Equals, Text = "true" }),
            SearchExpression.Where(new() { Field = SearchField.Duration, Comparison = SearchComparison.IsKnown, Number = 5 })
        };
        var deep = Presence(SearchField.Title, true); for (var index = 0; index < 16; index++) deep = SearchExpression.Not(deep); invalid.Add(deep);
        invalid.Add(SearchExpression.Any(Enumerable.Range(0, 256).Select(_ => Presence(SearchField.Title, true)).ToArray()));
        var cyclic = SearchExpression.All(Presence(SearchField.Title, true)); cyclic.Children.Add(cyclic); invalid.Add(cyclic);
        foreach (var expression in invalid)
        {
            Assert.ThrowsAny<ArgumentException>(() => catalog.QueryPage(new() { Expression = expression }));
            Assert.ThrowsAny<ArgumentException>(() => catalog.UpsertSavedSearch(new() { Name = "Bad", Expression = expression }));
        }
        Assert.Empty(catalog.GetSavedSearches());
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
