using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Opening media with other programs: command lines, checks and the original's custom media players.</summary>
public sealed class ExternalProgramTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-programs-" + Guid.NewGuid().ToString("N"));
    public ExternalProgramTests() => Directory.CreateDirectory(directory);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }
    private string PathFor(string name) => Path.Combine(directory, name);
    private static string Header => JsonSerializer.Serialize(new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('c', 64), sourceName = "fixture.sdf" });
    private static string Schema(string table, params string[] columns) => JsonSerializer.Serialize(new { type = "schema", table,
        columns = columns.Select((name, ordinal) => new { name, dataType = "nvarchar", nullable = true, ordinal }) });
    private static string Row(string table, object values) => JsonSerializer.Serialize(new { type = "row", table, values });
    private static string Footer(Dictionary<string, int> counts) => JsonSerializer.Serialize(new { type = "footer", tables = counts.Count, rows = counts.Values.Sum(), tableRows = counts });

    [Fact]
    public void PlaceholdersBecomeThePartsOfTheMedium()
    {
        var item = new MediaItem { Path = Path.Combine(directory, "Ferien 2024", "Strand.mp4"), Title = "Am \"Strand\"", Kind = MediaKind.Video };
        var program = new ExternalProgram { Name = "VLC", Path = @"C:\Tools\vlc.exe", Arguments = "--start-time={seconds} \"{path}\" --meta-title=\"{title}\" {NAME} \"{folder}\"" };
        Assert.Equal($"--start-time=83 \"{item.Path}\" --meta-title=\"Am 'Strand'\" Strand.mp4 \"{Path.Combine(directory, "Ferien 2024")}\"",
            ExternalPrograms.BuildArguments(program, item, 83.9));
        Assert.Equal($"\"{item.Path}\"", ExternalPrograms.BuildArguments(program with { Arguments = ExternalPrograms.DefaultArguments }, item));
        Assert.Equal("--start-time=0", ExternalPrograms.BuildArguments(program with { Arguments = "--start-time={seconds}" }, item, -5));
    }

    [Fact]
    public void KindsLimitWhereAProgramIsOfferedAndProblemsAreNamed()
    {
        var program = new ExternalProgram { Name = "Viewer", Path = @"C:\Tools\viewer.exe", Kinds = [MediaKind.Image] };
        Assert.True(ExternalPrograms.Applies(program, MediaKind.Image));
        Assert.False(ExternalPrograms.Applies(program, MediaKind.Video));
        Assert.True(ExternalPrograms.Applies(program with { Kinds = [] }, MediaKind.Audio));
        Assert.Null(ExternalPrograms.Problem(program));
        Assert.NotNull(ExternalPrograms.Problem(program with { Name = " " }));
        Assert.NotNull(ExternalPrograms.Problem(program with { Path = "viewer.exe" }));
        Assert.NotNull(ExternalPrograms.Problem(program with { Arguments = "--fullscreen" }));
        Assert.Null(ExternalPrograms.Problem(program with { Arguments = "/open \"{folder}\"" }));
    }

    [Fact]
    public void SubtitleFileAndOffsetTravelWithTheCatalogExport()
    {
        var item = new MediaItem { Path = PathFor("film.mkv"), Title = "Film", Kind = MediaKind.Video, SubtitlePath = PathFor("film.de.srt"), SubtitleDelaySeconds = -1.3 };
        using (var source = new CatalogRepository(PathFor("source.db"))) { source.Upsert(item); source.Export(PathFor("export.json")); }
        using var target = new CatalogRepository(PathFor("target.db"));
        Assert.Empty(target.ImportJson(PathFor("export.json")).Errors);
        var copy = target.GetMediaByPath(item.Path)!;
        Assert.Equal((item.SubtitlePath, -1.3), (copy.SubtitlePath, copy.SubtitleDelaySeconds));
    }

    [Fact]
    public void M3uListsEveryItemWithDurationTitleAndAbsolutePath()
    {
        var first = new MediaItem { Path = Path.Combine(directory, "a b.mp4"), Title = "Erster\nTeil", DurationSeconds = 61.6, Kind = MediaKind.Video };
        var second = new MediaItem { Path = Path.Combine(directory, "lied.mp3"), Kind = MediaKind.Audio };
        Assert.Equal($"#EXTM3U\n#EXTINF:62,Erster Teil\n{first.Path}\n#EXTINF:-1,lied\n{second.Path}\n", M3uPlaylists.Build([first, second]));
        var file = PathFor("liste.m3u8");
        M3uPlaylists.Write(file, [first]);
        var bytes = File.ReadAllBytes(file);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF, "No byte order mark.");
        Assert.StartsWith("#EXTM3U\n", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void TheOriginalsCustomPlayersComeFromTheRetainedRowsOncePerProgram()
    {
        var path = PathFor("legacy.jsonl");
        File.WriteAllLines(path, [Header,
            Schema("customplayersettings", "mediaplayername", "exename", "commandline", "clipcommandline", "instancetype", "hotkey", "active"),
            Row("customplayersettings", new { mediaplayername = "VLC", exename = @"C:\Program Files\VideoLAN\VLC\vlc.exe", commandline = "--fullscreen", clipcommandline = "m3u", instancetype = "Never", hotkey = "", active = 1 }),
            Row("customplayersettings", new { mediaplayername = "", exename = @"C:\Tools\mpc-hc64.exe", commandline = "", clipcommandline = "", instancetype = "Never", hotkey = "", active = 0 }),
            Row("customplayersettings", new { mediaplayername = "VLC again", exename = @"c:\program files\videolan\vlc\VLC.EXE", commandline = "", clipcommandline = "", instancetype = "", hotkey = "", active = 0 }),
            Footer(new() { ["customplayersettings"] = 3 })]);
        using var catalog = new CatalogRepository(PathFor("catalog.db"));
        Assert.Empty(catalog.ImportLegacyJsonl(path).Errors);
        var programs = catalog.GetOriginalExternalPrograms();
        Assert.Equal(["VLC", "mpc-hc64"], programs.Select(p => p.Name));
        Assert.Equal("\"{path}\" --fullscreen", programs[0].Arguments);
        Assert.Equal(ExternalPrograms.DefaultArguments, programs[1].Arguments);
        Assert.All(programs, p => Assert.Equal([MediaKind.Video], p.Kinds));
    }
}
