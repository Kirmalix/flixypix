using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Similar media from stored fingerprints: grouping by level and length, "kein Duplikat", the suggested version and
/// carrying tags and counters over to it.</summary>
public sealed class SimilarMediaTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-similar-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogRepository catalog;

    public SimilarMediaTests()
    {
        Directory.CreateDirectory(directory);
        catalog = new CatalogRepository(Path.Combine(directory, "catalog.db"));
    }

    public void Dispose() { catalog.Dispose(); SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }

    private MediaItem Add(string name, MediaKind kind, ulong[] hashes, double? duration = null, int width = 1280, int height = 720, long size = 1000)
    {
        var item = catalog.Upsert(new MediaItem { Path = Path.Combine(directory, name), Kind = kind, DurationSeconds = duration, Width = width, Height = height, FileSize = size });
        catalog.SaveFingerprint(new(item.Id, "stamp", hashes));
        return item;
    }

    private static ulong[] Frames(ulong seed, int flip = 0) =>
        Enumerable.Range(0, PerceptualHash.VideoFrames).Select(i => (seed * (ulong)(i + 3) * 0x9E3779B97F4A7C15UL) ^ ((1UL << flip) - 1)).ToArray();

    [Fact]
    public void HashOfAPictureIsStableAndMeasuresBits()
    {
        var pixels = Enumerable.Range(0, 1024).Select(i => (byte)(128 + 60 * Math.Sin(i % 32 / 5d) + 50 * Math.Cos(i / 32 / 7d))).ToArray();
        var noisy = pixels.Select((p, i) => (byte)Math.Clamp(p + (i % 7 == 0 ? 3 : 0), 0, 255)).ToArray();
        var inverted = pixels.Select(p => (byte)(255 - p)).ToArray();
        Assert.Equal(PerceptualHash.FromGray(pixels), PerceptualHash.FromGray(pixels));
        // A very plain picture has few strong frequencies, so light noise may flip a few bits; it stays "similar".
        Assert.InRange(PerceptualHash.Distance(PerceptualHash.FromGray(pixels), PerceptualHash.FromGray(noisy)), 0, (int)PerceptualHash.Threshold(SimilarityLevel.Similar));
        Assert.True(PerceptualHash.Distance(PerceptualHash.FromGray(pixels), PerceptualHash.FromGray(inverted)) > 20);
        Assert.Throws<ArgumentException>(() => PerceptualHash.FromGray(new byte[10]));
        // Two outlying stills (an inserted logo) do not decide.
        var a = Frames(5); var b = a.ToArray(); b[3] = ~b[3]; b[9] = ~b[9];
        Assert.Equal(0, PerceptualHash.Distance(a, b));
        Assert.Null(PerceptualHash.Distance(a, a.Take(4).ToArray()));
    }

    [Fact]
    public void VideosGroupByLookAndNearlySameLength()
    {
        var original = Add("film.mp4", MediaKind.Video, Frames(11), 600, 1920, 1080, 4_000);
        var copy = Add("film-720p.mp4", MediaKind.Video, Frames(11, 5), 603, 1280, 720, 2_000);
        Add("film-lang.mp4", MediaKind.Video, Frames(11), 900);
        Add("anderer.mp4", MediaKind.Video, Frames(99), 600);
        // 5 differing bits per still: similar, not exact.
        Assert.Empty(catalog.FindSimilarMedia(SimilarityLevel.Exact));
        var group = Assert.Single(catalog.FindSimilarMedia(SimilarityLevel.Similar));
        Assert.Equal(new[] { original.Id, copy.Id }.Order(), group.Items.Select(i => i.Id).Order());
        Assert.Equal((original.Id, 5d, MediaKind.Video), (group.Keep, group.Distance, group.Kind));
    }

    [Fact]
    public void ImagesGroupTransitivelyAndNotDuplicatesStayApart()
    {
        var a = Add("a.jpg", MediaKind.Image, [0UL]);
        var b = Add("b.jpg", MediaKind.Image, [0b111UL]);
        var c = Add("c.jpg", MediaKind.Image, [0b111_111UL]);
        Add("d.jpg", MediaKind.Image, [ulong.MaxValue]);
        // a–b and b–c are 3 bits apart; together they form one group even though a–c are 6 apart.
        var group = Assert.Single(catalog.FindSimilarMedia(SimilarityLevel.Exact, MediaKind.Image));
        Assert.Equal(3, group.Items.Count);
        Assert.Empty(catalog.FindSimilarMedia(SimilarityLevel.Exact, MediaKind.Video));
        catalog.MarkNotDuplicates([a.Id, b.Id, c.Id]);
        Assert.Empty(catalog.FindSimilarMedia(SimilarityLevel.Loose));
        Assert.Equal(3, catalog.GetPreferences().NotDuplicates.Count);
    }

    [Fact]
    public void KeptVersionGetsTagsCountersAndFingerprintsFollowTheMedia()
    {
        var keep = catalog.Upsert(new MediaItem { Path = Path.Combine(directory, "k.mp4"), Kind = MediaKind.Video, Rating = 4, PlayCount = 2, Tags = ["Strand"],
            TagCategories = new() { ["Darsteller"] = ["Jane Doe"] } });
        var other = catalog.Upsert(new MediaItem { Path = Path.Combine(directory, "o.mp4"), Kind = MediaKind.Video, Rating = 8, PlayCount = 3, IsFavorite = true,
            Tags = ["Nacht", "strand"], TagCategories = new() { ["Darsteller"] = ["John Roe"], ["Studios"] = ["Acme"] }, Description = "Text", ReleaseDate = new DateOnly(2020, 1, 2) });
        var merged = catalog.MergeDuplicateMetadata(keep.Id, [other.Id]);
        Assert.Equal(["Strand", "Nacht"], merged.Tags.Intersect(["Strand", "Nacht", "strand"]));
        Assert.Equal(["Jane Doe", "John Roe"], merged.TagCategories["Darsteller"]);
        Assert.Equal((8, 5L, true, "Text", new DateOnly(2020, 1, 2)), (merged.Rating, merged.PlayCount!.Value, merged.IsFavorite, merged.Description!, merged.ReleaseDate!.Value));
        Assert.Equal(merged.Tags, catalog.GetMedia(keep.Id)!.Tags);

        catalog.SaveFingerprint(new(other.Id, "1:2", [1UL]));
        Assert.Equal("1:2", catalog.GetFingerprintStamps()[other.Id]);
        catalog.Delete(other.Id);
        Assert.False(catalog.GetFingerprintStamps().ContainsKey(other.Id));
        // A fingerprint of a medium that is gone is not stored.
        catalog.SaveFingerprint(new(other.Id, "1:2", [1UL]));
        Assert.Empty(catalog.GetFingerprintStamps());
    }

    [Fact]
    public void SuggestionPrefersPixelsThenBitRate()
    {
        var small = new MediaItem { Path = "a", Width = 1280, Height = 720, FileSize = 9_000, DurationSeconds = 10 };
        var large = new MediaItem { Path = "b", Width = 1920, Height = 1080, FileSize = 1_000, DurationSeconds = 10 };
        var richer = large with { Path = "c", FileSize = 2_000 };
        Assert.Equal("c", CatalogRepository.SuggestKeep([small, large, richer]).Path);
    }
}
