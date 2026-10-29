using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public enum HistoricalParserMode { Standard, Regex, TagRules }
public enum HistoricalParserPropertyType { Text, Number, Percentage, Date }

/// <summary>Explicit formatting context. Confirmed means the caller has reviewed these values, not runtime parity.</summary>
public sealed record HistoricalTagFormatting
{
    public bool Confirmed { get; init; }
    public string SourceDescription { get; init; } = "";
    public string CultureName { get; init; } = "en-US";
    public string ExcludedSubstrings { get; init; } = "";
    public string BypassPatterns { get; init; } = "";
    public bool ExcludeCapitalStart { get; init; }
    public bool ExcludeAllCapitals { get; init; }
    public string ReservedDelimiter { get; init; } = "";
}

public sealed record HistoricalParserPropertyBinding(int PropertyId, string Name, HistoricalParserPropertyType Type, bool HasDomainValues = false);
public sealed record HistoricalParserPropertyValue(int PropertyId, string Name, HistoricalParserPropertyType Type, string Value);

public sealed record HistoricalParserOptions
{
    public HistoricalParserMode Mode { get; init; }
    public int AssignmentMode { get; init; }
    public string RemoveCharacters { get; init; } = "";
    public string ReplaceWithSpace { get; init; } = "";
    public string TagExclusions { get; init; } = "";
    public int TagSeparator { get; init; }
    public bool ParseFilename { get; init; }
    public bool ParsePath { get; init; }
    public bool ApplyExToFilename { get; init; }
    public bool ApplyExToPath { get; init; }
    public bool ApplyTagSepToFilename { get; init; }
    public bool ApplyTagSepToPath { get; init; }
    public int FolderLevel { get; init; }
    public int StandardMatchOption { get; init; }
    public bool RegexIncludePath { get; init; }
    public bool RegexCaseSensitive { get; init; }
    public int RegexMatchOption { get; init; }
    public string RegexTagExclusions { get; init; } = "";
    public string RegexExcludeByToken { get; init; } = "";
    public string RegexTemplate { get; init; } = "";
    public string TitleTemplate { get; init; } = "";
    public string RegexRemoveCharacters { get; init; } = "";
    public string RegexReplaceWithSpace { get; init; } = "";
    public string RegexDateFormat { get; init; } = "yyyy-MM-dd";
    public int RegexFormulaOption { get; init; }
    public List<LegacyParserCategoryTemplate> CategoryTemplates { get; init; } = [];
}

/// <summary>Independent, explicitly activated snapshot. The original structural draft remains inactive and unchanged.</summary>
public sealed record HistoricalParserDefinition
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public Guid SourceDraftId { get; init; }
    public string SourceId { get; init; } = "";
    public string LegacyId { get; init; } = "";
    public string SourcePayloadSha256 { get; init; } = "";
    public HistoricalParserOptions Options { get; init; } = new();
    public HistoricalTagFormatting Formatting { get; init; } = new();
    public List<FilenameTagRule> TagRules { get; init; } = [];
    public List<HistoricalParserPropertyBinding> PropertyBindings { get; init; } = [];
}

public sealed record LegacyParserTranslationPreview(Guid DraftId, string DraftSha256, HistoricalParserDefinition? Definition,
    IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings)
{
    public bool CanActivate => Definition != null && Blockers.Count == 0;
}

public sealed record HistoricalParserEvaluation(IReadOnlyList<string> Tags, IReadOnlyDictionary<string, List<string>> TagCategories,
    string? Title, IReadOnlyList<HistoricalParserPropertyValue> Properties);
public sealed record HistoricalParserMediaChange(Guid MediaId, string BeforeSha256, MediaItem Before, MediaItem After);
public sealed record HistoricalParserPreview(Guid DefinitionId, string StateSha256, IReadOnlyList<Guid> MediaIds,
    IReadOnlyList<HistoricalParserMediaChange> Changes);

/// <summary>Bounded data-only reconstruction of the observed filename-parser consumers; never opens a media path.</summary>
public static class HistoricalFilenameParser
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private static readonly string[] StringMembers = ["_removeCharacters", "_replaceWithSpace", "_tagExclusions", "_regexTagExclusions", "_regexExcludeByToken", "_regexTemplate", "_titleTemplate", "_regex_removeCharacters", "_regex_replaceWithSpace", "_regex_dateformat"];
    private static readonly string[] FlagMembers = ["_parseFilename", "_parsePath", "_applyExToFilename", "_applyExToPath", "_applyTagSepToFilename", "_applyTagSepToPath", "_matchExisting", "_useRegex", "_regexIncludePath", "_regexCaseSensitive", "_regexIncludeExtension", "_regexMatchExisting"];
    private static readonly string[] NumberMembers = ["_tagSeparator", "_folderLevel", "_standardMatchOption", "_regexMatchOption", "_activeTab", "_mode", "_regexFormulaOption"];

    public static LegacyParserTranslationPreview Translate(LegacyParserDraft draft, HistoricalTagFormatting formatting,
        IReadOnlyList<FilenameTagRule>? rules = null, IReadOnlyList<HistoricalParserPropertyBinding>? propertyBindings = null)
    {
        ArgumentNullException.ThrowIfNull(draft); ArgumentNullException.ThrowIfNull(formatting);
        var blockers = new List<string>();
        var warnings = new List<string> { "Static reconstruction; original runtime parity remains unverified. Regex errors/timeouts abort the complete operation." };
        var hash = Hash(draft);
        try
        {
            var decoded = LegacyObjectReader.ReadParserSettings(Convert.FromBase64String(draft.PayloadBase64), draft.SourceId, draft.LegacyId);
            static string Inventory(LegacyParserDraft value) => Hash(new { value.RootType, value.Status, value.Strings, value.Flags, value.Numbers, value.UnknownMembers, value.CategoryTemplates });
            if (decoded.PayloadSha256 != draft.PayloadSha256 || Inventory(decoded) != Inventory(draft)) blockers.Add("Stored parser inventory does not match its original bytes.");
            draft = decoded;
        }
        catch (Exception error) when (error is InvalidDataException or FormatException or ArgumentException)
        { blockers.Add("The parser source bytes cannot be verified: " + error.Message); }
        if (draft.RootType != "MyMediaViewer.frmParseFilenameToTags+ParseFilenameSettings" || draft.Status == HistoricalProjectionStatus.Opaque)
            blockers.Add("Unknown or opaque parser structure.");
        if (draft.UnknownMembers.Count > 0) blockers.Add("Unknown parser members: " + string.Join(", ", draft.UnknownMembers));
        if (StringMembers.Any(name => !draft.Strings.ContainsKey(name)) || FlagMembers.Any(name => !draft.Flags.ContainsKey(name)) || NumberMembers.Any(name => !draft.Numbers.ContainsKey(name)))
            blockers.Add("The parser inventory is incomplete.");
        if (draft.Warnings.Any(w => w.StartsWith("Category templates remain raw:", StringComparison.Ordinal) || w == "Missing parser member: _categoryTemplates"))
            blockers.Add("Category templates were not completely decoded.");
        if (!formatting.Confirmed || string.IsNullOrWhiteSpace(formatting.SourceDescription))
            blockers.Add("The global tag-formatting context must be reviewed explicitly.");
        var mode = draft.Numbers.GetValueOrDefault("_activeTab", -1);
        if (mode is < 0 or > 2) blockers.Add("Unknown parser mode.");
        foreach (var (key, maximum) in new[] { ("_tagSeparator", 5), ("_folderLevel", 5), ("_standardMatchOption", 2), ("_regexMatchOption", 2), ("_mode", 1), ("_regexFormulaOption", 1) })
            if (!draft.Numbers.TryGetValue(key, out var code) || code < 0 || code > maximum) blockers.Add("Unknown parser code: " + key);
        if (mode == 2 && (rules == null || rules.Count == 0)) blockers.Add("Tag-rule mode requires explicitly selected reconstructed rules.");
        if (draft.Flags.GetValueOrDefault("_regexIncludeExtension"))
            warnings.Add("Stored include-extension is ignored to match the original getter, which always returns false.");
        if (draft.Flags.GetValueOrDefault("_matchExisting") || draft.Flags.GetValueOrDefault("_regexMatchExisting") || draft.Flags.GetValueOrDefault("_useRegex"))
            warnings.Add("Legacy compatibility flags are retained in the source; the observed consumers use active tab and numeric match options.");
        string S(string name) => draft.Strings.GetValueOrDefault(name) ?? "";
        bool F(string name) => draft.Flags.GetValueOrDefault(name);
        int N(string name) => draft.Numbers.GetValueOrDefault(name);
        var options = new HistoricalParserOptions
        {
            Mode = (HistoricalParserMode)mode, AssignmentMode = N("_mode"), RemoveCharacters = S("_removeCharacters").Trim(), ReplaceWithSpace = S("_replaceWithSpace").Trim(),
            TagExclusions = S("_tagExclusions"), TagSeparator = N("_tagSeparator"), ParseFilename = F("_parseFilename"), ParsePath = F("_parsePath"),
            ApplyExToFilename = F("_applyExToFilename"), ApplyExToPath = F("_applyExToPath"), ApplyTagSepToFilename = F("_applyTagSepToFilename"),
            ApplyTagSepToPath = F("_applyTagSepToPath"), FolderLevel = N("_folderLevel"), StandardMatchOption = N("_standardMatchOption"),
            RegexIncludePath = F("_regexIncludePath"), RegexCaseSensitive = F("_regexCaseSensitive"), RegexMatchOption = N("_regexMatchOption"),
            RegexTagExclusions = S("_regexTagExclusions"), RegexExcludeByToken = S("_regexExcludeByToken"), RegexTemplate = S("_regexTemplate"),
            TitleTemplate = S("_titleTemplate"), RegexRemoveCharacters = S("_regex_removeCharacters"), RegexReplaceWithSpace = S("_regex_replaceWithSpace"),
            RegexDateFormat = string.IsNullOrEmpty(S("_regex_dateformat")) ? "yyyy-MM-dd" : S("_regex_dateformat"), RegexFormulaOption = N("_regexFormulaOption"),
            CategoryTemplates = draft.CategoryTemplates.OrderBy(t => t.Ordinal).ToList()
        };
        var bindings = propertyBindings?.ToList() ?? [];
        if (mode == 1 && options.AssignmentMode == 1)
            foreach (var template in options.CategoryTemplates.Where(t => t.IsProperty && !string.IsNullOrWhiteSpace(t.Template)))
            {
                var binding = bindings.FirstOrDefault(b => b.PropertyId == template.PropertyId);
                if (binding == null) blockers.Add("Missing property type binding: " + template.PropertyId);
                else if (binding.HasDomainValues) blockers.Add("Domain-valued property assignment is not yet reconstructed: " + template.PropertyId);
            }
        var definition = new HistoricalParserDefinition
        {
            Id = LegacyHistoricalReader.HistoricalId(draft.SourceId, "active-filename-parser", draft.LegacyId), Name = "Historischer Parser " + draft.LegacyId,
            SourceDraftId = draft.Id, SourceId = draft.SourceId, LegacyId = draft.LegacyId, SourcePayloadSha256 = draft.PayloadSha256,
            Options = options, Formatting = formatting, TagRules = rules?.ToList() ?? [], PropertyBindings = bindings
        };
        if (blockers.Count == 0)
        {
            try { Validate(definition); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException) { blockers.Add(error.Message); }
        }
        return new(draft.Id, hash, definition, blockers.Distinct().ToArray(), warnings);
    }

    public static HistoricalParserEvaluation Evaluate(HistoricalParserDefinition definition, MediaItem item,
        IReadOnlyList<TagVocabularyEntry>? vocabulary = null)
    {
        Validate(definition); ArgumentNullException.ThrowIfNull(item);
        if (item.Path.Length > 32768 || item.Title.Length > 32768) throw new InvalidDataException("Parser input exceeds 32768 characters.");
        var options = definition.Options; var formatting = definition.Formatting;
        var culture = CultureInfo.GetCultureInfo(formatting.CultureName);
        var tags = new List<string>(); var categories = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var properties = new List<HistoricalParserPropertyValue>(); string? title = null;
        var known = vocabulary ?? [];
        try
        {
            if (options.Mode == HistoricalParserMode.Standard)
            {
                var separator = new[] { " ", ",", ";", "_", "-", "" }[options.TagSeparator];
                if (options.ParsePath)
                {
                    var directory = Path.GetDirectoryName(item.Path) ?? "";
                    var parts = directory.TrimEnd('\\').Split('\\').Reverse().Where(part => !part.Contains(':'));
                    if (options.FolderLevel > 0) parts = parts.Take(options.FolderLevel);
                    var folder = string.Join(options.ApplyExToPath ? " " : ":", parts).Trim();
                    AddStandard(Split(folder, !options.ApplyExToPath ? ":" : options.ApplyTagSepToPath ? separator : ""), options.ApplyExToPath, options.ApplyTagSepToPath ? separator : "", separator);
                }
                if (options.ParseFilename) AddStandard([Path.GetFileNameWithoutExtension(item.Path)], options.ApplyExToFilename, options.ApplyTagSepToFilename ? separator : "", separator);
            }
            else if (options.Mode == HistoricalParserMode.Regex)
            {
                var name = Path.GetFileNameWithoutExtension(item.Path);
                if (options.RegexIncludePath)
                {
                    var directory = Path.GetDirectoryName(item.Path) ?? "";
                    var root = Path.GetPathRoot(item.Path) ?? "";
                    if (root.Length > 0) directory = directory.Replace(root, "", StringComparison.Ordinal);
                    name = Path.Combine(directory, name);
                }
                name = ReplaceCharacters(name, options.RegexRemoveCharacters, "");
                name = ReplaceCharacters(name, options.RegexReplaceWithSpace, " ");
                var tokens = new List<Token>();
                var hardcodedNames = options.CategoryTemplates.SelectMany(template => Regex.Split(template.Template ?? "", "\\\"(.+?)\\\"", RegexOptions.IgnoreCase, Timeout))
                    .Select(piece => piece.Trim()).Where(piece => piece.Length > 0 && !piece.StartsWith('$')).Select(piece => FormatTag(piece, formatting)).ToHashSet(StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(options.RegexTemplate))
                {
                    var regex = ParserRegex(options, culture);
                    var raw = options.RegexFormulaOption == 0 ? regex.Split(name) : regex.Matches(name).Select(m => m.Groups[1].Value).ToArray();
                    if (raw.Length > 10000) throw new InvalidDataException("Parser output exceeds 10000 tokens.");
                    var formatted = raw.Where(s => s.Trim().Length > 0).Select(s => FormatTag(s, formatting)).ToArray();
                    foreach (var value in MatchExisting(formatted, options.RegexMatchOption, known, culture)) AddToken(value, false, null);
                    foreach (var template in options.CategoryTemplates.Where(Relevant))
                        foreach (var piece in Regex.Split(template.Template ?? "", "\\\"(.+?)\\\"", RegexOptions.IgnoreCase, Timeout))
                            if (piece.Trim().Length > 0 && !piece.Trim().StartsWith('$')) AddToken(FormatTag(piece.Trim(), formatting), true, template);
                }
                var dynamicTokens = tokens.Where(t => !t.Hardcoded).ToArray();
                if (options.TitleTemplate.Length > 0 && dynamicTokens.Length > 0)
                {
                    var next = options.TitleTemplate;
                    for (var i = 0; i < tokens.Count; i++) if (!tokens[i].Hardcoded) next = next.Replace("$" + (i + 1).ToString(CultureInfo.InvariantCulture), tokens[i].Value, StringComparison.Ordinal);
                    foreach (var originalPiece in options.TitleTemplate.Split(' ')) if (originalPiece.Contains('$')) next = next.Replace(originalPiece, "", StringComparison.Ordinal);
                    if (next.Trim().Length > 0) title = next.Trim();
                }
                for (var i = 0; i < tokens.Count; i++)
                {
                    var token = tokens[i];
                    if (!token.Hardcoded && HasToken(options.RegexExcludeByToken, i + 1) || IsExcluded(token.Value, options.RegexTagExclusions, culture)) continue;
                    var template = token.Template;
                    if (template == null && !hardcodedNames.Contains(token.Value))
                        template = options.CategoryTemplates.FirstOrDefault(t => Relevant(t) && HasToken(t.Template ?? "", i + 1));
                    if (options.AssignmentMode == 0)
                    {
                        var committed = AddTag(token.Value);
                        if (committed.Length > 0 && template != null && !string.IsNullOrWhiteSpace(template.Category))
                        {
                            if (!categories.TryGetValue(template.Category, out var group)) categories[template.Category] = group = [];
                            if (!group.Contains(committed, StringComparer.OrdinalIgnoreCase)) group.Add(committed);
                        }
                    }
                    else if (template != null)
                    {
                        var committed = FormatTag(token.Value.Replace("\"", "", StringComparison.Ordinal), formatting).Trim();
                        if (committed.Length == 0) continue;
                        var binding = definition.PropertyBindings.Single(b => b.PropertyId == template.PropertyId);
                        var value = binding.Type switch
                        {
                            HistoricalParserPropertyType.Number or HistoricalParserPropertyType.Percentage => FiniteNumber(committed, culture),
                            HistoricalParserPropertyType.Date => DateTime.ParseExact(committed, options.RegexDateFormat, culture).ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                            _ => committed
                        };
                        properties.Add(new(binding.PropertyId, binding.Name, binding.Type, value));
                    }
                }
                bool Relevant(LegacyParserCategoryTemplate template) => !string.IsNullOrWhiteSpace(template.Template) && template.IsProperty == (options.AssignmentMode == 1);
                void AddToken(string value, bool hardcoded, LegacyParserCategoryTemplate? template)
                {
                    if (value.Trim().Length > 0 && !tokens.Any(t => t.Value.Equals(value, StringComparison.Ordinal))) tokens.Add(new(value, hardcoded, template));
                }
            }
            else
                foreach (var rule in definition.TagRules)
                    foreach (var value in CatalogRepository.EvaluateTagRule(item, rule)) AddTag(FormatTag(value, formatting));
        }
        catch (RegexMatchTimeoutException error) { throw new InvalidOperationException(T("Historical parser: the regular expression exceeded 250 ms; no changes were applied."), error); }
        if (tags.Count > 10000 || properties.Count > 10000) throw new InvalidDataException("Parser output exceeds 10000 values.");
        return new(tags, categories, title, properties);

        void AddStandard(IEnumerable<string> inputs, bool substitutions, string delimiter, string configuredSeparator)
        {
            var formatted = new List<string>();
            foreach (var input in inputs)
            {
                var text = Regex.Replace(input, "\\s{2,}", " ", RegexOptions.None, Timeout);
                if (substitutions)
                {
                    foreach (var value in options.ReplaceWithSpace.Split(' ')) if (value.Length > 0 && value != configuredSeparator) text = text.Replace(value, " ", StringComparison.Ordinal);
                    foreach (var value in options.RemoveCharacters.Split(' ')) if (value.Length > 0 && value != configuredSeparator) text = text.Replace(value, "", StringComparison.Ordinal);
                }
                formatted.AddRange(Split(text, delimiter).Select(value => FormatTag(value, formatting)));
            }
            foreach (var value in MatchExisting(formatted, options.StandardMatchOption, known, culture))
                if (!substitutions || !IsExcluded(value, options.TagExclusions, culture)) AddTag(value);
        }
        string AddTag(string value)
        {
            // The original applies formatTag again when committing parsed tags.
            value = FormatTag(value.Replace("\"", "", StringComparison.Ordinal), formatting).Trim();
            if (value.Length > 256) throw new InvalidDataException("Parsed tag exceeds the catalog's 256-character limit.");
            if (value.Length > 0 && !tags.Contains(value, StringComparer.OrdinalIgnoreCase)) tags.Add(value);
            return value;
        }
    }

    public static string FormatTag(string input, HistoricalTagFormatting formatting)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(formatting);
        var text = input.Trim();
        text = Regex.Replace(text, "<(?:[^>=]|='[^']*'|=\"[^\"]*\"|=[^'\"][^\\s>]*)*>", "", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace, Timeout);
        foreach (var character in "<;>\\/%\"") text = text.Replace(character.ToString(), "", StringComparison.Ordinal).Trim();
        text = text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        foreach (var character in Path.GetInvalidFileNameChars()) text = text.Replace(character.ToString(), "", StringComparison.Ordinal);
        if (formatting.ReservedDelimiter.Length > 0 && text.Contains(formatting.ReservedDelimiter, StringComparison.Ordinal)) return "";
        var bypass = formatting.BypassPatterns.Trim().Split(';').Any(pattern => pattern.Length > 0 &&
            (pattern.Length >= 2 && pattern.StartsWith('%') && pattern.EndsWith('%') ? text.Contains(pattern[1..^1], StringComparison.Ordinal) : text.Equals(pattern, StringComparison.Ordinal)));
        if (bypass) return text;
        foreach (var fragment in formatting.ExcludedSubstrings.Trim().Split(';')) if (fragment.Length > 0) text = text.Replace(fragment, "", StringComparison.Ordinal).Trim();
        if (text.Length > 0 && !text.Contains(' ') && formatting.ExcludeCapitalStart && text[0] is >= 'A' and <= 'Z') return "";
        if (text.Length > 0 && !text.Contains(' ') && formatting.ExcludeAllCapitals && text.All(c => c is >= 'A' and <= 'Z')) return "";
        return text;
    }

    internal static void Validate(HistoricalParserDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Id == Guid.Empty || definition.SourceDraftId == Guid.Empty || string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 4096 ||
            definition.SourcePayloadSha256 is not { Length: 64 } || definition.SourcePayloadSha256.Any(c => !char.IsAsciiHexDigitLower(c)) || definition.Options == null || definition.Formatting == null || definition.TagRules == null || definition.PropertyBindings == null)
            throw new ArgumentException("Invalid historical parser definition.");
        LegacyHistoricalReader.RequireIdentity(definition.SourceId, definition.LegacyId);
        var options = definition.Options; var formatting = definition.Formatting;
        if (!Enum.IsDefined(options.Mode) || options.AssignmentMode is < 0 or > 1 || options.TagSeparator is < 0 or > 5 || options.FolderLevel is < 0 or > 5 ||
            options.StandardMatchOption is < 0 or > 2 || options.RegexMatchOption is < 0 or > 2 || options.RegexFormulaOption is < 0 or > 1 || options.CategoryTemplates == null || options.CategoryTemplates.Count > 1000 ||
            definition.TagRules.Count > 1000 || definition.PropertyBindings.Count > 1000 || !formatting.Confirmed || string.IsNullOrWhiteSpace(formatting.SourceDescription)) throw new ArgumentException("Invalid or unreviewed historical parser options.");
        var strings = new[] { options.RemoveCharacters, options.ReplaceWithSpace, options.TagExclusions, options.RegexTagExclusions, options.RegexExcludeByToken, options.RegexTemplate, options.TitleTemplate,
            options.RegexRemoveCharacters, options.RegexReplaceWithSpace, options.RegexDateFormat, formatting.ExcludedSubstrings, formatting.BypassPatterns, formatting.ReservedDelimiter, formatting.SourceDescription };
        if (strings.Any(s => s == null || s.Length > 16384) || formatting.CultureName == null || formatting.CultureName.Length > 128) throw new ArgumentException("Oversized parser strings.");
        var culture = CultureInfo.GetCultureInfo(formatting.CultureName);
        if (options.CategoryTemplates.Any(t => t == null || t.Ordinal < 0 || t.Template?.Length > 16384 || t.Category?.Length > 256) || options.CategoryTemplates.Select(t => t.Ordinal).Distinct().Count() != options.CategoryTemplates.Count)
            throw new ArgumentException("Invalid category templates.");
        if (!options.CategoryTemplates.OrderBy(t => t.Ordinal).Select(t => t.Ordinal).SequenceEqual(Enumerable.Range(0, options.CategoryTemplates.Count)))
            throw new ArgumentException("Historical category-template ordinals must be contiguous from zero.");
        if (definition.PropertyBindings.Any(b => b == null || b.PropertyId < 0 || string.IsNullOrWhiteSpace(b.Name) || b.Name.Length > 256 || !Enum.IsDefined(b.Type)) || definition.PropertyBindings.Select(b => b.PropertyId).Distinct().Count() != definition.PropertyBindings.Count)
            throw new ArgumentException("Invalid property type bindings.");
        if (options.Mode == HistoricalParserMode.Regex && options.RegexTemplate.Length > 0) _ = ParserRegex(options, culture);
        if (options.Mode == HistoricalParserMode.Regex && options.AssignmentMode == 1 && options.CategoryTemplates.Where(t => t.IsProperty && !string.IsNullOrWhiteSpace(t.Template)).Any(t => !definition.PropertyBindings.Any(b => b.PropertyId == t.PropertyId && !b.HasDomainValues)))
            throw new ArgumentException("Missing property binding or unsupported domain-valued property.");
        if (options.Mode == HistoricalParserMode.TagRules && definition.TagRules.Count == 0) throw new ArgumentException("Tag-rule mode needs explicit rules.");
        foreach (var rule in definition.TagRules) _ = CatalogRepository.EvaluateTagRule(new MediaItem { Path = "C:\\parser-validation\\file.mp4" }, rule);
    }

    internal static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, CatalogRepository.Json)));
    public static string PropertyMetadataKey(string sourceId, int propertyId)
    {
        LegacyHistoricalReader.RequireIdentity(sourceId, propertyId.ToString(CultureInfo.InvariantCulture));
        if (propertyId < 0) throw new ArgumentOutOfRangeException(nameof(propertyId));
        return "historical.parser.property." + LegacyHistoricalReader.HashText(sourceId) + "." + propertyId.ToString(CultureInfo.InvariantCulture);
    }
    private sealed record Token(string Value, bool Hardcoded, LegacyParserCategoryTemplate? Template);
    private static string[] Split(string value, string separator) => separator.Length == 0 ? [value] : value.Split(separator, StringSplitOptions.None);
    private static bool IsExcluded(string value, string exclusions, CultureInfo culture) => exclusions.Split(';').Any(exclusion => string.Equals(exclusion.ToLower(culture), value.ToLower(culture), StringComparison.Ordinal));
    private static bool HasToken(string template, int ordinal) => Regex.Matches(template, "\\$\\d+", RegexOptions.None, Timeout).Any(m => int.TryParse(m.Value.AsSpan(1), out var value) && value == ordinal);
    private static string ReplaceCharacters(string input, string characters, string replacement)
    {
        foreach (var character in characters.Replace(" ", "", StringComparison.Ordinal).Trim()) input = input.Replace(character.ToString(), replacement, StringComparison.Ordinal);
        return input;
    }
    private static Regex ParserRegex(HistoricalParserOptions options, CultureInfo culture)
    {
        // Regex captures the current thread's culture at construction. Restore that thread context immediately.
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            return new Regex(options.RegexTemplate.Replace("(space)", " ", StringComparison.Ordinal).Replace("($space)", "(space)", StringComparison.Ordinal),
                options.RegexCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, Timeout);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }
    private static string FiniteNumber(string text, CultureInfo culture)
    {
        var number = double.Parse(text, NumberStyles.Float | NumberStyles.AllowThousands, culture);
        if (!double.IsFinite(number)) throw new InvalidDataException("Non-finite historical property value.");
        return number.ToString("R", CultureInfo.InvariantCulture);
    }
    private static IReadOnlyList<string> MatchExisting(IReadOnlyList<string> values, int option, IReadOnlyList<TagVocabularyEntry> vocabulary, CultureInfo culture)
    {
        if (option == 0) return values;
        if (vocabulary.Count == 0) return [];
        var words = string.Join(' ', values).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 10000) throw new InvalidDataException("Parser vocabulary matching exceeds 10000 words.");
        var root = new VocabularyNode();
        foreach (var entry in vocabulary)
        {
            var pieces = entry.Tag.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // The original candidate is rebuilt using one space; a differently spaced vocabulary value cannot match it.
            if (string.Join(' ', pieces) != entry.Tag) continue;
            var node = root;
            foreach (var piece in pieces)
            {
                var key = piece.ToLower(culture);
                if (!node.Children.TryGetValue(key, out var next)) node.Children[key] = next = new();
                node = next;
            }
            node.CanonicalValue ??= entry.Tag;
        }
        var output = new List<string>(); var offset = 0;
        while (offset < words.Length)
        {
            string? match = null; var consumed = 1;
            var node = root;
            for (var index = offset; index < words.Length; index++)
            {
                if (!node.Children.TryGetValue(words[index].ToLower(culture), out var next)) break;
                node = next;
                if (node.CanonicalValue != null) { match = node.CanonicalValue; consumed = index - offset + 1; }
            }
            if (match != null) output.Add(match); else if (option == 1) output.Add(words[offset]);
            offset += consumed;
        }
        return output;
    }
    private sealed class VocabularyNode
    {
        internal Dictionary<string, VocabularyNode> Children { get; } = new(StringComparer.Ordinal);
        internal string? CanonicalValue { get; set; }
    }
}

public sealed partial class CatalogRepository
{
    private void InitializeHistoricalParserExecutionSchema(SqliteTransaction transaction)
    {
        using var command = Command("CREATE TABLE IF NOT EXISTS historical_parser_definitions(id TEXT PRIMARY KEY,document TEXT NOT NULL)", transaction); command.ExecuteNonQuery();
    }

    public IReadOnlyList<HistoricalParserDefinition> GetHistoricalParserDefinitions() => ReadHistoricalDocuments<HistoricalParserDefinition>("historical_parser_definitions").OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    public void DeleteHistoricalParserDefinition(Guid id) => DeleteDocument("historical_parser_definitions", id);

    public LegacyParserTranslationPreview PreviewLegacyParserTranslation(Guid draftId, HistoricalTagFormatting formatting,
        IReadOnlyList<FilenameTagRule>? rules = null, IReadOnlyList<HistoricalParserPropertyBinding>? propertyBindings = null)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var draft = GetLegacyParserDrafts().Single(d => d.Id == draftId);
            return HistoricalFilenameParser.Translate(draft, formatting, rules, propertyBindings);
        }
    }

    public HistoricalParserDefinition ActivateLegacyParserTranslation(LegacyParserTranslationPreview preview, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.CanActivate) throw new InvalidOperationException("Historical parser has unresolved translation blockers.");
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            using var read = Command("SELECT document FROM legacy_parser_drafts WHERE id=$id", transaction, ("$id", preview.DraftId.ToString()));
            var draft = read.ExecuteScalar() is string raw ? JsonSerializer.Deserialize<LegacyParserDraft>(raw, Json)! : throw new KeyNotFoundException("Parser draft no longer exists.");
            var proposed = preview.Definition!;
            var current = HistoricalFilenameParser.Translate(draft, proposed.Formatting, proposed.TagRules, proposed.PropertyBindings);
            if (!current.CanActivate || current.DraftSha256 != preview.DraftSha256 || HistoricalFilenameParser.Hash(current.Definition) != HistoricalFilenameParser.Hash(proposed))
                throw new InvalidOperationException("Parser translation preview is stale or modified; create a new preview.");
            var definition = proposed with { Name = name ?? proposed.Name };
            HistoricalFilenameParser.Validate(definition);
            InitializeHistoricalParserExecutionSchema(transaction);
            using var exists = Command("SELECT document FROM historical_parser_definitions WHERE id=$id", transaction, ("$id", definition.Id.ToString()));
            if (exists.ExecuteScalar() is string existing && HistoricalFilenameParser.Hash(JsonSerializer.Deserialize<HistoricalParserDefinition>(existing, Json)) != HistoricalFilenameParser.Hash(definition))
                throw new InvalidOperationException("An activated definition already exists; it is not overwritten by reactivation.");
            SaveDocument("historical_parser_definitions", definition.Id, definition, transaction); transaction.Commit(); return definition;
        }
    }

    public HistoricalParserPreview PreviewHistoricalParser(Guid definitionId, IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);
        if (mediaIds.Count is < 1 or > 10000 || mediaIds.Distinct().Count() != mediaIds.Count) throw new ArgumentException("Select 1–10000 distinct media IDs.");
        lock (gate)
        {
            ThrowIfDisposed(); using var transaction = connection.BeginTransaction();
            var preview = PreviewHistoricalParserCore(definitionId, mediaIds.ToArray(), transaction, cancellationToken); transaction.Commit(); return preview;
        }
    }

    public int ApplyHistoricalParser(HistoricalParserPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.MediaIds == null || preview.MediaIds.Count is < 1 or > 10000 || preview.MediaIds.Distinct().Count() != preview.MediaIds.Count) throw new ArgumentException("Invalid historical parser preview selection.");
        lock (gate)
        {
            ThrowIfDisposed(); using var transaction = connection.BeginTransaction();
            var current = PreviewHistoricalParserCore(preview.DefinitionId, preview.MediaIds, transaction, cancellationToken);
            if (preview.StateSha256 != current.StateSha256) throw new InvalidOperationException("Parser application preview is stale; create a new preview.");
            foreach (var change in current.Changes) { cancellationToken.ThrowIfCancellationRequested(); UpsertCore(change.After, transaction); }
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return current.Changes.Count;
        }
    }

    private HistoricalParserPreview PreviewHistoricalParserCore(Guid definitionId, IReadOnlyList<Guid> mediaIds, SqliteTransaction transaction, CancellationToken token)
    {
        using var readDefinition = Command("SELECT document FROM historical_parser_definitions WHERE id=$id", transaction, ("$id", definitionId.ToString()));
        var definition = readDefinition.ExecuteScalar() is string raw ? JsonSerializer.Deserialize<HistoricalParserDefinition>(raw, Json)! : throw new KeyNotFoundException("Historical parser definition does not exist.");
        var preferences = ReadPreferences(transaction); var changes = new List<HistoricalParserMediaChange>(); var beforeHashes = new List<string>();
        foreach (var id in mediaIds)
        {
            token.ThrowIfCancellationRequested();
            using var readMedia = Command("SELECT document FROM media WHERE id=$id", transaction, ("$id", id.ToString()));
            var item = readMedia.ExecuteScalar() is string itemRaw ? JsonSerializer.Deserialize<MediaItem>(itemRaw, Json)! : throw new KeyNotFoundException("Selected media no longer exists: " + id);
            var hash = HistoricalFilenameParser.Hash(item); beforeHashes.Add(hash);
            var result = HistoricalFilenameParser.Evaluate(definition, item, preferences.TagVocabulary);
            var categories = item.TagCategories.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var (category, values) in result.TagCategories) categories[category] = categories.GetValueOrDefault(category, []).Concat(values).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var extra = new Dictionary<string, string?>(item.Extra);
            foreach (var group in result.Properties.GroupBy(p => p.PropertyId)) extra[HistoricalFilenameParser.PropertyMetadataKey(definition.SourceId, group.Key)] = JsonSerializer.Serialize(group.ToArray(), Json);
            var after = item with { Tags = item.Tags.Concat(result.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), TagCategories = categories, Title = result.Title ?? item.Title, Extra = extra };
            if (HistoricalFilenameParser.Hash(after) != hash) changes.Add(new(id, hash, item, after));
        }
        var state = HistoricalFilenameParser.Hash(new { Definition = definition, Vocabulary = preferences.TagVocabulary, MediaIds = mediaIds, Before = beforeHashes });
        return new(definitionId, state, mediaIds.ToArray(), changes);
    }

    private void SavePortableHistoricalParserDefinition(JsonElement value, SqliteTransaction transaction)
    {
        var definition = value.Deserialize<HistoricalParserDefinition>(Json) ?? throw new InvalidDataException("Null historical parser definition.");
        HistoricalFilenameParser.Validate(definition); InitializeHistoricalParserExecutionSchema(transaction);
        SaveDocument("historical_parser_definitions", definition.Id, definition, transaction);
    }
}
