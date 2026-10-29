using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class CategorizedTagSearchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-bound-tag-" + Guid.NewGuid());
    public CategorizedTagSearchTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void CategoryAndValueMustMatchTheSameTagRowInsideNestedExpressions()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var a = catalog.Upsert(new() { Path = Path.Combine(root, "a.jpg"), Kind = MediaKind.Image, TagCategories = new() { ["People"] = ["Alex"], ["Places"] = ["Berlin"] } });
        var b = catalog.Upsert(new() { Path = Path.Combine(root, "b.jpg"), Kind = MediaKind.Image, TagCategories = new() { ["People"] = ["Berlin"], ["Places"] = ["Alex"] } });
        var c = catalog.Upsert(new() { Path = Path.Combine(root, "c.jpg"), Kind = MediaKind.Image, Tags = ["Alex"] });
        var bound = CatalogRepository.HasCategorizedTag(new("pEoPlE", "aLeX"));
        Assert.Equal(a.Id, Assert.Single(catalog.QueryPage(new() { Expression = bound }).Items).Id);
        var independent = SearchExpression.All(SearchExpression.Where(new() { Field = SearchField.Tag, Comparison = SearchComparison.Equals, Text = "Alex" }),
            SearchExpression.Where(new() { Field = SearchField.TagCategory, Comparison = SearchComparison.Equals, Text = "People" }));
        Assert.Equal(2, catalog.QueryPage(new() { Expression = independent }).TotalCount);
        var notBound = CatalogRepository.HasCategorizedTag(new("People", "Alex"), negate: true);
        Assert.Equal(new[] { b.Id, c.Id }.Order(), catalog.QueryPage(new() { Expression = notBound }).Items.Select(item => item.Id).Order());
        var nested = SearchExpression.All(SearchExpression.Any(bound, CatalogRepository.HasCategorizedTag(new("Places", "Alex"))), SearchExpression.Not(CatalogRepository.HasCategorizedTag(new("People", "Berlin"))));
        Assert.Equal(a.Id, Assert.Single(catalog.QueryPage(new() { Expression = nested }).Items).Id);
    }

    [Fact]
    public void CategoryAndTagAreParametersAndRoundtripAsABoundCondition()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var key = new TagKey("People' OR 1=1 --", "Alex' OR 1=1 --");
        var match = catalog.Upsert(new() { Path = Path.Combine(root, "match.jpg"), Kind = MediaKind.Image, TagCategories = new() { [key.Category] = [key.Value] } });
        catalog.Upsert(new() { Path = Path.Combine(root, "other.jpg"), Kind = MediaKind.Image, Tags = [key.Value] });
        catalog.UpsertSavedSearch(new() { Name = "Bound", Expression = CatalogRepository.HasCategorizedTag(key) });
        var path = Path.Combine(root, "catalog.json"); catalog.Export(path);
        using var copy = new CatalogRepository(Path.Combine(root, "copy.db")); Assert.Empty(copy.ImportJson(path).Errors);
        var expression = Assert.Single(copy.GetSavedSearches()).Expression!; Assert.Equal(key.Category, expression.Condition!.TagCategory);
        Assert.Equal(match.Id, Assert.Single(copy.QueryPage(new() { Expression = expression }).Items).Id);
    }

    [Fact]
    public void InvalidBoundConditionsAndCategoryOnOtherFieldsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => CatalogRepository.HasCategorizedTag(new("", "tag")));
        Assert.Throws<ArgumentException>(() => CatalogRepository.HasCategorizedTag(new("category", new string('x', 257))));
        Assert.Throws<ArgumentException>(() => CatalogRepository.ValidateSearchExpression(SearchExpression.Where(new() { Field = SearchField.CategorizedTag, Comparison = SearchComparison.Contains, Text = "tag", TagCategory = "category" })));
        Assert.Throws<ArgumentException>(() => CatalogRepository.ValidateSearchExpression(SearchExpression.Where(new() { Field = SearchField.CategorizedTag, Comparison = SearchComparison.Equals, Text = "tag", TagCategory = "category", Number = 3 })));
        Assert.Throws<ArgumentException>(() => CatalogRepository.ValidateSearchExpression(SearchExpression.Where(new() { Field = SearchField.Tag, Comparison = SearchComparison.Equals, Text = "tag", TagCategory = "category" })));
    }

    [Fact]
    public void CategoryMovesRewriteOnlyExplicitlyBoundReferencesAndGlobalRenamePreservesTheCategory()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var media = catalog.Upsert(new() { Path = Path.Combine(root, "a.jpg"), Kind = MediaKind.Image, TagCategories = new() { ["People"] = ["Alex"], ["Places"] = ["Alex"] } });
        var bound = catalog.UpsertSavedSearch(new() { Name = "Bound", Expression = SearchExpression.Not(CatalogRepository.HasCategorizedTag(new("People", "Alex"), negate: true)) });
        var independent = catalog.UpsertSavedSearch(new() { Name = "Independent", Expression = SearchExpression.All(SearchExpression.Where(new() {
         Field = SearchField.Tag, Comparison = SearchComparison.Equals, Text = "Alex" }), SearchExpression.Where(new() {
         Field = SearchField.TagCategory, Comparison = SearchComparison.Equals, Text = "People" })) });
        var direct = catalog.UpsertSavedSearch(new() { Name = "Direct", Tag = "Alex", TagCategory = "People" });
        catalog.ChangeTag("Alex", "Jordan");
        var renamed = catalog.GetSavedSearches().Single(search => search.Id == bound.Id); Assert.Equal("Jordan", renamed.Expression!.Children[0].Condition!.Text); Assert.Equal("People", renamed.Expression.Children[0].Condition!.TagCategory);
        catalog.MoveTagCategory("Jordan", "People", "Performers"); var searches = catalog.GetSavedSearches();
        var moved = searches.Single(search => search.Id == bound.Id); Assert.Equal("Performers", moved.Expression!.Children[0].Condition!.TagCategory);
        Assert.Equal(media.Id, Assert.Single(catalog.QueryPage(new() { Expression = moved.Expression }).Items).Id);
        Assert.Equal("People", searches.Single(search => search.Id == independent.Id).Expression!.Children[1].Condition!.Text);
        Assert.Equal("Performers", searches.Single(search => search.Id == direct.Id).TagCategory);
        Assert.Equal(new[] { "Jordan" }, catalog.GetMedia(media.Id)!.TagCategories["Places"]);
    }
}
