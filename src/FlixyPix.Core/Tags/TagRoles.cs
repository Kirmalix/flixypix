using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>What the tags of a category stand for. Performers follow the original's cast categories ("tagcategorycast");
/// studios are new in FlixyPix.</summary>
public enum TagRole { Tag, Performer, Studio }

public sealed record TagCategoryRole(string Category, TagRole Role);

/// <summary>A tag category with its role and where the role comes from.</summary>
public sealed record TagCategoryInfo(string Category, TagRole Role, bool IsExplicit);

/// <summary>Roles of tag categories. A category without an explicit role gets one from its name: the original's default
/// cast categories and common studio names; everything else is a plain tag category.</summary>
public static class TagRoles
{
    /// <summary>The original's default value of "tagcategorycast" (first start), lower case and ":"-separated.</summary>
    public const string LegacyDefaultCast = "actors:actor:darsteller:schauspieler:actores:acteurs:acteur:actresses:actress:models (adult):adult models: models:performers (adult):performers:darstellerin:schauspielerinnen:actrices:actriz:actrice:actrices";

    private static readonly HashSet<string> PerformerNames = new(Split(LegacyDefaultCast).Concat(["cast", "besetzung", "personen", "people", "pornstars", "models", "model"]),
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> StudioNames = new(["studio", "studios", "label", "labels", "network", "networks", "produktionsfirma",
        "produktionsfirmen", "production company", "production companies", "producer", "producers", "sender", "verlag", "site", "sites", "website", "websites"],
        StringComparer.OrdinalIgnoreCase);

    public static TagRole Default(string category) =>
        PerformerNames.Contains(category.Trim()) ? TagRole.Performer : StudioNames.Contains(category.Trim()) ? TagRole.Studio : TagRole.Tag;

    public static TagRole Of(CatalogPreferences preferences, string category)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        foreach (var entry in preferences.TagCategoryRoles) if (string.Equals(entry.Category, category.Trim(), StringComparison.OrdinalIgnoreCase)) return entry.Role;
        return Default(category);
    }

    /// <summary>The original's list: lower-case names separated by ":"; it compared with "contains", FlixyPix with whole names.</summary>
    internal static IEnumerable<string> Split(string? value) =>
        (value ?? "").Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase);

    internal static List<TagCategoryRole> Set(IEnumerable<TagCategoryRole> roles, string category, TagRole role)
    {
        var name = category.Trim();
        var result = roles.Where(r => !string.Equals(r.Category, name, StringComparison.OrdinalIgnoreCase)).ToList();
        // Only deviations from the default are stored, so better defaults later still reach untouched categories.
        if (role != Default(name)) result.Add(new TagCategoryRole(name, role));
        return result;
    }

    /// <summary>Roles after taking over the original's "tagcategorycast" for the given categories: listed ones are performers,
    /// unlisted ones that FlixyPix would otherwise treat as performers by name become plain tag categories.</summary>
    internal static List<TagCategoryRole> FromLegacyCast(IEnumerable<TagCategoryRole> roles, string cast, IEnumerable<string> categories)
    {
        var listed = new HashSet<string>(Split(cast), StringComparer.OrdinalIgnoreCase);
        var result = roles.ToList();
        foreach (var category in categories.Concat(listed).Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (listed.Contains(category)) result = Set(result, category, TagRole.Performer);
            else if (Default(category) == TagRole.Performer) result = Set(result, category, TagRole.Tag);
        }
        return result;
    }
}

public sealed partial class CatalogRepository
{
    public TagRole GetTagRole(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        return TagRoles.Of(GetPreferences(), category);
    }

    /// <summary>Every category of the vocabulary and of the media, with its role, ordered by name.</summary>
    public IReadOnlyList<TagCategoryInfo> GetTagCategories()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var preferences = ReadPreferences();
            var explicitRoles = preferences.TagCategoryRoles.Select(r => r.Category);
            return GetFacets().TagCategories.Concat(preferences.TagVocabulary.Select(t => t.Category)).Concat(explicitRoles)
                .Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Select(c => new TagCategoryInfo(c, TagRoles.Of(preferences, c), preferences.TagCategoryRoles.Any(r => string.Equals(r.Category, c, StringComparison.OrdinalIgnoreCase))))
                .ToList();
        }
    }

    public void SetTagRole(string category, TagRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        if (!Enum.IsDefined(role)) throw new ArgumentException("Unknown tag role.", nameof(role));
        lock (gate)
        {
            ThrowIfDisposed();
            var preferences = ReadPreferences();
            SavePreferences(preferences with { TagCategoryRoles = TagRoles.Set(preferences.TagCategoryRoles, category, role) });
        }
    }

    /// <summary>Takes over "tagcategorycast" with the vocabulary: the roles are part of the data, so this also runs when the
    /// takeover assistant leaves the program settings to the user.</summary>
    private void ImportLegacyTagRoles(Func<string, IEnumerable<JsonElement>> rows, SqliteTransaction transaction) =>
        ApplyLegacyTagRoles(rows("configsettings"), transaction);

    /// <summary>Takes over "tagcategorycast" from a library migrated before FlixyPix knew roles; false if the batch has none.</summary>
    public bool AdoptLegacyTagRoles(string batch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batch);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var adopted = ApplyLegacyTagRoles(ReadRetainedRows(batch, "configsettings", transaction), transaction);
            transaction.Commit();
            return adopted;
        }
    }

    private bool ApplyLegacyTagRoles(IEnumerable<JsonElement> configRows, SqliteTransaction transaction)
    {
        var row = configRows.LastOrDefault(r => string.Equals(Text(r, "parameter"), "tagcategorycast", StringComparison.OrdinalIgnoreCase));
        if (row.ValueKind != JsonValueKind.Object) return false;
        var preferences = ReadPreferences(transaction);
        var categories = preferences.TagVocabulary.Select(t => t.Category);
        using (var command = Command("SELECT DISTINCT category FROM media_tags WHERE category<>''", transaction))
        using (var reader = command.ExecuteReader())
        {
            var used = new List<string>();
            while (reader.Read()) used.Add(reader.GetString(0));
            categories = categories.Concat(used).ToList();
        }
        SaveDocument("preferences", Guid.Empty, preferences with
        {
            TagCategoryRoles = TagRoles.FromLegacyCast(preferences.TagCategoryRoles, Text(row, "value1") ?? "", categories)
        }, transaction);
        AuditLegacy("configsettings", LegacyRowKey("configsettings", row), "partially-projected", "tag-roles", transaction);
        return true;
    }
}
