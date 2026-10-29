using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Metadata of the files: XMP sidecars read and written, the import with its preview, and the round trip.</summary>
public sealed class FileMetadataTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-metadata-" + Guid.NewGuid().ToString("N"));
    public FileMetadataTests() => Directory.CreateDirectory(directory);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }
    private string PathFor(string name) => Path.Combine(directory, name);
    private string Media(string name) { var path = PathFor(name); File.WriteAllBytes(path, [1, 2, 3]); return path; }

    // As Lightroom writes it: rating as attribute, the levels of the hierarchy repeated in dc:subject, a title in two languages.
    private const string Lightroom = """
        <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/"
            xmlns:lr="http://ns.adobe.com/lightroom/1.0/" xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/" xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/"
            xmp:Rating="4" photoshop:DateCreated="2024-07-14T18:30:00+02:00" crs:Exposure2012="+0.35">
           <dc:title><rdf:Alt><rdf:li xml:lang="de-DE">Abend am Meer</rdf:li><rdf:li xml:lang="x-default">Evening by the sea</rdf:li></rdf:Alt></dc:title>
           <dc:description><rdf:Alt><rdf:li xml:lang="x-default">OLYMPUS DIGITAL CAMERA</rdf:li></rdf:Alt></dc:description>
           <dc:subject><rdf:Bag><rdf:li>Personen</rdf:li><rdf:li>Familie</rdf:li><rdf:li>Anna</rdf:li><rdf:li>Strand</rdf:li></rdf:Bag></dc:subject>
           <lr:hierarchicalSubject><rdf:Bag><rdf:li>Personen|Familie|Anna</rdf:li><rdf:li>Orte|Sylt</rdf:li></rdf:Bag></lr:hierarchicalSubject>
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>
        <?xpacket end="w"?>
        """;

    [Fact]
    public void LightroomSidecarBecomesTitleRatingDateAndTagsByCategory()
    {
        var metadata = XmpSidecar.Parse(Lightroom)!;
        Assert.Equal("Evening by the sea", metadata.Title);
        Assert.Null(metadata.Description);
        Assert.Equal(4, metadata.Stars);
        Assert.Equal(new DateTimeOffset(2024, 7, 14, 18, 30, 0, TimeSpan.FromHours(2)), metadata.TakenAt);
        var tags = metadata.TagsByCategory();
        Assert.Equal(new[] { "Anna" }, tags["Personen"]);
        Assert.Equal(new[] { "Sylt" }, tags["Orte"]);
        // The levels "Personen" and "Familie" are no tags of their own; "Anna" is already in its category.
        Assert.Equal(new[] { "Strand" }, tags[""]);
    }

    [Fact]
    public void TextsAndDatesAreCleanedUp()
    {
        Assert.Null(FileMetadata.Clean("  SONY DSC "));
        Assert.Equal(new[] { "Urlaub", "Strand" }, FileMetadata.SplitKeywords("Urlaub; Strand;;"));
        Assert.Equal(new DateTime(2023, 1, 2, 3, 4, 5), FileMetadata.ParseDate("2023:01:02 03:04:05")!.Value.DateTime);
        Assert.Null(FileMetadata.ParseDate("2023"));
        Assert.Null(XmpSidecar.Parse("<kein><xmp/></kein>"));
        Assert.Null(XmpSidecar.Parse("<!DOCTYPE x [<!ENTITY a \"b\">]><x/>"));
    }

    [Fact]
    public void SidecarIsFoundBesideTheFileAndByNameOnlyWhenUnambiguous()
    {
        var photo = Media("bild.jpg");
        Assert.Null(XmpSidecar.Find(photo));
        Assert.Equal(photo + ".xmp", XmpSidecar.PathFor(photo));
        File.WriteAllText(PathFor("bild.xmp"), Lightroom);
        Assert.Equal(PathFor("bild.xmp"), XmpSidecar.Find(photo));
        Media("bild.mp4");
        Assert.Null(XmpSidecar.Find(photo));
        File.WriteAllText(photo + ".xmp", Lightroom);
        Assert.Equal(photo + ".xmp", XmpSidecar.Find(photo));
    }

    [Fact]
    public void ImportFillsOnlyWhatIsOpenAndReadsWithoutWriting()
    {
        var first = Media("strand.jpg"); var second = Media("eigen.jpg");
        File.WriteAllText(first + ".xmp", Lightroom); File.WriteAllText(second + ".xmp", Lightroom);
        var before = File.ReadAllBytes(first + ".xmp");
        using var catalog = new CatalogRepository(PathFor("catalog.db"));
        var plain = catalog.Upsert(new MediaItem { Path = first, Title = "strand", Kind = MediaKind.Image, FileCreatedAt = DateTimeOffset.UnixEpoch });
        var own = catalog.Upsert(new MediaItem { Path = second, Title = "Mein Titel", Description = "Eigene Worte", Rating = 10, Kind = MediaKind.Image, Tags = ["Strand"] });

        var plan = catalog.PlanMetadataImport([plain.Id, own.Id], embedded: null);
        Assert.Equal(2, plan.Checked); Assert.Equal(0, plan.WithoutMetadata); Assert.Empty(plan.Problems);
        var change = plan.Changes.Single(c => c.MediaId == plain.Id);
        Assert.Equal("Evening by the sea", change.Title); Assert.Equal(8, change.Rating); Assert.Equal("strand.jpg.xmp", change.Source);
        var mine = plan.Changes.Single(c => c.MediaId == own.Id);
        Assert.Null(mine.Title); Assert.Null(mine.Description); Assert.Null(mine.Rating);
        Assert.False(mine.Tags.ContainsKey(""));

        Assert.Equal(2, catalog.ApplyMetadataImport(plan.Changes));
        var item = catalog.GetMedia(plain.Id)!;
        Assert.Equal("Evening by the sea", item.Title); Assert.Equal(8, item.Rating); Assert.Equal(2024, item.FileCreatedAt!.Value.Year);
        Assert.Equal(new[] { "Anna" }, item.TagCategories["Personen"]);
        Assert.Contains("Strand", item.Tags); Assert.Contains("Sylt", item.Tags);
        Assert.Equal("Mein Titel", catalog.GetMedia(own.Id)!.Title);
        Assert.Equal(before, File.ReadAllBytes(first + ".xmp"));
        Assert.Empty(catalog.PlanMetadataImport([plain.Id, own.Id], null).Changes);
    }

    [Fact]
    public void SidecarComesBeforeTheFileAndAChangedMediumIsSkipped()
    {
        var path = Media("clip.mp4");
        File.WriteAllText(path + ".xmp", Lightroom);
        using var catalog = new CatalogRepository(PathFor("catalog.db"));
        var item = catalog.Upsert(new MediaItem { Path = path, Title = "clip", Kind = MediaKind.Video });
        var embedded = new FixedReader(new FileMetadata { Title = "Aus der Datei", Description = "Beschreibung aus der Datei", Keywords = ["Wellen"] });
        var change = catalog.PlanMetadataImport([item.Id], embedded).Changes.Single();
        Assert.Equal("Evening by the sea", change.Title);
        Assert.Equal("Beschreibung aus der Datei", change.Description);
        Assert.Contains("Wellen", change.Tags[""]);
        Assert.Equal("clip.mp4.xmp + " + Texts.T("in the file"), change.Source);
        catalog.Upsert(catalog.GetMedia(item.Id)! with { Title = "Inzwischen geändert" });
        Assert.Equal(0, catalog.ApplyMetadataImport([change]));
        Assert.Equal("Inzwischen geändert", catalog.GetMedia(item.Id)!.Title);
    }

    [Fact]
    public void ExportWritesANewSidecarAndTheImportReadsTheSameBack()
    {
        var path = Media("ausflug.jpg");
        using var catalog = new CatalogRepository(PathFor("catalog.db"));
        var item = catalog.Upsert(new MediaItem { Path = path, Title = "Ausflug an den See", Description = "Mit Picknick.", Rating = 7, Kind = MediaKind.Image,
            Tags = ["Sommer"], TagCategories = new() { ["Personen"] = ["Anna", "Ben"] } });
        var result = catalog.ExportSidecars([item.Id]);
        Assert.Equal(1, result.Written); Assert.Empty(result.Problems);
        Assert.True(File.Exists(path + ".xmp")); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        var again = catalog.ExportSidecars([item.Id]);
        Assert.Equal((0, 1), (again.Written, again.Unchanged));

        using var other = new CatalogRepository(PathFor("other.db"));
        var fresh = other.Upsert(new MediaItem { Path = path, Title = "ausflug", Kind = MediaKind.Image });
        other.ApplyMetadataImport(other.PlanMetadataImport([fresh.Id], null).Changes);
        var back = other.GetMedia(fresh.Id)!;
        Assert.Equal("Ausflug an den See", back.Title); Assert.Equal("Mit Picknick.", back.Description); Assert.Equal(8, back.Rating);
        Assert.Equal(new[] { "Anna", "Ben" }, back.TagCategories["Personen"].Order());
        Assert.Equal(new[] { "Anna", "Ben", "Sommer" }, back.Tags.Order());
    }

    [Fact]
    public void ExportMergesIntoAForeignSidecarAndKeepsEverythingElse()
    {
        var path = Media("meer.jpg");
        File.WriteAllText(PathFor("meer.xmp"), Lightroom);
        var item = new MediaItem { Path = path, Title = "Meer im Juli", Kind = MediaKind.Image, Tags = ["Urlaub"] };
        Assert.True(XmpSidecar.Write(item));
        Assert.False(File.Exists(path + ".xmp"));
        var text = File.ReadAllText(PathFor("meer.xmp"));
        Assert.Contains("crs:Exposure2012=\"+0.35\"", text);
        Assert.Contains("Abend am Meer", text);
        Assert.Contains("xpacket", text);
        var metadata = XmpSidecar.Read(PathFor("meer.xmp"))!;
        Assert.Equal("Meer im Juli", metadata.Title);
        Assert.Equal(4, metadata.Stars);
        Assert.Contains("Urlaub", metadata.Keywords); Assert.Contains("Strand", metadata.Keywords);
        Assert.Contains("Orte|Sylt", metadata.HierarchicalKeywords);
        Assert.False(XmpSidecar.Write(item));
    }

    [Fact]
    public void AnUnreadableSidecarIsReportedAndLeftAlone()
    {
        var path = Media("kaputt.jpg");
        File.WriteAllText(path + ".xmp", "kein XML");
        using var catalog = new CatalogRepository(PathFor("catalog.db"));
        var item = catalog.Upsert(new MediaItem { Path = path, Title = "kaputt", Kind = MediaKind.Image });
        var result = catalog.ExportSidecars([item.Id]);
        Assert.Equal(0, result.Written); Assert.Single(result.Problems);
        Assert.Equal("kein XML", File.ReadAllText(path + ".xmp"));
        Assert.Single(catalog.PlanMetadataImport([item.Id], null).Problems);
    }

    private sealed class FixedReader(FileMetadata metadata) : IFileMetadataReader
    {
        public FileMetadata? Read(string path, MediaKind kind) => metadata;
    }
}
