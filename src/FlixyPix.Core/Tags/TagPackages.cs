using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlixyPix.Core;

/// <summary>One line of a tag package. Missing values stay null and are filled from the dialog or the existing tag.</summary>
public sealed record TagPackageRow(int Line, string Name, string? Category, TagRole? Role, IReadOnlyList<string> Aliases, bool? Favorite, string? Description,
    TagProfile? Profile = null);

public sealed record TagPackage(IReadOnlyList<TagPackageRow> Rows, IReadOnlyList<string> Warnings);

public enum TagPackageStatus { New, Exists, Alias, Conflict, Invalid }

/// <summary>What importing one row would do. <see cref="Target"/> is the existing tag a row updates (Exists, Alias).</summary>
public sealed record TagPackageItem(TagPackageRow Row, string Category, TagRole Role, TagPackageStatus Status, string Note, string? Target)
{
    public bool CanApply => Status is TagPackageStatus.New or TagPackageStatus.Exists or TagPackageStatus.Alias;
}

public sealed record TagPackagePlan(IReadOnlyList<TagPackageItem> Items, string PreferencesSha256);

/// <summary>The FlixyPix tag package: a CSV file (".flixytags.csv") that spreadsheets open and people can keep by hand.
/// <code>
/// # FlixyPix-Tagpaket 1
/// Rolle;Kategorie;Name;Aliasse;Favorit;Beschreibung
/// </code>
/// Profile columns follow when an entry has properties (<see cref="ProfileColumns"/>): dates as yyyy-MM-dd, numbers without
/// grouping, links separated by "|".
/// Only Name is required; aliases are separated by "|". ";" is written, "," and tab are recognised; fields with the separator,
/// line breaks or quotes are quoted (RFC 4180). Lines starting with "#" are comments. A single column without heading is a
/// plain list of names. Written as UTF-8 with BOM (Excel); read as UTF-8, UTF-16 or Windows-1252.</summary>
public static class TagPackages
{
    public const string Signature = "# FlixyPix-Tagpaket 1";
    public const string Extension = ".flixytags.csv";
    public const int MaximumRows = 100_000;
    private const int MaximumLength = 256;
    private static readonly string[] Columns = ["Rolle", "Kategorie", "Name", "Aliasse", "Favorit", "Beschreibung"];
    private static readonly Dictionary<string, string> ColumnNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rolle"] = "Rolle", ["role"] = "Rolle", ["kategorie"] = "Kategorie", ["category"] = "Kategorie", ["name"] = "Name", ["tag"] = "Name",
        ["aliasse"] = "Aliasse", ["alias"] = "Aliasse", ["aliases"] = "Aliasse", ["favorit"] = "Favorit", ["favorite"] = "Favorit", ["favourite"] = "Favorit",
        ["beschreibung"] = "Beschreibung", ["description"] = "Beschreibung",
        ["geschlecht"] = "Geschlecht", ["gender"] = "Geschlecht", ["geburtsdatum"] = "Geburtsdatum", ["birthdate"] = "Geburtsdatum", ["birth date"] = "Geburtsdatum",
        ["todesdatum"] = "Todesdatum", ["deathdate"] = "Todesdatum", ["death date"] = "Todesdatum", ["land"] = "Land", ["country"] = "Land",
        ["ethnie"] = "Ethnie", ["ethnicity"] = "Ethnie", ["haarfarbe"] = "Haarfarbe", ["hair color"] = "Haarfarbe", ["haircolor"] = "Haarfarbe",
        ["augenfarbe"] = "Augenfarbe", ["eye color"] = "Augenfarbe", ["eyecolor"] = "Augenfarbe", ["größe"] = "Größe", ["height"] = "Größe",
        ["gewicht"] = "Gewicht", ["weight"] = "Gewicht", ["maße"] = "Maße", ["measurements"] = "Maße", ["brust"] = "Brust", ["breasts"] = "Brust",
        ["tattoos"] = "Tattoos", ["piercings"] = "Piercings", ["aktiv von"] = "Aktiv von", ["career start"] = "Aktiv von", ["aktiv bis"] = "Aktiv bis",
        ["career end"] = "Aktiv bis", ["gehört zu"] = "Gehört zu", ["parent studio"] = "Gehört zu", ["network"] = "Gehört zu", ["links"] = "Links", ["urls"] = "Links",
    };

    /// <summary>The profile columns in the order they are written; height in cm, weight in kg.</summary>
    public static readonly string[] ProfileColumns = ["Geschlecht", "Geburtsdatum", "Todesdatum", "Land", "Ethnie", "Haarfarbe", "Augenfarbe", "Größe", "Gewicht",
        "Maße", "Brust", "Tattoos", "Piercings", "Aktiv von", "Aktiv bis", "Gehört zu", "Links"];

    private static readonly Dictionary<string, PerformerGender> GenderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weiblich"] = PerformerGender.Female, ["frau"] = PerformerGender.Female, ["female"] = PerformerGender.Female, ["f"] = PerformerGender.Female,
        ["männlich"] = PerformerGender.Male, ["mann"] = PerformerGender.Male, ["male"] = PerformerGender.Male, ["m"] = PerformerGender.Male,
        ["transfrau"] = PerformerGender.TransFemale, ["trans woman"] = PerformerGender.TransFemale, ["transgender_female"] = PerformerGender.TransFemale, ["transfemale"] = PerformerGender.TransFemale,
        ["transmann"] = PerformerGender.TransMale, ["trans man"] = PerformerGender.TransMale, ["transgender_male"] = PerformerGender.TransMale, ["transmale"] = PerformerGender.TransMale,
        ["nichtbinär"] = PerformerGender.NonBinary, ["non-binary"] = PerformerGender.NonBinary, ["non_binary"] = PerformerGender.NonBinary, ["nonbinary"] = PerformerGender.NonBinary,
        ["intersex"] = PerformerGender.Intersex, ["intergeschlechtlich"] = PerformerGender.Intersex,
    };

    private static string GenderName(PerformerGender gender) => gender switch
    {
        PerformerGender.Female => "weiblich", PerformerGender.Male => "männlich", PerformerGender.TransFemale => "Transfrau", PerformerGender.TransMale => "Transmann",
        PerformerGender.NonBinary => "nichtbinär", _ => "intersex",
    };

    /// <summary>The profile of one row; null when it has no profile cells. Values that cannot be read are a format error.</summary>
    private static TagProfile? ReadProfile(Func<string, string?> cell)
    {
        if (ProfileColumns.All(c => cell(c) is null)) return null;
        DateOnly? Date(string column) => cell(column) is not { } text ? null
            : DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date
            : throw new FormatException(T("“{0}” is no date like 1990-03-15.", text));
        int? Number(string column) => cell(column) is not { } text ? null
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number
            : throw new FormatException(T("“{0}” is no whole number.", text));
        var gender = cell("Geschlecht") is { } g ? GenderNames.TryGetValue(g, out var known) ? known : Enum.TryParse<PerformerGender>(g, true, out var parsed) && Enum.IsDefined(parsed) ? parsed
            : throw new FormatException(T("Unknown gender “{0}”.", g)) : (PerformerGender?)null;
        var breasts = cell("Brust")?.ToLowerInvariant() switch
        {
            null => (BreastType?)null, "natürlich" or "natural" or "natur" => BreastType.Natural, "vergrößert" or "enhanced" or "fake" or "operiert" => BreastType.Enhanced,
            var other => throw new FormatException(T("Unknown value “{0}” for breasts (natürlich or vergrößert).", other)),
        };
        var profile = new TagProfile
        {
            Gender = gender, BirthDate = Date("Geburtsdatum"), DeathDate = Date("Todesdatum"), Country = cell("Land"), Ethnicity = cell("Ethnie"), HairColor = cell("Haarfarbe"),
            EyeColor = cell("Augenfarbe"), HeightCm = Number("Größe"), WeightKg = Number("Gewicht"), Measurements = cell("Maße"), Breasts = breasts, Tattoos = cell("Tattoos"),
            Piercings = cell("Piercings"), CareerStart = Number("Aktiv von"), CareerEnd = Number("Aktiv bis"), ParentStudio = cell("Gehört zu"),
            Links = cell("Links")?.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
        }.Normalized();
        try { profile?.Validate(DateOnly.FromDateTime(DateTime.Now)); }
        catch (ArgumentException error) { throw new FormatException(error.Message); }
        return profile;
    }

    private static string[] ProfileCells(TagProfile? profile)
    {
        if (profile is null) return ProfileColumns.Select(_ => "").ToArray();
        static string Date(DateOnly? date) => date is { } value ? TagProfile.IsoDate(value) : "";
        static string Number(int? number) => number?.ToString(CultureInfo.InvariantCulture) ?? "";
        return
        [
            profile.Gender is { } gender ? GenderName(gender) : "", Date(profile.BirthDate), Date(profile.DeathDate), profile.Country ?? "", profile.Ethnicity ?? "",
            profile.HairColor ?? "", profile.EyeColor ?? "", Number(profile.HeightCm), Number(profile.WeightKg), profile.Measurements ?? "",
            profile.Breasts switch { BreastType.Natural => "natürlich", BreastType.Enhanced => "vergrößert", _ => "" }, profile.Tattoos ?? "", profile.Piercings ?? "",
            Number(profile.CareerStart), Number(profile.CareerEnd), profile.ParentStudio ?? "", string.Join('|', profile.Links ?? []),
        ];
    }

    public static string RoleName(TagRole role) => role switch { TagRole.Performer => "Darsteller", TagRole.Studio => "Studio", _ => "Tag" };
    /// <summary>A translated role for display; RoleName remains the stable CSV value.</summary>
    public static string RoleLabel(TagRole role) => role switch { TagRole.Performer => T("Performers"), TagRole.Studio => T("Studio"), _ => T("Tag") };

    public static TagRole? ParseRole(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "tag" or "tags" or "schlagwort" => TagRole.Tag,
        "darsteller" or "darstellerin" or "person" or "personen" or "performer" or "actor" or "actress" or "cast" => TagRole.Performer,
        "studio" or "studios" or "label" => TagRole.Studio,
        _ => throw new FormatException(T("Unknown role “{0}” (allowed: Tag, Darsteller, Studio).", value)),
    };

    private static bool? ParseFlag(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "ja" or "j" or "1" or "true" or "wahr" or "yes" or "y" or "x" => true,
        "nein" or "n" or "0" or "false" or "falsch" or "no" => false,
        _ => throw new FormatException(T("Unknown value “{0}” for favorite (ja or nein).", value)),
    };

    public static TagPackage Read(string path) => Parse(Decode(File.ReadAllBytes(path)));

    /// <summary>UTF-8 or UTF-16 by byte order mark, else UTF-8 when the bytes are valid UTF-8, else Windows-1252 (Excel "CSV").</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    public static TagPackage Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var warnings = new List<string>();
        var records = Records(text, Delimiter(text), warnings);
        var rows = new List<TagPackageRow>();
        if (records.Count == 0) return new(rows, warnings);
        var (firstLine, firstCells) = records[0];
        var heading = firstCells.Select(c => ColumnNames.GetValueOrDefault(c.Trim())).ToList();
        Dictionary<string, int> index;
        var start = 0;
        if (heading.Any(h => h is not null))
        {
            start = 1;
            index = new(StringComparer.Ordinal);
            for (var i = 0; i < heading.Count; i++)
            {
                if (heading[i] is not { } name) { if (firstCells[i].Trim().Length > 0) warnings.Add(T("Line {0}: unknown column “{1}” is ignored.", firstLine, firstCells[i].Trim())); continue; }
                if (!index.TryAdd(name, i)) warnings.Add(T("Line {0}: column “{1}” appears twice; the first one applies.", firstLine, name));
            }
            if (!index.ContainsKey("Name")) throw new InvalidDataException(T("The header row has no column “Name”."));
        }
        else
        {
            index = new() { ["Name"] = 0 };
            if (firstCells.Count > 1) warnings.Add(T("Without a header row, only the first column is read as the name."));
        }
        foreach (var (line, cells) in records.Skip(start))
        {
            string? Cell(string column) => index.TryGetValue(column, out var i) && i < cells.Count && cells[i].Trim() is { Length: > 0 } value ? value : null;
            var name = Cell("Name");
            if (name is null) { if (cells.Any(c => c.Trim().Length > 0)) warnings.Add(T("Line {0}: no name, skipped.", line)); continue; }
            if (rows.Count == MaximumRows) throw new InvalidDataException(Tn(MaximumRows, "A package may have at most 1 entry.", "A package may have at most {0:N0} entries."));
            try
            {
                var aliases = (Cell("Aliasse") ?? "").Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(a => !Same(a, name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var row = new TagPackageRow(line, name, Cell("Kategorie"), ParseRole(Cell("Rolle")), aliases, ParseFlag(Cell("Favorit")), Cell("Beschreibung"), ReadProfile(Cell));
                if (new[] { row.Name, row.Category ?? "" }.Concat(aliases).Any(v => v.Length > MaximumLength)) throw new FormatException(Tn(MaximumLength, "Names, categories and aliases have at most 1 character.", "Names, categories and aliases have at most {0} characters."));
                rows.Add(row);
            }
            catch (FormatException error) { warnings.Add(T("Line {0}: {1} Skipped.", line, error.Message)); }
        }
        return new(rows, warnings);
    }

    /// <summary>The separator that occurs most often outside quotes in the first data line: ";", "," or tab (";" when none).</summary>
    private static char Delimiter(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            var counts = new Dictionary<char, int> { [';'] = 0, [','] = 0, ['\t'] = 0 };
            var quoted = false;
            foreach (var c in line) { if (c == '"') quoted = !quoted; else if (!quoted && counts.ContainsKey(c)) counts[c]++; }
            var best = counts.MaxBy(p => p.Value);
            return best.Value == 0 ? ';' : best.Key;
        }
        return ';';
    }

    /// <summary>RFC 4180 records with the line number they start on; "#" lines (outside quotes) are comments.</summary>
    private static List<(int Line, List<string> Cells)> Records(string text, char delimiter, List<string> warnings)
    {
        var records = new List<(int, List<string>)>();
        var cells = new List<string>(); var cell = new StringBuilder();
        var (line, start, quoted, atStart, comment) = (1, 1, false, true, false);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (comment)
            {
                if (c == '\n') { line++; start = line; comment = false; atStart = true; }
                continue;
            }
            if (atStart && cells.Count == 0 && cell.Length == 0 && c == '#')
            {
                var end = text.IndexOf('\n', i); var commentText = (end < 0 ? text[i..] : text[i..end]).Trim();
                if (commentText.StartsWith("# FlixyPix-Tagpaket", StringComparison.OrdinalIgnoreCase) && commentText != Signature)
                    warnings.Add(T("Line {0}: “{1}” is another version of the format; it is read like version 1.", line, commentText));
                comment = true; continue;
            }
            atStart = false;
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else { if (c == '\n') line++; cell.Append(c); }
                continue;
            }
            if (c == '"' && cell.ToString().Trim().Length == 0) { cell.Clear(); quoted = true; }
            else if (c == delimiter) { cells.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n')
            {
                cells.Add(cell.ToString()); cell.Clear();
                if (cells.Any(v => v.Length > 0) || cells.Count > 1) records.Add((start, cells));
                cells = []; line++; start = line; atStart = true;
            }
            else cell.Append(c);
        }
        if (quoted) warnings.Add(T("Line {0}: quotation mark not closed; the rest of the file belongs to one field.", start));
        cells.Add(cell.ToString());
        if (cells.Any(v => v.Length > 0)) records.Add((start, cells));
        return records;
    }

    public static string Write(IEnumerable<DirectoryEntry> entries)
    {
        var list = entries.ToList();
        // Packages without properties keep the six columns that hand-made packages and older versions know.
        var profiles = list.Any(e => e.Profile is not null);
        var text = new StringBuilder();
        text.Append(Signature).Append("\r\n").Append(string.Join(';', profiles ? Columns.Concat(ProfileColumns) : Columns)).Append("\r\n");
        foreach (var entry in list)
        {
            string[] cells = [RoleName(entry.Role), entry.Category, entry.Tag, string.Join('|', entry.AliasList), entry.IsFavorite ? "ja" : "", entry.Description ?? ""];
            text.Append(string.Join(';', (profiles ? cells.Concat(ProfileCells(entry.Profile)) : cells).Select(Quote))).Append("\r\n");
        }
        return text.ToString();
    }

    /// <summary>Writes UTF-8 with byte order mark, so Excel shows umlauts.</summary>
    public static void Save(string path, IEnumerable<DirectoryEntry> entries) =>
        File.WriteAllText(path, Write(entries), new UTF8Encoding(true));

    private static string Quote(string value) =>
        value.IndexOfAny([';', ',', '\t', '"', '\r', '\n']) >= 0 || value.StartsWith('#') || value.Trim().Length != value.Length
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    /// <summary>Comparison key ignoring case and accents ("Café" = "cafe").</summary>
    public static string Fold(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) folded.Append(c);
        return folded.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

public sealed partial class CatalogRepository
{
    /// <summary>Compares a package with the directory without changing anything.</summary>
    public TagPackagePlan PlanTagPackage(TagPackage package, string? defaultCategory = null, bool allowRoleChanges = false)
    {
        ArgumentNullException.ThrowIfNull(package);
        var directory = GetDirectory();
        lock (gate)
        {
            ThrowIfDisposed();
            var preferences = ReadPreferences();
            var byName = directory.GroupBy(e => TagPackages.Fold(e.Tag)).ToDictionary(g => g.Key, g => g.ToList());
            var categories = directory.Where(e => e.Category.Length > 0).Select(e => e.Category).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seen = new Dictionary<string, int>();
            var items = new List<TagPackageItem>();
            foreach (var row in package.Rows)
            {
                var category = (row.Category ?? defaultCategory)?.Trim() ?? "";
                if (category.Length == 0) { items.Add(new(row, "", row.Role ?? TagRole.Tag, TagPackageStatus.Invalid, T("category missing"), null)); continue; }
                var current = TagRoles.Of(preferences, category);
                var role = row.Role ?? current;
                var key = TagPackages.Fold(row.Name);
                if (!seen.TryAdd(key + "\0" + TagPackages.Fold(category), row.Line))
                { items.Add(new(row, category, role, TagPackageStatus.Invalid, T("duplicate, already in line {0}", seen[key + "\0" + TagPackages.Fold(category)]), null)); continue; }
                if (!allowRoleChanges && row.Role is { } wanted && wanted != current && categories.Contains(category))
                { items.Add(new(row, category, role, TagPackageStatus.Conflict, T("Category “{0}” is already {1}", category, TagPackages.RoleLabel(current)), null)); continue; }
                if (byName.TryGetValue(key, out var named))
                {
                    if (named.FirstOrDefault(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase)) is { } same)
                        items.Add(new(row, category, role, TagPackageStatus.Exists, T("already exists; additional information will be added"), same.Tag));
                    else if (named.All(e => e.Category.Length == 0))
                        items.Add(new(row, category, role, TagPackageStatus.Exists, T("present without a category, gets “{0}”", category), named[0].Tag));
                    else if (row.Role is not null && named.All(e => e.Role != role)) items.Add(new(row, category, role, TagPackageStatus.New, T("new"), null));
                    else items.Add(new(row, category, role, TagPackageStatus.Conflict, T("Already exists in these categories: {0}", string.Join(", ", named.Select(e => e.Category).Where(c => c.Length > 0))), null));
                    continue;
                }
                var keys = row.Aliases.Append(row.Name).Select(TagPackages.Fold).ToHashSet();
                var matches = directory.Where(e => (e.Role == role || allowRoleChanges && Same(e.Category, category)) && (keys.Contains(TagPackages.Fold(e.Tag)) || e.AliasList.Any(a => keys.Contains(TagPackages.Fold(a))))).ToList();
                var local = matches.Where(e => Same(e.Category, category)).ToList();
                if (local.Count == 1) { items.Add(new(row, category, role, TagPackageStatus.Alias, T("matches “{0}”, will be added there", local[0].Tag), local[0].Tag)); continue; }
                if (matches.Count > 0) { items.Add(new(row, category, role, TagPackageStatus.Conflict, T("Conflict"), null)); continue; }
                items.Add(new(row, category, role, TagPackageStatus.New, categories.Contains(category) ? T("new") : T("new, new category ({0})", TagPackages.RoleLabel(role)), null));
            }
            return new(items, PreferencesHash(preferences));
        }
    }

    /// <summary>Adds the chosen rows to the vocabulary in one step; media are never changed. Returns how many rows were applied.</summary>
    public int ApplyTagPackage(TagPackagePlan plan, IReadOnlyCollection<int> lines)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(lines);
        var chosen = plan.Items.Where(i => i.CanApply && lines.Contains(i.Row.Line)).ToList();
        if (chosen.Count == 0) return 0;
        var directory = GetDirectory();
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var count = ApplyTagPackageCore(plan, lines, transaction, false, directory);
            transaction.Commit(); return count;
        }
    }

    private int ApplyTagPackageCore(TagPackagePlan plan, IReadOnlyCollection<int> lines, Microsoft.Data.Sqlite.SqliteTransaction transaction, bool changeRoles, IReadOnlyList<DirectoryEntry> directory)
    {
            var chosen = plan.Items.Where(i => i.CanApply && lines.Contains(i.Row.Line)).ToList();
            var preferences = ReadPreferences(transaction);
            if (PreferencesHash(preferences) != plan.PreferencesSha256) throw new InvalidOperationException(T("The directory has changed in the meantime. Please check the package again."));
            var vocabulary = preferences.TagVocabulary.ToList(); var roles = preferences.TagCategoryRoles.ToList();
            var known = directory.Where(e => e.Category.Length > 0).Select(e => e.Category).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in chosen)
            {
                if (item.Row.Role is { } role && (changeRoles || !known.Contains(item.Category))) { roles = TagRoles.Set(roles, item.Category, role); known.Add(item.Category); }
                var name = item.Target ?? item.Row.Name.Trim();
                var index = vocabulary.FindIndex(v => Same(v.Tag, name) && Same(v.Category, item.Category));
                var existing = index >= 0 ? vocabulary[index] : new TagVocabularyEntry(name, item.Category);
                var extra = item.Target is not null && !Same(item.Row.Name, item.Target) ? [item.Row.Name.Trim()] : Array.Empty<string>();
                var aliases = (existing.Aliases ?? []).Concat(item.Row.Aliases).Concat(extra).Where(a => !Same(a, name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var updated = existing with
                {
                    IsFavorite = item.Row.Favorite ?? existing.IsFavorite, Aliases = aliases.Count == 0 ? null : aliases,
                    Description = item.Row.Description ?? existing.Description,
                    // Properties in the package replace those of the entry; the entry keeps what the package leaves empty.
                    Profile = item.Row.Profile?.FillFrom(existing.Profile).Normalized() ?? existing.Profile,
                };
                if (index >= 0) vocabulary[index] = updated; else vocabulary.Add(updated);
            }
            var saved = preferences with { TagVocabulary = vocabulary, TagCategoryRoles = roles };
            ValidatePreferences(saved);
            if (PreferencesHash(saved) != PreferencesHash(preferences)) SaveDocument("preferences", Guid.Empty, saved, transaction);
            return chosen.Count;
    }
}
