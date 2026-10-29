using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlixyPix.Core;

/// <summary>Gender of a performer; the values follow the common directories (Stash, StashDB), so later matching can map them.</summary>
public enum PerformerGender { Female, Male, TransFemale, TransMale, NonBinary, Intersex }

public enum BreastType { Natural, Enhanced }

/// <summary>The profile of a directory entry: properties of a performer, the network of a studio, links. Every field is
/// optional; fields of later versions are kept when an older one saves.</summary>
public sealed record TagProfile
{
    public PerformerGender? Gender { get; init; }
    public DateOnly? BirthDate { get; init; }
    public DateOnly? DeathDate { get; init; }
    /// <summary>Country of origin or nationality as written by the user.</summary>
    public string? Country { get; init; }
    public string? Ethnicity { get; init; }
    public string? HairColor { get; init; }
    public string? EyeColor { get; init; }
    public int? HeightCm { get; init; }
    public int? WeightKg { get; init; }
    /// <summary>Free text such as "34C-24-34".</summary>
    public string? Measurements { get; init; }
    public BreastType? Breasts { get; init; }
    public string? Tattoos { get; init; }
    public string? Piercings { get; init; }
    public int? CareerStart { get; init; }
    public int? CareerEnd { get; init; }
    /// <summary>Studios: the name of the studio or network this one belongs to.</summary>
    public string? ParentStudio { get; init; }
    public List<string>? Links { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; init; }

    public const int MaximumText = 256, MaximumLinks = 20, MaximumLink = 2048;

    [JsonIgnore]
    public bool IsEmpty => Gender is null && BirthDate is null && DeathDate is null && Blank(Country) && Blank(Ethnicity) && Blank(HairColor) && Blank(EyeColor) &&
        HeightCm is null && WeightKg is null && Blank(Measurements) && Breasts is null && Blank(Tattoos) && Blank(Piercings) && CareerStart is null && CareerEnd is null &&
        Blank(ParentStudio) && (Links is null || Links.Count == 0) && (Unknown is null || Unknown.Count == 0);

    /// <summary>Full years on the given day; null without birth date or before it. A performer who died stays at the age of death.
    /// Someone born on 29 February has birthday on 1 March in other years, as in the search (SQLite date arithmetic).</summary>
    public int? AgeOn(DateOnly day)
    {
        if (BirthDate is not { } birth) return null;
        if (DeathDate is { } death && death < day) day = death;
        if (day < birth) return null;
        var age = day.Year - birth.Year;
        if (day < Birthday(birth, day.Year)) age--;
        return age;
    }

    private static DateOnly Birthday(DateOnly birth, int year) =>
        birth is { Month: 2, Day: 29 } && !DateTime.IsLeapYear(year) ? new DateOnly(year, 3, 1) : new DateOnly(year, birth.Month, birth.Day);

    /// <summary>This profile with its empty fields filled from the other one (merging entries).</summary>
    public TagProfile FillFrom(TagProfile? other)
    {
        if (other is null) return this;
        var unknown = new Dictionary<string, JsonElement>(other.Unknown ?? []);
        foreach (var (key, value) in Unknown ?? []) unknown[key] = value;
        return this with
        {
            Gender = Gender ?? other.Gender, BirthDate = BirthDate ?? other.BirthDate, DeathDate = DeathDate ?? other.DeathDate,
            Country = Pick(Country, other.Country), Ethnicity = Pick(Ethnicity, other.Ethnicity), HairColor = Pick(HairColor, other.HairColor), EyeColor = Pick(EyeColor, other.EyeColor),
            HeightCm = HeightCm ?? other.HeightCm, WeightKg = WeightKg ?? other.WeightKg, Measurements = Pick(Measurements, other.Measurements), Breasts = Breasts ?? other.Breasts,
            Tattoos = Pick(Tattoos, other.Tattoos), Piercings = Pick(Piercings, other.Piercings), CareerStart = CareerStart ?? other.CareerStart, CareerEnd = CareerEnd ?? other.CareerEnd,
            ParentStudio = Pick(ParentStudio, other.ParentStudio),
            Links = (Links ?? []).Concat(other.Links ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } links ? links : null,
            Unknown = unknown.Count == 0 ? null : unknown,
        };
    }

    /// <summary>The profile with trimmed texts and without empty ones; null when nothing is left.</summary>
    public TagProfile? Normalized()
    {
        var links = Links?.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = this with
        {
            Country = Clean(Country), Ethnicity = Clean(Ethnicity), HairColor = Clean(HairColor), EyeColor = Clean(EyeColor), Measurements = Clean(Measurements),
            Tattoos = Clean(Tattoos), Piercings = Clean(Piercings), ParentStudio = Clean(ParentStudio), Links = links is { Count: > 0 } ? links : null,
        };
        return result.IsEmpty ? null : result;
    }

    /// <summary>Rejects values out of range: dates in the future, death before birth, sizes and years beyond what people have.</summary>
    public void Validate(DateOnly today)
    {
        foreach (var text in new[] { Country, Ethnicity, HairColor, EyeColor, Measurements, Tattoos, Piercings, ParentStudio })
            if (text?.Length > MaximumText) throw new ArgumentException(T("Profile texts may be at most 256 characters long."));
        if (Gender is { } gender && !Enum.IsDefined(gender) || Breasts is { } breasts && !Enum.IsDefined(breasts)) throw new ArgumentException("Unknown profile value.");
        if (BirthDate > today || DeathDate > today) throw new ArgumentException(T("Birth and death dates cannot be in the future."));
        if (BirthDate is { } birth && DeathDate is { } death && death < birth) throw new ArgumentException(T("The date of death lies before the date of birth."));
        if (BirthDate?.Year < 1850 || DeathDate?.Year < 1850) throw new ArgumentException(T("Birth and death dates start in 1850."));
        if (HeightCm is < 50 or > 260) throw new ArgumentException(T("The height must be between 50 and 260 cm."));
        if (WeightKg is < 20 or > 400) throw new ArgumentException(T("The weight must be between 20 and 400 kg."));
        if (CareerStart is < 1850 || CareerEnd is < 1850 || CareerStart > today.Year || CareerEnd > today.Year || CareerStart > CareerEnd)
            throw new ArgumentException(T("Active years must lie between 1850 and this year, the first not after the last."));
        if (Links is { } links && (links.Count > MaximumLinks || links.Any(l => l.Length > MaximumLink || !Uri.TryCreate(l, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))))
            throw new ArgumentException(T("Links must be web addresses (http or https); at most 20."));
    }

    /// <summary>The value of a property that is compared as text; numbers and dates come from <see cref="Number"/>.</summary>
    public string? Text(ProfileProperty property) => property switch
    {
        ProfileProperty.Gender => Gender?.ToString(), ProfileProperty.Breasts => Breasts?.ToString(),
        ProfileProperty.Country => Country, ProfileProperty.Ethnicity => Ethnicity, ProfileProperty.HairColor => HairColor, ProfileProperty.EyeColor => EyeColor,
        ProfileProperty.Tattoos => Tattoos, ProfileProperty.Piercings => Piercings, _ => null,
    };

    public double? Number(ProfileProperty property, DateOnly today) => property switch
    {
        ProfileProperty.Height => HeightCm, ProfileProperty.Weight => WeightKg, ProfileProperty.Age => AgeOn(today), ProfileProperty.CareerStart => CareerStart, _ => null,
    };

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Pick(string? own, string? other) => Blank(own) ? other : own;

    internal static string IsoDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>What a profile condition of the search looks at. Text properties compare as text, the others as numbers;
/// <see cref="AgeAtRecording"/> needs the medium's release date (or file date) and <see cref="Studio"/> follows the
/// network of studios.</summary>
public enum ProfileProperty
{
    Gender, Country, Ethnicity, HairColor, EyeColor, Breasts, Tattoos, Piercings,
    Height, Weight, Age, CareerStart, AgeAtRecording,
    /// <summary>The studio named in the condition's text or one of the studios that belong to it, at any depth.</summary>
    Studio,
}

public static class ProfileProperties
{
    public static bool IsNumber(ProfileProperty property) => property is ProfileProperty.Height or ProfileProperty.Weight or ProfileProperty.Age or
        ProfileProperty.CareerStart or ProfileProperty.AgeAtRecording;

    /// <summary>Properties with a fixed list of values (enum names in the condition's text).</summary>
    public static bool IsChoice(ProfileProperty property) => property is ProfileProperty.Gender or ProfileProperty.Breasts;

    public static string Label(ProfileProperty property) => property switch
    {
        ProfileProperty.Gender => T("Gender"), ProfileProperty.Country => T("Country"), ProfileProperty.Ethnicity => T("Ethnicity"),
        ProfileProperty.HairColor => T("Hair color"), ProfileProperty.EyeColor => T("Eye color"), ProfileProperty.Breasts => T("Breasts"),
        ProfileProperty.Tattoos => T("Tattoos"), ProfileProperty.Piercings => T("Piercings"), ProfileProperty.Height => T("Height (cm)"),
        ProfileProperty.Weight => T("Weight (kg)"), ProfileProperty.Age => T("Age today"), ProfileProperty.CareerStart => T("Active since"),
        ProfileProperty.AgeAtRecording => T("Age at recording"), _ => T("Studio with sub-studios"),
    };

    public static string Label(PerformerGender gender) => gender switch
    {
        PerformerGender.Female => T("Female"), PerformerGender.Male => T("Male"), PerformerGender.TransFemale => T("Trans woman"),
        PerformerGender.TransMale => T("Trans man"), PerformerGender.NonBinary => T("Non-binary"), _ => T("Intersex"),
    };

    public static string Label(BreastType breasts) => breasts == BreastType.Natural ? T("Natural") : T("Enhanced");
}
