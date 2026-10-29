using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using FlixyPix.Archives;
using Xunit;

namespace FlixyPix.Archives.Tests;

public sealed class ImageArchiveServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flixypix-archives-" + Guid.NewGuid().ToString("N"));
    public ImageArchiveServiceTests() => Directory.CreateDirectory(_root);
    private ImageArchiveService Service => new(Path.Combine(_root, "cache"));
    private string Zip(params (string Name, byte[] Data)[] entries)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            using var output = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
            output.Write(data);
        }
        return path;
    }

    [Fact]
    public async Task ExtractionPreservesSourceReusesCacheAndSeparatesBasenameCollisions()
    {
        var path = Zip(("a/photo.jpg", new byte[] { 1, 2, 3 }), ("b/photo.jpg", new byte[] { 4, 5 }), ("readme.txt", new byte[] { 6 }));
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var entries = Service.Inspect(path);
        Assert.Equal(2, entries.Count);
        var first = await Service.ExtractAsync(entries[0]);
        var second = await Service.ExtractAsync(entries[1]);
        Assert.NotEqual(first, second);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(first));
        var modified = File.GetLastWriteTimeUtc(first);
        Assert.Equal(first, await Service.ExtractAsync(entries[0]));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(first));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData("../escape.jpg")]
    [InlineData("/absolute.jpg")]
    [InlineData("C:/drive.jpg")]
    [InlineData("a\\escape.jpg")]
    [InlineData("a/../escape.jpg")]
    [InlineData("a./escape.jpg")]
    [InlineData("image.jpg:stream")]
    public void RejectsUnsafePathsBeforeCreatingCache(string name)
    {
        var path = Zip((name, new byte[] { 1 }));
        Assert.Throws<InvalidDataException>(() => Service.Inspect(path));
        Assert.False(Directory.Exists(Path.Combine(_root, "cache")));
    }

    [Fact]
    public void RejectsDuplicateAndCaseCollidingNames()
    {
        Assert.Throws<InvalidDataException>(() => Service.Inspect(Zip(("a.jpg", new byte[] { 1 }), ("A.JPG", new byte[] { 2 }))));
    }

    [Fact]
    public void RejectsEntryCountBeyondBound()
    {
        var entries = Enumerable.Range(0, ImageArchiveService.MaximumEntryCount + 1)
            .Select(i => ($"image{i}.jpg", Array.Empty<byte>())).ToArray();
        Assert.Throws<InvalidDataException>(() => Service.Inspect(Zip(entries)));
    }

    [Fact]
    public void RejectsEncryptionOnlyFlaggedInLocalHeader()
    {
        var path = Zip(("a.jpg", new byte[] { 1 }));
        var bytes = File.ReadAllBytes(path);
        bytes[6] |= 1;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => Service.Inspect(path));
    }

    [Theory]
    [InlineData("encrypted")]
    [InlineData("symlink")]
    [InlineData("large")]
    [InlineData("bomb")]
    public void RejectsHostileCentralDirectoryMetadata(string mode)
    {
        var path = Zip(("a.jpg", new byte[] { 1 }));
        var bytes = File.ReadAllBytes(path);
        var central = FindCentral(bytes);
        if (mode == "encrypted") bytes[central + 8] |= 1;
        if (mode == "symlink") BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 38), 0xa1ff0000);
        if (mode == "large") BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24), (uint)ImageArchiveService.MaximumImageSize + 1);
        if (mode == "bomb") BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24), 1000000);
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => Service.Inspect(path));
    }

    [Fact]
    public async Task RejectsChangedSourceAndCorruptCache()
    {
        var path = Zip(("a.jpg", new byte[] { 1, 2, 3 }));
        var entry = Service.Inspect(path).Single();
        var cache = await Service.ExtractAsync(entry);
        File.WriteAllBytes(cache, new byte[] { 8, 9 });
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.ExtractAsync(entry));
        File.AppendAllText(path, "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.ExtractAsync(entry));
    }

    [Fact]
    public async Task CancellationCreatesNoPartialOutput()
    {
        var entry = Service.Inspect(Zip(("a.jpg", new byte[] { 1 }))).Single();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.ExtractAsync(entry, cancel.Token));
        Assert.False(Directory.Exists(Path.Combine(_root, "cache")));
    }

    private static int FindCentral(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length - 4; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x02014b50) return i;
        throw new InvalidDataException();
    }
    public void Dispose() => Directory.Delete(_root, true);
}
