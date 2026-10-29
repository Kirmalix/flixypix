using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Kodi-style .nfo files: finding them, reading their elements, previewing and applying title, description and categorized tags.</summary>
public sealed class NfoImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-nfo-" + Guid.NewGuid().ToString("N"));
    public NfoImportTests() => Directory.CreateDirectory(root);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }

    private const string Movie = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <movie>
          <title>Der lange Weg</title>
          <year>2019</year>
          <plot>Eine Reise über die Alpen.</plot>
          <genre>Abenteuer</genre><genre>Drama</genre>
          <director>Anna Berg</director>
          <studio>Nordlicht</studio>
          <set><name>Wege-Reihe</name></set>
          <actor><name>Max Roth</name><role>Wanderer</role></actor>
          <actor><name>Lea Sand</name></actor>
          <fileinfo><streamdetails><video><codec>h264</codec></video></streamdetails></fileinfo>
        </movie>
        """;

    private string Video(string relative, string? nfo = null, string? nfoName = null)
    {
        var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, [0]);
        if (nfo != null) File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, nfoName ?? Path.GetFileNameWithoutExtension(path) + ".nfo"), nfo);
        return path;
    }

    [Fact]
    public void FindsAndReadsKodiFiles()
    {
        var beside = Video("a/film.mp4", Movie);
        var movieNfo = Video("b/x.mkv", Movie, "movie.nfo");
        var nested = Video("c/serie.mp4"); Directory.CreateDirectory(Path.Combine(root, "c", "info")); File.WriteAllText(Path.Combine(root, "c", "info", "serie.nfo"), Movie);
        Assert.EndsWith("film.nfo", CatalogRepository.FindNfo(beside, false));
        Assert.EndsWith("movie.nfo", CatalogRepository.FindNfo(movieNfo, false));
        Assert.Null(CatalogRepository.FindNfo(nested, false));
        Assert.EndsWith("serie.nfo", CatalogRepository.FindNfo(nested, true));
        var values = CatalogRepository.ReadNfo(CatalogRepository.FindNfo(beside, false)!)!;
        Assert.Equal(["Abenteuer", "Drama"], values["genre"]);
        Assert.Equal(["Max Roth", "Lea Sand"], values["actor"]);
        Assert.Equal(["Wege-Reihe"], values["set"]);
        Assert.False(values.ContainsKey("fileinfo"));
        File.WriteAllText(Path.Combine(root, "link.nfo"), "https://www.imdb.com/title/tt0000000/");
        Assert.Null(CatalogRepository.ReadNfo(Path.Combine(root, "link.nfo")));
    }

    [Fact]
    public void PreviewAndApplyAddCategorizedTagsTitleAndDescription()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var film = catalog.Upsert(new MediaItem { Path = Video("a/film.mp4", Movie), Title = "film", Kind = MediaKind.Video, TagCategories = new() { ["Genre"] = ["Drama"] } });
        var plain = catalog.Upsert(new MediaItem { Path = Video("a/ohne.mp4"), Title = "ohne", Kind = MediaKind.Video });
        var plan = catalog.PlanNfoImport([film.Id, plain.Id]);
        Assert.Equal((2, 1), (plan.Checked, plan.WithoutNfo));
        var change = Assert.Single(plan.Changes);
        Assert.Equal("Der lange Weg", change.Title);
        Assert.Equal(["Abenteuer"], change.Tags["Genre"]); // "Drama" is already there
        Assert.Equal(["Max Roth", "Lea Sand"], change.Tags["Darsteller"]);
        Assert.StartsWith("Titel: Der lange Weg\nJahr: 2019", change.Description);
        Assert.EndsWith("Darsteller: Max Roth, Lea Sand\n\nEine Reise über die Alpen.", change.Description);
        Assert.Equal(1, catalog.ApplyNfoImport(plan.Changes));
        var updated = catalog.GetMedia(film.Id)!;
        Assert.Equal("Der lange Weg", updated.Title);
        Assert.Equal(["Drama", "Abenteuer"], updated.TagCategories["Genre"]);
        Assert.Contains("Anna Berg", updated.Tags);
        Assert.Empty(catalog.PlanNfoImport([film.Id]).Changes); // nothing left to change
    }

    [Fact]
    public void EditsAfterThePreviewAreKeptAndSettingsLimitWhatIsRead()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var film = catalog.Upsert(new MediaItem { Path = Video("a/film.mp4", Movie), Title = "film", Kind = MediaKind.Video });
        var plan = catalog.PlanNfoImport([film.Id]);
        catalog.Upsert(catalog.GetMedia(film.Id)! with { Rating = 8 });
        Assert.Equal(0, catalog.ApplyNfoImport(plan.Changes));
        catalog.SaveNfoPreferences(new NfoPreferences { ImportTitle = false, ImportDescription = false, TagElements = "genre" });
        var change = Assert.Single(catalog.PlanNfoImport([film.Id]).Changes);
        Assert.Null(change.Title); Assert.Null(change.Description); Assert.Equal(["Genre"], change.Tags.Keys);
    }

    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public void LocalThumbnailAndFanartBecomeStoredPicturesWebLinksAreSkipped()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var path = Video("p/film.mp4", """
            <movie><title>Bilder</title>
              <thumb>https://example.invalid/poster.jpg</thumb>
              <thumb aspect="poster">film-poster.png</thumb>
              <fanart><thumb>art/fanart.png</thumb><thumb>missing.png</thumb><thumb>film-poster.png</thumb></fanart>
              <actor><name>Max Roth</name><thumb>actor.png</thumb></actor>
            </movie>
            """);
        Directory.CreateDirectory(Path.Combine(root, "p", "art"));
        File.WriteAllBytes(Path.Combine(root, "p", "film-poster.png"), Png); File.WriteAllBytes(Path.Combine(root, "p", "actor.png"), [.. Png, 1]);
        File.WriteAllBytes(Path.Combine(root, "p", "art", "fanart.png"), [.. Png, 2]);
        var film = catalog.Upsert(new MediaItem { Path = path, Title = "film", Kind = MediaKind.Video, ThumbnailPath = Path.Combine(root, "old.jpg") });
        var plan = catalog.PlanNfoImport([film.Id]);
        Assert.Equal(1, plan.SkippedLinks); Assert.Contains(plan.Problems, problem => problem.Contains("missing.png"));
        var change = Assert.Single(plan.Changes);
        // The poster is the thumbnail only once; the same bytes as fanart are not added again, the actor picture is left out.
        Assert.Equal([("film-poster.png", false), ("fanart.png", true)], change.Images.Select(p => (Path.GetFileName(p.Path), p.IsFanart)));
        Assert.Equal(1, catalog.ApplyNfoImport(plan.Changes));
        var item = catalog.GetMedia(film.Id)!; var assets = catalog.GetMediaAssets(film.Id);
        Assert.Null(item.ThumbnailPath);
        var thumbnail = assets.Single(a => a.Id.ToString() == item.Extra["thumbnail.assetId"]);
        Assert.Equal((MediaAssetKind.Thumbnail, true), (thumbnail.Kind, thumbnail.IsCustom)); Assert.Equal(Png, thumbnail.Data);
        Assert.Equal(MediaAssetKind.Artwork, assets.Single(a => a.Id != thumbnail.Id).Kind);
        // Reading again finds nothing new; switched off, pictures are not planned at all.
        Assert.Empty(catalog.PlanNfoImport([film.Id]).Changes.SelectMany(c => c.Images));
        File.WriteAllBytes(Path.Combine(root, "p", "art", "fanart.png"), [.. Png, 3]);
        catalog.SaveNfoPreferences(new NfoPreferences { ImportThumbnail = false, ImportFanart = false });
        Assert.Empty(catalog.PlanNfoImport([film.Id]).Changes.SelectMany(c => c.Images));
    }

    [Fact]
    public void PictureChangedAfterThePreviewIsLeftOut()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var path = Video("q/film.mp4", "<movie><thumb>poster.png</thumb></movie>"); var poster = Path.Combine(root, "q", "poster.png"); File.WriteAllBytes(poster, Png);
        var film = catalog.Upsert(new MediaItem { Path = path, Title = "film", Kind = MediaKind.Video });
        var plan = catalog.PlanNfoImport([film.Id]); File.WriteAllBytes(poster, [.. Png, 9]);
        Assert.Equal(1, catalog.ApplyNfoImport(plan.Changes));
        Assert.Empty(catalog.GetMediaAssets(film.Id)); Assert.False(catalog.GetMedia(film.Id)!.Extra.ContainsKey("thumbnail.assetId"));
        Assert.Null(CatalogRepository.ResolveNfoPicture("https://example.invalid/a.png", path, path));
        File.WriteAllText(Path.Combine(root, "q", "text.png"), "not a picture");
        Assert.Null(CatalogRepository.ResolveNfoPicture("text.png", path, path));
        Assert.Equal(poster, CatalogRepository.ResolveNfoPicture(poster, path, path));
    }

    [Fact]
    public void OriginalNfoSettingsAreAdopted()
    {
        string[] keys = ["chkimportnfo_title", "chkimportnfo_tags", "importnfodata_tags", "chkimportnfo_description", "importnfodata_description", "chkimportnfo_searchsubfolders", "chkimportnfo_thumbnail", "chkimportnfo_fanart"];
        string[] values = ["False", "True", "Genre; Actor", "True", "plot", "True", "False", "True"];
        var records = new List<object> { new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('7', 64) },
            new { type = "schema", table = "configsettings", columns = new[] { new { name = "ID" }, new { name = "parameter" }, new { name = "value1" } } } };
        records.AddRange(keys.Select((key, index) => (object)new { type = "row", table = "configsettings", values = new { ID = index + 1, parameter = key, value1 = values[index] } }));
        records.Add(new { type = "footer", tables = 1, rows = keys.Length, tableRows = new Dictionary<string, int> { ["configsettings"] = keys.Length } });
        var path = Path.Combine(root, "nfo.jsonl"); File.WriteAllLines(path, records.Select(r => JsonSerializer.Serialize(r)));
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var result = catalog.ImportLegacyJsonl(path, new LegacyImportOptions { AdoptAppSettings = false });
        var plan = catalog.PlanLegacySettings(result.Migration!.Batch);
        Assert.Contains(LegacySettingGroup.Nfo, plan.Groups);
        catalog.ApplyLegacySettings(plan, [LegacySettingGroup.Nfo]);
        Assert.Equal(new NfoPreferences { ImportTitle = false, TagElements = "genre;actor", DescriptionElements = "plot", SearchSubfolders = true, ImportThumbnail = false }, catalog.GetPreferences().Nfo);
    }
}
