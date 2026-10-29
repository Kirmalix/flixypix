using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    private static readonly TimeSpan RuleTimeout = TimeSpan.FromMilliseconds(250);
    private static List<TagRuleGroup> ValidateRuleGroups(List<TagRuleGroup> groups)
    {
        if (groups.Count > 256) throw new ArgumentException("A rule supports at most 256 groups.");
        var normalized = new List<TagRuleGroup>();
        foreach (var group in groups)
        {
            if (group is null || !Enum.IsDefined(group.Match) || group.Conditions is null || group.Tags is null || group.Conditions.Count is 0 or > 64)
                throw new ArgumentException("Every tag rule group needs 1–64 valid conditions and output tags.");
            foreach (var condition in group.Conditions)
            {
                if (condition is null || !Enum.IsDefined(condition.Field) || !Enum.IsDefined(condition.Comparison) || string.IsNullOrWhiteSpace(condition.Value) || condition.Value.Length > 4096)
                    throw new ArgumentException("Tag rule conditions need a field, comparison and value (up to 4096 characters).");
                if (condition.Field != TagRuleField.Tag && condition.Comparison == TagRuleComparison.HasTag)
                    throw new ArgumentException("HasTag applies only to tag conditions.");
                if (condition.Comparison == TagRuleComparison.Matches && condition.Field != TagRuleField.Tag)
                    _ = new Regex(condition.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RuleTimeout);
            }
            var tags = group.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (tags.Count == 0) throw new ArgumentException("Every tag rule group needs at least one output tag.");
            normalized.Add(group with { Tags = tags, Conditions = group.Conditions.ToList() });
        }
        return normalized;
    }

    public static IReadOnlyList<string> EvaluateTagRule(MediaItem item, FilenameTagRule rule) => EvaluateTagRuleCore(item,ValidateFilenameRule(rule));
    private static IReadOnlyList<string> EvaluateTagRuleCore(MediaItem item, FilenameTagRule valid)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!valid.Enabled) return [];
        if (valid.Groups.Count == 0) return System.IO.Path.GetFileName(item.Path).Contains(valid.FilenameContains, StringComparison.OrdinalIgnoreCase) ? valid.Tags : [];
        try
        {
            return valid.Groups.Where(g => g.Match == TagRuleMatch.Any ? g.Conditions.Any(Matches) : g.Conditions.All(Matches))
                .SelectMany(g => g.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (RegexMatchTimeoutException error) { throw new InvalidOperationException(T("Rule '{0}': the regular expression exceeds the time limit of 250 ms.", valid.Name),error); }

        bool Matches(TagRuleCondition condition)
        {
            if (condition.Field == TagRuleField.Tag)
                return condition.Value.Split(';',StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Any(value => item.Tags.Any(t => t.Equals(value,StringComparison.InvariantCultureIgnoreCase)));
            var directory = System.IO.Path.GetDirectoryName(item.Path) ?? "";
            var root = System.IO.Path.GetPathRoot(item.Path) ?? "";
            var value = condition.Field switch
            {
                TagRuleField.Filename => System.IO.Path.GetFileNameWithoutExtension(item.Path),
                TagRuleField.FilenameWithExtension => System.IO.Path.GetFileName(item.Path),
                TagRuleField.Path => directory.StartsWith(root,StringComparison.OrdinalIgnoreCase) ? directory[root.Length..] : directory,
                _ => item.Title
            };
            return condition.Comparison == TagRuleComparison.Matches
                ? Regex.IsMatch(value,condition.Value,RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,RuleTimeout)
                : value.Contains(condition.Value,StringComparison.InvariantCultureIgnoreCase);
        }
    }

    /// <summary>Reads the observed human-readable Original TagRulesJson exchange. No BinaryFormatter or assembly loading.</summary>
    public ImportResult ImportOriginalTagRules(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Rule file exceeds 8 MiB.");
            var original = File.ReadAllText(path); using var document = JsonDocument.Parse(original);
            var list = Property(document.RootElement,"tagrules");
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 1000) throw new InvalidDataException("Expected original tagrules array (up to 1000 rules).");
            var imported = new List<FilenameTagRule>();
            foreach (var entry in list.EnumerateArray())
            {
                var groups = new List<TagRuleGroup>();
                var entries = Property(entry,"ruleitems"); if (entries.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Rule is missing ruleitems.");
                foreach (var condition in entries.EnumerateArray())
                {
                    var join = (Text(condition,"join") ?? "none").ToLowerInvariant();
                    if (join is not ("and" or "or" or "none" or "")) throw new InvalidDataException("Unknown original join: " + join);
                    if (groups.Count == 0 || join is "none" or "")
                        groups.Add(new() { Tags = (Text(condition,"tags") ?? "").Split(';',StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList() });
                    var group = groups[^1];
                    if (join == "or") { group = group with { Match = TagRuleMatch.Any }; groups[^1] = group; }
                    var field = (Text(condition,"type") ?? "").ToLowerInvariant() switch
                    {
                        "filename" => TagRuleField.Filename, "path" => TagRuleField.Path,
                        "title" => TagRuleField.Title, "tag" => TagRuleField.Tag,
                        _ => throw new InvalidDataException("Unknown original rule field.")
                    };
                    var comparison = (Text(condition,"comparison") ?? "contains").ToLowerInvariant();
                    if (comparison is not ("contains" or "matches" or "hastag")) throw new InvalidDataException("Unknown original comparison.");
                    group.Conditions.Add(new(field, field == TagRuleField.Tag ? TagRuleComparison.HasTag : comparison == "matches" ? TagRuleComparison.Matches : TagRuleComparison.Contains, Text(condition,"key") ?? ""));
                }
                imported.Add(ValidateFilenameRule(new() { Name = Text(entry,"rulename") ?? "", Groups = groups,
                    Extra = new() { ["legacy.tagRuleJson"] = entry.GetRawText() } }));
            }
            lock (gate)
            {
                ThrowIfDisposed(); using var transaction = connection.BeginTransaction();
                foreach (var rule in imported) SaveDocument("filename_rules",rule.Id,rule,transaction);
                transaction.Commit();
            }
            return new() { Imported = imported.Count };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new() { Errors = [error.Message] }; }
    }

    public void ExportOriginalTagRules(string path, IEnumerable<Guid>? selectedIds = null)
    {
        var selection = selectedIds?.ToHashSet();
        var rules = GetFilenameTagRules().Where(r => selection is null || selection.Contains(r.Id)).ToArray();
        if (rules.Any(r => !r.Enabled))
            throw new InvalidOperationException(T("The original format stores no activation state. Please transfer deactivated rules through the complete JSON catalog backup or activate them before exporting the rules."));
        if (rules.Any(r => r.Groups.Count == 0 || r.Groups.Any(g => g.Conditions.Any(c => c.Field == TagRuleField.FilenameWithExtension))))
            throw new InvalidOperationException(T("Simple rules with a file extension cannot be represented losslessly in the original format. Use the JSON catalog backup or change the rule type in the group editor."));
        var payload = new { tagrules = rules.Select(rule => new { rulename = rule.Name, ruleitems = rule.Groups.SelectMany(group => group.Conditions.Select((condition,index) => new
        {
            type = condition.Field.ToString().ToLowerInvariant(), join = index == 0 ? "none" : group.Match == TagRuleMatch.Any ? "or" : "and",
            comparison = condition.Field == TagRuleField.Tag ? "hastag" : condition.Comparison.ToString().ToLowerInvariant(), key = condition.Value,
            tags = index == 0 ? string.Join(';',group.Tags) : ""
        })) }) };
        var fullPath = System.IO.Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary,JsonSerializer.SerializeToUtf8Bytes(payload,new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary,fullPath,overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
