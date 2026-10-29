using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class TagAssetTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-tag-assets-" + Guid.NewGuid());
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aRb0AAAAASUVORK5CYII=");
    public TagAssetTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private CatalogRepository Open() => new(Path.Combine(root, "catalog.db"));
    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Fact]
    public void ExplicitFileImportRetainsBytesAndSourcePathsAndDeduplicatesWithinTheSameTag()
    {
        using var catalog = Open(); var firstPath = Path.Combine(root, "first.png"); var secondPath = Path.Combine(root, "second.png");
        File.WriteAllBytes(firstPath, Png); File.WriteAllBytes(secondPath, Png); var originalTime = File.GetLastWriteTimeUtc(firstPath);
        var first = catalog.ImportTagAssetFile(firstPath, new("People", "Alex"));
        var second = catalog.ImportTagAssetFile(secondPath, new("people", "ALEX"));
        Assert.Equal(first.Id, second.Id); Assert.Equal(Png, second.Data); Assert.Equal(Hash(Png), second.Sha256); Assert.Equal(Hash(Png), second.SourceSha256);
        Assert.Equal(new[] { firstPath, secondPath }.Order(), second.SourcePaths.Order()); Assert.Equal(".png", second.Format); Assert.False(second.IsOpaque);
        Assert.Equal(Png, File.ReadAllBytes(firstPath)); Assert.Equal(originalTime, File.GetLastWriteTimeUtc(firstPath)); Assert.Empty(catalog.Search());
        var summary = Assert.Single(catalog.GetTagAssetSummaries(new("PEOPLE", "alex"))); Assert.Equal(Png.Length, summary.ByteLength); Assert.Equal(first.Id, summary.Id);
    }

    [Fact]
    public void PictureBytesWithoutFileAndReplacingTheShownPictureKeepTheEarlierOne()
    {
        using var catalog = Open(); var key = new TagKey("Darsteller", "Alex");
        var first = catalog.ImportTagAssetData(Png, key, "Zwischenablage", "clipboard");
        Assert.Empty(first.SourcePaths); Assert.Equal("clipboard", first.Provenance["import.method"]); Assert.Equal(first.Id, catalog.ImportTagAssetData(Png, key, "again", "clipboard").Id);
        Assert.Throws<InvalidDataException>(() => catalog.ImportTagAssetData(Encoding.UTF8.GetBytes("no picture"), key, "x", "clipboard"));
        var other = (byte[])Png.Clone(); other[^5] ^= 1; // A different checksum; still recognized as PNG by its header.
        var second = catalog.ReplaceTagPicture(key, () => catalog.ImportTagAssetData(other, key, "Standbild", "video-still"));
        Assert.Equal(second.Id, catalog.GetTagPictureIds()[key]);
        var released = catalog.GetTagAsset(first.Id)!; Assert.Null(released.Tag); Assert.Contains(key, released.PreviousTags);
        // A failing import releases nothing.
        Assert.Throws<InvalidDataException>(() => catalog.ReplaceTagPicture(key, () => throw new InvalidDataException()));
        Assert.Equal(second.Id, catalog.GetTagPictureIds()[key]);
    }

    [Fact]
    public void UnsupportedContentRemainsOpaqueAndIndependentOfMedia()
    {
        using var catalog = Open(); byte[] data = Encoding.UTF8.GetBytes("unrecognized historical graphic bytes");
        var asset = catalog.UpsertTagAsset(new() { Tag = new("People", "Alex"), Data = data, Label = "Unknown format", SourcePaths = ["U:\\offline\\old-picture.bin"], Provenance = new() { ["legacy.table"] = "tag-image" } });
        Assert.True(asset.IsOpaque); Assert.Null(asset.Format); Assert.Equal(Hash(data), asset.Sha256); Assert.Empty(catalog.Search());
        var loaded = catalog.GetTagAsset(asset.Id)!; Assert.Equal(data, loaded.Data); Assert.Equal("tag-image", loaded.Provenance["legacy.table"]);
        Assert.Equal("U:\\offline\\old-picture.bin", Assert.Single(loaded.SourcePaths));
        Assert.Throws<InvalidDataException>(() => catalog.MaterializeTagAsset(loaded, Path.Combine(root, "cache")));
        var exported = catalog.ExportTagAsset(asset.Id, Path.Combine(root, "opaque.bin")); Assert.Equal(data, File.ReadAllBytes(exported));
    }

    [Fact]
    public void ExistingAssetBytesAreImmutableAndExplicitDeletionDoesNotTouchTheSource()
    {
        using var catalog = Open(); var source = Path.Combine(root, "source.png"); File.WriteAllBytes(source, Png);
        var asset = catalog.ImportTagAssetFile(source, new("People", "Alex"));
        Assert.Throws<InvalidOperationException>(() => catalog.UpsertTagAsset(asset with { Data = [1, 2, 3], Sha256 = "", ByteLength = 0 }));
        Assert.Equal(Png, catalog.GetTagAsset(asset.Id)!.Data);
        Assert.True(catalog.DeleteTagAsset(asset.Id)); Assert.False(catalog.DeleteTagAsset(asset.Id)); Assert.Null(catalog.GetTagAsset(asset.Id)); Assert.Equal(Png, File.ReadAllBytes(source));
    }

    [Fact]
    public void GlobalRenameMergeCategoryMoveAndTagDeletionRetainEveryArtworkIdAndByteCopy()
    {
        using var catalog = Open();
        var first = catalog.UpsertTagAsset(new() { Tag = new("People", "Alex"), Data = Png });
        var second = catalog.UpsertTagAsset(new() { Tag = new("People", "Jordan"), Data = [1, 2, 3] });
        var third = catalog.UpsertTagAsset(new() { Tag = new("Places", "Alex"), Data = [4, 5, 6] });
        catalog.ChangeTag("Alex", "Jordan"); Assert.Equal(2, catalog.GetTagAssetSummaries(new("People", "Jordan")).Count);
        catalog.MoveTagCategory("Jordan", "People", "Performers"); Assert.Empty(catalog.GetTagAssetSummaries(new("People", "Jordan")));
        Assert.Equal(2, catalog.GetTagAssetSummaries(new("Performers", "Jordan")).Count); Assert.Single(catalog.GetTagAssetSummaries(new("Places", "Jordan")));
        catalog.ChangeTag("Jordan", null); var orphaned = catalog.GetTagAssetSummaries(unassignedOnly: true); Assert.Equal(3, orphaned.Count);
        foreach (var original in new[] { first, second, third })
        {
            var loaded = catalog.GetTagAsset(original.Id)!; Assert.Null(loaded.Tag); Assert.Equal(original.Data, loaded.Data); Assert.Equal(original.Sha256, loaded.Sha256); Assert.NotEmpty(loaded.PreviousTags);
        }
        Assert.Contains(catalog.GetTagAsset(first.Id)!.PreviousTags, key => key.Category == "People" && key.Value == "Alex");
    }

    [Fact]
    public void ReimportingTheOldTagAfterRenameDoesNotStealTheRenamedAssetsIdentity()
    {
        using var catalog = Open(); var source = Path.Combine(root, "source.png"); File.WriteAllBytes(source, Png);
        var renamed = catalog.ImportTagAssetFile(source, new("People", "Alex")); catalog.ChangeTag("Alex", "Jordan");
        var reimported = catalog.ImportTagAssetFile(source, new("People", "Alex"));
        Assert.NotEqual(renamed.Id, reimported.Id); Assert.Equal("Jordan", catalog.GetTagAsset(renamed.Id)!.Tag!.Value);
        Assert.Equal(reimported.Id, catalog.ImportTagAssetFile(source, new("People", "Alex")).Id); Assert.Equal(2, catalog.GetTagAssetSummaries().Count);
    }

    [Fact]
    public void ReassignmentRetainsPriorAssociationAndOriginMetadata()
    {
        using var catalog = Open();
        var asset = catalog.UpsertTagAsset(new() { Tag = new("People", "Alex"), Data = Png, SourcePaths = ["U:\\offline\\first.png"] });
        var changed = catalog.UpsertTagAsset(asset with { Tag = new("Places", "Paris"), SourcePaths = ["U:\\offline\\second.png"] });
        Assert.Contains(new TagKey("People", "Alex"), changed.PreviousTags); Assert.Equal(2, changed.SourcePaths.Count); Assert.Equal(Png, changed.Data);
    }

    [Fact]
    public void MetadataQueriesArePagedAndUnassignedGraphicsAreSeparate()
    {
        using var catalog = Open();
        for (var i = 0; i < 5; i++) catalog.UpsertTagAsset(new() { Tag = new("People", "Alex"), Label = i.ToString(), Data = [(byte)i] });
        catalog.UpsertTagAsset(new() { Data = [99] });
        Assert.Equal(2, catalog.GetTagAssetSummaries(new("People", "Alex"), offset: 0, limit: 2).Count);
        Assert.Single(catalog.GetTagAssetSummaries(new("People", "Alex"), offset: 4, limit: 2)); Assert.Single(catalog.GetTagAssetSummaries(unassignedOnly: true));
        Assert.Throws<ArgumentException>(() => catalog.GetTagAssetSummaries(new("People", "Alex"), unassignedOnly: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.GetTagAssetSummaries(limit: 1001));
    }

    [Fact]
    public void OverLimitAndEmptyFilesAreReportedWithoutChangingTheSourceOrCatalog()
    {
        using var catalog = Open(); var oversized = Path.Combine(root, "too-large.bin");
        using (var file = File.Create(oversized)) file.SetLength((long)CatalogRepository.MaximumTagAssetBytes + 1);
        var empty = Path.Combine(root, "empty.bin"); File.WriteAllBytes(empty, []);
        Assert.Throws<InvalidDataException>(() => catalog.ImportTagAssetFile(oversized, new("People", "Alex")));
        Assert.Throws<InvalidDataException>(() => catalog.ImportTagAssetFile(empty, new("People", "Alex")));
        Assert.Equal((long)CatalogRepository.MaximumTagAssetBytes + 1, new FileInfo(oversized).Length); Assert.Empty(catalog.GetTagAssetSummaries());
    }

    [Fact]
    public void ExportCreatesANewVerifiedFileAndDoesNotOverwriteExistingBytes()
    {
        using var catalog = Open(); var asset = catalog.UpsertTagAsset(new() { Tag = new("People", "Alex"), Data = Png });
        var output = catalog.ExportTagAsset(asset.Id, Path.Combine(root, "export.png")); Assert.Equal(asset.Sha256, Hash(File.ReadAllBytes(output)));
        Assert.Throws<IOException>(() => catalog.ExportTagAsset(asset.Id, output)); Assert.Equal(Png, File.ReadAllBytes(output));
    }

    [Fact]
    public void HashNamedPreviewCacheDetectsModifiedCacheWithoutChangingPreservedArtwork()
    {
        using var catalog = Open(); var asset = catalog.UpsertTagAsset(new() { Tag = new("People", "Alex"), Data = Png });
        var directory = Path.Combine(root, "cache"); var path = catalog.MaterializeTagAsset(asset, directory);
        Assert.Equal(asset.Sha256 + ".png", Path.GetFileName(path)); Assert.Equal(path, catalog.MaterializeTagAsset(asset, directory));
        File.WriteAllBytes(path, [0]); Assert.Throws<InvalidDataException>(() => catalog.MaterializeTagAsset(asset, directory)); Assert.Equal(Png, catalog.GetTagAsset(asset.Id)!.Data);
    }

    [Fact]
    public void ExportAndMaterializationRejectProtectedOfflineDriveBeforeAnyFilesystemAccessThere()
    {
        using var catalog = Open(); var asset = catalog.UpsertTagAsset(new() { Tag = new("People", "Alex"), Data = Png });
        Assert.Throws<UnauthorizedAccessException>(() => catalog.ExportTagAsset(asset.Id, "U:\\blocked-tag-export.png"));
        Assert.Throws<UnauthorizedAccessException>(() => catalog.MaterializeTagAsset(asset, "U:\\blocked-cache"));
    }

    [Fact]
    public async Task CanceledFileImportDoesNotRequireAnAccessibleSource()
    {
        using var catalog = Open(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => catalog.ImportTagAssetFile("U:\\not-accessed.png", new("People", "Alex"), cancellationToken: canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ImportTagAssetFileAsync("U:\\not-accessed.png", new("People", "Alex"), cancellationToken: canceled.Token));
        Assert.Empty(catalog.GetTagAssetSummaries());
    }

    [Fact]
    public async Task PortableAndBackupRoundtripsRetainAssignedOpaqueAndOrphanedArtwork()
    {
        using var catalog = Open();
        var assigned = catalog.UpsertTagAsset(new() { Tag = new("People", "Alex"), Data = Png, SourcePaths = ["U:\\offline\\tag.png"] });
        var orphan = catalog.UpsertTagAsset(new() { Tag = new("People", "Removed"), Data = [1, 2, 3], Provenance = new() { ["note"] = "opaque original" } });
        catalog.ChangeTag("Removed", null); var json = Path.Combine(root, "portable.json"); catalog.Export(json);
        using (var copy = new CatalogRepository(Path.Combine(root, "copy.db")))
        {
            Assert.Empty(copy.ImportJson(json).Errors); Assert.Equal(Png, copy.GetTagAsset(assigned.Id)!.Data);
            Assert.True(copy.GetTagAsset(orphan.Id)!.IsOpaque); Assert.Null(copy.GetTagAsset(orphan.Id)!.Tag); Assert.Single(copy.GetTagAsset(orphan.Id)!.PreviousTags);
        }
        var package = Path.Combine(root, "tag-assets.flixypixbackup"); await catalog.CreateBackupAsync(package, false);
        var restored = await CatalogRepository.RestoreBackupAsync(package, Path.Combine(root, "restored"));
        using var restoredCatalog = new CatalogRepository(restored.DatabasePath);
        Assert.Equal(assigned.Sha256, restoredCatalog.GetTagAsset(assigned.Id)!.Sha256); Assert.Equal(orphan.Data, restoredCatalog.GetTagAsset(orphan.Id)!.Data);
    }

    [Fact]
    public void PortableChecksumMismatchRollsBackInsteadOfImportingAlteredBytes()
    {
        using var catalog = Open(); catalog.UpsertTagAsset(new() { Tag = new("People", "Alex"), Data = Png });
        var export = Path.Combine(root, "portable.json"); catalog.Export(export); var json = JsonNode.Parse(File.ReadAllText(export))!;
        json["tagAssets"]![0]!["sha256"] = new string('0', 64); File.WriteAllText(export, json.ToJsonString());
        using var copy = new CatalogRepository(Path.Combine(root, "copy.db")); Assert.NotEmpty(copy.ImportJson(export).Errors); Assert.Empty(copy.GetTagAssetSummaries());
    }
}
