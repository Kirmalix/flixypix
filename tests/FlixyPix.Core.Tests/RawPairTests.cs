using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Camera RAW files in the catalog: a RAW file and the camera's picture of the same shot are one medium, on
/// import, in the folder check and when one of the two comes later. The files here only have the names; nothing reads them.</summary>
public sealed class RawPairTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-rawpair-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogRepository repository;
    public RawPairTests() { Directory.CreateDirectory(directory); repository = new CatalogRepository(Path.Combine(directory, "catalog.db")); }
    public void Dispose() { repository.Dispose(); SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }

    private string File_(string folder, string name, int bytes = 3)
    {
        Directory.CreateDirectory(Path.Combine(directory, folder));
        var path = Path.Combine(directory, folder, name); File.WriteAllBytes(path, new byte[bytes]); return path;
    }

    [Fact]
    public void RawFilesAreImagesAndKnownApartFromFinishedPictures()
    {
        Assert.Equal(MediaKind.Image, MediaFormats.GetKind(@"C:\Fotos\IMG_0042.CR3"));
        Assert.True(MediaFormats.IsRaw("shot.NEF")); Assert.False(MediaFormats.IsRaw("shot.jpg")); Assert.False(MediaFormats.IsRaw(null));
        Assert.DoesNotContain(".dng", MediaFormats.PictureExtensions); Assert.Contains(".dng", MediaFormats.ImageExtensions);
        Assert.Contains("*.arw", MediaFormats.ImageDialogFilter); Assert.DoesNotContain("*.arw", MediaFormats.PictureDialogFilter);
    }

    [Fact]
    public async Task AnImportMakesOneMediumOfAShotWhicheverFileComesFirst()
    {
        var picture = File_("shoot", "IMG_0001.JPG"); var raw = File_("shoot", "IMG_0001.CR2", 9);
        var alone = File_("shoot", "IMG_0002.CR2"); var plain = File_("shoot", "IMG_0003.jpg");
        var result = await new MediaScanner(repository).ScanAsync([Path.Combine(directory, "shoot")]);
        Assert.Empty(result.Errors);
        var media = repository.Search();
        Assert.Equal(3, media.Count);
        var pair = Assert.Single(media, item => item.Files.Count == 1);
        Assert.Equal(picture, pair.Path); Assert.Equal(raw, pair.Files[0].Path); Assert.Equal(9, pair.Files[0].FileSize); Assert.NotNull(pair.Files[0].FileModifiedAtUtc);
        Assert.Contains(media, item => item.Path == alone && item.Kind == MediaKind.Image);
        Assert.Contains(media, item => item.Path == plain && item.Files.Count == 0);
        // Both names lead to the one medium, and a second import adds nothing.
        Assert.Equal(pair.Id, repository.GetMediaByPath(raw)!.Id);
        Assert.Equal(0, (await new MediaScanner(repository).ScanAsync([Path.Combine(directory, "shoot")])).Imported);
        Assert.Equal(3, repository.Search().Count);
    }

    [Fact]
    public async Task ARawFileChosenAloneBringsItsPictureAndALaterOneJoinsIt()
    {
        var picture = File_("a", "DSC01.jpg"); var raw = File_("a", "DSC01.ARW");
        await new MediaScanner(repository).ScanAsync([raw]);
        var pair = Assert.Single(repository.Search());
        Assert.Equal(picture, pair.Path); Assert.Equal(raw, Assert.Single(pair.Files).Path);

        // The picture was in the catalog first, with its rating; the RAW file joins it and the rating stays.
        var later = File_("b", "DSC02.jpeg");
        await new MediaScanner(repository).ScanAsync([later]);
        var known = repository.GetMediaByPath(later)!; repository.Upsert(known with { Rating = 8 });
        var lateRaw = File_("b", "DSC02.NEF");
        var result = await new MediaScanner(repository).ScanAsync([Path.Combine(directory, "b")]);
        Assert.Equal(known.Id, Assert.Single(result.ImportedIds));
        var joined = repository.GetMedia(known.Id)!;
        Assert.Equal(8, joined.Rating); Assert.Equal(lateRaw, Assert.Single(joined.Files).Path);
        Assert.Equal(2, repository.Search().Count);
    }

    [Fact]
    public async Task WithRawTypesSwitchedOffThePictureStaysAlone()
    {
        var preferences = repository.GetPreferences();
        repository.SavePreferences(preferences with { Previews = preferences.Previews with { ImageExtensions = [".jpg"] } });
        File_("shoot", "P1.jpg"); File_("shoot", "P1.RW2");
        await new MediaScanner(repository).ScanAsync([Path.Combine(directory, "shoot")]);
        Assert.Empty(Assert.Single(repository.Search()).Files);
    }

    [Fact]
    public void LibrariesFromBeforeGetTheRawTypesOnceAndKeepWhatTheUserRemovesLater()
    {
        // A library saved before RAW files were known: its own list of image types, no revision.
        var old = repository.GetPreferences();
        repository.SavePreferences(old with { General = old.General with { TileBadges = [TileBadge.Resolution] }, Previews = old.Previews with { ImageExtensions = [".jpg", ".png"], TypesRevision = 0 } });
        var upgraded = repository.GetPreferences();
        Assert.Contains(".jpg", upgraded.Previews.ImageExtensions); Assert.Contains(".cr3", upgraded.Previews.ImageExtensions); Assert.Contains(".dng", upgraded.Previews.ImageExtensions);
        Assert.Equal(PreviewPreferences.CurrentTypesRevision, upgraded.Previews.TypesRevision);
        Assert.Equal([TileBadge.Resolution, TileBadge.Raw], upgraded.General.TileBadges);
        repository.SavePreferences(upgraded with { General = upgraded.General with { TileBadges = [] }, Previews = upgraded.Previews with { ImageExtensions = [".jpg"] } });
        Assert.Equal([".jpg"], repository.GetPreferences().Previews.ImageExtensions);
        Assert.Empty(repository.GetPreferences().General.TileBadges);
    }

    [Fact]
    public void TheFolderCheckLetsANewRawFileJoinItsPicture()
    {
        var folder = Path.Combine(directory, "watched");
        var known = File_("watched", "A.jpg"); var knownRaw = File_("watched", "A.dng", 7);
        var fresh = File_("watched", "B.JPG"); var freshRaw = File_("watched", "B.RAF"); var lone = File_("watched", "C.orf");
        var picture = repository.Upsert(new MediaItem { Path = known, Title = "A", Kind = MediaKind.Image, Rating = 6, FileSize = 3, FileModifiedAtUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(known), TimeSpan.Zero) });
        var plan = repository.PlanFolderSync([new LibraryFolder { Path = folder }]);
        var changes = plan.Changes.Where(change => change.Kind == FolderChangeKind.New).ToDictionary(change => change.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(4, changes.Count);
        Assert.Equal(known, changes[knownRaw].PairWith); Assert.Equal(fresh, changes[freshRaw].PairWith);
        Assert.Null(changes[fresh].PairWith); Assert.Null(changes[lone].PairWith);

        var result = repository.ApplyFolderSync(plan, plan.Changes.ToList());
        Assert.Equal(2, result.Imported); Assert.Equal(2, result.Paired); Assert.Empty(result.Skipped);
        var joined = repository.GetMedia(picture.Id)!;
        Assert.Equal(6, joined.Rating); Assert.Equal(knownRaw, Assert.Single(joined.Files).Path); Assert.Equal(7, joined.Files[0].FileSize);
        Assert.Equal(freshRaw, Assert.Single(repository.GetMediaByPath(fresh)!.Files).Path);
        Assert.Equal(3, repository.Search().Count);
        // Everything is known now.
        Assert.DoesNotContain(repository.PlanFolderSync([new LibraryFolder { Path = folder }]).Changes, change => change.Kind == FolderChangeKind.New);
    }

    [Fact]
    public void ARawFileTakenOverWithoutItsPictureIsAMediumOfItsOwn()
    {
        var folder = Path.Combine(directory, "watched");
        File_("watched", "B.jpg"); var raw = File_("watched", "B.cr3");
        var plan = repository.PlanFolderSync([new LibraryFolder { Path = folder }]);
        var result = repository.ApplyFolderSync(plan, plan.Changes.Where(change => change.Path == raw).ToList());
        Assert.Equal(1, result.Imported); Assert.Equal(0, result.Paired);
        Assert.Equal(raw, Assert.Single(repository.Search()).Path);
    }

    [Fact]
    public void APictureAndItsRawFileCanBeCombinedAndParted()
    {
        var picture = repository.Upsert(new MediaItem { Path = File_("m", "X.jpg"), Title = "X", Kind = MediaKind.Image, Tags = ["a"] });
        var raw = repository.Upsert(new MediaItem { Path = File_("m", "X.pef"), Title = "X", Kind = MediaKind.Image, Tags = ["b"], Rating = 4 });
        var other = repository.Upsert(new MediaItem { Path = File_("m", "Y.png"), Title = "Y", Kind = MediaKind.Image });
        // Two finished pictures are two media, and the RAW file is never the main file.
        Assert.Throws<ArgumentException>(() => repository.MergeVersions(picture.Id, [other.Id]));
        Assert.Throws<ArgumentException>(() => repository.MergeVersions(raw.Id, [picture.Id]));
        var merged = repository.MergeVersions(picture.Id, [raw.Id]);
        Assert.Equal(raw.Path, Assert.Single(merged.Files).Path); Assert.Equal(4, merged.Rating); Assert.Equal(["a", "b"], merged.Tags);
        Assert.Equal(2, repository.Search().Count);
        var parted = repository.SplitVersion(merged.Id, raw.Path);
        Assert.Equal(MediaKind.Image, parted.Kind); Assert.Empty(repository.GetMedia(merged.Id)!.Files);
    }

    [Fact]
    public void AMediumShowsItsRawFileOnceThePictureIsGone()
    {
        var picture = File_("m", "Z.jpg"); var raw = File_("m", "Z.nef");
        var item = new MediaItem { Path = picture, Title = "Z", Kind = MediaKind.Image, Files = [RawPairs.FileOf(raw)] };
        Assert.Equal(picture, RawPairs.DisplayPath(item)); Assert.Equal(raw, RawPairs.RawOf(item));
        File.Delete(picture);
        Assert.Equal(raw, RawPairs.DisplayPath(item));
        File.Delete(raw);
        Assert.Equal(picture, RawPairs.DisplayPath(item));
        Assert.Null(RawPairs.RawOf(item with { Files = [] }));
        Assert.Equal(raw, RawPairs.RawOf(new MediaItem { Path = raw, Title = "Z", Kind = MediaKind.Image }));
    }

    [Fact]
    public void PartnersAreFoundByNameInTheSameFolder()
    {
        var picture = File_("p", "IMG_7.JPG"); var raw = File_("p", "IMG_7.cr2"); File_("p", "IMG_8.jpg"); File_("q", "IMG_7.jpg");
        Assert.Equal(picture, RawPairs.PictureBeside(raw)); Assert.Equal(raw, RawPairs.RawBeside(picture));
        Assert.Null(RawPairs.RawBeside(Path.Combine(directory, "p", "IMG_8.jpg"))); Assert.Null(RawPairs.RawBeside(Path.Combine(directory, "q", "IMG_7.jpg")));
        // A PNG is no camera picture, and a picture is not the partner of a picture.
        File_("p", "IMG_9.png"); var lone = File_("p", "IMG_9.dng");
        Assert.Null(RawPairs.PictureBeside(lone)); Assert.Null(RawPairs.PictureBeside(picture));
        var listing = new RawPairs.FolderListing();
        Assert.Equal(picture, listing.PictureBeside(raw)); Assert.Equal(raw, listing.RawBeside(picture)); Assert.Null(listing.PictureBeside(lone));
    }

    [Fact]
    public void TheSidecarOfAShotBelongsToThePair()
    {
        var picture = File_("s", "N1.jpg"); var raw = File_("s", "N1.nef");
        var sidecar = Path.Combine(directory, "s", "N1.xmp"); File.WriteAllText(sidecar, "<x/>");
        Assert.Equal(sidecar, XmpSidecar.Find(picture)); Assert.Equal(sidecar, XmpSidecar.Find(raw));
        // A third file of the same name, or two finished pictures: nobody can say whose it is.
        File_("s", "N1.tif");
        Assert.Null(XmpSidecar.Find(picture));
        var one = File_("t", "N2.jpg"); File_("t", "N2.png"); File.WriteAllText(Path.Combine(directory, "t", "N2.xmp"), "<x/>");
        Assert.Null(XmpSidecar.Find(one));
    }
}
