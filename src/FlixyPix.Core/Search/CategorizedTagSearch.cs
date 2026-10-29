using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>A category and value identify one categorized tag, never two independent filter conditions.</summary>
public sealed record TagKey(string Category, string Value);

public sealed partial class CatalogRepository
{
    public static SearchExpression HasCategorizedTag(TagKey key, bool negate = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        var condition = new SearchCondition
        {
            Field = SearchField.CategorizedTag, Comparison = negate ? SearchComparison.NotEquals : SearchComparison.Equals,
            TagCategory = key.Category?.Trim(), Text = key.Value?.Trim()
        };
        ValidateCategorizedTagCondition(condition); return SearchExpression.Where(condition);
    }

    private static void ValidateCategorizedTagCondition(SearchCondition condition)
    {
        if (condition.Field != SearchField.CategorizedTag || condition.Comparison is not (SearchComparison.Equals or SearchComparison.NotEquals) ||
            string.IsNullOrWhiteSpace(condition.TagCategory) || condition.TagCategory.Length > 256 || string.IsNullOrWhiteSpace(condition.Text) || condition.Text.Length > 256 ||
            condition.Number is not null || condition.Flag is not null || condition.Date is not null)
            throw new ArgumentException("A categorized tag condition requires an exact category and tag (1–256 characters each), Equals/NotEquals and no scalar value.");
    }

    private static string BuildCategorizedTagSql(SearchCondition condition, Func<object, string> parameter)
    {
        ValidateCategorizedTagCondition(condition);
        var category = parameter(condition.TagCategory!); var tag = parameter(condition.Text!);
        var exists = $"EXISTS (SELECT 1 FROM media_tags t WHERE t.media_id=q.id AND t.category={category} COLLATE EFP_ORDINAL AND t.tag={tag} COLLATE EFP_ORDINAL)";
        return condition.Comparison == SearchComparison.NotEquals ? "NOT " + exists : exists;
    }

    // Only the explicit bound condition and an explicitly paired direct filter follow a category move.
    // Separate Tag and TagCategory nodes retain their existing independent semantics.
    private void MoveCategorizedTagSearchReferences(TagKey before, TagKey after, SqliteTransaction transaction, CancellationToken token)
    {
        _ = HasCategorizedTag(before); _ = HasCategorizedTag(after);
        foreach (var search in ReadDocuments<SavedSearch>("saved_searches", transaction))
        {
            token.ThrowIfCancellationRequested();
            var directMatch = Same(search.Tag, before.Value) && Same(search.TagCategory, before.Category);
            var changed = search with { Tag = directMatch ? after.Value : search.Tag, TagCategory = directMatch ? after.Category : search.TagCategory, Expression = Rewrite(search.Expression) };
            if (JsonSerializer.Serialize(search, Json) != JsonSerializer.Serialize(changed, Json)) SaveDocument("saved_searches", changed.Id, changed, transaction);
        }
        SearchExpression? Rewrite(SearchExpression? expression)
        {
            if (expression == null) return null;
            if (expression.Condition is { Field: SearchField.CategorizedTag } condition && Same(condition.TagCategory, before.Category) && Same(condition.Text, before.Value))
                return expression with { Condition = condition with { TagCategory = after.Category, Text = after.Value } };
            return expression with { Children = expression.Children.Select(child => Rewrite(child)!).ToList() };
        }
    }
}
