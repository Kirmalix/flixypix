using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class LegacyTagImagesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-tag-images-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogRepository catalog;
    public LegacyTagImagesTests() { Directory.CreateDirectory(root); catalog = new(Path.Combine(root, "catalog.db")); }
    private string Image(string name, byte value = 1)
    {
        var folder = Path.Combine(root, "images", "People"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name); File.WriteAllBytes(path, [value, 2, 3]); return path;
    }
    [Fact] public void ExactCategoryAndTagMappingPreservesBytesAndIsIdempotent()
    {
        catalog.UpsertVocabularyTag(new("Alex", "People")); var source = Image("Alex.jpg"); Image("Unknown.png");
        var preview = catalog.PreviewLegacyTagImages(Path.Combine(root, "images"));
        Assert.Equal(1, catalog.ImportLegacyTagImages(preview).Imported);
        var asset = catalog.GetTagAsset(Assert.Single(catalog.GetTagAssetSummaries()).Id)!;
        Assert.Equal(new TagKey("People", "Alex"), asset.Tag); Assert.Equal(File.ReadAllBytes(source), asset.Data);
        catalog.ImportLegacyTagImages(preview); Assert.Single(catalog.GetTagAssetSummaries()); Assert.Equal(1, preview.Files.Count(value => !value.IsUnambiguous));
    }
    [Fact] public void SourceOrVocabularyChangesRejectWholeImport()
    {
        catalog.UpsertVocabularyTag(new("Alex", "People")); var file = Image("Alex.jpg");
        var preview = catalog.PreviewLegacyTagImages(Path.Combine(root, "images")); File.WriteAllBytes(file, [9, 2, 3]);
        Assert.Throws<InvalidOperationException>(() => catalog.ImportLegacyTagImages(preview)); Assert.Empty(catalog.GetTagAssetSummaries());
        preview = catalog.PreviewLegacyTagImages(Path.Combine(root, "images")); catalog.UpsertVocabularyTag(new("Other", "People"));
        Assert.Throws<InvalidOperationException>(() => catalog.ImportLegacyTagImages(preview));
    }
    [Fact] public void SuffixMappingIsExplicitAndAmbiguityIsNotGuessed()
    {
        catalog.UpsertVocabularyTag(new("Alex", "People")); catalog.UpsertVocabularyTag(new("Alex_one", "People")); Image("Alex_one.jpg");
        Assert.True(Assert.Single(catalog.PreviewLegacyTagImages(Path.Combine(root, "images")).Files).IsUnambiguous);
        var preview = catalog.PreviewLegacyTagImages(Path.Combine(root, "images"), ["People"]);
        Assert.False(Assert.Single(preview.Files).IsUnambiguous); Assert.Equal(1, catalog.ImportLegacyTagImages(preview).Ambiguous); Assert.Empty(catalog.GetTagAssetSummaries());
    }
    [Fact] public void CancellationAndLinksDoNotPublishAssets()
    {
        catalog.UpsertVocabularyTag(new("Alex", "People")); var file = Image("Alex.jpg");
        var preview = catalog.PreviewLegacyTagImages(Path.Combine(root, "images"));
        Assert.Throws<OperationCanceledException>(() => catalog.ImportLegacyTagImages(preview, new CancellationToken(true)));
        var link = Path.Combine(root, "images", "Link");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { "/d", "/c", "mklink", "/J", link, Path.GetDirectoryName(file)! }) start.ArgumentList.Add(value);
        using (var process = System.Diagnostics.Process.Start(start)!) { var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit(); Assert.True(process.ExitCode == 0, output + error); }
        Assert.Throws<InvalidDataException>(() => catalog.PreviewLegacyTagImages(Path.Combine(root, "images"))); Assert.Empty(catalog.GetTagAssetSummaries());
        Directory.Delete(link);
    }
    public void Dispose() { catalog.Dispose(); Directory.Delete(root, true); }
}
