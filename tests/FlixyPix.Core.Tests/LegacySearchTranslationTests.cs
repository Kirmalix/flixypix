using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class LegacySearchTranslationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-search-translation-" + Guid.NewGuid().ToString("N"));
    private CatalogRepository Open(string file = "catalog.db") => new(Path.Combine(root, file));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static string Video(string fields) => "<SearchSettings_Video>" + fields + "</SearchSettings_Video>";
    private static string Image(string fields) => "<SearchSettings_Image>" + fields + "</SearchSettings_Image>";
    private static LegacySearchDraft Import(CatalogRepository catalog, string xml, int type = 500, int id = 1)
    {
        catalog.ImportHistoricalRecords([new HistoricalLegacyRow("search_settings", JsonSerializer.SerializeToElement(new { ID = id, searchtype = type, xml }))], "source");
        return catalog.GetLegacySearchDrafts().Single(draft => draft.LegacyId == id.ToString());
    }
    private MediaItem Item(string filename, MediaKind kind = MediaKind.Video) => new() { Path = Path.Combine(root, filename), Kind = kind };
    private static IReadOnlyList<MediaItem> Query(CatalogRepository catalog, string xml, int type = 500)
    {
        var draft = Import(catalog, xml, type);
        var preview = catalog.PreviewLegacySearchTranslation(draft.Id);
        Assert.True(preview.CanApply, string.Join("; ", preview.Blockers));
        return catalog.SearchSaved(catalog.ApplyLegacySearchTranslation(preview).Id);
    }

    [Fact]
    public void ImageFiltersUseOriginalKilobytesDimensionsHalfStarsAndImageKind()
    {
        using var catalog = Open();
        var match = catalog.Upsert(Item("match.jpg", MediaKind.Image) with { FileSize = 2000, Width = 640, Height = 480, Rating = 7 });
        catalog.Upsert(Item("small.jpg", MediaKind.Image) with { FileSize = 1999, Width = 640, Height = 480, Rating = 7 });
        catalog.Upsert(Item("video.mp4") with { FileSize = 2000, Width = 640, Height = 480, Rating = 7 });
        var xml = Image("<ChkPictureFilesize>true</ChkPictureFilesize><CmbPictureFilesize>&gt;=</CmbPictureFilesize><NPictureFilesize>2</NPictureFilesize>" +
            "<ChkPictureWidth>true</ChkPictureWidth><CmbPictureWidth>=</CmbPictureWidth><NPictureWidth>640</NPictureWidth>" +
            "<ChkPictureHeight>true</ChkPictureHeight><CmbPictureHeight>&lt;=</CmbPictureHeight><NPictureHeight>480</NPictureHeight>" +
            "<ChkPictureRating>true</ChkPictureRating><CmbPicRatingOp>=</CmbPicRatingOp><ImgStarRating>7</ImgStarRating>");
        Assert.Equal(match.Id, Assert.Single(Query(catalog, xml, 502)).Id);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(501)]
    public void VideoFiltersUseMegabytesOriginalLengthEpochAndExactExtension(int type)
    {
        using var catalog = Open();
        var match = catalog.Upsert(Item("match.AVI") with { FileSize = 2_000_000, DurationSeconds = 37 });
        catalog.Upsert(Item("other.avif") with { FileSize = 2_000_000, DurationSeconds = 37 });
        catalog.Upsert(Item("small.avi") with { FileSize = 2000, DurationSeconds = 37 });
        var xml = Video("<ChkMovieFilesize>true</ChkMovieFilesize><CmbMovieFilesize>=</CmbMovieFilesize><NMovieFilesize>2</NMovieFilesize>" +
            "<ChkMovieLength>true</ChkMovieLength><CmbMovieLength>=</CmbMovieLength><NMovieLength>2000-01-01T00:00:37</NMovieLength>" +
            "<ChkMovieType>true</ChkMovieType><CmbMovieType>avi</CmbMovieType>");
        Assert.Equal(match.Id, Assert.Single(Query(catalog, xml, type)).Id);
    }

    [Theory]
    [InlineData(502)]
    [InlineData(503)]
    public void EmptySavedOrLastImageSearchSelectsOnlyImages(int type)
    {
        using var catalog = Open();
        var image = catalog.Upsert(Item("image.jpg", MediaKind.Image)); catalog.Upsert(Item("video.mp4"));
        Assert.Equal(image.Id, Assert.Single(Query(catalog, Image(""), type)).Id);
    }

    [Fact]
    public void FilenameDefaultDoesNotMatchParentFolderOrTitle()
    {
        using var catalog = Open();
        var match = catalog.Upsert(Item("holiday.mp4") with { Title = "Different" });
        catalog.Upsert(Item(Path.Combine("holiday", "other.mp4")) with { Title = "holiday" });
        Assert.Equal(match.Id, Assert.Single(Query(catalog, Video("<CmboKeyword>holiday</CmboKeyword>"))).Id);
    }

    [Fact]
    public void OriginalFullpathColumnSearchesOnlyDirectoryWithTrailingSeparator()
    {
        using var catalog = Open();
        var match = catalog.Upsert(Item(Path.Combine("holiday", "different.mp4")));
        catalog.Upsert(Item("holiday.mp4"));
        Assert.Equal(match.Id, Assert.Single(Query(catalog, Video("<CmboKeyword>holiday</CmboKeyword><ChkKeyFilter2>true</ChkKeyFilter2>"))).Id);
    }

    [Fact]
    public void TitleOnlyDoesNotMatchFilenameAndSelectedColumnsAreAlternatives()
    {
        using var catalog = Open();
        var title = catalog.Upsert(Item("different.mp4") with { Title = "holiday" });
        var filename = catalog.Upsert(Item("holiday.mp4") with { Title = "Different" });
        var body = "<CmboKeyword>holiday</CmboKeyword><ChkKeyFilter4>true</ChkKeyFilter4>";
        Assert.Equal(title.Id, Assert.Single(Query(catalog, Video(body))).Id);
        Assert.Equal(new[] { title.Id, filename.Id }.OrderBy(id => id), Query(catalog, Video(body + "<ChkKeyFilter1>true</ChkKeyFilter1>")).Select(item => item.Id).OrderBy(id => id));
    }

    [Theory]
    [InlineData("red blue", "blue red.mp4", "red.mp4")]
    [InlineData("red AND blue", "blue red.mp4", "red.mp4")]
    [InlineData("red,blue", "red.mp4", "green.mp4")]
    [InlineData("red OR blue", "blue.mp4", "green.mp4")]
    [InlineData("&quot;red blue&quot;", "red blue.mp4", "blue red.mp4")]
    [InlineData("red -blue", "red green.mp4", "red blue.mp4")]
    [InlineData("red*", "red green.mp4", "green red.mp4")]
    [InlineData("*red", "green red.mp4", "red green.mp4")]
    public void KeywordGrammarPreservesOriginalGroupingAndEdgeWildcards(string keyword, string matching, string excluded)
    {
        using var catalog = Open();
        var match = catalog.Upsert(Item(matching)); catalog.Upsert(Item(excluded));
        Assert.Equal(match.Id, Assert.Single(Query(catalog, Video("<CmboKeyword>" + keyword + "</CmboKeyword>"))).Id);
    }

    [Theory]
    [InlineData("<ChkMovieDate>true</ChkMovieDate>", "filedate")]
    [InlineData("<ChkMovieKbps>true</ChkMovieKbps>", "numeric")]
    [InlineData("<ChkBookmark>true</ChkBookmark>", "renamed")]
    [InlineData("<G_moviesearchkeywords>legacy tags</G_moviesearchkeywords>", "tag criteria")]
    [InlineData("<RbSearchCriteria1>true</RbSearchCriteria1><G_moviesearchkeywords>Rot" + D + "x</G_moviesearchkeywords>", "unavailable")]
    [InlineData("<RbSearchCriteria4>true</RbSearchCriteria4><G_moviesearchkeywords>Rot" + D + "x</G_moviesearchkeywords>", "without tags")]
    [InlineData("<RbSearchCriteria1>true</RbSearchCriteria1><G_moviesearchkeywords>R_t" + D + "x</G_moviesearchkeywords>", "wildcard")]
    [InlineData("<ChkListDirectory>C:\\legacy</ChkListDirectory>", "directory")]
    [InlineData("<PropertiesValueList><item /></PropertiesValueList>", "property")]
    [InlineData("<NewOption>true</NewOption>", "Unknown")]
    [InlineData("<ChkMovieWidth>perhaps</ChkMovieWidth>", "boolean")]
    [InlineData("<CmboKeyword>a_b</CmboKeyword>", "wildcard")]
    [InlineData("<CmboKeyword>-only</CmboKeyword>", "positive")]
    public void UnsupportedOrAmbiguousActiveSemanticsBlockInsteadOfBroadeningSearch(string body, string reason)
    {
        using var catalog = Open(); var draft = Import(catalog, Video(body));
        var preview = catalog.PreviewLegacySearchTranslation(draft.Id);
        Assert.False(preview.CanApply); Assert.Contains(preview.Blockers, blocker => blocker.Contains(reason, StringComparison.OrdinalIgnoreCase));
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyLegacySearchTranslation(preview));
        Assert.Empty(catalog.GetSavedSearches()); Assert.False(Assert.Single(catalog.GetLegacySearchDrafts()).IsExecutable);
    }

    private const string D = "DT5ttEr9RE90reFjmw";
    private static string Tags(params string[] keys) => "<G_moviesearchkeywords>" + string.Join(D + D, keys.Select(key => key + D + key)) + "</G_moviesearchkeywords>";

    /// <summary>A regular takeover with keyword rows that give tags to a category and a folder, plus one stored video search.</summary>
    private static IReadOnlyList<MediaItem> QueryWithTakeover(CatalogRepository catalog, string root, string xml, params (int Type, string Keyword, string KeyTags)[] inherited)
    {
        object Schema(string table, params string[] fields) => new { type = "schema", table, columns = fields.Select(name => new { name }) };
        var records = new List<object> { new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('e', 64) }, Schema("keyword", "ID", "type", "keyword", "keytags") };
        records.AddRange(inherited.Select((row, index) => (object)new { type = "row", table = "keyword", values = new { ID = index + 1, type = row.Type, keyword = row.Keyword, keytags = row.KeyTags } }));
        records.Add(Schema("search_settings", "ID", "searchtype", "xml")); records.Add(new { type = "row", table = "search_settings", values = new { ID = 99, searchtype = 500, xml } });
        records.Add(new { type = "footer", tables = 2, rows = inherited.Length + 1, tableRows = new Dictionary<string, int> { ["keyword"] = inherited.Length, ["search_settings"] = 1 } });
        var path = Path.Combine(root, "takeover-" + Guid.NewGuid().ToString("N") + ".jsonl"); Directory.CreateDirectory(root);
        File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(record)));
        Assert.Empty(catalog.ImportLegacyJsonl(path).Errors);
        var preview = catalog.PreviewLegacySearchTranslation(Assert.Single(catalog.GetLegacySearchDrafts()).Id);
        Assert.True(preview.CanApply, string.Join("; ", preview.Blockers));
        return catalog.SearchSaved(catalog.ApplyLegacySearchTranslation(preview).Id);
    }

    [Fact]
    public void TagSearchIncludesTagsGivenToCategoriesAndFoldersAndNegatesThemForNoneOfThese()
    {
        using var catalog = Open();
        var own = catalog.Upsert(Item("a/own.mp4") with { Tags = ["Rotwein"] });
        var byCategory = catalog.Upsert(Item("a/category.mp4") with { Category = "Reise" });
        var byFolder = catalog.Upsert(Item("ordner/sub/folder.mp4"));
        var other = catalog.Upsert(Item("a/other.mp4") with { Category = "Andere", Tags = ["Blau"] });
        var plain = catalog.Upsert(Item("a/plain.mp4"));
        (int, string, string)[] rows = [(20, "Category: Reise", "\"Rot\""), (18, "Folder: " + Path.Combine(root, "ordner") + Path.DirectorySeparatorChar, "\"Rot\" \"Grün\""), (20, "Category: Andere", "\"Gelb\"")];
        // "Rot" is part of the tag "Rotwein"; the category and the folder carry it as a whole tag.
        Assert.Equal(new[] { own.Id, byCategory.Id, byFolder.Id }.Order(), QueryWithTakeover(catalog, root, Video("<RbSearchCriteria1>true</RbSearchCriteria1>" + Tags("Rot")), rows).Select(i => i.Id).Order());
        using var none = Open("none.db");
        foreach (var item in new[] { own, byCategory, byFolder, other, plain }) none.Upsert(item);
        // A video without category drops out, as "categoryid NOT IN (…)" is never true for it.
        Assert.Equal(other.Id, Assert.Single(QueryWithTakeover(none, Path.Combine(root, "n"), Video("<RbSearchCriteria3>true</RbSearchCriteria3>" + Tags("Rot")), rows)).Id);
    }

    [Fact]
    public void AllTagsExactAndExcludedEntriesAndUnionWithTheKeyword()
    {
        using var catalog = Open();
        var both = catalog.Upsert(Item("both.mp4") with { Tags = ["Rot", "Blau"] });
        catalog.Upsert(Item("partial.mp4") with { Tags = ["Rotwein", "Blau"] });
        catalog.Upsert(Item("excluded.mp4") with { Tags = ["Rot", "Blau", "Grau"] });
        var named = catalog.Upsert(Item("zebra.mp4"));
        var xml = Video("<RbSearchCriteria2>true</RbSearchCriteria2><ChkMovieSearchUnion>true</ChkMovieSearchUnion><CmboKeyword>zebra</CmboKeyword>" +
            Tags("[***!***]Rot", "Blau", "[!!!*!!!]Grau"));
        Assert.Equal(new[] { both.Id, named.Id }.Order(), QueryWithTakeover(catalog, root, xml).Select(i => i.Id).Order());
    }

    [Fact]
    public void OrientationAndBitrateCompareHeightToWidthAndSizeToDuration()
    {
        using var catalog = Open();
        var portrait = catalog.Upsert(Item("portrait.mp4") with { Width = 1080, Height = 1920, FileSize = 1_000_000, DurationSeconds = 10 });
        var square = catalog.Upsert(Item("square.mp4") with { Width = 500, Height = 500, FileSize = 10_000_000, DurationSeconds = 10 });
        catalog.Upsert(Item("unknown.mp4"));
        Assert.Equal(portrait.Id, Assert.Single(Query(catalog, Video("<ChkVideoOrientation>true</ChkVideoOrientation><CmbVideoOrientation>Portrait</CmbVideoOrientation>"))).Id);
        using var second = Open("second.db"); foreach (var item in new[] { portrait, square }) second.Upsert(item); second.Upsert(Item("unknown.mp4"));
        Assert.Equal(square.Id, Assert.Single(Query(second, Video("<ChkVideoOrientation>true</ChkVideoOrientation><CmbVideoOrientation>Landscape</CmbVideoOrientation>"))).Id);
        // 10 MB in 10 s are 8000 kbit/s, 1 MB in 10 s 800 kbit/s.
        using var third = Open("third.db"); foreach (var item in new[] { portrait, square }) third.Upsert(item);
        Assert.Equal(square.Id, Assert.Single(Query(third, Video("<ChkMovieKbps>true</ChkMovieKbps><CmbMovieKBPS>&gt;</CmbMovieKBPS><NMovieKbps>1000</NMovieKbps>"))).Id);
    }

    [Fact]
    public void InactiveUnsupportedOptionsDoNotPreventKnownNumericTranslation()
    {
        using var catalog = Open();
        var match = catalog.Upsert(Item("match.mp4") with { Rating = 6 });
        var xml = Video("<ChkMovieDate>false</ChkMovieDate><DtpMovieDate>not-used</DtpMovieDate><ChkMovieKbps>false</ChkMovieKbps><NMovieKbps>9</NMovieKbps>" +
            "<ChkMovieRating>true</ChkMovieRating><CmbVidRatingOp>&gt;=</CmbVidRatingOp><ImgStarRating>6</ImgStarRating><PropertiesValueList />");
        Assert.Equal(match.Id, Assert.Single(Query(catalog, xml)).Id);
    }

    [Fact]
    public void DuplicateFieldsMalformedComparatorsAndRootTypeDisagreementRemainInactive()
    {
        using var catalog = Open();
        var duplicate = Import(catalog, Video("<ChkMovieRating>false</ChkMovieRating><ChkMovieRating>true</ChkMovieRating>"));
        Assert.Contains(catalog.PreviewLegacySearchTranslation(duplicate.Id).Blockers, text => text.Contains("Duplicate"));
        var comparison = Import(catalog, Video("<ChkMovieRating>true</ChkMovieRating><ImgStarRating>5</ImgStarRating><CmbVidRatingOp>DROP TABLE</CmbVidRatingOp>"));
        Assert.Contains(catalog.PreviewLegacySearchTranslation(comparison.Id).Blockers, text => text.Contains("comparison"));
        var mismatch = Import(catalog, Image(""), 500);
        Assert.Contains(catalog.PreviewLegacySearchTranslation(mismatch.Id).Blockers, text => text.Contains("same video/image"));
    }

    [Fact]
    public void PreviewIsReadOnlyAndExplicitApplicationKeepsDraftAndStableTargetIdentity()
    {
        using var catalog = Open(); var draft = Import(catalog, Video(""));
        var preview = catalog.PreviewLegacySearchTranslation(draft.Id, "Meine Suche");
        Assert.True(preview.CanApply); Assert.Empty(catalog.GetSavedSearches()); Assert.Null(preview.CurrentSearch);
        var saved = catalog.ApplyLegacySearchTranslation(preview);
        Assert.Equal("Meine Suche", saved.Name); Assert.Equal(draft.RawXml, Assert.Single(catalog.GetLegacySearchDrafts()).RawXml);
        Assert.False(Assert.Single(catalog.GetLegacySearchDrafts()).IsExecutable);
        var again = catalog.PreviewLegacySearchTranslation(draft.Id, "Meine Suche");
        Assert.Equal(saved.Id, again.CurrentSearch!.Id); Assert.Equal(saved.Id, catalog.ApplyLegacySearchTranslation(again).Id);
        Assert.Single(catalog.GetSavedSearches());
    }

    [Fact]
    public void SourceChangeTargetEditAndForgedProposalRejectStalePreview()
    {
        using var catalog = Open(); var draft = Import(catalog, Video(""));
        var original = catalog.PreviewLegacySearchTranslation(draft.Id);
        var forged = original with { ProposedSearch = original.ProposedSearch! with { Text = "unexpected" } };
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyLegacySearchTranslation(forged)); Assert.Empty(catalog.GetSavedSearches());
        var saved = catalog.ApplyLegacySearchTranslation(original);
        var beforeEdit = catalog.PreviewLegacySearchTranslation(draft.Id);
        catalog.UpsertSavedSearch(saved with { Name = "Local edit" });
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyLegacySearchTranslation(beforeEdit));
        Assert.Equal("Local edit", Assert.Single(catalog.GetSavedSearches()).Name);
        var beforeSource = catalog.PreviewLegacySearchTranslation(draft.Id);
        Import(catalog, Video("<CmboKeyword>changed</CmboKeyword>"));
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyLegacySearchTranslation(beforeSource));
    }

    [Fact]
    public void CancellationPublishesNoSavedSearchAndSourceBytesAreChecked()
    {
        using var catalog = Open(); var draft = Import(catalog, Video(""));
        var preview = catalog.PreviewLegacySearchTranslation(draft.Id);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => catalog.PreviewLegacySearchTranslation(draft.Id, cancellationToken: canceled.Token));
        Assert.Throws<OperationCanceledException>(() => catalog.ApplyLegacySearchTranslation(preview, canceled.Token));
        Assert.Empty(catalog.GetSavedSearches());
        Assert.False(LegacyHistoricalReader.TranslateSearchSettings(draft with { RawXmlSha256 = new string('0', 64) }).CanApply);
    }

    [Fact]
    public void PortableRoundTripRetainsIndependentActiveSearchAndInactiveOriginal()
    {
        using var catalog = Open(); var media = catalog.Upsert(Item("match.mp4") with { Rating = 9 });
        var draft = Import(catalog, Video("<ChkMovieRating>true</ChkMovieRating><CmbVidRatingOp>&gt;</CmbVidRatingOp><ImgStarRating>8</ImgStarRating>"));
        var saved = catalog.ApplyLegacySearchTranslation(catalog.PreviewLegacySearchTranslation(draft.Id));
        var path = Path.Combine(root, "catalog.json"); catalog.Export(path);
        using var copy = Open("copy.db"); Assert.Empty(copy.ImportJson(path).Errors);
        Assert.Equal(saved.Id, Assert.Single(copy.GetSavedSearches()).Id);
        Assert.Equal(media.Id, Assert.Single(copy.SearchSaved(saved.Id)).Id);
        Assert.Equal(draft.RawXmlSha256, Assert.Single(copy.GetLegacySearchDrafts()).RawXmlSha256);
        Assert.False(Assert.Single(copy.GetLegacySearchDrafts()).IsExecutable);
    }
}
