using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Phase 7: the remaining original settings that FlixyPix adopts.</summary>
public sealed class RemainingSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-remaining-" + Guid.NewGuid().ToString("N"));
    public RemainingSettingsTests() => Directory.CreateDirectory(root);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }

    /// <summary>A library that took over the given configsettings rows of the original.</summary>
    private CatalogRepository TakeOver(params (string Key, string Value)[] settings)
    {
        var records = new List<object> { new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('7', 64) },
            new { type = "schema", table = "configsettings", columns = new[] { new { name = "ID" }, new { name = "parameter" }, new { name = "value1" } } } };
        records.AddRange(settings.Select((s, index) => (object)new { type = "row", table = "configsettings", values = new { ID = index + 1, parameter = s.Key, value1 = s.Value } }));
        records.Add(new { type = "footer", tables = 1, rows = settings.Length, tableRows = new Dictionary<string, int> { ["configsettings"] = settings.Length } });
        var path = Path.Combine(root, "legacy.jsonl"); File.WriteAllLines(path, records.Select(r => JsonSerializer.Serialize(r)));
        var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        Assert.Empty(catalog.ImportLegacyJsonl(path).Errors);
        return catalog;
    }

    [Fact]
    public void PlayerDisplayAndScreenshotSettingsAreAdopted()
    {
        var folder = Path.Combine(root, "Fotos");
        using var catalog = TakeOver(("ezfpplayermessages", "False"), ("ezfpplayerautohidecontrols", "False"), ("enablekeyshortcuts", "False"), ("showchapters", "False"),
            ("videoscreenshots-directory", folder), ("videoscreenshots-prefix", " Schnappschuss "), ("videoscreenshots-format", ".JPG"), ("videoscreenshots-seqnumbering", "True"),
            ("hotkeyswitchmousewheel", "CTRL+W"));
        var playback = catalog.GetPreferences().Playback;
        Assert.Equal((false, false, false, false), (playback.ShowMessages, playback.AutoHideControls, playback.KeyboardShortcuts, playback.ShowMarks));
        Assert.Equal(new ScreenshotPreferences { Folder = folder, Prefix = "Schnappschuss", Format = ScreenshotFormat.Jpg, Numbering = true }, playback.Screenshots);
        Assert.Equal("Ctrl+W", playback.Hotkeys[PlayerActions.SwitchWheel]);
    }

    [Fact]
    public void UnusableScreenshotValuesKeepTheDefaults()
    {
        using var catalog = TakeOver(("videoscreenshots-directory", "relativ\\ordner"), ("videoscreenshots-format", "bmp"));
        Assert.Equal(new ScreenshotPreferences(), catalog.GetPreferences().Playback.Screenshots);
        Assert.Throws<ArgumentException>(() => catalog.SavePlaybackPreferences(catalog.GetPreferences().Playback with { Screenshots = new() { Folder = "nur-ein-name" } }));
    }

    [Fact]
    public void LibrarySettingsAreAdopted()
    {
        using var catalog = TakeOver(("displayvideotitle", "False"), ("displaymoviename", "True"), ("showvideoprogress", ""), ("clickvidthumb", "Plays video"),
            ("enablemousehoverzoomthumbnail", "False"), ("pageselectioncontrols", "False"), ("navigationhistorystore", "12"), ("rememberform", "False"),
            ("videotagmatchtype", "match"), ("fileexclusionchars", "##[]"), ("filereplacechars", "_."), ("filteroutpic", "thumb, folder;cover"),
            ("image_fade", "True"), ("deletetorecyclebin", "False"), ("recentlywatcheddays", "14"));
        var preferences = catalog.GetPreferences(); var general = preferences.General;
        Assert.Equal((false, true, false, true, false, false), (general.ShowTitles, general.FullTitles, general.ShowProgress, general.ClickPlays, general.HoverLift, general.ShowPager));
        Assert.Equal((12, false, true, true, false), (general.HistoryLength, general.RememberWindow, general.TagsMatchAny, general.ImageFade, general.DeleteToRecycleBin));
        Assert.Equal(("#[]", "_."), (general.TitleRemoveCharacters, general.TitleSpaceCharacters));
        Assert.Equal(["thumb", "folder", "cover"], general.SkipImageNames);
        Assert.Equal(14, preferences.Home.RecentDays);
    }

    [Theory]
    [InlineData("clickvidthumb", "Shows thumbnail")]
    [InlineData("showvideoprogress", "X")]
    [InlineData("videotagmatchtype", "none")]
    [InlineData("navigationhistorystore", "5000")]
    public void OtherOriginalValuesKeepFlixyPixBehaviour(string key, string value)
    {
        using var catalog = TakeOver((key, value));
        var general = catalog.GetPreferences().General;
        Assert.Equal((false, true, false, 50), (general.ClickPlays, general.ShowProgress, general.TagsMatchAny, general.HistoryLength));
    }

    [Fact]
    public async Task ImportCleansTitlesAndSkipsImagesOnlyInFolders()
    {
        var folder = Path.Combine(root, "import"); Directory.CreateDirectory(folder);
        foreach (var name in new[] { "Am_See.Abend[2019].jpg", "folder.jpg", "Urlaub.mp4" }) File.WriteAllBytes(Path.Combine(folder, name), [1, 2, 3]);
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        catalog.SaveGeneralPreferences(catalog.GetPreferences().General with { TitleRemoveCharacters = "[]", TitleSpaceCharacters = "_.", SkipImageNames = ["FOLDER"] });
        var scanner = new MediaScanner(catalog);
        await scanner.ScanAsync([folder]);
        Assert.Equal(["Am See Abend2019", "Urlaub"], catalog.Search().Select(i => i.Title).Order());
        // Chosen one by one, the image comes anyway.
        await scanner.ScanAsync([Path.Combine(folder, "folder.jpg")]);
        Assert.Contains(catalog.Search(), i => i.Title == "folder");
        Assert.Equal("Name", new GeneralPreferences { TitleRemoveCharacters = "_" }.ImportTitle(@"C:\x\Name.mp4"));
        Assert.Equal("___", new GeneralPreferences { TitleRemoveCharacters = "_" }.ImportTitle(@"C:\x\___.mp4"));
        Assert.Throws<ArgumentException>(() => catalog.SaveGeneralPreferences(catalog.GetPreferences().General with { HistoryLength = 0 }));
        Assert.Throws<ArgumentException>(() => catalog.SaveGeneralPreferences(catalog.GetPreferences().General with { SkipImageNames = [" "] }));
    }

    [Fact]
    public void RecentlyWatchedRowCoversTheChosenDays()
    {
        var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        MediaItem Video(string title, int daysAgo)
        {
            var item = catalog.Upsert(new() { Path = Path.Combine(root, title + ".mp4"), Title = title, Kind = MediaKind.Video, DurationSeconds = 100, ThumbnailPath = Path.Combine(root, title + ".jpg") });
            catalog.RecordPlay(item.Id, now.AddDays(-daysAgo)); return item;
        }
        Video("Heute", 0); Video("Vorgestern", 2); Video("Alt", 10);
        catalog.SavePreferences(catalog.GetPreferences() with { Home = new() { Rows = [HomeRowKind.RecentlyWatched], RecentDays = 3 } });
        var row = catalog.BuildHomeFeed(now).Rows.Single();
        Assert.Equal("Zuletzt gesehen", row.Title);
        Assert.Equal(["Heute", "Vorgestern"], row.Cards.Select(c => c.Item.Title));
        Assert.Equal(["heute", "vor 2 Tagen"], row.Cards.Select(c => c.Reason));
        Assert.Throws<ArgumentException>(() => catalog.SavePreferences(catalog.GetPreferences() with { Home = new() { RecentDays = 0 } }));
    }

    [Fact]
    public void LoweringTheHistoryDropsTheOldestViews()
    {
        var history = new ViewHistory(10);
        foreach (var view in new[] { "all", "Video", "Image", "Audio" }) history.Record(new ViewState { View = view });
        history.Capacity = 2;
        Assert.Equal("Audio", history.Current!.View);
        Assert.Equal("Image", history.Back()!.View);
        Assert.False(history.CanGoBack);
        Assert.Throws<ArgumentOutOfRangeException>(() => history.Capacity = 0);
    }

    [Fact]
    public void ScreenshotNamesFollowPrefixFormatAndNumbering()
    {
        var timed = new ScreenshotPreferences();
        Assert.Equal(Path.Combine(root, "Mein: Film".Replace(':', '_') + " 01-02-03.png"), timed.NextPath(root, "Mein: Film", 3723));
        File.WriteAllText(Path.Combine(root, "Mein_ Film 01-02-03.png"), "");
        Assert.EndsWith("Mein_ Film 01-02-03 (2).png", timed.NextPath(root, "Mein: Film", 3723));
        var numbered = new ScreenshotPreferences { Prefix = "Shot", Format = ScreenshotFormat.Jpg, Numbering = true };
        Assert.EndsWith("Shot 0001.jpg", numbered.NextPath(root, "egal", 0));
        // Numbering continues after the highest number, also across formats; gaps are not refilled.
        File.WriteAllText(Path.Combine(root, "Shot 0001.jpg"), ""); File.WriteAllText(Path.Combine(root, "Shot 0007.png"), "");
        Assert.EndsWith("Shot 0008.jpg", numbered.NextPath(root, "egal", 0));
        Assert.EndsWith("Bild 0001.jpg", (numbered with { Prefix = "" }).NextPath(root, " ", 0));
    }
}
