using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Profiles of the directory: pictures, undo of merging and deleting, performers and studios in their own category.</summary>
public sealed class TagProfileTests : IDisposable
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aRb0AAAAASUVORK5CYII=");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-profiles-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogRepository catalog;
    private readonly MediaItem first, second;

    public TagProfileTests()
    {
        Directory.CreateDirectory(directory);
        catalog = new CatalogRepository(Path.Combine(directory, "catalog.db"));
        first = catalog.Upsert(new() { Path = PathFor("a.mp4"), Kind = MediaKind.Video, Tags = ["Strand"], TagCategories = new() { ["Darsteller"] = ["Jane Doe"], ["Studios"] = ["Acme"] } });
        second = catalog.Upsert(new() { Path = PathFor("b.mp4"), Kind = MediaKind.Video, Tags = ["Nacht"], TagCategories = new() { ["Darsteller"] = ["Jane Doe", "Janet"] } });
        catalog.UpsertVocabularyTag(new("Jane Doe", "Darsteller", IsFavorite: true, Aliases: ["J. Doe"], Description: "Hauptrolle"));
    }
    public void Dispose() { catalog.Dispose(); SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }
    private string PathFor(string name) => Path.Combine(directory, name);
    private DirectoryEntry Entry(string tag) => catalog.GetDirectory().Single(e => e.Tag == tag);

    [Fact]
    public void DeletingCanBeUndoneWithMediaSearchesRulesVocabularyAndPicture()
    {
        var picture = PathFor("jane.png"); File.WriteAllBytes(picture, Png);
        var asset = catalog.ImportTagAssetFile(picture, new("Darsteller", "Jane Doe"));
        catalog.UpsertSavedSearch(new SavedSearch { Name = "Jane", Tag = "Jane Doe" });
        catalog.UpsertFilenameTagRule(new FilenameTagRule { Name = "Regel", FilenameContains = "jane", Tags = ["Jane Doe"] });
        var before = catalog.GetDirectory();

        var result = catalog.DeleteTags(["Jane Doe"]);
        Assert.Equal(2, result.MediaChanged);
        Assert.DoesNotContain(catalog.GetDirectory(), e => e.Tag == "Jane Doe");
        Assert.Null(catalog.GetTagAsset(asset.Id)!.Tag);
        Assert.False(result.Undo!.IsEmpty);

        Assert.True(catalog.UndoTagChange(result.Undo));
        Assert.Equal(before, catalog.GetDirectory(), new DirectoryComparer());
        Assert.Equal(["Jane Doe"], catalog.GetMedia(first.Id)!.TagCategories["Darsteller"]);
        Assert.Equal("Jane Doe", catalog.GetSavedSearches().Single().Tag);
        Assert.True(catalog.GetFilenameTagRules().Single().Enabled);
        Assert.Equal(new TagKey("Darsteller", "Jane Doe"), catalog.GetTagAsset(asset.Id)!.Tag);
        Assert.Equal(Png, catalog.GetTagAsset(asset.Id)!.Data);
        Assert.Equal(2, catalog.QueryPage(new MediaQuery { Tag = "Jane Doe" }).TotalCount);
        // Once taken back, the same undo no longer fits.
        Assert.False(catalog.UndoTagChange(result.Undo));
    }

    [Fact]
    public void MergingCanBeUndoneAndAliasesGoBack()
    {
        var result = catalog.MergeTags(["Janet"], "Jane Doe");
        Assert.Contains("Janet", Entry("Jane Doe").AliasList);
        Assert.True(catalog.UndoTagChange(result.Undo!));
        Assert.Equal(["J. Doe"], Entry("Jane Doe").AliasList);
        Assert.Equal(1, Entry("Janet").MediaCount);
        Assert.Equal(["Jane Doe", "Janet"], catalog.GetMedia(second.Id)!.TagCategories["Darsteller"]);
    }

    [Fact]
    public void UndoIsRefusedWhenSomethingChangedSince()
    {
        var result = catalog.DeleteTags(["Jane Doe"]);
        catalog.UpdateTags([first.Id], ["Später"], []);
        Assert.False(catalog.UndoTagChange(result.Undo!));
        Assert.DoesNotContain("Jane Doe", catalog.GetMedia(second.Id)!.Tags);
        Assert.Contains("Später", catalog.GetMedia(first.Id)!.Tags);
    }

    [Fact]
    public void PicturesAreFoundByCategoryAndNameAndOpaqueOnesAreLeftOut()
    {
        var picture = PathFor("acme.png"); File.WriteAllBytes(picture, Png);
        var logo = catalog.ImportTagAssetFile(picture, new("Studios", "Acme"));
        catalog.UpsertTagAsset(new TagAsset { Tag = new("Darsteller", "Jane Doe"), Data = [1, 2, 3, 4], Label = "unbekannt" });
        var pictures = catalog.GetTagPictureIds();
        Assert.Equal(logo.Id, pictures[new TagKey("studios", "ACME")]);
        Assert.False(pictures.ContainsKey(new TagKey("Darsteller", "Jane Doe")));
    }

    [Fact]
    public void PerformersAreAddedAndRemovedInTheirCategory()
    {
        Assert.Equal(2, catalog.UpdateCategoryTags([first.Id, second.Id], "Darsteller", ["Max Roth"], []));
        Assert.Contains("Max Roth", catalog.GetMedia(first.Id)!.TagCategories["Darsteller"]);
        Assert.Contains("Max Roth", catalog.GetMedia(first.Id)!.Tags);
        Assert.Equal(0, catalog.UpdateCategoryTags([first.Id], "Darsteller", ["max roth"], []));

        // Removing from one category keeps a tag that another category of the medium still has.
        catalog.UpdateCategoryTags([first.Id], "Studios", ["Jane Doe"], []);
        catalog.UpdateCategoryTags([first.Id], "Darsteller", [], ["Jane Doe"]);
        var item = catalog.GetMedia(first.Id)!;
        Assert.DoesNotContain("Jane Doe", item.TagCategories["Darsteller"]);
        Assert.Contains("Jane Doe", item.Tags);
        catalog.UpdateCategoryTags([first.Id], "Studios", [], ["Jane Doe", "Acme"]);
        item = catalog.GetMedia(first.Id)!;
        Assert.False(item.TagCategories.ContainsKey("Studios"));
        Assert.DoesNotContain("Jane Doe", item.Tags); Assert.DoesNotContain("Acme", item.Tags);
        Assert.Throws<ArgumentException>(() => catalog.UpdateCategoryTags([first.Id], "Darsteller", ["X"], ["x"]));
    }

    [Fact]
    public void RolesAndAliasesTravelThroughTheExchangeFormat()
    {
        catalog.SetTagRole("Personen", TagRole.Tag);
        catalog.SetTagRole("Labels", TagRole.Performer);
        var path = PathFor("export.json");
        catalog.Export(path);
        using var target = new CatalogRepository(PathFor("target.db"));
        Assert.Empty(target.ImportJson(path).Errors);
        Assert.Equal(TagRole.Tag, target.GetTagRole("Personen"));
        Assert.Equal(TagRole.Performer, target.GetTagRole("Labels"));
        var jane = target.GetDirectory().Single(e => e.Tag == "Jane Doe");
        Assert.Equal((true, "Hauptrolle"), (jane.IsFavorite, jane.Description));
        Assert.Equal(["J. Doe"], jane.AliasList);
    }

    private sealed class DirectoryComparer : IEqualityComparer<IReadOnlyList<DirectoryEntry>>
    {
        private static string Key(DirectoryEntry e) => $"{e.Tag}|{e.Category}|{e.Role}|{e.MediaCount}|{e.IsFavorite}|{string.Join(",", e.AliasList)}|{e.Description}";
        public bool Equals(IReadOnlyList<DirectoryEntry>? x, IReadOnlyList<DirectoryEntry>? y) => x!.Select(Key).SequenceEqual(y!.Select(Key));
        public int GetHashCode(IReadOnlyList<DirectoryEntry> obj) => 0;
    }
}
