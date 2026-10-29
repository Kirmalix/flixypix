using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>Searching by the profiles of performers and studios. The condition is resolved when the search runs: the directory
/// entries whose profile matches become a list of tags, so a saved search also finds performers given the property later.</summary>
public sealed partial class CatalogRepository
{
    public static SearchExpression HasProfile(ProfileProperty property, SearchComparison comparison, string? text = null, double? number = null)
    {
        var condition = new SearchCondition { Field = SearchField.Profile, Profile = property, Comparison = comparison, Text = text, Number = number };
        ValidateProfileCondition(condition);
        return SearchExpression.Where(condition);
    }

    private static void ValidateProfileCondition(SearchCondition condition)
    {
        if (condition.Profile is not { } property || !Enum.IsDefined(property) || condition.TagCategory is not null || condition.Date is not null || condition.Flag is not null)
            throw new ArgumentException("A profile condition names a known profile property and no category, date or flag.");
        if (PresenceComparison(condition.Comparison))
        {
            if (condition.Text is not null || condition.Number is not null || property is ProfileProperty.Studio or ProfileProperty.AgeAtRecording)
                throw new ArgumentException("Known/unknown profile conditions have no value and do not apply to studios or the age at recording.");
            return;
        }
        if (ProfileProperties.IsNumber(property))
        {
            if (condition.Text is not null || condition.Number is not { } number || !double.IsFinite(number) || number is < 0 or > 10000 ||
                condition.Comparison is not (SearchComparison.Equals or SearchComparison.GreaterThan or SearchComparison.GreaterOrEqual or SearchComparison.LessThan or SearchComparison.LessOrEqual) ||
                property == ProfileProperty.AgeAtRecording && number != Math.Floor(number))
                throw new ArgumentException("Numeric profile conditions require a number from 0 to 10,000 (whole years for the age at recording) and a numeric comparison.");
            return;
        }
        if (condition.Number is not null || condition.Text is not { Length: > 0 and <= 256 })
            throw new ArgumentException("Profile text conditions require 1–256 characters.");
        if (ProfileProperties.IsChoice(property))
        {
            var known = property == ProfileProperty.Gender ? Enum.TryParse<PerformerGender>(condition.Text, true, out var gender) && Enum.IsDefined(gender)
                : Enum.TryParse<BreastType>(condition.Text, true, out var breasts) && Enum.IsDefined(breasts);
            if (!known || condition.Comparison != SearchComparison.Equals) throw new ArgumentException("This profile property compares one of its values with Equals.");
        }
        else if (condition.Comparison is not (SearchComparison.Contains or SearchComparison.Equals or SearchComparison.StartsWith or SearchComparison.EndsWith))
            throw new ArgumentException("Profile text conditions compare with Contains, Equals, StartsWith or EndsWith.");
        if (property == ProfileProperty.Studio && condition.Comparison != SearchComparison.Equals) throw new ArgumentException("A studio condition names the studio exactly.");
    }

    private string BuildProfileSql(SearchCondition condition, Func<object, string> parameter, SqliteTransaction? transaction)
    {
        ValidateProfileCondition(condition);
        var vocabulary = ReadPreferences(transaction).TagVocabulary;
        var property = condition.Profile!.Value;
        const string Join = "JOIN json_each({0}) b ON t.tag=json_extract(b.value,'$[1]') COLLATE EFP_ORDINAL " +
            "AND (json_extract(b.value,'$[0]') IS NULL OR t.category=json_extract(b.value,'$[0]') COLLATE EFP_ORDINAL)";
        if (property == ProfileProperty.AgeAtRecording)
        {
            var births = vocabulary.Where(e => e.Profile?.BirthDate is not null).Select(e => new object?[] { e.Category, e.Tag, TagProfile.IsoDate(e.Profile!.BirthDate!.Value) }).ToList();
            if (births.Count == 0) return "0";
            var years = (int)condition.Number!.Value;
            // Recorded on the release date, otherwise on the file date; "at least N" means on or after the N-th birthday.
            const string Recorded = "coalesce((SELECT julianday(json_extract(document,'$.releaseDate')) FROM media WHERE id=q.id),q.file_date)";
            string Birthday(int age) => $"julianday(json_extract(b.value,'$[2]'),'+{age} years')";
            var test = condition.Comparison switch
            {
                SearchComparison.GreaterOrEqual => $"{Recorded}>={Birthday(years)}",
                SearchComparison.GreaterThan => $"{Recorded}>={Birthday(years + 1)}",
                SearchComparison.LessThan => $"{Recorded}<{Birthday(years)}",
                SearchComparison.LessOrEqual => $"{Recorded}<{Birthday(years + 1)}",
                _ => $"{Recorded}>={Birthday(years)} AND {Recorded}<{Birthday(years + 1)}",
            };
            return $"EXISTS (SELECT 1 FROM media_tags t {string.Format(Join, parameter(JsonSerializer.Serialize(births)))} WHERE t.media_id=q.id AND {test})";
        }
        var matches = MatchingProfiles(vocabulary, condition, DateOnly.FromDateTime(DateTime.Now)).ToList();
        if (matches.Count == 0) return "0";
        return $"EXISTS (SELECT 1 FROM media_tags t {string.Format(Join, parameter(JsonSerializer.Serialize(matches)))} WHERE t.media_id=q.id)";
    }

    /// <summary>The (category, tag) pairs whose profile matches; a null category stands for the tag in any category.</summary>
    internal static IEnumerable<string?[]> MatchingProfiles(IReadOnlyList<TagVocabularyEntry> vocabulary, SearchCondition condition, DateOnly today)
    {
        var property = condition.Profile!.Value;
        if (property == ProfileProperty.Studio)
        {
            // The named studio also counts when it has no directory entry of its own.
            foreach (var studio in StudioTree(vocabulary, condition.Text!)) yield return [null, studio];
            yield break;
        }
        foreach (var entry in vocabulary)
        {
            if (entry.Profile is not { } profile) continue;
            bool matches;
            if (PresenceComparison(condition.Comparison))
            {
                var known = ProfileProperties.IsNumber(property) ? profile.Number(property, today) is not null : profile.Text(property) is not null;
                matches = known == (condition.Comparison == SearchComparison.IsKnown);
            }
            else if (ProfileProperties.IsNumber(property))
            {
                if (profile.Number(property, today) is not { } value) continue;
                var number = condition.Number!.Value;
                matches = condition.Comparison switch
                {
                    SearchComparison.GreaterThan => value > number, SearchComparison.GreaterOrEqual => value >= number,
                    SearchComparison.LessThan => value < number, SearchComparison.LessOrEqual => value <= number, _ => value == number,
                };
            }
            else
            {
                if (profile.Text(property) is not { } value) continue;
                var text = condition.Text!.Trim();
                matches = condition.Comparison switch
                {
                    SearchComparison.Contains => value.Contains(text, StringComparison.OrdinalIgnoreCase),
                    SearchComparison.StartsWith => value.StartsWith(text, StringComparison.OrdinalIgnoreCase),
                    SearchComparison.EndsWith => value.EndsWith(text, StringComparison.OrdinalIgnoreCase),
                    _ => string.Equals(value, text, StringComparison.OrdinalIgnoreCase),
                };
            }
            if (matches) yield return [entry.Category, entry.Tag];
        }
    }
}
