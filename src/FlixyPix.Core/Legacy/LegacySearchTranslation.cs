using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FlixyPix.Core;

/// <summary>A proposed independent saved search. Historical drafts themselves never become executable.</summary>
public sealed record LegacySearchTranslation(SavedSearch? ProposedSearch, IReadOnlyList<string> Blockers, IReadOnlyList<string> Notes)
{
    public bool CanApply => ProposedSearch is not null && Blockers.Count == 0;
}

/// <summary>Both source and existing target are fingerprinted; applying a preview always recomputes its proposal.</summary>
public sealed record LegacySearchTranslationPreview(Guid DraftId, string SourceSha256, string? ExistingSearchSha256,
    SavedSearch? CurrentSearch, SavedSearch? ProposedSearch, IReadOnlyList<string> Blockers, IReadOnlyList<string> Notes)
{
    public bool CanApply => ProposedSearch is not null && Blockers.Count == 0;
}

/// <summary>An original keyword row that gives its tags to a category (type 20), a folder (18) or a folder group (21) of videos.</summary>
public sealed record LegacyInheritedTags(int Type, string Keyword, string KeyTags);

public static partial class LegacyHistoricalReader
{
    // Constants of the original program: its field delimiter (module1.UDELIM) and its markers for exact and excluded tags and for content rows.
    private const string KeywordDelimiter = "DT5ttEr9RE90reFjmw", ExactTag = "[***!***]", ExcludeTag = "[!!!*!!!]";
    private const string CategoryPrefix = "Category: ", FolderPrefix = "Folder: ";

    /// <summary>Translate only understood active semantics. This reads data, never SQL or original objects.</summary>
    /// <param name="inherited">The source's keyword rows of types 18, 20 and 21 (tags of folders and categories); null when they
    /// are not available, which blocks tag searches because the original included those tags.</param>
    public static LegacySearchTranslation TranslateSearchSettings(LegacySearchDraft draft, string? name = null, IReadOnlyList<LegacyInheritedTags>? inherited = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var blockers = new List<string>();
        var notes = new List<string> { "Translation follows static original consumers; runtime equivalence remains unverified.",
            "Text comparisons use FlixyPix OrdinalIgnoreCase; original SQL CE collation may differ." };
        if (draft.RawXmlSha256 != HashText(draft.RawXml))
            return new(null, ["Historical XML hash does not match its preserved bytes."], notes);
        LegacySearchDraft inventory;
        try { inventory = ReadSearchSettings(draft.RawXml, draft.SourceId, draft.LegacyId, draft.SearchType); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        { return new(null, ["Historical search cannot be translated: " + error.Message], notes); }
        if (inventory.Kind is not (MediaKind.Image or MediaKind.Video) || inventory.Status == HistoricalProjectionStatus.Opaque)
            return new(null, ["Unknown historical search root."], notes);
        var kind = inventory.Kind.Value;
        if (draft.SearchType is not (500 or 501 or 502 or 503) || (draft.SearchType is 500 or 501) != (kind == MediaKind.Video))
            blockers.Add("Historical search type and XML root must identify the same video/image search.");
        var image = kind == MediaKind.Image;
        var prefix = image ? "Picture" : "Movie";
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            image ? "CmboKeyword1" : "CmboKeyword", image ? "ChkKeyPicFilter1" : "ChkKeyFilter1",
            image ? "ChkKeyPicFilter2" : "ChkKeyFilter2", image ? "ChkKeyPicFilter4" : "ChkKeyFilter4",
            "Chk" + prefix + "SearchUnion", "Chk" + prefix + "Filesize", "Cmb" + prefix + "Filesize", "N" + prefix + "Filesize",
            "Chk" + prefix + "Width", "Cmb" + prefix + "Width", "N" + prefix + "Width",
            "Chk" + prefix + "Height", "Cmb" + prefix + "Height", "N" + prefix + "Height",
            "Chk" + prefix + "Date", "Cmb" + prefix + "Date", "Dtp" + prefix + "Date",
            "Chk" + prefix + "Type", "Cmb" + prefix + "Type", image ? "ChkImageOrientation" : "ChkVideoOrientation",
            image ? "CmbImageOrientation" : "CmbVideoOrientation", "Chk" + prefix + "Rating",
            image ? "CmbPicRatingOp" : "CmbVidRatingOp", "ImgStarRating",
            image ? "G_pictureSearchKeywords" : "G_moviesearchkeywords", image ? "ChkListDirectory1" : "ChkListDirectory",
            "PropertiesValueList"
        };
        for (var index = 1; index <= 4; index++) known.Add((image ? "RbSearchPicCriteria" : "RbSearchCriteria") + index);
        if (!image) known.UnionWith(["ChkBookmark", "ChkMovieKbps", "CmbMovieKBPS", "NMovieKbps", "ChkMovieLength", "CmbMovieLength", "NMovieLength"]);
        // ReadSearchSettings has already prohibited DTD/entities and bounded XML depth/size.
        var root = XDocument.Parse(draft.RawXml).Root!;
        if (root.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration)) blockers.Add("Unrecognized attributes on historical search root.");
        var fields = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in root.Elements())
        {
            if (element.Name.NamespaceName.Length != 0 || !known.Contains(element.Name.LocalName))
            { blockers.Add("Unknown search field: " + element.Name); continue; }
            if (!fields.TryAdd(element.Name.LocalName, element)) blockers.Add("Duplicate search field: " + element.Name.LocalName);
            if (element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration &&
                !(attribute.Name.NamespaceName == "http://www.w3.org/2001/XMLSchema-instance" && attribute.Name.LocalName == "nil" && attribute.Value == "true" && !element.HasElements && element.Value.Length == 0)))
                blockers.Add("Unrecognized attributes on search field: " + element.Name.LocalName);
            if (element.HasElements && element.Name.LocalName != "PropertiesValueList") blockers.Add("Complex search field: " + element.Name.LocalName);
        }
        string Text(string field) => fields.GetValueOrDefault(field)?.Value ?? "";
        bool Flag(string field)
        {
            if (!fields.ContainsKey(field)) return false;
            var text = Text(field).Trim();
            if (text == "true" || text == "1") return true;
            if (text != "false" && text != "0") blockers.Add("Invalid boolean search field: " + field);
            return false;
        }
        // Validate every present flag, including inactive options, rather than treating malformed values as false.
        var flags = fields.Keys.Where(field => field.StartsWith("Chk", StringComparison.Ordinal) && !field.StartsWith("ChkListDirectory", StringComparison.Ordinal) || field.StartsWith("RbSearch", StringComparison.Ordinal))
            .ToDictionary(field => field, Flag, StringComparer.Ordinal);
        bool Enabled(string field) => flags.GetValueOrDefault(field);
        var conditions = new List<SearchExpression> { SearchExpression.Where(new() { Field = SearchField.Kind, Comparison = SearchComparison.Equals, Text = kind.ToString() }) };
        void Numeric(string suffix, SearchField field, double scale = 1, string? valueName = null, string? comparisonName = null)
        {
            if (!Enabled("Chk" + prefix + suffix)) return;
            var text = Text(valueName ?? "N" + prefix + suffix).Trim();
            // Width/height/rating are explicitly ignored by the original consumer when their input is empty.
            if (text.Length == 0 && suffix is "Width" or "Height" or "Rating") return;
            if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < 0 ||
                suffix is "Filesize" && value != Math.Truncate(value) || field == SearchField.Rating && value > 10)
            { blockers.Add("Invalid numeric search value: " + (valueName ?? "N" + prefix + suffix)); return; }
            if (!TryComparison(Text(comparisonName ?? "Cmb" + prefix + suffix), out var comparison))
            { blockers.Add("Unsupported numeric comparison: " + (comparisonName ?? "Cmb" + prefix + suffix)); return; }
            if (!double.IsFinite(value * scale)) { blockers.Add("Numeric search value exceeds range."); return; }
            conditions.Add(SearchExpression.Where(new() { Field = field, Comparison = comparison, Number = value * scale }));
        }
        Numeric("Filesize", SearchField.FileSize, image ? 1000 : 1_000_000);
        Numeric("Width", SearchField.Width);
        Numeric("Height", SearchField.Height);
        Numeric("Rating", SearchField.Rating, valueName: "ImgStarRating", comparisonName: image ? "CmbPicRatingOp" : "CmbVidRatingOp");
        if (!image && Enabled("ChkMovieLength"))
        {
            if (!DateTime.TryParse(Text("NMovieLength"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) || value.Kind != DateTimeKind.Unspecified ||
                !TryComparison(Text("CmbMovieLength"), out var comparison) || value < new DateTime(2000, 1, 1))
                blockers.Add("Video length requires a timezone-free original DateTime at or after 2000-01-01 and a known comparison.");
            else conditions.Add(SearchExpression.Where(new() { Field = SearchField.Duration, Comparison = comparison,
                Number = Math.Round((value - new DateTime(2000, 1, 1)).TotalSeconds, MidpointRounding.ToEven) }));
        }
        if (Enabled("Chk" + prefix + "Type") && Text("Cmb" + prefix + "Type").Trim() is { Length: > 0 } extension)
        {
            if (extension.Length > 32 || extension.Any(character => !char.IsAsciiLetterOrDigit(character))) blockers.Add("Unsupported filename extension filter.");
            else conditions.Add(SearchExpression.Where(new() { Field = SearchField.Extension, Comparison = SearchComparison.Equals, Text = extension }));
        }
        if (Enabled("Chk" + prefix + "Date")) blockers.Add("Historical date filters address filedate, not FlixyPix import time; exact timezone-free filedate projection is required.");
        // Portrait is height > width, landscape height <= width; any other text adds no condition, as in the original.
        if (Enabled(image ? "ChkImageOrientation" : "ChkVideoOrientation") && Text(image ? "CmbImageOrientation" : "CmbVideoOrientation").Trim() is "Portrait" or "Landscape")
            conditions.Add(SearchExpression.Where(new() { Field = SearchField.Portrait, Comparison = SearchComparison.Equals,
                Flag = Text(image ? "CmbImageOrientation" : "CmbVideoOrientation").Trim() == "Portrait" }));
        if (!image && Enabled("ChkMovieKbps"))
        {
            Numeric("Kbps", SearchField.Bitrate, valueName: "NMovieKbps", comparisonName: "CmbMovieKBPS");
            notes.Add("Bitrate compares the average bit rate from file size and duration; the original compared the kbps value its media scan stored.");
        }
        if (!image && Enabled("ChkBookmark")) blockers.Add("Historical ChkBookmark filters the renamed marker, not actual bookmarks; this marker is not yet translated.");
        SearchExpression? tagExpression = null;
        var tagCriteria = Enumerable.Range(1, 4).Where(index => Enabled((image ? "RbSearchPicCriteria" : "RbSearchCriteria") + index)).ToArray();
        if (Text(image ? "G_pictureSearchKeywords" : "G_moviesearchkeywords").Length != 0)
        {
            if (image) blockers.Add("Historical image tag searches use their own folder and untagged semantics and are not yet translated.");
            else tagExpression = TranslateLegacyTags(Text("G_moviesearchkeywords"), tagCriteria, inherited, blockers);
        }
        if (Text(image ? "ChkListDirectory1" : "ChkListDirectory").Length != 0) blockers.Add("Historical checked-directory/category choices are not yet translated.");
        SearchExpression? keywordExpression = null;
        if (fields.TryGetValue("PropertiesValueList", out var properties) && (properties.HasElements || properties.Value.Length != 0))
            blockers.Add("Historical property-search definitions are not yet translated.");
        var keyword = Text(image ? "CmboKeyword1" : "CmboKeyword").Trim();
        if (keyword.Length != 0)
        {
            var columns = new List<SearchField>();
            var filename = Enabled(image ? "ChkKeyPicFilter1" : "ChkKeyFilter1");
            var path = Enabled(image ? "ChkKeyPicFilter2" : "ChkKeyFilter2");
            var title = Enabled(image ? "ChkKeyPicFilter4" : "ChkKeyFilter4");
            if (filename || !path && !title) columns.Add(SearchField.Filename);
            if (path) columns.Add(SearchField.Directory);
            if (title) columns.Add(SearchField.Title);
            keywordExpression = TranslateLegacyKeyword(keyword, columns, blockers);
        }
        // "Union" returns keyword matches and tag matches together (both with the other filters), except for "none of these tags".
        if (keywordExpression is not null && tagExpression is not null && Enabled("Chk" + prefix + "SearchUnion") && !tagCriteria.SequenceEqual([3]))
            conditions.Add(SearchExpression.Any(keywordExpression, tagExpression));
        else conditions.AddRange(new[] { keywordExpression, tagExpression }.OfType<SearchExpression>());
        var selectedName = (name ?? draft.Name).Trim();
        if (selectedName.Length is < 1 or > 4096) blockers.Add("Saved search name must contain 1–4096 characters.");
        var search = new SavedSearch { Id = HistoricalId(draft.SourceId, "translated-search", draft.LegacyId), Name = selectedName,
            Expression = conditions.Count == 1 ? conditions[0] : SearchExpression.All(conditions.ToArray()) };
        try { CatalogRepository.ValidateSearchExpression(search.Expression); }
        catch (ArgumentException error) { blockers.Add("Search expression exceeds the supported bounds: " + error.Message); }
        return new(search, blockers.Distinct(StringComparer.Ordinal).ToArray(), notes);
    }

    /// <summary>Video tag search of the original (refreshVideoRecordset with parseTagSQL): stored entries are tags, optionally marked
    /// exact or excluded. Plain entries match part of a tag, exact ones a whole tag. Criteria 1 and 3 join them with OR, 2 with AND;
    /// excluded tags always apply. Besides the video's own tags, tags given to its category or a folder above it count as well;
    /// criterion 3 ("none of these") negates all of that. Criterion 4 (videos without tags) is matched by the original through
    /// file names or checksums and stays blocked.</summary>
    private static SearchExpression? TranslateLegacyTags(string stored, int[] criteria, IReadOnlyList<LegacyInheritedTags>? inherited, List<string> blockers)
    {
        if (criteria.Length != 1) { blockers.Add("Historical tag search needs exactly one of the four tag criteria."); return null; }
        var mode = criteria[0];
        if (mode == 4) { blockers.Add("Historical \"videos without tags\" matches tagged rows by file name or checksum; this is not yet translated."); return null; }
        var entries = new List<(string Tag, bool Exact, bool Exclude)>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in stored.Split(KeywordDelimiter + KeywordDelimiter))
        {
            if (!entry.Contains(KeywordDelimiter, StringComparison.Ordinal)) continue;
            var key = entry.Split(KeywordDelimiter)[0];
            if (!seen.Add(key)) continue;
            var exclude = key.Contains(ExcludeTag, StringComparison.Ordinal); var exact = !exclude && key.Contains(ExactTag, StringComparison.Ordinal);
            var tag = key.Replace(ExcludeTag, "", StringComparison.Ordinal).Replace(ExactTag, "", StringComparison.Ordinal);
            if (tag.Length is 0 or > 200 || tag.IndexOfAny(['%', '_', '[', ']', '"', '\r', '\n']) >= 0)
            { blockers.Add("Historical tag entry is empty, too long or uses SQL wildcard or double quote characters."); return null; }
            entries.Add((tag, exact, exclude));
        }
        if (entries.Count is 0 or > 64) { blockers.Add("Historical tag search needs 1–64 readable entries."); return null; }
        if (inherited is null) { blockers.Add("Tags the original gave to categories and folders are unavailable for this source; the tag search cannot be completed."); return null; }
        SearchExpression Tag(string tag, bool whole) => SearchExpression.Where(new() { Field = SearchField.Tag, Comparison = whole ? SearchComparison.Equals : SearchComparison.Contains, Text = tag });
        var positive = entries.Where(e => !e.Exclude).ToArray(); var excluded = entries.Where(e => e.Exclude).ToArray();
        var anyOf = mode is 1 or 3;
        // The original compares the keytags text "a" "b" of one row, so a plain entry is part of one tag and an exact or excluded entry a whole tag.
        var parts = new List<SearchExpression>();
        if (positive.Length > 0)
        {
            var matches = positive.Select(e => Tag(e.Tag, e.Exact)).ToArray();
            parts.Add(matches.Length == 1 ? matches[0] : anyOf ? SearchExpression.Any(matches) : SearchExpression.All(matches));
        }
        parts.AddRange(excluded.Select(e => SearchExpression.Not(Tag(e.Tag, true))));
        // Only videos with a keyword row take part; a video without tags has none.
        var own = SearchExpression.All([SearchExpression.Where(new() { Field = SearchField.Tag, Comparison = SearchComparison.IsKnown }), .. parts]);
        bool RowMatches(string keyTags)
        {
            bool Has(string tag, bool whole) => keyTags.Contains(whole ? "\"" + tag + "\"" : tag, StringComparison.OrdinalIgnoreCase);
            var found = positive.Length == 0 || (anyOf ? positive.Any(e => Has(e.Tag, e.Exact)) : positive.All(e => Has(e.Tag, e.Exact)));
            return found && excluded.All(e => !Has(e.Tag, true));
        }
        var categories = new List<string>(); var folders = new List<string>();
        foreach (var row in inherited.Where(row => RowMatches(row.KeyTags)))
        {
            if (row.Type == 20 && row.Keyword.StartsWith(CategoryPrefix, StringComparison.Ordinal) && row.Keyword.Length > CategoryPrefix.Length) categories.Add(row.Keyword[CategoryPrefix.Length..]);
            else if (row.Type == 18 && row.Keyword.StartsWith(FolderPrefix, StringComparison.Ordinal) && row.Keyword.Length > FolderPrefix.Length &&
                row.Keyword.IndexOfAny(['%', '_', '[', ']']) < 0) folders.Add(row.Keyword[FolderPrefix.Length..]);
            else { blockers.Add("A matching tag row of a folder group or with an unreadable name is not yet translated."); return null; }
        }
        var inheritedMatches = categories.Distinct(StringComparer.OrdinalIgnoreCase).Select(category => SearchExpression.Where(new() { Field = SearchField.Category, Comparison = SearchComparison.Equals, Text = category }))
            .Concat(folders.Distinct(StringComparer.OrdinalIgnoreCase).Select(folder => SearchExpression.Where(new() { Field = SearchField.Directory, Comparison = SearchComparison.StartsWith, Text = folder }))).ToArray();
        if (inheritedMatches.Length > 128) { blockers.Add("Too many categories and folders carry the searched tags."); return null; }
        var any = inheritedMatches.Length == 0 ? own : SearchExpression.Any([own, .. inheritedMatches]);
        if (mode != 3) return any;
        // "categoryid NOT IN (…)" is false for videos without a category once some category matches.
        return categories.Count == 0 ? SearchExpression.Not(any)
            : SearchExpression.All(SearchExpression.Where(new() { Field = SearchField.Category, Comparison = SearchComparison.IsKnown }), SearchExpression.Not(any));
    }

    private static bool TryComparison(string value, out SearchComparison comparison)
    {
        comparison = value.Trim() switch { "=" => SearchComparison.Equals, "<>" or "!=" => SearchComparison.NotEquals,
            ">" => SearchComparison.GreaterThan, ">=" => SearchComparison.GreaterOrEqual,
            "<" => SearchComparison.LessThan, "<=" => SearchComparison.LessOrEqual, _ => (SearchComparison)(-1) };
        return Enum.IsDefined(comparison);
    }

    private static SearchExpression? TranslateLegacyKeyword(string value, IReadOnlyList<SearchField> columns, List<string> blockers)
    {
        if (value.Length > 4096 || value.IndexOfAny(['%', '_', '[', ']', '\t', '\r', '\n']) >= 0 || value.Contains(":\\", StringComparison.Ordinal) || value.Contains('|'))
        { blockers.Add("Keyword uses an unsupported SQL wildcard, control character or historical multi-path syntax."); return null; }
        var negative = new List<string>();
        if (value.Split(' ').Any(term => term.StartsWith('-') && term.Length > 1))
        {
            if (value.Contains('"')) { blockers.Add("Combined quoted and excluded keyword syntax needs separate original verification."); return null; }
            foreach (var term in value.Split(' ').Where(term => term.StartsWith('-') && term.Length > 1).Distinct())
            { negative.Add(term[1..]); value = value.Replace(term, "", StringComparison.Ordinal); }
        }
        var positive = new List<(string Value, bool Exact)>();
        var quoted = Regex.Matches(value, "\"([^\"]*)\"", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        foreach (Match match in quoted)
        {
            if (match.Groups[1].Value.Length > 0) positive.Add((match.Groups[1].Value, true));
            value = value.Replace(match.Value, "", StringComparison.OrdinalIgnoreCase);
        }
        if (value.Contains('"')) { blockers.Add("Unbalanced keyword quotes."); return null; }
        value = value.Trim();
        if (value.EndsWith(" or", StringComparison.OrdinalIgnoreCase)) value = value[..^3].Trim();
        if (value.EndsWith(" and", StringComparison.OrdinalIgnoreCase)) value = value[..^4].Trim();
        value = Regex.Replace(value, " (?:and) ", "*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        value = Regex.Replace(value, " (?:or) ", ",", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        value = value.Replace(", ", ",", StringComparison.Ordinal).Replace(' ', '*');
        foreach (var chunk in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (chunk.Contains('*') && !chunk.StartsWith('*') && !chunk.EndsWith('*'))
                positive.AddRange(chunk.Split('*', StringSplitOptions.RemoveEmptyEntries).Where(term => !term.Equals("and", StringComparison.OrdinalIgnoreCase) && !term.Equals("or", StringComparison.OrdinalIgnoreCase)).Select(term => ("*" + term + "*", false)));
            else if (!chunk.Equals("and", StringComparison.OrdinalIgnoreCase) && !chunk.Equals("or", StringComparison.OrdinalIgnoreCase)) positive.Add((chunk, false));
        }
        if (positive.Count == 0 || positive.Count + negative.Count > 64)
        { blockers.Add("Keyword translation requires 1–64 positive/excluded terms and at least one positive term."); return null; }
        var all = positive.Any(term => term.Value.Contains('*'));
        var perColumn = new List<SearchExpression>();
        foreach (var column in columns)
        {
            SearchExpression? Match(string term, bool exact = false)
            {
                var comparison = SearchComparison.Contains;
                var text = term;
                if (exact && term.Contains('*')) { blockers.Add("Quoted wildcard keyword is not yet translated."); return null; }
                if (!exact && term.Contains('*'))
                {
                    text = term.Trim('*');
                    if (text.Length == 0 || text.Contains('*')) { blockers.Add("Complex or empty wildcard keyword is not yet translated."); return null; }
                    if (term.StartsWith('*') && !term.EndsWith('*')) text += "."; // Original '*name' becomes SQL pattern '%name.%'.
                    else if (!term.StartsWith('*')) comparison = SearchComparison.StartsWith;
                }
                return SearchExpression.Where(new() { Field = column, Comparison = comparison, Text = text });
            }
            var matches = positive.Select(term => Match(term.Value, term.Exact)).ToArray();
            if (matches.Any(expression => expression is null)) return null;
            var completeMatches = matches.Cast<SearchExpression>().ToArray();
            var group = matches.Length == 1 ? matches[0]! : all ? SearchExpression.All(completeMatches) : SearchExpression.Any(completeMatches);
            var parts = new List<SearchExpression> { group };
            foreach (var term in negative) { var match = Match(term); if (match is null) return null; parts.Add(SearchExpression.Not(match)); }
            perColumn.Add(parts.Count == 1 ? group : SearchExpression.All(parts.ToArray()));
        }
        return perColumn.Count == 1 ? perColumn[0] : SearchExpression.Any(perColumn.ToArray());
    }
}

public sealed partial class CatalogRepository
{
    public LegacySearchTranslationPreview PreviewLegacySearchTranslation(Guid draftId, string? name = null, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            var (raw, draft) = ReadLegacySearchTranslationSource(draftId, transaction);
            var translation = LegacyHistoricalReader.TranslateSearchSettings(draft, name, ReadLegacyInheritedTags(draft.SourceId, transaction));
            var existing = ReadLegacySearchTranslationTarget(translation.ProposedSearch?.Id, transaction);
            transaction.Commit();
            return new(draftId, LegacyHistoricalReader.HashText(raw), existing is null ? null : LegacyHistoricalReader.HashText(existing),
                existing is null ? null : JsonSerializer.Deserialize<SavedSearch>(existing, Json), translation.ProposedSearch, translation.Blockers, translation.Notes);
        }
    }

    public SavedSearch ApplyLegacySearchTranslation(LegacySearchTranslationPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        lock (gate)
        {
            ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            var (raw, draft) = ReadLegacySearchTranslationSource(preview.DraftId, transaction);
            if (LegacyHistoricalReader.HashText(raw) != preview.SourceSha256) throw new InvalidOperationException("Historical search changed after preview; preview it again.");
            var translation = LegacyHistoricalReader.TranslateSearchSettings(draft, preview.ProposedSearch?.Name, ReadLegacyInheritedTags(draft.SourceId, transaction));
            if (!preview.CanApply || !translation.CanApply || JsonSerializer.Serialize(preview.ProposedSearch, Json) != JsonSerializer.Serialize(translation.ProposedSearch, Json))
                throw new InvalidOperationException("Historical search proposal is incomplete or changed; preview it again.");
            var search = translation.ProposedSearch!;
            var existing = ReadLegacySearchTranslationTarget(search.Id, transaction);
            if ((existing is null ? null : LegacyHistoricalReader.HashText(existing)) != preview.ExistingSearchSha256)
                throw new InvalidOperationException("Saved search changed after preview; preview it again.");
            ValidateSavedSearch(search);
            SaveDocument("saved_searches", search.Id, search, transaction);
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return search;
        }
    }

    private (string Raw, LegacySearchDraft Draft) ReadLegacySearchTranslationSource(Guid id, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        using var command = Command("SELECT document FROM legacy_search_drafts WHERE id=$id", transaction, ("$id", id.ToString()));
        var raw = command.ExecuteScalar() as string ?? throw new KeyNotFoundException("Historical search draft does not exist.");
        return (raw, JsonSerializer.Deserialize<LegacySearchDraft>(raw, Json) ?? throw new InvalidDataException("Invalid historical search draft."));
    }

    /// <summary>Keyword rows of types 18, 20 and 21 from the latest takeover of a source, kept verbatim; null when no takeover of it is stored.</summary>
    private List<LegacyInheritedTags>? ReadLegacyInheritedTags(string sourceId, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        using var latest = Command("SELECT batch FROM legacy_imports WHERE source_id=$source ORDER BY rowid DESC LIMIT 1", transaction, ("$source", sourceId));
        if (latest.ExecuteScalar() is not string batch) return null;
        using var command = Command("SELECT json_extract(record,'$.values.type'),json_extract(record,'$.values.keyword'),json_extract(record,'$.values.keytags') FROM legacy_records " +
            "WHERE batch=$batch AND json_extract(record,'$.type')='row' AND json_extract(record,'$.table')='keyword' COLLATE NOCASE AND json_extract(record,'$.values.type') IN (18,20,21) ORDER BY ordinal",
            transaction, ("$batch", batch));
        using var reader = command.ExecuteReader(); var rows = new List<LegacyInheritedTags>();
        while (reader.Read()) rows.Add(new(reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2)));
        return rows;
    }

    private string? ReadLegacySearchTranslationTarget(Guid? id, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        if (id is null) return null;
        using var command = Command("SELECT document FROM saved_searches WHERE id=$id", transaction, ("$id", id.Value.ToString()));
        return command.ExecuteScalar() as string;
    }
}
