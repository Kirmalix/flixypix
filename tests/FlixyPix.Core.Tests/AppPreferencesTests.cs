using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class AppPreferencesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-preferences-" + Guid.NewGuid().ToString("N"));
    public AppPreferencesTests() => Directory.CreateDirectory(root);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }

    [Theory]
    [InlineData("Right", "Right")]
    [InlineData("ALT+SHIFT+CTRL+Right", "Ctrl+Shift+Alt+Right")]
    [InlineData("CTRL+Return", "Ctrl+Enter")]
    [InlineData("Next", "PageDown")]
    [InlineData("set hotkey", "")]
    [InlineData("", "")]
    public void LegacyHotkeysUseModernNotation(string legacy, string expected) => Assert.Equal(expected, CatalogRepository.ConvertLegacyHotkey(legacy));

    [Fact]
    public void OriginalSettingsAreAdoptedAndAudited()
    {
        string[] keys = ["fullscreen", "nhotkeysmallffwd", "nhotkeylargerwnd", "hotkeysmallffwd", "hotkeyplaypause", "hotkeyfullscreen",
         "ffmpegcommandline", "vidthumbcolumns", "vidthumbrows", "vidthumbwidth", "numvidthumbproc", "bypassvideoswithcustomthumbnail",
         "videofiletypes", "imagefiletypes", "vidthumbcolumns2", "customvideoplayer", "ezfphoverenabled", "ezfphoverthumbnails", "ezfphoverspeed",
         "ezfphoverdelay", "ezfphovertimestamp"];
        string[] values = ["True", "15", "45", "CTRL+Right", "Space", "Space", "", "3", "2", "1280", "4", "False", "mp4;MKV;.avi", "jpg;png", "9", @"C:\player.exe", "False", "15", "Fast", "False", "True"];
        var records = new List<object> { new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('e', 64) },
            new { type = "schema", table = "configsettings", columns = new[] { new { name = "ID" }, new { name = "parameter" }, new { name = "value1" } } } };
        records.AddRange(keys.Select((key, index) => (object)new { type = "row", table = "configsettings", values = new { ID = index + 1, parameter = key, value1 = values[index] } }));
        records.Add(new { type = "footer", tables = 1, rows = keys.Length, tableRows = new Dictionary<string, int> { ["configsettings"] = keys.Length } });
        var path = Path.Combine(root, "legacy.jsonl"); File.WriteAllLines(path, records.Select(r => JsonSerializer.Serialize(r)));
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var result = catalog.ImportLegacyJsonl(path);
        Assert.Empty(result.Errors);
        var preferences = catalog.GetPreferences();
        Assert.True(preferences.Playback.AlwaysFullscreen);
        Assert.Equal(15, preferences.Playback.SmallJumpForwardSeconds); Assert.Equal(45, preferences.Playback.LargeJumpBackwardSeconds);
        Assert.Equal(10, preferences.Playback.SmallJumpBackwardSeconds);
        Assert.Equal("Ctrl+Right", preferences.Playback.Hotkeys[PlayerActions.SmallForward]);
        Assert.Equal("Space", preferences.Playback.Hotkeys[PlayerActions.PlayPause]);
        Assert.Equal("", preferences.Playback.Hotkeys[PlayerActions.Fullscreen]);
        Assert.Contains(result.Warnings, w => w.Contains("mehrfach belegt"));
        Assert.False(preferences.Playback.HardwareDecoding);
        Assert.Equal((3, 2, 1280, 4), (preferences.Previews.SheetColumns, preferences.Previews.SheetRows, preferences.Previews.SheetWidth, preferences.Previews.GeneratorProcesses));
        Assert.False(preferences.Previews.SkipWhenCustomThumbnail);
        Assert.Equal([".mp4", ".mkv", ".avi"], preferences.Previews.VideoExtensions);
        Assert.Equal([".jpg", ".png"], preferences.Previews.ImageExtensions);
        var rows = catalog.GetMigrationRows(result.Migration!.Batch, 0, 100);
        Assert.Equal((false, 15, HoverSpeed.Fast, false, true), (preferences.Previews.HoverEnabled, preferences.Previews.HoverFrames, preferences.Previews.HoverSpeed, preferences.Previews.HoverDelay, preferences.Previews.HoverTimestamp));
        Assert.Equal(19, rows.Count(r => r.Code == "app-setting"));
        Assert.Equal(2, rows.Count(r => r.Status == "raw-retained"));
    }

    [Theory]
    [InlineData("Bottom Right", SheetTimestamp.BottomRight)]
    [InlineData("Top Left", SheetTimestamp.TopLeft)]
    [InlineData("None", SheetTimestamp.None)]
    [InlineData("3", SheetTimestamp.None)]
    public void OriginalSheetStyleIsAdoptedAndInvalidValuesKeepDefaults(string stamp, SheetTimestamp expected)
    {
        string[] keys = ["vidthumbnailheader", "vidthumbborder", "vidthumbtimestamp", "vidthumbbackcolor", "vidthumbforecolor", "thumbnailfont", "vidthumbheight"];
        string[] values = ["True", "5", stamp, "#ff8000", "blue", "Verdana:14:True:False:False", "720"];
        var records = new List<object> { new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('f', 64) },
            new { type = "schema", table = "configsettings", columns = new[] { new { name = "ID" }, new { name = "parameter" }, new { name = "value1" } } } };
        records.AddRange(keys.Select((key, index) => (object)new { type = "row", table = "configsettings", values = new { ID = index + 1, parameter = key, value1 = values[index] } }));
        records.Add(new { type = "footer", tables = 1, rows = keys.Length, tableRows = new Dictionary<string, int> { ["configsettings"] = keys.Length } });
        var path = Path.Combine(root, "sheet.jsonl"); File.WriteAllLines(path, records.Select(r => JsonSerializer.Serialize(r)));
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        Assert.Empty(catalog.ImportLegacyJsonl(path).Errors);
        var previews = catalog.GetPreferences().Previews;
        Assert.Equal((true, 5, expected, "#FF8000", "Verdana"), (previews.SheetHeader, previews.SheetBorder, previews.SheetTimestamp, previews.SheetBackground, previews.SheetFont));
        // A named colour is not a #RRGGBB value; the FlixyPix default stays.
        Assert.Equal(new PreviewPreferences().SheetForeground, previews.SheetForeground);
        CatalogRepository.ValidatePreviews(previews);
    }

    [Theory]
    [InlineData("7", "Random", "True", "False", "Slow", "True", 7, SlideshowOrder.Random, true, false, HoverSpeed.Slow, true)]
    [InlineData("0", "In Order", "False", "True", "Fast", "False", .25, SlideshowOrder.InOrder, false, true, HoverSpeed.Fast, false)]
    [InlineData("45", "3", "x", "x", "9", "x", 5, SlideshowOrder.LessRandom, true, false, HoverSpeed.Medium, false)]
    public void OriginalSlideshowSettingsAreAdoptedAndOffered(string interval, string order, string fade, string pan, string speed, string details,
        double expectedInterval, SlideshowOrder expectedOrder, bool expectedFade, bool expectedPan, HoverSpeed expectedSpeed, bool expectedInfo)
    {
        string[] keys = ["slideshow_interval", "slideshow_display", "slideshow_fade", "slideshow_pan", "slideshow_speed", "image_details"];
        string[] values = [interval, order, fade, pan, speed, details];
        var records = new List<object> { new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('b', 64) },
            new { type = "schema", table = "configsettings", columns = new[] { new { name = "ID" }, new { name = "parameter" }, new { name = "value1" } } } };
        records.AddRange(keys.Select((key, index) => (object)new { type = "row", table = "configsettings", values = new { ID = index + 1, parameter = key, value1 = values[index] } }));
        records.Add(new { type = "footer", tables = 1, rows = keys.Length, tableRows = new Dictionary<string, int> { ["configsettings"] = keys.Length } });
        var path = Path.Combine(root, "slideshow.jsonl"); File.WriteAllLines(path, records.Select(r => JsonSerializer.Serialize(r)));
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var result = catalog.ImportLegacyJsonl(path, new LegacyImportOptions { AdoptAppSettings = false });
        Assert.Equal(new SlideshowPreferences(), catalog.GetPreferences().Slideshow);
        var plan = catalog.PlanLegacySettings(result.Migration!.Batch);
        catalog.ApplyLegacySettings(plan, [LegacySettingGroup.Slideshow]);
        var slideshow = catalog.GetPreferences().Slideshow;
        Assert.Equal((expectedInterval, expectedOrder, expectedFade, expectedPan, expectedSpeed, expectedInfo),
            (slideshow.IntervalSeconds, slideshow.Order, slideshow.Fade, slideshow.PanAndZoom, slideshow.Speed, slideshow.ShowInfo));
        Assert.Throws<ArgumentException>(() => catalog.SaveSlideshowPreferences(slideshow with { IntervalSeconds = 31 }));
        Assert.Throws<ArgumentException>(() => catalog.SaveSlideshowPreferences(slideshow with { Order = (SlideshowOrder)7 }));
    }

    [Fact]
    public void LaterActionsGetDefaultsWithoutTakingAKeyInUse()
    {
        var saved = PlayerActions.Defaults().Where(p => PlayerActions.All.Take(13).Any(a => a.Action == p.Key)).ToDictionary(p => p.Key, p => p.Value);
        saved[PlayerActions.Fullscreen] = "S"; // the user gave S to full screen before "Screenshot" existed
        var complete = PlayerActions.Complete(saved);
        Assert.Equal(PlayerActions.All.Count, complete.Count);
        Assert.Equal(("", "N", "S"), (complete[PlayerActions.Screenshot], complete[PlayerActions.NextVideo], complete[PlayerActions.Fullscreen]));
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        catalog.SavePreferences(catalog.GetPreferences() with { Playback = catalog.GetPreferences().Playback with { Hotkeys = saved } });
        Assert.Equal("", catalog.GetPreferences().Playback.Hotkeys[PlayerActions.Screenshot]);
        CatalogRepository.ValidatePlayback(catalog.GetPreferences().Playback);
    }

    [Fact]
    public void OriginalMouseActionsAreAdoptedByListPosition()
    {
        string[] keys = ["ezfpleftclick", "ezfprightclick", "ezfpmiddleclick", "ezfpx1click", "ezfpx2click", "ezfpwheelposition"];
        string[] values = ["3", "11", "", "99", "x", "4"];
        var records = new List<object> { new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('9', 64) },
            new { type = "schema", table = "configsettings", columns = new[] { new { name = "ID" }, new { name = "parameter" }, new { name = "value1" } } } };
        records.AddRange(keys.Select((key, index) => (object)new { type = "row", table = "configsettings", values = new { ID = index + 1, parameter = key, value1 = values[index] } }));
        records.Add(new { type = "footer", tables = 1, rows = keys.Length, tableRows = new Dictionary<string, int> { ["configsettings"] = keys.Length } });
        var path = Path.Combine(root, "mouse.jsonl"); File.WriteAllLines(path, records.Select(r => JsonSerializer.Serialize(r)));
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        Assert.Empty(catalog.ImportLegacyJsonl(path).Errors);
        var playback = catalog.GetPreferences().Playback;
        // "99" and "x" are no list positions: the FlixyPix defaults stay.
        Assert.Equal((PlayerMouseAction.NextVideo, PlayerMouseAction.Screenshot, PlayerMouseAction.Nothing, PlayerMouseAction.PreviousVideo, PlayerMouseAction.NextVideo, PlayerWheelAction.FrameStep),
            (playback.LeftClick, playback.RightClick, playback.MiddleClick, playback.BackClick, playback.ForwardClick, playback.Wheel));
        Assert.Throws<ArgumentException>(() => catalog.SavePlaybackPreferences(playback with { Wheel = (PlayerWheelAction)9 }));
    }

    [Fact]
    public void WheelOfOlderLibrariesSeeksOnceWhileLaterChoicesStay()
    {
        var path = Path.Combine(root, "wheel.db");
        using (var catalog = new CatalogRepository(path))
        {
            Assert.Equal(PlayerWheelAction.Seek, catalog.GetPreferences().Playback.Wheel);
            catalog.SavePlaybackPreferences(catalog.GetPreferences().Playback with { Wheel = PlayerWheelAction.Volume });
        }
        // A library saved before the revision existed: same document without "wheelRevision".
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE preferences SET document=json_remove(document,'$.playback.wheelRevision')";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        using (var catalog = new CatalogRepository(path))
        {
            Assert.Equal(PlayerWheelAction.Seek, catalog.GetPreferences().Playback.Wheel);
            catalog.SavePlaybackPreferences(catalog.GetPreferences().Playback with { Wheel = PlayerWheelAction.Volume });
        }
        using (var catalog = new CatalogRepository(path)) Assert.Equal(PlayerWheelAction.Volume, catalog.GetPreferences().Playback.Wheel);
    }

    [Fact]
    public void RightClickOfOlderLibrariesOpensThePlayerMenuOnceWhileLaterChoicesStay()
    {
        var path = Path.Combine(root, "right-click.db");
        using (var catalog = new CatalogRepository(path))
        {
            Assert.Equal(PlayerMouseAction.ContextMenu, catalog.GetPreferences().Playback.RightClick);
            catalog.SavePlaybackPreferences(catalog.GetPreferences().Playback with { RightClick = PlayerMouseAction.Nothing });
        }
        // A library saved before the revision existed: same document without "mouseRevision".
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE preferences SET document=json_remove(document,'$.playback.mouseRevision')";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        using (var catalog = new CatalogRepository(path))
        {
            Assert.Equal(PlayerMouseAction.ContextMenu, catalog.GetPreferences().Playback.RightClick);
            catalog.SavePlaybackPreferences(catalog.GetPreferences().Playback with { RightClick = PlayerMouseAction.Nothing });
        }
        using (var catalog = new CatalogRepository(path)) Assert.Equal(PlayerMouseAction.Nothing, catalog.GetPreferences().Playback.RightClick);
    }

    [Fact]
    public void FurtherOriginalShortcutsAreAdopted()
    {
        string[] keys = ["hotkeyplaynext", "hotkeyplayprevious", "hotkeyclipstart", "hotkeyclipend", "hotkeyclipcreate", "hotkeybookmarktoggle", "hotkeyscreenshot", "hotkeyslowmo"];
        string[] values = ["CTRL+Next", "CTRL+Prior", "Q", "W", "SHIFT+Q", "T", "F12", "set hotkey"];
        var records = new List<object> { new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('c', 64) },
            new { type = "schema", table = "configsettings", columns = new[] { new { name = "ID" }, new { name = "parameter" }, new { name = "value1" } } } };
        records.AddRange(keys.Select((key, index) => (object)new { type = "row", table = "configsettings", values = new { ID = index + 1, parameter = key, value1 = values[index] } }));
        records.Add(new { type = "footer", tables = 1, rows = keys.Length, tableRows = new Dictionary<string, int> { ["configsettings"] = keys.Length } });
        var path = Path.Combine(root, "hotkeys.jsonl"); File.WriteAllLines(path, records.Select(r => JsonSerializer.Serialize(r)));
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        Assert.Empty(catalog.ImportLegacyJsonl(path).Errors);
        var hotkeys = catalog.GetPreferences().Playback.Hotkeys;
        Assert.Equal(("Ctrl+PageDown", "Ctrl+PageUp", "Q", "W", "Shift+Q", "T", "F12", ""),
            (hotkeys[PlayerActions.NextVideo], hotkeys[PlayerActions.PreviousVideo], hotkeys[PlayerActions.ClipStart], hotkeys[PlayerActions.ClipEnd],
             hotkeys[PlayerActions.ClipCreate], hotkeys[PlayerActions.ToggleBookmark], hotkeys[PlayerActions.Screenshot], hotkeys[PlayerActions.SlowMotion]));
    }

    [Fact]
    public void SheetStyleIsValidated()
    {
        var valid = new PreviewPreferences();
        CatalogRepository.ValidatePreviews(valid);
        foreach (var invalid in new[] { valid with { SheetBorder = 21 }, valid with { SheetSkipPercent = -1 }, valid with { SheetBackground = "#12345" },
            valid with { SheetForeground = "white" }, valid with { SheetTimestamp = (SheetTimestamp)9 }, valid with { SheetFont = " " } })
            Assert.Throws<ArgumentException>(() => CatalogRepository.ValidatePreviews(invalid));
    }

    [Fact]
    public void HoverPreviewSpreadsFramesAndKeepsTiming()
    {
        Assert.Equal([20d, 40, 60, 80], HoverPreview.FrameTimes(100, 4));
        Assert.Empty(HoverPreview.FrameTimes(0, 5));
        Assert.Equal(HoverPreview.MaximumFrames, HoverPreview.FrameTimes(60, 50).Count);
        Assert.True(HoverPreview.Interval(HoverSpeed.Slow) > HoverPreview.Interval(HoverSpeed.Medium) && HoverPreview.Interval(HoverSpeed.Medium) > HoverPreview.Interval(HoverSpeed.Fast));
        Assert.True(HoverPreview.StartDelay(true) > HoverPreview.StartDelay(false));
        Assert.NotEqual(HoverPreview.CacheFileName(Guid.Empty, 5, 0), HoverPreview.CacheFileName(Guid.Empty, 8, 0));
    }

    [Fact]
    public void PlayerVisibilityAndShortcutChoicesSurviveReopening()
    {
        var path = Path.Combine(root, "remembered-player.db");
        using (var catalog = new CatalogRepository(path))
        {
            var original = catalog.GetPreferences().Playback;
            Assert.True(original.KeyboardShortcuts);
            Assert.True(original.ShowMarks);
            catalog.SavePlaybackPreferences(original with { KeyboardShortcuts = false, ShowMarks = false });
        }
        using (var reopened = new CatalogRepository(path))
        {
            var saved = reopened.GetPreferences().Playback;
            Assert.False(saved.KeyboardShortcuts);
            Assert.False(saved.ShowMarks);
            Assert.Equal("B", saved.Hotkeys[PlayerActions.CreateBookmark]);
            reopened.SavePlaybackPreferences(saved with { ShowMarks = true });
            Assert.False(reopened.GetPreferences().Playback.KeyboardShortcuts);
        }
        using var again = new CatalogRepository(path);
        Assert.True(again.GetPreferences().Playback.ShowMarks);
        Assert.False(again.GetPreferences().Playback.KeyboardShortcuts);
    }

    [Fact]
    public void SavedPreferencesAreValidated()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var playback = catalog.GetPreferences().Playback;
        Assert.Throws<ArgumentException>(() => catalog.SavePlaybackPreferences(playback with { SmallJumpForwardSeconds = 0 }));
        var hotkeys = new Dictionary<string, string>(playback.Hotkeys) { [PlayerActions.Fullscreen] = "Space" };
        Assert.Throws<ArgumentException>(() => catalog.SavePlaybackPreferences(playback with { Hotkeys = hotkeys }));
        Assert.Throws<ArgumentException>(() => catalog.SavePreviewPreferences(catalog.GetPreferences().Previews with { SheetColumns = 7 }));
        Assert.Throws<ArgumentException>(() => catalog.SavePreviewPreferences(catalog.GetPreferences().Previews with { VideoExtensions = ["mp4"] }));
        Assert.Throws<ArgumentException>(() => catalog.SavePreviewPreferences(catalog.GetPreferences().Previews with { HoverFrames = 21 }));
        Assert.Throws<ArgumentException>(() => catalog.SavePreviewPreferences(catalog.GetPreferences().Previews with { HoverSpeed = (HoverSpeed)7 }));
        catalog.SavePlaybackPreferences(playback with { AlwaysFullscreen = true, LargeJumpForwardSeconds = 60 });
        Assert.Equal(60, catalog.GetPreferences().Playback.LargeJumpForwardSeconds);
        Assert.True(catalog.GetPreferences().Playback.AlwaysFullscreen);
    }
}
