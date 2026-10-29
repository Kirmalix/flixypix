using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Properties of performers and studios: storing, merging, the studio network and searching by them.</summary>
public sealed class PerformerProfileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-performer-profiles-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogRepository catalog;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);

    public PerformerProfileTests()
    {
        Directory.CreateDirectory(directory);
        catalog = new CatalogRepository(Path.Combine(directory, "catalog.db"));
        // Jane was 20 in 2010 (release date), John 30 by the file date of b.mp4; c.mp4 has nobody.
        catalog.Upsert(new() { Path = PathFor("a.mp4"), Kind = MediaKind.Video, ReleaseDate = new DateOnly(2010, 6, 1),
            TagCategories = new() { ["Darsteller"] = ["Jane Doe"], ["Studios"] = ["Acme Teens"] } });
        catalog.Upsert(new() { Path = PathFor("b.mp4"), Kind = MediaKind.Video, FileCreatedAt = new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TagCategories = new() { ["Darsteller"] = ["John Roe"], ["Studios"] = ["Acme"] } });
        catalog.Upsert(new() { Path = PathFor("c.mp4"), Kind = MediaKind.Video, TagCategories = new() { ["Studios"] = ["Other"] } });
        Save("Jane Doe", "Darsteller", new TagProfile { Gender = PerformerGender.Female, BirthDate = new DateOnly(1990, 3, 15), HairColor = "Blond", HeightCm = 165, Country = "Deutschland" });
        Save("John Roe", "Darsteller", new TagProfile { Gender = PerformerGender.Male, BirthDate = new DateOnly(1984, 12, 31), HairColor = "Brown", HeightCm = 185 });
    }

    public void Dispose() { catalog.Dispose(); SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }
    private string PathFor(string name) => Path.Combine(directory, name);
    private DirectoryEntry Entry(string tag) => catalog.GetDirectory().Single(e => e.Tag == tag);
    private void Save(string tag, string category, TagProfile profile) => catalog.SaveDirectoryEntry(tag, category, Entry(tag) with { Profile = profile });
    private string[] Find(SearchExpression expression) =>
        catalog.QueryPage(new MediaQuery { Expression = expression, Limit = 100 }).Items.Select(m => Path.GetFileName(m.Path)).Order().ToArray();
    private string[] Find(ProfileProperty property, SearchComparison comparison, string? text = null, double? number = null) =>
        Find(CatalogRepository.HasProfile(property, comparison, text, number));

    [Fact]
    public void ProfileIsStoredNormalizedAndValidated()
    {
        Save("Jane Doe", "Darsteller", Entry("Jane Doe").Profile! with { Country = "  ", EyeColor = " Grün ", Links = [" https://example.org/jane ", ""] });
        var profile = Entry("Jane Doe").Profile!;
        Assert.Equal((null, "Grün"), (profile.Country, profile.EyeColor));
        Assert.Equal(["https://example.org/jane"], profile.Links);
        Assert.Throws<ArgumentException>(() => Save("Jane Doe", "Darsteller", profile with { BirthDate = Today.AddDays(1) }));
        Assert.Throws<ArgumentException>(() => Save("Jane Doe", "Darsteller", profile with { DeathDate = new DateOnly(1980, 1, 1) }));
        Assert.Throws<ArgumentException>(() => Save("Jane Doe", "Darsteller", profile with { HeightCm = 400 }));
        Assert.Throws<ArgumentException>(() => Save("Jane Doe", "Darsteller", profile with { Links = ["javascript:alert(1)"] }));
        Assert.Throws<ArgumentException>(() => Save("Jane Doe", "Darsteller", profile with { CareerStart = 2010, CareerEnd = 2005 }));
        Assert.Equal(165, Entry("Jane Doe").Profile!.HeightCm);
        // An emptied profile is removed instead of stored empty.
        Save("Jane Doe", "Darsteller", new TagProfile { HairColor = " " });
        Assert.Null(Entry("Jane Doe").Profile);
    }

    [Fact]
    public void AgeCountsFullYearsAndStopsAtDeath()
    {
        var profile = new TagProfile { BirthDate = new DateOnly(2000, 2, 29) };
        Assert.Equal(17, profile.AgeOn(new DateOnly(2018, 2, 28)));
        Assert.Equal(18, profile.AgeOn(new DateOnly(2018, 3, 1)));
        Assert.Null(profile.AgeOn(new DateOnly(1999, 1, 1)));
        Assert.Equal(20, (profile with { DeathDate = new DateOnly(2020, 6, 1) }).AgeOn(new DateOnly(2030, 1, 1)));
        Assert.Null(new TagProfile().AgeOn(Today));
    }

    [Fact]
    public void RenameMergeAndDeleteKeepProfilesAndStudioNetwork()
    {
        Save("Acme Teens", "Studios", new TagProfile { ParentStudio = "Acme" });
        catalog.UpsertVocabularyTag(new("Acme", "Studios"));
        Save("Acme", "Studios", new TagProfile { ParentStudio = "Acme Network" });
        // A loop is refused: the network cannot belong to its own sub-studio.
        catalog.UpsertVocabularyTag(new("Acme Network", "Studios"));
        Assert.Throws<ArgumentException>(() => Save("Acme Network", "Studios", new TagProfile { ParentStudio = "Acme Teens" }));
        Assert.Throws<ArgumentException>(() => Save("Acme", "Studios", new TagProfile { ParentStudio = "Acme" }));

        catalog.SaveDirectoryEntry("Acme", "Studios", Entry("Acme") with { Tag = "Acme Media" });
        Assert.Equal("Acme Media", Entry("Acme Teens").Profile!.ParentStudio);
        Assert.Equal("Acme Network", Entry("Acme Media").Profile!.ParentStudio);

        // Merging fills empty fields of the target from the merged entry; the target's own values win.
        catalog.UpsertVocabularyTag(new("J. Roe", "Darsteller", Profile: new TagProfile { HairColor = "Black", EyeColor = "Blue" }));
        catalog.MergeTags(["J. Roe"], "John Roe");
        var john = Entry("John Roe").Profile!;
        Assert.Equal(("Brown", "Blue", 185), (john.HairColor, john.EyeColor, john.HeightCm));

        var undo = catalog.DeleteTags(["Acme Media"]).Undo!;
        Assert.Null(Entry("Acme Teens").Profile);
        Assert.True(catalog.UndoTagChange(undo));
        Assert.Equal("Acme Media", Entry("Acme Teens").Profile!.ParentStudio);
    }

    [Fact]
    public void ProfileConditionsFindMediaOfMatchingPerformers()
    {
        Assert.Equal(["a.mp4"], Find(ProfileProperty.HairColor, SearchComparison.Equals, "blond"));
        Assert.Equal(["b.mp4"], Find(ProfileProperty.Gender, SearchComparison.Equals, "Male"));
        Assert.Equal(["b.mp4"], Find(ProfileProperty.Height, SearchComparison.GreaterOrEqual, number: 180));
        Assert.Equal(["a.mp4"], Find(ProfileProperty.Country, SearchComparison.IsKnown));
        Assert.Equal(["a.mp4", "b.mp4"], Find(ProfileProperty.Age, SearchComparison.GreaterThan, number: 30));
        Assert.Empty(Find(ProfileProperty.EyeColor, SearchComparison.Contains, "green"));
        // A performer given the property later is found by the same (saved) condition.
        Save("John Roe", "Darsteller", Entry("John Roe").Profile! with { Country = "Canada" });
        Assert.Equal(["a.mp4", "b.mp4"], Find(ProfileProperty.Country, SearchComparison.IsKnown));
        Assert.Throws<ArgumentException>(() => CatalogRepository.HasProfile(ProfileProperty.Gender, SearchComparison.Equals, "robot"));
        Assert.Throws<ArgumentException>(() => CatalogRepository.HasProfile(ProfileProperty.AgeAtRecording, SearchComparison.Equals, number: 20.5));
    }

    [Fact]
    public void AgeAtRecordingUsesReleaseDateOtherwiseFileDate()
    {
        // Jane: born 1990-03-15, release 2010-06-01 → 20. John: born 1984-12-31, file date 2015-01-01 → 30.
        Assert.Equal(["a.mp4"], Find(ProfileProperty.AgeAtRecording, SearchComparison.Equals, number: 20));
        Assert.Equal(["b.mp4"], Find(ProfileProperty.AgeAtRecording, SearchComparison.GreaterOrEqual, number: 30));
        Assert.Equal(["a.mp4"], Find(ProfileProperty.AgeAtRecording, SearchComparison.LessThan, number: 21));
        Assert.Equal(["a.mp4"], Find(ProfileProperty.AgeAtRecording, SearchComparison.LessOrEqual, number: 20));
        Assert.Empty(Find(ProfileProperty.AgeAtRecording, SearchComparison.GreaterThan, number: 30));
        // The release date counts on its own as well.
        Assert.Equal(["a.mp4"], Find(SearchExpression.Where(new() { Field = SearchField.ReleaseDate, Comparison = SearchComparison.LessThan, Date = new DateTimeOffset(2011, 1, 1, 0, 0, 0, TimeSpan.Zero) })));
    }

    [Fact]
    public void StudioFilterCanIncludeSubStudios()
    {
        catalog.UpsertVocabularyTag(new("Acme", "Studios"));
        Save("Acme Teens", "Studios", new TagProfile { ParentStudio = "Acme" });
        var acme = new TagFacet("Studios", "Acme", TagRole.Studio);
        var now = DateTimeOffset.Now;
        Assert.Equal(["b.mp4"], Find(new MediaFilter { Include = [acme] }.ToExpression(now)!));
        Assert.Equal(["a.mp4", "b.mp4"], Find(new MediaFilter { Include = [acme], IncludeSubStudios = true }.ToExpression(now)!));
        var blond = new MediaFilter { Profiles = [CatalogRepository.HasProfile(ProfileProperty.HairColor, SearchComparison.Equals, "Blond").Condition!] };
        Assert.Equal(["a.mp4"], Find(blond.ToExpression(now)!));
        Assert.Contains(blond.Describe(), d => d.Contains("Blond"));
        // Renaming the network keeps a saved studio condition working.
        catalog.UpsertSavedSearch(new SavedSearch { Name = "Acme", Expression = CatalogRepository.HasProfile(ProfileProperty.Studio, SearchComparison.Equals, "Acme") });
        catalog.SaveDirectoryEntry("Acme", "Studios", Entry("Acme") with { Tag = "Acme Media" });
        var saved = catalog.GetSavedSearches().Single(s => s.Name == "Acme");
        Assert.Equal("Acme Media", saved.Expression!.Condition!.Text);
        Assert.Equal(["a.mp4", "b.mp4"], Find(saved.Expression));
    }

    [Fact]
    public void TagPackagesCarryProfilesOnlyWhenThereAreAny()
    {
        Assert.DoesNotContain("Geschlecht", TagPackages.Write([new DirectoryEntry("Acme", "Studios", TagRole.Studio, 1)]));
        var text = TagPackages.Write(catalog.GetDirectory().Where(e => e.Role == TagRole.Performer));
        Assert.Contains("Geschlecht;Geburtsdatum", text);
        Assert.Contains("weiblich;1990-03-15", text);
        var package = TagPackages.Parse(text);
        Assert.Empty(package.Warnings);
        Assert.Equal(Entry("Jane Doe").Profile, package.Rows.Single(r => r.Name == "Jane Doe").Profile);

        // Into another library: package values win, fields the package leaves empty stay.
        Save("Jane Doe", "Darsteller", new TagProfile { HairColor = "Red", EyeColor = "Green" });
        var plan = catalog.PlanTagPackage(package);
        catalog.ApplyTagPackage(plan, plan.Items.Select(i => i.Row.Line).ToList());
        var jane = Entry("Jane Doe").Profile!;
        Assert.Equal(("Blond", "Green", 165, PerformerGender.Female), (jane.HairColor, jane.EyeColor, jane.HeightCm, jane.Gender));

        // Hand-made packages may use English headers and words; unreadable values skip the row with a note.
        var english = TagPackages.Parse("Name;Category;Gender;Birth date;Height;Breasts\r\nAnna;Performers;female;2001-02-03;170;natural\r\nBad;Performers;robot;;;\r\n");
        Assert.Equal((PerformerGender.Female, new DateOnly(2001, 2, 3), 170, BreastType.Natural),
            (english.Rows[0].Profile!.Gender, english.Rows[0].Profile!.BirthDate, english.Rows[0].Profile!.HeightCm, english.Rows[0].Profile!.Breasts));
        Assert.Single(english.Rows);
        Assert.Single(english.Warnings);
    }

    [Fact]
    public void NfoWritesPerformersStudiosAndReleaseDateAndReadsTheDateBack()
    {
        var item = catalog.Search(tag: "Jane Doe").Single();
        var nfo = Path.Combine(directory, "export.nfo");
        var preferences = catalog.GetPreferences();
        CatalogInterchange.WriteNfo(item, nfo, category => TagRoles.Of(preferences, category));
        var values = CatalogRepository.ReadNfo(nfo)!;
        Assert.Equal(["Jane Doe"], values["actor"]);
        Assert.Equal(["Acme Teens"], values["studio"]);
        Assert.Equal(["2010-06-01"], values["premiered"]);

        // The import reads <premiered> (or <aired>) into the release date of a medium that has none.
        File.WriteAllText(Path.ChangeExtension(PathFor("c.mp4"), ".nfo"), "<movie><title>C</title><aired>2019-11-02</aired></movie>");
        var other = catalog.Search(tag: "Other").Single();
        var plan = catalog.PlanNfoImport([other.Id]);
        Assert.Equal(new DateOnly(2019, 11, 2), plan.Changes.Single().ReleaseDate);
        catalog.ApplyNfoImport(plan.Changes);
        Assert.Equal(new DateOnly(2019, 11, 2), catalog.GetMedia(other.Id)!.ReleaseDate);
    }

    [Fact]
    public void UnknownProfileFieldsSurviveAnOlderSave()
    {
        catalog.UpsertVocabularyTag(new("Jane Doe", "Darsteller", Profile: System.Text.Json.JsonSerializer.Deserialize<TagProfile>(
            """{"hairColor":"Red","futureField":{"a":1}}""", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))));
        Save("Jane Doe", "Darsteller", Entry("Jane Doe").Profile! with { EyeColor = "Blue" });
        var profile = Entry("Jane Doe").Profile!;
        Assert.Equal(("Red", "Blue"), (profile.HairColor, profile.EyeColor));
        Assert.True(profile.Unknown!.ContainsKey("futureField"));
    }
}
