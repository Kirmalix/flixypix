using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Package P6: technical data in search, filter bar and sorting; several files per medium (merge, split, main file,
/// best version); the codecs of the statistics; the upgrade of a version 13 library.</summary>
public sealed class MediaVersionsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-versions-" + Guid.NewGuid().ToString("N"));
    private readonly string path;
    private CatalogRepository catalog;

    public MediaVersionsTests()
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "catalog.db");
        catalog = new CatalogRepository(path);
    }

    public void Dispose() { catalog.Dispose(); SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }

    private MediaItem Add(string title, int lines, string? codec = null, double? fps = null, long? bitRate = null, bool hdr = false, long size = 1_000_000) => catalog.Upsert(new MediaItem
    {
        Path = Path.Combine(directory, title + ".mp4"), Title = title, Kind = MediaKind.Video, DurationSeconds = 100, FileSize = size,
        Width = lines * 16 / 9, Height = lines,
        Technical = codec is null ? null : new TechnicalInfo { VideoCodec = codec, AudioCodec = "aac", FrameRate = fps, BitRate = bitRate, Hdr = hdr, Container = "mov,mp4,m4a,3gp,3g2,mj2" },
    });

    private IReadOnlyList<string> Titles(MediaQuery query) => catalog.QueryPage(query with { Limit = 100 }).Items.Select(m => m.Title).ToList();
    private IReadOnlyList<string> Filtered(MediaFilter filter) => Titles(new MediaQuery { Expression = filter.ToExpression(DateTimeOffset.UtcNow) });

    [Fact]
    public void TechnicalDataFiltersAndSorts()
    {
        Add("Alt", 480, size: 50_000_000);
        Add("Klar", 1080, "h264", 29.97, 8_000_000);
        Add("Flüssig", 1080, "hevc", 59.94, 12_000_000);
        Add("Hell", 2160, "hevc", 23.976, 40_000_000, hdr: true);
        Assert.Equal(["Flüssig", "Hell"], Filtered(new MediaFilter { VideoCodecs = ["hevc"] }));
        Assert.Equal(["Flüssig", "Hell", "Klar"], Filtered(new MediaFilter { VideoCodecs = ["hevc", "h264"] }));
        Assert.Equal(["Hell"], Filtered(new MediaFilter { Hdr = true }));
        // Not HDR needs the data to be read: "Alt" has none.
        Assert.Equal(["Flüssig", "Klar"], Filtered(new MediaFilter { Hdr = false }));
        Assert.Equal(["Flüssig"], Filtered(new MediaFilter { MinFrameRate = 60 }));
        Assert.Equal(["Hell"], Filtered(new MediaFilter { Resolution = ResolutionClass.UHD }));
        Assert.Equal(4, Filtered(new MediaFilter { MultipleFiles = false }).Count);
        // Equal lines fall back to the title, in the same direction.
        Assert.Equal(["Hell", "Klar", "Flüssig", "Alt"], Titles(new MediaQuery { Sort = MediaSort.Resolution, Descending = true }));
        // Bit rate: read where known (40 Mbit/s), else from size and duration (50 MB over 100 s = 4 Mbit/s).
        Assert.Equal(["Hell", "Flüssig", "Klar", "Alt"], Titles(new MediaQuery { Sort = MediaSort.Bitrate, Descending = true }));
        Assert.Equal(["Alt"], Titles(new MediaQuery { Expression = SearchExpression.Where(new() { Field = SearchField.VideoCodec, Comparison = SearchComparison.IsUnknown }) }));
        Assert.Equal(["Hell"], Titles(new MediaQuery { Expression = SearchExpression.Where(new() { Field = SearchField.Bitrate, Comparison = SearchComparison.GreaterThan, Number = 20_000 }) }));
        var statistics = catalog.BuildStatistics(DateTimeOffset.UtcNow);
        Assert.Equal([("HEVC", 2L), ("H.264", 1L), (Texts.T("not read yet"), 1L)], statistics.VideoCodecs.Select(b => (b.Label, b.Count)));
        Assert.Equal(["Flüssig", "Hell"], Titles(new MediaQuery { Expression = statistics.VideoCodecs[0].Expression }));
        Assert.Equal(["HEVC", "HDR", Texts.T("from {0} fps", "60")], new MediaFilter { VideoCodecs = ["hevc"], Hdr = true, MinFrameRate = 60 }.Describe());
    }

    [Fact]
    public void LabelsNameCodecsResolutionsAndRates()
    {
        Assert.Equal("H.264", TechnicalLabels.Codec("h264")); Assert.Equal("HEVC", TechnicalLabels.Codec("hevc")); Assert.Equal("XYZ", TechnicalLabels.Codec("xyz"));
        Assert.Equal("4K", TechnicalLabels.Resolution(3840, 2160)); Assert.Equal("1080p", TechnicalLabels.Resolution(1080, 1920)); Assert.Equal("SD", TechnicalLabels.Resolution(640, 480));
        Assert.Equal("", TechnicalLabels.Resolution(null, 480));
        Assert.True(TechnicalLabels.HighFrameRate(59.94)); Assert.False(TechnicalLabels.HighFrameRate(30));
        Assert.Equal("60 fps", TechnicalLabels.FrameRate(59.94).Replace(' ', ' '));
    }

    [Fact]
    public void MergingMakesVersionsAndMovesWhatBelongsToThem()
    {
        var main = Add("Szene", 1080, "h264", 30, 8_000_000);
        var better = catalog.Upsert(Add("Szene 4K", 2160, "hevc", 60, 30_000_000) with { Tags = ["Strand"], Rating = 8, PlayCount = 2, OCount = 1 });
        var bookmark = catalog.UpsertBookmark(new Bookmark { MediaId = better.Id, Title = "Mitte", PositionSeconds = 50 });
        catalog.UpsertScene(new Scene { MediaId = better.Id, Title = "Anfang", StartSeconds = 0, EndSeconds = 10 });
        catalog.BeginPlayback(better.Id, DateTimeOffset.UtcNow);
        var collection = catalog.UpsertPlaylist(new Playlist { Name = "Sammlung", MediaIds = [better.Id, main.Id] });

        var merged = catalog.MergeVersions(main.Id, [better.Id]);
        Assert.Equal(main.Path, merged.Path);
        Assert.Equal([better.Path], merged.Files.Select(f => f.Path));
        Assert.Equal("hevc", merged.Files[0].Technical?.VideoCodec);
        Assert.Equal(["Strand"], merged.Tags); Assert.Equal(8, merged.Rating); Assert.Equal(2, merged.PlayCount); Assert.Equal(1, merged.OCount);
        Assert.Null(catalog.GetMedia(better.Id));
        Assert.Equal(main.Id, Assert.Single(catalog.GetBookmarks(main.Id)).MediaId);
        Assert.Equal(bookmark.Id, catalog.GetBookmarks(main.Id)[0].Id);
        Assert.Single(catalog.GetScenes(main.Id));
        Assert.Single(catalog.GetHistory(main.Id));
        Assert.Equal([main.Id], catalog.GetPlaylists().Single(p => p.Id == collection.Id).MediaIds);
        // The version is cataloged with its medium: lookups find the medium, nothing may take the file.
        Assert.Equal(main.Id, catalog.GetMediaByPath(better.Path)?.Id);
        Assert.Throws<ArgumentException>(() => catalog.Upsert(new MediaItem { Path = better.Path, Title = "Doppelt", Kind = MediaKind.Video }));
        Assert.Equal(["Szene"], Titles(new MediaQuery { Expression = new MediaFilter { MultipleFiles = true }.ToExpression(DateTimeOffset.UtcNow) }));

        // Best version: most lines; a missing file is skipped; preferring the main file plays it while it exists.
        Assert.Equal(better.Path, MediaVersions.PlaybackPath(merged, preferBest: true, _ => true));
        Assert.Equal(main.Path, MediaVersions.PlaybackPath(merged, preferBest: false, _ => true));
        Assert.Equal(main.Path, MediaVersions.PlaybackPath(merged, preferBest: true, file => file != better.Path));
        Assert.Equal(better.Path, MediaVersions.PlaybackPath(merged, preferBest: false, file => file != main.Path));
        Assert.Null(MediaVersions.PlaybackPath(merged, preferBest: true, _ => false));

        // Main file: the version takes its place, the former main file becomes a version; search and sort follow.
        var swapped = catalog.SetMainVersion(main.Id, better.Path);
        Assert.Equal(better.Path, swapped.Path); Assert.Equal(2160, swapped.Height); Assert.Equal("hevc", swapped.Technical?.VideoCodec);
        Assert.Equal([main.Path], swapped.Files.Select(f => f.Path));
        Assert.Equal(main.Id, catalog.GetMediaByPath(main.Path)?.Id);
        Assert.Equal(["Szene"], Filtered(new MediaFilter { Resolution = ResolutionClass.UHD }));

        // Split: the version becomes a medium of its own with tags and rating; bookmarks stay.
        var split = catalog.SplitVersion(main.Id, main.Path);
        Assert.Equal(main.Path, split.Path); Assert.Equal(["Strand"], split.Tags); Assert.Equal(8, split.Rating); Assert.Null(split.PlayCount);
        Assert.Empty(catalog.GetMedia(main.Id)!.Files);
        Assert.Single(catalog.GetBookmarks(main.Id));
        Assert.Equal(split.Id, catalog.GetMediaByPath(main.Path)?.Id);
        Assert.Empty(Titles(new MediaQuery { Expression = new MediaFilter { MultipleFiles = true }.ToExpression(DateTimeOffset.UtcNow) }));
    }

    [Fact]
    public void MergingRefusesImagesAndMixedKinds()
    {
        var video = Add("Video", 720);
        var image = catalog.Upsert(new MediaItem { Path = Path.Combine(directory, "Bild.jpg"), Title = "Bild", Kind = MediaKind.Image });
        Assert.Throws<ArgumentException>(() => catalog.MergeVersions(video.Id, [image.Id]));
        Assert.Throws<ArgumentException>(() => catalog.MergeVersions(video.Id, []));
        Assert.NotNull(catalog.GetMedia(image.Id));
    }

    [Fact]
    public void FolderSyncDoesNotOfferVersionsAsNew()
    {
        var folder = Path.Combine(directory, "Ordner"); Directory.CreateDirectory(folder);
        var first = Path.Combine(folder, "a.mp4"); var second = Path.Combine(folder, "a-4k.mp4");
        File.WriteAllBytes(first, new byte[10]); File.WriteAllBytes(second, new byte[20]);
        var a = catalog.Upsert(new MediaItem { Path = first, Title = "a", Kind = MediaKind.Video });
        var b = catalog.Upsert(new MediaItem { Path = second, Title = "a 4K", Kind = MediaKind.Video });
        catalog.MergeVersions(a.Id, [b.Id]);
        var plan = catalog.PlanFolderSync([new LibraryFolder { Path = folder }]);
        Assert.DoesNotContain(plan.Changes, c => c.Kind == FolderChangeKind.New);
    }

    [Fact]
    public void UpgradeFromVersion13AddsTheTechnicalColumns()
    {
        var video = Add("Alt", 1080, "vp9", 25, 5_000_000);
        catalog.Dispose(); SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            connection.CreateCollation("EFP_ORDINAL", (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
            using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP INDEX media_query_lines_page; DROP INDEX media_query_bit_rate_page; DROP INDEX media_query_video_codec;
                DROP TRIGGER media_query_insert; DROP TRIGGER media_query_update;
                ALTER TABLE media_query DROP COLUMN video_codec; ALTER TABLE media_query DROP COLUMN frame_rate; ALTER TABLE media_query DROP COLUMN bit_rate;
                ALTER TABLE media_query DROP COLUMN hdr; ALTER TABLE media_query DROP COLUMN versions; ALTER TABLE media_query DROP COLUMN lines;
                DROP TABLE media_files; PRAGMA user_version=13;
                """;
            downgrade.ExecuteNonQuery();
        }
        catalog = new CatalogRepository(path);
        Assert.Contains("before-v17-v13", catalog.MigrationBackupPath!);
        Assert.Equal(["Alt"], Filtered(new MediaFilter { VideoCodecs = ["vp9"] }));
        Assert.Equal(["Alt"], Filtered(new MediaFilter { Resolution = ResolutionClass.FullHD }));
        Assert.Equal(video.Id, catalog.GetMediaByPath(video.Path)?.Id);
    }
}
