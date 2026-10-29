using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    private void ImportLegacyCategories(Func<string, IEnumerable<JsonElement>> rows, LegacyMediaMap media,
        SqliteTransaction transaction, List<string> warnings)
    {
        IEnumerable<JsonElement> Rows(string table) => rows(table);
        var categories = Rows("categories").Where(x => Text(x, "ID") is not null).GroupBy(x => Text(x, "ID")!)
            .ToDictionary(x => x.Key, x => x.ToArray());
        var vocabulary = Rows("searchcriteria").Where(x => string.Equals(Text(x, "movie"), "SEARCHTAGINFO", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(Text(x, "value1")) && !string.IsNullOrWhiteSpace(Text(x, "category")) &&
            !string.Equals(Text(x, "category"), "Recent", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Text(x, "category"), "Uncategorized", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => Text(x, "value1")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(v => Text(v, "category")!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var watchLaterRows = Rows("videowatchlater").Where(x => Text(x, "videoid") is not null).ToArray();
        var watchLater = watchLaterRows.Select(x => Text(x, "videoid")!).ToHashSet(StringComparer.Ordinal);
        var usedCategories = new Dictionary<string, List<Guid>>(StringComparer.Ordinal); var watching = new Dictionary<string, Guid>(StringComparer.Ordinal);
        // Category of each medium, by the key of the media map ("movies:ID", "pictures:ID").
        var categoryIds = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var table in new[] { "movies", "pictures" })
            foreach (var row in Rows(table)) if (Text(row, "ID") is { } id) categoryIds[table + ":" + id] = Text(row, "categoryid");
        var preferences = ReadPreferences(transaction);
        foreach (var row in Rows("configsettings"))
        {
            var parameter = Text(row, "parameter");
            if (parameter is not ("favrating" or "picfavrating")) continue;
            var stars = Number(row, "value1");
            if (stars is < 1 or > 5 || stars is null || stars % 1 != 0)
            { warnings.Add($"configsettings/{parameter}: invalid favorite threshold retained raw; current threshold kept."); continue; }
            var threshold = (int)stars.Value * 2;
            preferences = parameter == "favrating" ? preferences with { VideoFavoriteMinimumRating = threshold }
                : preferences with { ImageFavoriteMinimumRating = threshold };
            AuditLegacy("configsettings", parameter, "partially-projected", "app-setting", transaction);
        }
        SaveDocument("preferences", Guid.Empty, preferences, transaction);
        foreach (var (key, item) in media)
        {
            var category = item.Category;
            var categoryId = categoryIds.GetValueOrDefault(key);
            var mtype = item.Kind == MediaKind.Image ? 1 : 0;
            if (categoryId is not null && categories.TryGetValue(categoryId, out var candidates))
            {
                var matching = candidates.Where(x => Number(x, "mtype") == mtype && !string.IsNullOrWhiteSpace(Text(x, "category"))).ToArray();
                if (matching.Length == 1)
                {
                    category = Text(matching[0], "category");
                    if (!usedCategories.TryGetValue(categoryId, out var users)) usedCategories[categoryId] = users = [];
                    users.Add(item.Id);
                }
                else warnings.Add($"{item.LegacyId}: media category reference unresolved; categoryid retained raw.");
            }
            else if (categoryId is not null && categoryId != "0")
                warnings.Add($"{item.LegacyId}: media category {categoryId} missing; categoryid retained raw.");
            var tagCategories = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in item.Tags)
            {
                if (!vocabulary.TryGetValue(tag, out var labels)) continue;
                if (labels.Length > 1) warnings.Add($"{item.LegacyId}: tag '{tag}' has multiple vocabulary categories; all candidates retained.");
                foreach (var label in labels)
                {
                    if (!tagCategories.TryGetValue(label, out var tags)) tagCategories[label] = tags = [];
                    tags.Add(tag);
                }
            }
            var remoteId = key[(key.IndexOf(':') + 1)..]; // the original ID; keys are "movies:ID" or "pictures:ID"
            var saved = UpsertCore(item with
            {
                Category = category, TagCategories = tagCategories,
                WatchLater = item.Kind == MediaKind.Video && remoteId is not null && watchLater.Contains(remoteId)
            }, transaction);
            media[key] = saved;
            if (saved.WatchLater && remoteId is not null) watching[remoteId] = saved.Id;
        }
        // The report names what the categories and the watch-later list became; the first media are enough as targets.
        foreach (var (id, users) in usedCategories)
            AuditLegacy("categories", id, "partially-projected", "media-category", transaction, [.. users.Take(10)]);
        foreach (var row in watchLaterRows)
            if (watching.TryGetValue(Text(row, "videoid")!, out var target)) AuditLegacy("videowatchlater", Text(row, "ID"), "partially-projected", "watch-later", transaction, target);
    }
}
