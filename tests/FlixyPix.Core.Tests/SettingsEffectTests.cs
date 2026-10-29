using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Settings must change behaviour, not only be stored: import type lists, hidden folders and the
/// general options adopted from the original.</summary>
public sealed class SettingsEffectTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-settings-effect-" + Guid.NewGuid().ToString("N"));
    public SettingsEffectTests() => Directory.CreateDirectory(root);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
    private CatalogRepository Open() => new(Path.Combine(root, "catalog.db"));
    private string Fixture(string relative)
    {
        var path = Path.Combine(root, "media", relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "synthetic media fixture"); return path;
    }

    [Fact]
    public async Task TypeListsNarrowFolderImportsButChosenFilesAreAlwaysImported()
    {
        var movie = Fixture("clips/movie.mp4"); Fixture("clips/other.mkv"); Fixture("photos/still.jpg");
        using var catalog = Open();
        catalog.SavePreviewPreferences(catalog.GetPreferences().Previews with { VideoExtensions = [".mkv"], ImageExtensions = [".png"] });
        var scanner = new MediaScanner(catalog);
        var folder = await scanner.ScanAsync([Path.Combine(root, "media")]);
        Assert.Equal(["other.mkv"], catalog.Search().Select(i => Path.GetFileName(i.Path)));
        Assert.Equal(1, folder.Imported);
        var chosen = await scanner.ScanAsync([movie]);
        Assert.Equal(1, chosen.Imported);
        Assert.Contains(catalog.Search(), i => Path.GetFileName(i.Path) == Path.GetFileName(movie) && i.Kind == MediaKind.Video);
    }

    [Fact]
    public async Task HiddenSubfoldersAreSkippedUnlessEnabledOrChosen()
    {
        Fixture("visible/a.jpg"); var hidden = Path.Combine(root, "media", "secret"); Fixture("secret/b.jpg");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        using var catalog = Open();
        var scanner = new MediaScanner(catalog);
        await scanner.ScanAsync([Path.Combine(root, "media")]);
        Assert.Equal(["a.jpg"], catalog.Search().Select(i => Path.GetFileName(i.Path)));
        await scanner.ScanAsync([hidden]);
        Assert.Contains(catalog.Search(), i => i.Path.EndsWith("b.jpg", StringComparison.Ordinal));
        foreach (var item in catalog.Search()) catalog.Delete(item.Id);
        catalog.SaveGeneralPreferences(catalog.GetPreferences().General with { IncludeHiddenFolders = true });
        await scanner.ScanAsync([Path.Combine(root, "media")]);
        Assert.Equal(2, catalog.Search().Count);
    }

    [Fact]
    public void GeneralOptionsFromTheOriginalAreAdopted()
    {
        string[] keys = ["searchhiddenfolders", "confirmdelete", "showmediaextension", "highlightvideonotfound", "alwaysshowimporterrors", "loadlastvideoview", "maximagethumbmousewheelcmd", "disablescreensaver", "ezfpmediakeys",
            "showvideofoldercounts", "showimagefoldercounts", "showimagecategorycounts", "showvideotagcategorycounts"];
        string[] values = ["True", "True", "True", "False", "True", "True", "1", "False", "False", "False", "True", "False", "True"];
        var records = new List<object> { new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('d', 64) },
            new { type = "schema", table = "configsettings", columns = new[] { new { name = "ID" }, new { name = "parameter" }, new { name = "value1" } } } };
        records.AddRange(keys.Select((key, index) => (object)new { type = "row", table = "configsettings", values = new { ID = index + 1, parameter = key, value1 = values[index] } }));
        records.Add(new { type = "footer", tables = 1, rows = keys.Length, tableRows = new Dictionary<string, int> { ["configsettings"] = keys.Length } });
        var path = Path.Combine(root, "legacy.jsonl"); File.WriteAllLines(path, records.Select(r => JsonSerializer.Serialize(r)));
        using var catalog = Open();
        var result = catalog.ImportLegacyJsonl(path);
        Assert.Empty(result.Errors);
        var preferences = catalog.GetPreferences();
        var general = preferences.General;
        Assert.True(general.IncludeHiddenFolders);
        Assert.False(general.ConfirmRemove); // "Never prompt when deleting media" was on
        Assert.True(general.ShowExtension); Assert.False(general.MarkMissingFiles); Assert.True(general.AlwaysShowImportReport); Assert.True(general.RestoreLastView);
        Assert.Equal(ImageWheelAction.Zoom, general.ImageWheel);
        Assert.False(preferences.Playback.PreventSleep); Assert.False(preferences.Playback.MediaKeys);
        // The video switch decides; without it the image switch counts.
        Assert.False(general.ShowFolderCounts); Assert.False(general.ShowCategoryCounts); Assert.True(general.ShowTagCategoryCounts);
        Assert.Equal(keys.Length, catalog.GetMigrationRows(result.Migration!.Batch, 0, 100).Count(r => r.Code == "app-setting"));
    }

    [Fact]
    public void DefaultsFollowTheOriginal()
    {
        var general = new GeneralPreferences();
        Assert.False(general.IncludeHiddenFolders); Assert.True(general.ConfirmRemove); Assert.True(general.MarkMissingFiles);
        Assert.Equal(ImageWheelAction.Navigate, general.ImageWheel);
        using var catalog = Open();
        Assert.Throws<ArgumentException>(() => catalog.SaveGeneralPreferences(general with { ImageWheel = (ImageWheelAction)9 }));
    }
}
