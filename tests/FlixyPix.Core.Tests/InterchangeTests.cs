using System.Text;
using System.Xml;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class InterchangeTests : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flixypix-interchange-" + Guid.NewGuid().ToString("N"));
    public InterchangeTests() => Directory.CreateDirectory(directory);
    private string PathFor(string name) => System.IO.Path.Combine(directory, name);

    [Fact]
    public void CsvRoundtripHandlesUnicodeQuotesCommasAndNewlinesLosslessly()
    {
        var item = new MediaItem
        {
            Path = PathFor("a,ä.jpg"), Title = "Title, \"quoted\"\r\nsecond line", Description = "Plot\nwith newline", Tags = ["A,B", "\"Tag\"", "äöü"],
            Rating = 9, FileSize = 34, Width = 120, Height = 90, DurationSeconds = 12.34, IsFavorite = true, WatchLater = true,
            TagCategories = new() { ["People"] = ["äöü"] }, Extra = new() { ["future-field"] = "verbatim" }
        };
        var path = PathFor("catalog.csv");
        CatalogInterchange.ExportCsv([item], path);
        var source = File.ReadAllBytes(path);
        Assert.Contains("format", File.ReadAllText(path));
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        var result = CatalogInterchange.ImportCsv(catalog, path);
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.Imported);
        var restored = Assert.Single(catalog.Search());
        Assert.Equal(item.Title, restored.Title);
        Assert.Equal(item.Description, restored.Description);
        Assert.Equal(item.Tags, restored.Tags);
        Assert.Equal(item.DurationSeconds, restored.DurationSeconds);
        Assert.True(restored.IsFavorite);
        Assert.True(restored.WatchLater);
        Assert.Equal("verbatim", restored.Extra["future-field"]);
        Assert.Equal(source, File.ReadAllBytes(path));
        Assert.Throws<IOException>(() => CatalogInterchange.ExportCsv([], path));
        Assert.Equal(source, File.ReadAllBytes(path));
    }

    [Fact]
    public void InvalidCsvEntityRollsBackEarlierValidRows()
    {
        var path = PathFor("invalid.csv");
        CatalogInterchange.ExportCsv([new MediaItem { Path = PathFor("good.jpg") }, new MediaItem { Path = PathFor("bad.jpg"), Rating = 99 }], path);
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        Assert.NotEmpty(CatalogInterchange.ImportCsv(catalog, path).Errors);
        Assert.Empty(catalog.Search());
    }

    [Fact]
    public void TruncatedQuotedCsvRejectsAllRows()
    {
        var path = PathFor("truncated.csv");
        CatalogInterchange.ExportCsv([new MediaItem { Path = PathFor("good.jpg") }], path);
        File.AppendAllText(path, "\"unfinished");
        using var catalog = new CatalogRepository(PathFor("catalog.sqlite"));
        Assert.NotEmpty(CatalogInterchange.ImportCsv(catalog, path).Errors);
        Assert.Empty(catalog.Search());
    }

    [Fact]
    public void NfoReadsKodiFieldsAndKeepsUnknownXmlThroughWrite()
    {
        var path = PathFor("movie.nfo");
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <movie><title>Ärger &amp; Spaß</title><plot>First
            second</plot><ratings><rating name="source" max="100" default="true"><value>85</value></rating></ratings>
            <runtime>2.5</runtime><genre>Drama</genre><genre>Comedy</genre><tag>Family</tag>
            <unknown private="yes"><value>retain me</value></unknown></movie>
            """;
        File.WriteAllText(path, xml, new UTF8Encoding(false));
        var metadata = CatalogInterchange.ReadNfo(path);
        Assert.Equal("Ärger & Spaß", metadata.Title);
        Assert.Equal(9, metadata.Rating);
        Assert.Equal(150, metadata.DurationSeconds);
        var item = CatalogInterchange.ApplyNfo(new MediaItem { Path = PathFor("movie.mp4"), Kind = MediaKind.Video }, metadata);
        Assert.Equal(xml, item.Extra["nfo.originalXml"]);
        Assert.Equal(new[] { "Drama", "Comedy" }, item.TagCategories["Genre"]);
        var export = PathFor("new.nfo");
        CatalogInterchange.WriteNfo(item with { Title = "Edited" }, export);
        Assert.Contains("retain me", File.ReadAllText(export));
        var restored = CatalogInterchange.ReadNfo(export);
        Assert.Equal("Edited", restored.Title);
        Assert.Equal(9, restored.Rating);
        Assert.Equal(150, restored.DurationSeconds);
        Assert.Equal(xml, File.ReadAllText(path));
        Assert.Throws<IOException>(() => CatalogInterchange.WriteNfo(item, path));
        Assert.Equal(xml, File.ReadAllText(path));
    }

    [Fact]
    public void NfoProhibitsDtdAndExternalEntityResolution()
    {
        var privateFile = PathFor("private.txt");
        File.WriteAllText(privateFile, "must not be read");
        var path = PathFor("entity.nfo");
        File.WriteAllText(path, "<!DOCTYPE movie [<!ENTITY leak SYSTEM '" + new Uri(privateFile).AbsoluteUri + "'>]><movie><plot>&leak;</plot></movie>");
        Assert.Throws<XmlException>(() => CatalogInterchange.ReadNfo(path));
    }

    [Fact]
    public void NfoHonorsDeclaredLatin1EncodingAndPreservesOriginalText()
    {
        var path = PathFor("latin1.nfo");
        var xml = "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?><movie><title>Grüße aus Köln</title><plot>Ältere Metadaten</plot></movie>";
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(xml));
        var result = CatalogInterchange.ReadNfo(path);
        Assert.Equal("Grüße aus Köln", result.Title);
        Assert.Equal(xml, result.OriginalXml);
        Assert.Equal(Encoding.Latin1.GetBytes(xml), File.ReadAllBytes(path));
    }

    [Fact]
    public void NfoRejectsOversizeAndInvalidRating()
    {
        File.WriteAllText(PathFor("huge.nfo"), "<movie>" + new string('x', 8 * 1024 * 1024) + "</movie>");
        Assert.Throws<InvalidDataException>(() => CatalogInterchange.ReadNfo(PathFor("huge.nfo")));
        File.WriteAllText(PathFor("invalid.nfo"), "<movie><rating>11</rating></movie>");
        Assert.Throws<InvalidDataException>(() => CatalogInterchange.ReadNfo(PathFor("invalid.nfo")));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
