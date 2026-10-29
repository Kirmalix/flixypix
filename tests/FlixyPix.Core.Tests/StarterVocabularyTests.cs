using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class StarterVocabularyTests
{
    [Theory]
    [InlineData("de")] [InlineData("en")] [InlineData("fr")] [InlineData("es")] [InlineData("it")]
    [InlineData("pt")] [InlineData("nl")] [InlineData("pl")] [InlineData("ja")] [InlineData("ru")]
    public void StarterPackagesRoundTripAndNeverChangeMedia(string language)
    {
        var asset = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets/tag-vocabularies", language + TagPackages.Extension));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(asset)[..3]);
        var package = TagPackages.Read(asset);
        Assert.Empty(package.Warnings);
        Assert.Equal(62, package.Rows.Count);
        Assert.Equal(62, package.Rows.Select(r => TagPackages.Fold(r.Name)).Distinct().Count());
        Assert.All(package.Rows, r => { Assert.False(string.IsNullOrWhiteSpace(r.Category)); Assert.Equal(TagRole.Tag, r.Role); });
        var directory = Path.Combine(Path.GetTempPath(), "flixypix-starter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var catalog = new CatalogRepository(Path.Combine(directory, "catalog.db"));
            var media = catalog.Upsert(new() { Path = Path.Combine(directory, "fixture.mp4"), Kind = MediaKind.Video, Tags = ["existing-user-tag"] });
            var plan = catalog.PlanTagPackage(package);
            Assert.All(plan.Items, item => Assert.Equal(TagPackageStatus.New, item.Status));
            Assert.Equal(62, catalog.ApplyTagPackage(plan, plan.Items.Select(i => i.Row.Line).ToArray()));
            Assert.Equal(new[] { "existing-user-tag" }, catalog.Search().Single(i => i.Id == media.Id).Tags);
            var again = catalog.PlanTagPackage(package);
            Assert.All(again.Items, item => Assert.Equal(TagPackageStatus.Exists, item.Status));
            catalog.ApplyTagPackage(again, again.Items.Select(i => i.Row.Line).ToArray());
            Assert.Equal(62, catalog.GetPreferences().TagVocabulary.Count);
            var exported = TagPackages.Parse(TagPackages.Write(catalog.GetDirectory().Where(e => e.Category.Length > 0)));
            Assert.Empty(exported.Warnings);
            Assert.Equal(package.Rows.Select(r => (r.Name, r.Category)).OrderBy(r => r.Name), exported.Rows.Select(r => (r.Name, r.Category)).OrderBy(r => r.Name));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
