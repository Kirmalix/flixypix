namespace FlixyPix.Core;

/// <summary>A time window for file date or import date. Relative windows are resolved when the search runs, so a saved
/// "last 30 days" stays current.</summary>
public enum DateWindow { Any, Last7Days, Last30Days, LastYear, OlderThanYear, OlderThan2Years, OlderThan5Years, Custom }

public sealed record DateFilter(DateWindow Window = DateWindow.Any, DateTimeOffset? From = null, DateTimeOffset? To = null)
{
    public bool IsActive => Window != DateWindow.Any && (Window != DateWindow.Custom || From is not null || To is not null);

    public string Describe() => Window switch
    {
        DateWindow.Last7Days => T("last 7 days"), DateWindow.Last30Days => T("last 30 days"), DateWindow.LastYear => T("last year"),
        DateWindow.OlderThanYear => T("older than 1 year"), DateWindow.OlderThan2Years => T("older than 2 years"), DateWindow.OlderThan5Years => T("older than 5 years"),
        DateWindow.Custom => (From, To) switch
        {
            ({ } from, { } to) => $"{from:d}–{to:d}", ({ } from, null) => T("from {0:d}", from), (null, { } to) => T("until {0:d}", to), _ => "",
        },
        _ => "",
    };

    internal IEnumerable<SearchExpression> Conditions(SearchField field, DateTimeOffset now)
    {
        SearchExpression At(SearchComparison comparison, DateTimeOffset date) =>
            SearchExpression.Where(new() { Field = field, Comparison = comparison, Date = date });
        switch (Window)
        {
            case DateWindow.Last7Days: yield return At(SearchComparison.GreaterOrEqual, now.AddDays(-7)); break;
            case DateWindow.Last30Days: yield return At(SearchComparison.GreaterOrEqual, now.AddDays(-30)); break;
            case DateWindow.LastYear: yield return At(SearchComparison.GreaterOrEqual, now.AddYears(-1)); break;
            case DateWindow.OlderThanYear: yield return At(SearchComparison.LessThan, now.AddYears(-1)); break;
            case DateWindow.OlderThan2Years: yield return At(SearchComparison.LessThan, now.AddYears(-2)); break;
            case DateWindow.OlderThan5Years: yield return At(SearchComparison.LessThan, now.AddYears(-5)); break;
            case DateWindow.Custom:
                if (From is { } from) yield return At(SearchComparison.GreaterOrEqual, from);
                // "To" includes the whole day.
                if (To is { } to) yield return At(SearchComparison.LessThan, to.Date == to.DateTime ? to.AddDays(1) : to);
                break;
        }
    }
}

/// <summary>A performer, studio or tag in the filter bar; an empty category is a tag used without one.</summary>
public sealed record TagFacet(string Category, string Tag, TagRole Role)
{
    internal SearchExpression Condition() => Category.Length == 0
        ? SearchExpression.Where(new() { Field = SearchField.Tag, Comparison = SearchComparison.Equals, Text = Tag })
        : CatalogRepository.HasCategorizedTag(new TagKey(Category, Tag));
}

/// <summary>Resolution classes like the tile badges: the shorter side of the picture counts.</summary>
public enum ResolutionClass { Any, HD, FullHD, UHD, EightK }

/// <summary>The filter bar: performers and studios to include or exclude, ranges for duration and size, file date and import
/// date, resolution and watched state. It becomes one search expression next to the advanced search.</summary>
public sealed record MediaFilter
{
    public bool UnratedOnly { get; init; }
    public bool? HasThumbnail { get; init; }
    public List<TagFacet> Include { get; init; } = [];
    public List<TagFacet> Exclude { get; init; } = [];
    /// <summary>Roles whose included entries need only one match ("mindestens einer"); the others need all.</summary>
    public List<TagRole> AnyOf { get; init; } = [];
    public double? MinMinutes { get; init; }
    public double? MaxMinutes { get; init; }
    public double? MinMegabytes { get; init; }
    public double? MaxMegabytes { get; init; }
    public ResolutionClass Resolution { get; init; }
    public DateFilter FileDate { get; init; } = new();
    public DateFilter Imported { get; init; } = new();
    /// <summary>true: watched at least once; false: never watched.</summary>
    public bool? Watched { get; init; }
    /// <summary>Profile conditions (<see cref="SearchField.Profile"/>); a medium needs a performer or studio matching each.</summary>
    public List<SearchCondition> Profiles { get; init; } = [];
    /// <summary>Included studios also find the media of the studios that belong to them.</summary>
    public bool IncludeSubStudios { get; init; }
    public DateFilter Released { get; init; } = new();
    /// <summary>Tags a bookmark or scene of the medium must have, each of them (<see cref="SearchField.MarkTag"/>).</summary>
    public List<string> MarkTags { get; init; } = [];
    /// <summary>Video codecs (ffprobe names) of which the medium has one.</summary>
    public List<string> VideoCodecs { get; init; } = [];
    /// <summary>true: only HDR; false: only media read as not HDR.</summary>
    public bool? Hdr { get; init; }
    /// <summary>At least this many frames per second.</summary>
    public double? MinFrameRate { get; init; }
    /// <summary>true: media with further versions; false: media with one file.</summary>
    public bool? MultipleFiles { get; init; }

    public bool IsEmpty => !UnratedOnly && HasThumbnail is null && Include.Count == 0 && Exclude.Count == 0 && MinMinutes is null && MaxMinutes is null && MinMegabytes is null && MaxMegabytes is null &&
        Resolution == ResolutionClass.Any && !FileDate.IsActive && !Imported.IsActive && Watched is null && Profiles.Count == 0 && !Released.IsActive && MarkTags.Count == 0 &&
        VideoCodecs.Count == 0 && Hdr is null && MinFrameRate is null && MultipleFiles is null;

    public static int Lines(ResolutionClass resolution) => resolution switch
    {
        ResolutionClass.HD => 720, ResolutionClass.FullHD => 1080, ResolutionClass.UHD => 2160, ResolutionClass.EightK => 4320, _ => 0,
    };

    public SearchExpression? ToExpression(DateTimeOffset now)
    {
        var parts = new List<SearchExpression>();
        if (UnratedOnly) parts.Add(SearchExpression.Where(new() { Field = SearchField.Rating, Comparison = SearchComparison.Equals, Number = 0 }));
        if (HasThumbnail is { } has) parts.Add(SearchExpression.Where(new() { Field = SearchField.HasThumbnail, Comparison = SearchComparison.Equals, Flag = has }));
        foreach (var group in Include.GroupBy(f => f.Role))
        {
            var conditions = group.Select(f => IncludeSubStudios && f.Role == TagRole.Studio
                ? CatalogRepository.HasProfile(ProfileProperty.Studio, SearchComparison.Equals, f.Tag) : f.Condition()).ToArray();
            parts.Add(conditions.Length == 1 ? conditions[0] : AnyOf.Contains(group.Key) ? SearchExpression.Any(conditions) : SearchExpression.All(conditions));
        }
        if (Exclude.Count > 0) parts.Add(SearchExpression.Not(Exclude.Count == 1 ? Exclude[0].Condition() : SearchExpression.Any(Exclude.Select(f => f.Condition()).ToArray())));
        SearchExpression Number(SearchField field, SearchComparison comparison, double value) =>
            SearchExpression.Where(new() { Field = field, Comparison = comparison, Number = value });
        if (MinMinutes is { } minMinutes) parts.Add(Number(SearchField.Duration, SearchComparison.GreaterOrEqual, minMinutes * 60));
        if (MaxMinutes is { } maxMinutes) parts.Add(Number(SearchField.Duration, SearchComparison.LessOrEqual, maxMinutes * 60));
        if (MinMegabytes is { } minSize) parts.Add(Number(SearchField.FileSize, SearchComparison.GreaterOrEqual, minSize * 1024 * 1024));
        if (MaxMegabytes is { } maxSize) parts.Add(Number(SearchField.FileSize, SearchComparison.LessOrEqual, maxSize * 1024 * 1024));
        if (Lines(Resolution) is > 0 and var lines) parts.Add(Number(SearchField.Lines, SearchComparison.GreaterOrEqual, lines));
        if (VideoCodecs.Count > 0)
        {
            var codecs = VideoCodecs.Select(codec => SearchExpression.Where(new() { Field = SearchField.VideoCodec, Comparison = SearchComparison.Equals, Text = codec })).ToArray();
            parts.Add(codecs.Length == 1 ? codecs[0] : SearchExpression.Any(codecs));
        }
        if (Hdr is { } hdr) parts.Add(SearchExpression.Where(new() { Field = SearchField.Hdr, Comparison = SearchComparison.Equals, Flag = hdr }));
        // Rates such as 59.94 count as 60: the limit allows for the NTSC fraction.
        if (MinFrameRate is { } rate) parts.Add(Number(SearchField.FrameRate, SearchComparison.GreaterOrEqual, rate * 0.99));
        if (MultipleFiles is { } multiple) parts.Add(Number(SearchField.Versions, multiple ? SearchComparison.GreaterThan : SearchComparison.Equals, 1));
        parts.AddRange(FileDate.Conditions(SearchField.FileDate, now));
        parts.AddRange(Imported.Conditions(SearchField.AddedAt, now));
        parts.AddRange(Released.Conditions(SearchField.ReleaseDate, now));
        parts.AddRange(Profiles.Select(SearchExpression.Where));
        parts.AddRange(MarkTags.Select(tag => SearchExpression.Where(new() { Field = SearchField.MarkTag, Comparison = SearchComparison.Equals, Text = tag })));
        if (Watched is { } watched)
        {
            var seen = Number(SearchField.WatchCount, SearchComparison.GreaterThan, 0);
            parts.Add(watched ? seen : SearchExpression.Not(seen));
        }
        return parts.Count switch { 0 => null, 1 => parts[0], _ => SearchExpression.All(parts.ToArray()) };
    }

    /// <summary>Short labels for the active parts, for chips and the count line.</summary>
    public IEnumerable<string> Describe()
    {
        static string Number(double value) => value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        if (UnratedOnly) yield return T("Unrated");
        if (HasThumbnail is { } has) yield return has ? T("With thumbnail") : T("Without thumbnail");
        foreach (var facet in Include) yield return facet.Tag;
        foreach (var facet in Exclude) yield return T("without {0}", facet.Tag);
        foreach (var profile in Profiles) yield return DescribeProfile(profile);
        foreach (var tag in MarkTags) yield return T("Jump mark: {0}", tag);
        if (MinMinutes is not null || MaxMinutes is not null)
            yield return (MinMinutes, MaxMinutes) switch
            {
                ({ } min, { } max) => T("{0}–{1} min", Number(min), Number(max)), ({ } min, null) => T("from {0} min", Number(min)), (null, { } max) => T("up to {0} min", Number(max)), _ => "",
            };
        if (MinMegabytes is not null || MaxMegabytes is not null)
            yield return (MinMegabytes, MaxMegabytes) switch
            {
                ({ } min, { } max) => T("{0}–{1} MB", Number(min), Number(max)), ({ } min, null) => T("from {0} MB", Number(min)), (null, { } max) => T("up to {0} MB", Number(max)), _ => "",
            };
        if (Resolution != ResolutionClass.Any) yield return T("from {0}", Resolution switch { ResolutionClass.HD => "HD", ResolutionClass.FullHD => T("Full HD"), ResolutionClass.UHD => "4K", _ => "8K" });
        if (FileDate.IsActive) yield return T("File {0}", FileDate.Describe());
        if (Imported.IsActive) yield return T("imported {0}", Imported.Describe());
        if (Watched is { } watched) yield return watched ? T("watched") : T("never watched");
        if (Released.IsActive) yield return T("released {0}", Released.Describe());
        foreach (var codec in VideoCodecs) yield return TechnicalLabels.Codec(codec);
        if (Hdr is { } hdr) yield return hdr ? "HDR" : T("without HDR");
        if (MinFrameRate is { } rate) yield return T("from {0} fps", rate.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture));
        if (MultipleFiles is { } multiple) yield return multiple ? T("several versions") : T("one file");
    }

    /// <summary>A short label such as "Hair color: blond" or "Age at recording ≥ 25".</summary>
    public static string DescribeProfile(SearchCondition condition)
    {
        var property = condition.Profile ?? ProfileProperty.Country;
        var label = ProfileProperties.Label(property);
        var value = property switch
        {
            ProfileProperty.Gender when Enum.TryParse<PerformerGender>(condition.Text, true, out var gender) => ProfileProperties.Label(gender),
            ProfileProperty.Breasts when Enum.TryParse<BreastType>(condition.Text, true, out var breasts) => ProfileProperties.Label(breasts),
            _ => condition.Number?.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) ?? condition.Text ?? "",
        };
        return condition.Comparison switch
        {
            SearchComparison.IsKnown => T("{0}: known", label), SearchComparison.IsUnknown => T("{0}: unknown", label),
            SearchComparison.GreaterOrEqual => $"{label} ≥ {value}", SearchComparison.GreaterThan => $"{label} > {value}",
            SearchComparison.LessOrEqual => $"{label} ≤ {value}", SearchComparison.LessThan => $"{label} < {value}",
            SearchComparison.Contains => T("{0} contains “{1}”", label, value),
            _ => $"{label}: {value}",
        };
    }
}
