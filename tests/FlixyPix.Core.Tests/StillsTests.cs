using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Pictures taken from videos: how many a range makes, where they go and how they join the library.</summary>
public sealed class StillsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-stills-" + Guid.NewGuid().ToString("N"));
    public StillsTests() => Directory.CreateDirectory(directory);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }

    [Fact]
    public void ARangeCountsBothEndsAndFollowsItsSpacing()
    {
        // 0.2 s to 0.5 s at 10 frames per second: the frames at 0.2, 0.3, 0.4 and 0.5.
        Assert.Equal(4, new StillRange(.2, .5).Count(10));
        Assert.Equal(2, new StillRange(.2, .5, StillSpacing.EveryNthFrame, 2).Count(10));
        Assert.Equal(1, new StillRange(.2, .5, StillSpacing.EveryNthFrame, 5).Count(10));
        Assert.Equal(7, new StillRange(10, 13, StillSpacing.EverySeconds, .5).Count(29.97));
        Assert.Equal(.5, new StillRange(0, 1, StillSpacing.EverySeconds, .5).Step(25));
        Assert.Equal(.2, new StillRange(0, 1, StillSpacing.EveryNthFrame, 5).Step(25), 9);
        Assert.Throws<ArgumentException>(() => new StillRange(2, 1).Validate(25));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StillRange(0, 1, StillSpacing.EveryNthFrame, 0).Validate(25));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StillRange(0, 1).Validate(0));
        // An hour of every frame is refused; one picture a second of it is fine.
        Assert.Throws<ArgumentException>(() => new StillRange(0, 3600).Validate(25));
        Assert.Equal(3601, new StillRange(0, 3600, StillSpacing.EverySeconds, 1).Validate(25).Count(25));
    }

    [Fact]
    public void PicturesGoIntoAFolderPerVideoAndTellTheirTimeToTheThousandth()
    {
        var screenshots = new ScreenshotPreferences();
        Assert.True(screenshots.Subfolders); Assert.True(screenshots.AddToLibrary);
        Assert.Equal(Path.Combine(directory, "Mein_ Film"), screenshots.FolderFor(directory, "Mein: Film. "));
        Assert.Equal(directory, (screenshots with { Subfolders = false }).FolderFor(directory, "Mein: Film"));
        Assert.Equal(directory, screenshots.FolderFor(directory, " . "));
        Assert.Equal(100, ScreenshotPreferences.FolderName(new string('a', 300)).Length);
        Assert.EndsWith("Film 00-01-23-480.png", screenshots.NextPath(directory, "Film", 83.48, precise: true));
        Assert.EndsWith("Film 00-01-23.png", screenshots.NextPath(directory, "Film", 83.48));
        Assert.Equal("Vorsatz", (screenshots with { Prefix = "Vorsatz" }).NameFor("Film"));
        Assert.Equal(Texts.T("Image"), screenshots.NameFor(" "));
    }

    [Fact]
    public void SavedPicturesJoinTheLibraryOnceAndRememberTheirVideo()
    {
        using var catalog = new CatalogRepository(Path.Combine(directory, "catalog.db"));
        var video = catalog.Upsert(new MediaItem { Path = Path.Combine(directory, "film.mp4"), Title = "Film", Kind = MediaKind.Video });
        var first = Path.Combine(directory, "Film 00-00-01-000.png"); var second = Path.Combine(directory, "Film 00-00-02-500.png");
        File.WriteAllBytes(first, [1]); File.WriteAllBytes(second, [1, 2]);

        var added = catalog.AddStills(video.Id, [new(first, 1), new(second, 2.5), new(Path.Combine(directory, "fehlt.png"), 3)]);
        Assert.Equal(2, added.Count);
        var still = catalog.GetMediaByPath(second)!;
        Assert.Equal(MediaKind.Image, still.Kind); Assert.Equal(2, still.FileSize); Assert.Equal(ReviewState.Unreviewed, still.ReviewState);
        Assert.Equal((video.Id, 2.5), Stills.SourceOf(still));
        Assert.Null(Stills.SourceOf(video));
        // Saving over a known file adds nothing.
        Assert.Empty(catalog.AddStills(video.Id, [new(first, 1)]));
        Assert.Equal(3, catalog.Search().Count);
    }

    [Fact]
    public void TheStillsModeHasItsShortcutUnlessTheKeyIsTaken()
    {
        Assert.Equal("E", PlayerActions.Complete(new Dictionary<string, string>())[PlayerActions.StillsMode]);
        Assert.Equal("", PlayerActions.Complete(new Dictionary<string, string> { [PlayerActions.Mute] = "E" })[PlayerActions.StillsMode]);
        Assert.Contains(PlayerActions.All, action => action.Action == PlayerActions.StillsMode);
    }
}
