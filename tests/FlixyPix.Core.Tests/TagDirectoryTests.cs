using System.Text;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class TagDirectoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-directory-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogRepository catalog;
    public TagDirectoryTests()
    {
        Directory.CreateDirectory(directory);
        catalog = new CatalogRepository(Path.Combine(directory, "catalog.db"));
        catalog.Upsert(new() { Path = PathFor("a.mp4"), Kind = MediaKind.Video, Tags = ["Strand"], TagCategories = new() { ["Darsteller"] = ["Jane Doe"], ["Studios"] = ["Acme"] } });
        catalog.Upsert(new() { Path = PathFor("b.mp4"), Kind = MediaKind.Video, Tags = ["Strand", "Nacht"], TagCategories = new() { ["Darsteller"] = ["Jane Doe", "John Roe"] } });
        catalog.UpsertVocabularyTag(new("Komödie", "Genre", Aliases: ["Comedy"]));
    }
    public void Dispose() { catalog.Dispose(); SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }
    private string PathFor(string name) => Path.Combine(directory, name);
    private DirectoryEntry Entry(string tag) => catalog.GetDirectory().Single(e => e.Tag == tag);

    [Fact]
    public void DirectoryListsMediaTagsAndVocabularyWithRoleAndCount()
    {
        var entries = catalog.GetDirectory();
        Assert.Equal(["Acme", "Jane Doe", "John Roe", "Komödie", "Nacht", "Strand"], entries.Select(e => e.Tag));
        Assert.Equal(("Darsteller", TagRole.Performer, 2), (Entry("Jane Doe").Category, Entry("Jane Doe").Role, Entry("Jane Doe").MediaCount));
        Assert.Equal((TagRole.Studio, 1), (Entry("Acme").Role, Entry("Acme").MediaCount));
        Assert.Equal(("", TagRole.Tag, 2), (Entry("Strand").Category, Entry("Strand").Role, Entry("Strand").MediaCount));
        Assert.Equal((0, "Genre"), (Entry("Komödie").MediaCount, Entry("Komödie").Category));
        Assert.Equal(["Comedy"], Entry("Komödie").AliasList);
    }

    [Fact]
    public void SavingRenamesMovesAndStoresAliasesInOneStep()
    {
        var result = catalog.SaveDirectoryEntry("Jane Doe", "Darsteller", Entry("Jane Doe") with { Tag = "Jane Q. Doe", Category = "Stars", Aliases = ["J. Doe"], IsFavorite = true, Description = "Hauptrolle" });
        Assert.Equal(2, result.MediaChanged);
        var renamed = Entry("Jane Q. Doe");
        Assert.Equal(("Stars", 2, true, "Hauptrolle"), (renamed.Category, renamed.MediaCount, renamed.IsFavorite, renamed.Description));
        Assert.Contains("J. Doe", renamed.AliasList);
        Assert.All(catalog.Search(tag: "Jane Q. Doe"), m => Assert.Contains("Jane Q. Doe", m.TagCategories["Stars"]));
        Assert.Empty(catalog.Search(tag: "Jane Doe"));
        // A tag without category gets one on its media.
        catalog.SaveDirectoryEntry("Strand", "", Entry("Strand") with { Category = "Orte" });
        Assert.All(catalog.Search(tag: "Strand"), m => Assert.Equal(["Strand"], m.TagCategories["Orte"]));
        // Removing the category of a categorized tag is refused and changes nothing.
        Assert.Throws<ArgumentException>(() => catalog.SaveDirectoryEntry("Acme", "Studios", Entry("Acme") with { Tag = "Acme Pictures", Category = "" }));
        Assert.Equal("Studios", Entry("Acme").Category);
    }

    [Fact]
    public void PreviewChangesNothingDeleteAndMergeChangeEverything()
    {
        catalog.UpsertSavedSearch(new SavedSearch { Name = "Strand", Tag = "Strand" });
        var preview = catalog.PreviewTagChange("Strand", null);
        Assert.Equal((2, 1), (preview.MediaChanged, preview.SearchesChanged));
        Assert.Equal(2, Entry("Strand").MediaCount);
        Assert.Equal(3, catalog.DeleteTags(["Strand", "Nacht"]).MediaChanged);
        Assert.DoesNotContain(catalog.GetDirectory(), e => e.Tag is "Strand" or "Nacht");
        catalog.MergeTags(["John Roe", "Jane Doe"], "Jane Doe");
        var merged = Entry("Jane Doe");
        Assert.Equal(2, merged.MediaCount);
        Assert.DoesNotContain(catalog.GetDirectory(), e => e.Tag == "John Roe");
        catalog.UpsertVocabularyTag(new("Jane Doe", "Darsteller", Aliases: ["JD"]));
        catalog.UpsertVocabularyTag(new("Janet", "Darsteller", IsFavorite: true, Aliases: ["Jay"], Description: "Zweitname"));
        catalog.MergeTags(["Janet"], "Jane Doe");
        merged = Entry("Jane Doe");
        Assert.True(merged.IsFavorite); Assert.Equal("Zweitname", merged.Description);
        Assert.Equal(["JD", "Janet", "Jay"], merged.AliasList.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PackagesAreReadInEveryFormThePlanPromises()
    {
        var package = TagPackages.Parse("""
            # FlixyPix-Tagpaket 1
            # Kommentar
            Rolle;Kategorie;Name;Aliasse;Favorit;Beschreibung;Herkunft
            Darsteller;Darsteller;Jane Doe;J. Doe|Jane D.|Jane Doe;ja;;x
            Studio;Studios;"Acme; Pictures";;nein;"Sagt ""Hallo""
            über zwei Zeilen";x
            Unsinn;Genre;Krimi;;;;
            ;;;;;;
            Tag;Genre;Thriller;;vielleicht;;
            """);
        Assert.Equal(["Jane Doe", "Acme; Pictures"], package.Rows.Select(r => r.Name));
        Assert.Equal(["J. Doe", "Jane D."], package.Rows[0].Aliases);
        Assert.Equal((TagRole.Performer, true), (package.Rows[0].Role!.Value, package.Rows[0].Favorite!.Value));
        Assert.Equal("Sagt \"Hallo\"\nüber zwei Zeilen", package.Rows[1].Description!.Replace("\r", ""));
        Assert.Equal([4, 5], package.Rows.Select(r => r.Line));
        Assert.Contains(package.Warnings, w => w.Contains("Herkunft"));
        Assert.Contains(package.Warnings, w => w.Contains("Unsinn"));
        Assert.Contains(package.Warnings, w => w.Contains("vielleicht"));

        var english = TagPackages.Parse("Category,Name,Aliases\r\nGenre,Comedy,Komödie|Funny\r\n");
        Assert.Equal(("Genre", "Comedy"), (english.Rows[0].Category!, english.Rows[0].Name));
        var list = TagPackages.Parse("Jane Doe\nJohn Roe\n\n");
        Assert.Equal(["Jane Doe", "John Roe"], list.Rows.Select(r => r.Name)); Assert.Null(list.Rows[0].Category);
        Assert.Throws<InvalidDataException>(() => TagPackages.Parse("Kategorie;Aliasse\nGenre;x\n"));

        Assert.Equal("Komödie", TagPackages.Decode(Encoding.Latin1.GetBytes("Komödie")));
        Assert.Equal("Komödie", TagPackages.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Komödie")]));
        Assert.Equal("Komödie", TagPackages.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Komödie")]));
        Assert.Equal("Komödie „€“", TagPackages.Decode(Encoding.UTF8.GetBytes("Komödie „€“")));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal("„Krimi“ 5 €", TagPackages.Decode(Encoding.GetEncoding(1252).GetBytes("„Krimi“ 5 €")));
    }

    [Fact]
    public void PlanSortsRowsAndApplyingTouchesOnlyTheDirectory()
    {
        var package = TagPackages.Parse("""
            Rolle;Kategorie;Name;Aliasse;Favorit
            ;Darsteller;jane doe;Janie;ja
            ;Genre;Comedy;;
            ;Darsteller;Komodie;;
            Studio;Darsteller;Neu;;
            ;;Ohne Kategorie;;
            Darsteller;Stars;Neue Person;;
            ;Darsteller;Jane Doe;;
            ;Genre;Krimi;;
            """);
        var plan = catalog.PlanTagPackage(package);
        Assert.Equal([TagPackageStatus.Exists, TagPackageStatus.Alias, TagPackageStatus.Conflict, TagPackageStatus.Conflict, TagPackageStatus.Invalid,
            TagPackageStatus.New, TagPackageStatus.Invalid, TagPackageStatus.New], plan.Items.Select(i => i.Status));
        Assert.Equal("Komödie", plan.Items[1].Target);
        Assert.Equal([TagPackageStatus.New, TagPackageStatus.Exists], catalog.PlanTagPackage(TagPackages.Parse("Ohne Kategorie\nKomödie\n"), "Genre").Items.Select(i => i.Status));
        var mediaBefore = catalog.Search().Select(m => System.Text.Json.JsonSerializer.Serialize(m)).ToList();
        Assert.Equal(4, catalog.ApplyTagPackage(plan, plan.Items.Select(i => i.Row.Line).ToList()));
        Assert.Equal(mediaBefore, catalog.Search().Select(m => System.Text.Json.JsonSerializer.Serialize(m)));
        var jane = Entry("Jane Doe");
        Assert.True(jane.IsFavorite); Assert.Contains("Janie", jane.AliasList);
        Assert.Contains("Comedy", Entry("Komödie").AliasList);
        Assert.Equal(("Stars", TagRole.Performer), (Entry("Neue Person").Category, catalog.GetTagRole("Stars")));
        Assert.Equal("Genre", Entry("Krimi").Category);
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyTagPackage(plan, [plan.Items[0].Row.Line]));
    }

    [Fact]
    public void ExportedPackageReadsBackUnchanged()
    {
        catalog.SaveDirectoryEntry("Acme", "Studios", Entry("Acme") with { Tag = "Acme; \"Pictures\"", Aliases = ["#1 Studio", " Space "], Description = "Zeile 1\nZeile 2", IsFavorite = true });
        var path = PathFor("export" + TagPackages.Extension);
        TagPackages.Save(path, catalog.GetDirectory());
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path)[..3]);
        Assert.StartsWith(TagPackages.Signature, File.ReadAllText(path));
        var read = TagPackages.Read(path);
        Assert.Empty(read.Warnings);
        var acme = read.Rows.Single(r => r.Name.StartsWith("Acme", StringComparison.Ordinal));
        Assert.Equal(("Acme; \"Pictures\"", "Studios", TagRole.Studio, true, "Zeile 1\nZeile 2"), (acme.Name, acme.Category!, acme.Role!.Value, acme.Favorite!.Value, acme.Description!.Replace("\r", "")));
        Assert.Equal(["#1 Studio", "Space"], acme.Aliases);
        // Tags without category are exported too; importing them needs a category.
        Assert.All(catalog.PlanTagPackage(read).Items, i => Assert.True(i.Status == TagPackageStatus.Exists || i.Category.Length == 0, i.Row.Name + ": " + i.Status));
    }
}
