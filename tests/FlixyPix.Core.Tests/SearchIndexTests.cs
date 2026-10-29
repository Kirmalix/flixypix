using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>The trigram index must find exactly what the row-by-row OrdinalIgnoreCase comparison finds, and follow every change.</summary>
public sealed class SearchIndexTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-search-index-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(root, "catalog.db");
    public SearchIndexTests() => Directory.CreateDirectory(root);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }

    private static readonly string[] Titles =
    [
        "Straße am Fluß", "STRASSE", "Äpfel und Öl", "ſchloss", "Schloss", "Kelvin K", "kelvin k", "ΣΊΣΥΦΟΣ σίσυφος ς",
        "ıstanbul", "Istanbul", "İzmir", "IMG_2019 [1] *best*?", "𐐀𐐨 Deseret", "😀 Lachen", "naïve café", "x"
    ];
    private static readonly string[] Terms =
    [
        "straße", "STRASSE", "äpfel", "ÖL", "schloss", "ſchl", "kelvin", "K k", "σίσυφος", "ΣΊΣ", "istanbul", "ıstan", "izmir", "İzm",
        "img_", "[1]", "*best*", "?", "𐐨𐐀", "😀 lach", "CAFÉ", "naïve", "zzz", "fl", "ss", "st"
    ];

    [Fact]
    public void FoldingMatchesOrdinalIgnoreCaseForEveryCodePoint()
    {
        var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        for (var point = 0; point <= 0x10FFFF; point++)
        {
            if (point is >= 0xD800 and <= 0xDFFF) continue;
            var text = char.ConvertFromUtf32(point); var folded = SearchFold.Fold(text);
            Assert.True(string.Equals(text, folded, StringComparison.OrdinalIgnoreCase), $"U+{point:X4} is not equal to its folding.");
            Assert.Equal(folded, SearchFold.Fold(folded));
            if (distinct.Add(folded)) Assert.True(images.Add(folded), $"Two foldings of the same class at U+{point:X4}.");
        }
        Assert.Equal("\uD800", SearchFold.Fold("\uD800"));
    }

    [Fact]
    public void IndexedSearchFindsExactlyWhatTheRowByRowComparisonFinds()
    {
        using var catalog = new CatalogRepository(Database);
        var items = Titles.Select(title => catalog.Upsert(new MediaItem { Path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".jpg"), Title = title, Kind = MediaKind.Image })).ToList();
        foreach (var term in Terms.Concat(Terms.Select(t => t.ToLowerInvariant())).Concat(Terms.Select(t => t.ToUpperInvariant())))
        {
            // Quick search covers title, path, description and tags; the path holds this test's folder name.
            var expected = items.Where(i => term.Split(' ').All(part => (i.Title + "\n" + i.Path).Contains(part, StringComparison.OrdinalIgnoreCase)))
                .Select(i => i.Title).Order(StringComparer.Ordinal).ToList();
            var rowByRow = catalog.Search(term).Select(i => i.Title).Order(StringComparer.Ordinal).ToList();
            var page = catalog.QueryPage(new MediaQuery { Text = term, Limit = 100 });
            Assert.Equal(expected, rowByRow);
            Assert.Equal(expected, page.Items.Select(i => i.Title).Order(StringComparer.Ordinal).ToList());
            Assert.Equal(expected.Count, page.TotalCount);
        }
        Assert.Equal(0, catalog.PendingSearchIndexRows());
    }

    [Fact]
    public void IndexFollowsChangesDeletionsTagsAndPaths()
    {
        using var catalog = new CatalogRepository(Database);
        var item = catalog.Upsert(new MediaItem { Path = Path.Combine(root, "urlaub", "a.jpg"), Title = "Abendlicht", Kind = MediaKind.Image, Tags = ["Strandtag"] });
        long Hits(string text) => catalog.QueryPage(new MediaQuery { Text = text, Limit = 10 }).TotalCount;
        Assert.Equal((1, 1, 1), (Hits("abendl"), Hits("strandtag"), Hits("urlaub")));
        catalog.Upsert(item with { Title = "Morgenrot", Tags = ["Bergtag"], Description = "Gipfelkreuz" });
        Assert.Equal((0, 1, 0, 1, 1), (Hits("abendl"), Hits("morgen"), Hits("strandtag"), Hits("bergtag"), Hits("gipfel")));
        catalog.Delete(item.Id);
        Assert.Equal(0, Hits("morgen"));
        Assert.Equal(0, catalog.PendingSearchIndexRows());
    }

    [Fact]
    public void ExistingCatalogsAndChangedCasingTablesRebuildTheIndex()
    {
        using (var catalog = new CatalogRepository(Database))
        {
            for (var i = 0; i < 12; i++) catalog.Upsert(new MediaItem { Path = Path.Combine(root, $"m{i}.jpg"), Title = "Sonnenuntergang " + i, Kind = MediaKind.Image });
            Assert.Equal(12, catalog.PrepareSearchIndex());
        }
        // A catalog from before the index, and one indexed with other casing tables.
        foreach (var damage in new[] { "DROP TABLE media_search; DROP TABLE media_search_state;", "UPDATE media_search_state SET value='older-runtime'; DELETE FROM media_search;" })
        {
            SqliteConnection.ClearAllPools();
            using (var raw = new SqliteConnection($"Data Source={Database}")) { raw.Open(); using var command = raw.CreateCommand(); command.CommandText = damage; command.ExecuteNonQuery(); }
            using var catalog = new CatalogRepository(Database);
            Assert.Equal(12, catalog.PendingSearchIndexRows());
            Assert.Equal(12, catalog.QueryPage(new MediaQuery { Text = "untergang", Limit = 50 }).TotalCount);
            Assert.Equal(0, catalog.PendingSearchIndexRows());
        }
    }

    [Fact]
    public void PreparingInPortionsIndexesEverythingOnce()
    {
        using var catalog = new CatalogRepository(Database);
        for (var i = 0; i < 5100; i++) catalog.Upsert(new MediaItem { Path = Path.Combine(root, $"p{i}.jpg"), Title = $"Bild {i:D5}", Kind = MediaKind.Image });
        Assert.Equal(5100, catalog.PendingSearchIndexRows());
        Assert.Equal(5100, catalog.PrepareSearchIndex());
        Assert.Equal(0, catalog.PrepareSearchIndex());
        Assert.Equal(1, catalog.QueryPage(new MediaQuery { Text = "bild 05099", Limit = 5 }).TotalCount);
        Assert.Equal(11, catalog.QueryPage(new MediaQuery { Text = "0509", Limit = 50 }).TotalCount); // 05090–05099 and 00509
    }
}
