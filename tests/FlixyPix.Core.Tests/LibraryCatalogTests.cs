using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Several libraries on one computer: the list, a read-only look into a folder, moving and compacting.</summary>
public sealed class LibraryCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-libraries-" + Guid.NewGuid().ToString("N"));
    public LibraryCatalogTests() => Directory.CreateDirectory(root);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
    private string Library(string name, int media)
    {
        var directory = Path.Combine(root, name);
        using var catalog = new CatalogRepository(LibraryCatalogs.CatalogPath(directory));
        for (var i = 0; i < media; i++) catalog.Upsert(new() { Path = Path.Combine(root, "media", name + i + ".mp4"), Title = name + i });
        File.WriteAllBytes(Path.Combine(directory, "thumbnail.jpg"), new byte[1000]);
        return directory;
    }

    [Fact]
    public void TheListRemembersEachFolderOnceAndOrdersTheOpenOneFirst()
    {
        var first = Path.Combine(root, "Urlaub"); var second = Path.Combine(root, "Arbeit");
        IReadOnlyList<KnownLibrary> list = [];
        list = LibraryCatalogs.Remember(list, first, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        list = LibraryCatalogs.Remember(list, second + Path.DirectorySeparatorChar, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        list = LibraryCatalogs.Remember(list, first.ToUpperInvariant(), new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(2, list.Count);
        Assert.Equal(["Urlaub", "Arbeit"], LibraryCatalogs.Ordered(list, null).Select(LibraryCatalogs.DisplayName));
        Assert.Equal(["Arbeit", "Urlaub"], LibraryCatalogs.Ordered(list, second).Select(LibraryCatalogs.DisplayName));

        list = LibraryCatalogs.Rename(list, first, "  Ferien ");
        Assert.Equal("Ferien", LibraryCatalogs.DisplayName(list.Single(l => LibraryCatalogs.SameDirectory(l.Directory, first))));
        list = LibraryCatalogs.Rename(list, first, " ");
        Assert.Equal("Urlaub", LibraryCatalogs.DisplayName(list.Single(l => LibraryCatalogs.SameDirectory(l.Directory, first))));

        var moved = Path.Combine(root, "elsewhere", "Urlaub");
        list = LibraryCatalogs.Relocate(LibraryCatalogs.Rename(list, first, "Ferien"), first, moved);
        var entry = list.Single(l => LibraryCatalogs.SameDirectory(l.Directory, moved));
        Assert.Equal("Ferien", entry.Name); Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), entry.LastOpened);
        Assert.Single(LibraryCatalogs.Forget(list, moved));
        Assert.Equal("Meine Bibliothek", LibraryCatalogs.DisplayName(new KnownLibrary(Path.Combine(root, "FlixyPix", "Library"))));
    }

    [Fact]
    public void ANewLibraryNeedsAValidNameAndAFreeFolder()
    {
        Assert.Equal(Path.Combine(root, "Fotos"), LibraryCatalogs.NewLibraryDirectory(root, " Fotos "));
        Assert.Throws<ArgumentException>(() => LibraryCatalogs.NewLibraryDirectory(root, " "));
        Assert.Throws<ArgumentException>(() => LibraryCatalogs.NewLibraryDirectory(root, "a/b"));
        Directory.CreateDirectory(Path.Combine(root, "Leer"));
        Assert.Equal(Path.Combine(root, "Leer"), LibraryCatalogs.NewLibraryDirectory(root, "Leer"));
        Library("Belegt", 0);
        Assert.Throws<IOException>(() => LibraryCatalogs.NewLibraryDirectory(root, "Belegt"));
    }

    [Fact]
    public void InspectCountsMediaWithoutChangingTheCatalogAndDoesNotOpenEncryptedOnes()
    {
        var plain = Library("Plain", 3);
        var catalog = LibraryCatalogs.CatalogPath(plain);
        var written = File.GetLastWriteTimeUtc(catalog);
        var summary = LibraryCatalogs.Inspect(plain);
        Assert.True(summary.HasCatalog); Assert.False(summary.Encrypted); Assert.Equal(3, summary.MediaCount); Assert.Null(summary.Problem);
        Assert.True(summary.Bytes >= 1000);
        Assert.Equal(written, File.GetLastWriteTimeUtc(catalog));

        var locked = Library("Locked", 2);
        CatalogEncryption.EnableEncryption(LibraryCatalogs.CatalogPath(locked), "korrekt-pferd-batterie");
        var encrypted = LibraryCatalogs.Inspect(locked);
        Assert.True(encrypted.Encrypted); Assert.Null(encrypted.MediaCount); Assert.Null(encrypted.Problem);

        var empty = Path.Combine(root, "Empty"); Directory.CreateDirectory(empty);
        Assert.Equal((false, 0L), (LibraryCatalogs.Inspect(empty).HasCatalog, LibraryCatalogs.Inspect(empty).MediaCount ?? -1));
        var missing = LibraryCatalogs.Inspect(Path.Combine(root, "Missing"));
        Assert.False(missing.Exists); Assert.NotNull(missing.Problem);
    }

    [Fact]
    public async Task MovingKeepsEveryFileAndRefusesOccupiedOrNestedTargets()
    {
        var source = Library("Source", 2);
        Directory.CreateDirectory(Path.Combine(source, "thumbnails")); File.WriteAllText(Path.Combine(source, "thumbnails", "a.jpg"), "x");
        await Assert.ThrowsAsync<IOException>(() => LibraryCatalogs.MoveAsync(source, Path.Combine(source, "inner")));
        Directory.CreateDirectory(Path.Combine(root, "Taken"));
        await Assert.ThrowsAsync<IOException>(() => LibraryCatalogs.MoveAsync(source, Path.Combine(root, "Taken")));

        var target = await LibraryCatalogs.MoveAsync(source, Path.Combine(root, "nested", "Moved"));
        Assert.False(Directory.Exists(source));
        Assert.Equal("x", File.ReadAllText(Path.Combine(target, "thumbnails", "a.jpg")));
        using var moved = new CatalogRepository(LibraryCatalogs.CatalogPath(target));
        Assert.Equal(2, moved.Search().Count);
    }

    [Fact]
    public async Task CopyingToAnotherDriveChecksEveryFileAndCleansUpWhenCancelled()
    {
        var source = Library("Copied", 1);
        Directory.CreateDirectory(Path.Combine(source, "thumbnails", "deep")); File.WriteAllText(Path.Combine(source, "thumbnails", "deep", "b.jpg"), "y");
        var reports = new List<double>();
        await LibraryCatalogs.CopyAsync(source, Path.Combine(root, "Copy"), new SyncProgress(reports.Add));
        Assert.Equal("y", File.ReadAllText(Path.Combine(root, "Copy", "thumbnails", "deep", "b.jpg")));
        Assert.Equal(1, reports[^1], 3);
        Assert.True(File.Exists(LibraryCatalogs.CatalogPath(source)));

        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LibraryCatalogs.CopyAsync(source, Path.Combine(root, "Cancelled"), null, cancel.Token));
        Assert.False(Directory.Exists(Path.Combine(root, "Cancelled")));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }

    [Fact]
    public void CompactingShrinksAFileWithFreedPagesAndKeepsTheData()
    {
        var directory = Library("Big", 0);
        using var catalog = new CatalogRepository(LibraryCatalogs.CatalogPath(directory));
        var items = Enumerable.Range(0, 400).Select(i => new MediaItem { Path = Path.Combine(root, "media", $"clip{i}.mp4"), Title = new string('x', 2000) + i }).ToList();
        foreach (var item in items) catalog.Upsert(item);
        foreach (var item in items.Skip(10)) catalog.Delete(catalog.GetMediaByPath(item.Path)!.Id);
        var (before, after) = catalog.Compact();
        Assert.True(after < before, $"{before} -> {after}");
        Assert.Equal(10, catalog.Search().Count);
    }
}
