using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlixyPix.Core;
using FlixyPix.MediaTools;
using Xunit;

namespace FlixyPix.MediaTools.Tests;

/// <summary>Decoder evidence for representative temporal/track/orientation variants, using generated data only.</summary>
public sealed class VideoCompatibilityTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "flixypix-video-variants-" + Guid.NewGuid().ToString("N"));
    private readonly string ffmpeg;
    private readonly string ffprobe;
    private readonly MediaToolService tools;

    public VideoCompatibilityTests()
    {
        Directory.CreateDirectory(scratch); var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Cannot locate the locked media toolchain.");
        var media = Path.Combine(root.FullName, ".tools", "media");
        ffmpeg = Directory.GetFiles(Path.Combine(media, "ffmpeg"), "ffmpeg.exe", SearchOption.AllDirectories).Single();
        ffprobe = Directory.GetFiles(Path.Combine(media, "ffmpeg"), "ffprobe.exe", SearchOption.AllDirectories).Single();
        tools = new(new(ffmpeg, ffprobe, Path.Combine(media, "mpv", "mpv.exe"), HeadlessPlayback: true));
    }

    [Fact]
    public async Task TwelveSecondVideoSeeksBothDirectionsAndResumesPersistedPositionInNewPlayer()
    {
        var source = Path.Combine(scratch, "Zwölf Sekunden ü.mp4");
        await Generate(["-f", "lavfi", "-i", "testsrc2=size=160x90:rate=25:duration=12", "-c:v", "libx264", "-preset", "ultrafast", "-g", "50", "-pix_fmt", "yuv420p", source]);
        var before = Hash(source); var probe = await tools.ProbeAsync(source);
        Assert.InRange(probe.DurationSeconds ?? 0, 11.9, 12.1);
        var catalogPath = Path.Combine(scratch, "resume.db"); Guid id;
        using (var catalog = new CatalogRepository(catalogPath))
        {
            var item = catalog.Upsert(new() { Path = source, Kind = MediaKind.Video, DurationSeconds = probe.DurationSeconds }); id = item.Id;
            await using var player = await tools.PlayAsync(source); await player.PauseAsync(true);
            await player.SeekAsync(9.25); await At(player, 9.25);
            await player.SeekAsync(2.5); await At(player, 2.5);
            await player.SeekAsync(9.25); var position = await At(player, 9.25);
            catalog.Upsert(item with { PlaybackPositionSeconds = position });
        }
        using (var reopened = new CatalogRepository(catalogPath))
        {
            var saved = reopened.GetMedia(id)!; Assert.InRange(saved.PlaybackPositionSeconds, 9.1, 9.4);
            await using var resumed = await tools.PlayAsync(source, saved.PlaybackPositionSeconds); await resumed.PauseAsync(true);
            await At(resumed, saved.PlaybackPositionSeconds);
            await resumed.SetSpeedAsync(8); await resumed.PauseAsync(false); await End(resumed);
            Assert.InRange((await resumed.GetPositionAsync()) ?? 0, 11.8, 12.2);
        }
        var preview = Path.Combine(scratch, "late.png"); await tools.CreateThumbnailAsync(source, preview, 10.5);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, File.ReadAllBytes(preview)[..8]);
        Assert.Equal(before, Hash(source));
    }

    [Fact]
    public async Task VariableFrameRateHasDifferentPacketTimesAndDecodesAcrossTheRateChange()
    {
        var source = Path.Combine(scratch, "variable-rate.mp4");
        await Generate(["-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30:duration=4", "-vf", "select='if(lt(t,1),not(mod(n,3)),1)'",
            "-fps_mode", "vfr", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", source]);
        var before = Hash(source); var probe = await tools.ProbeAsync(source); Assert.Equal("h264", probe.VideoCodec);
        using var frames = JsonDocument.Parse(await Run(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_frames", "-show_entries", "frame=best_effort_timestamp_time", "-of", "json", source]));
        var times = frames.RootElement.GetProperty("frames").EnumerateArray().Select(frame => double.Parse(frame.GetProperty("best_effort_timestamp_time").GetString()!, CultureInfo.InvariantCulture)).ToArray();
        var intervals = times.Zip(times.Skip(1), (first, second) => second - first).ToArray();
        Assert.True(intervals.Count(interval => interval > .09 && interval < .11) >= 5, "The fixture must contain its 10fps section.");
        Assert.True(intervals.Count(interval => interval > .03 && interval < .04) >= 30, "The fixture must contain its 30fps section.");
        await using var player = await tools.PlayAsync(source); await player.PauseAsync(true);
        await player.SeekAsync(.6); await At(player, .6, .15);
        await player.SeekAsync(2.4); await At(player, 2.4);
        await player.SetSpeedAsync(4); await player.PauseAsync(false); await End(player);
        Assert.InRange((await player.GetPositionAsync()) ?? 0, 3.8, 4.2);
        await tools.CreateThumbnailAsync(source, Path.Combine(scratch, "vfr.png"), 1.8); Assert.Equal(before, Hash(source));
    }

    [Fact]
    public async Task MatroskaDualAudioAndEmbeddedUnicodeSubtitlesCanBeSelectedAndDecoded()
    {
        var subtitle = Path.Combine(scratch, "Untertitel ü.srt");
        await File.WriteAllTextAsync(subtitle, "1\n00:00:00,200 --> 00:00:03,500\nGrüße aus München\n\n", new UTF8Encoding(false));
        var source = Path.Combine(scratch, "mehrere Spuren.mkv");
        await Generate(["-f", "lavfi", "-i", "testsrc2=size=160x90:rate=25:duration=4", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=4", "-f", "srt", "-i", subtitle,
            "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0", "-map", "3:s:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-c:s", "srt", "-metadata:s:a:0", "title=Deutsch", "-metadata:s:a:0", "language=deu",
            "-metadata:s:a:1", "title=English", "-metadata:s:a:1", "language=eng", "-metadata:s:s:0", "title=Untertitel", "-metadata:s:s:0", "language=deu", source]);
        var before = Hash(source); var subtitleBefore = Hash(subtitle); var probe = await tools.ProbeAsync(source);
        using (var streams = JsonDocument.Parse(probe.RawJson))
        {
            Assert.Equal(2, streams.RootElement.GetProperty("streams").EnumerateArray().Count(stream => stream.GetProperty("codec_type").GetString() == "audio"));
            Assert.Contains(streams.RootElement.GetProperty("streams").EnumerateArray(), stream => stream.GetProperty("codec_name").GetString() == "subrip");
        }
        var extracted = await Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", source, "-map", "0:s:0", "-f", "srt", "-"]);
        Assert.Contains("Grüße aus München", extracted);
        await using var player = await tools.PlayAsync(source); await player.PauseAsync(true);
        var tracks = await player.GetTracksAsync(); var audio = tracks.Where(track => track.Type == "audio").ToArray(); var sub = Assert.Single(tracks, track => track.Type == "sub");
        Assert.Equal(2, audio.Length); Assert.Contains(audio, track => track.Title == "Deutsch"); Assert.Contains(audio, track => track.Title == "English");
        await player.SelectAudioTrackAsync(audio[1].Id); await Selected(player, "audio", audio[1].Id);
        await player.SelectAudioTrackAsync(audio[0].Id); await Selected(player, "audio", audio[0].Id);
        await player.SelectSubtitleTrackAsync(sub.Id); await Selected(player, "sub", sub.Id);
        await player.SeekAsync(1.25); await At(player, 1.25);
        await player.SelectSubtitleTrackAsync(null); await Selected(player, "sub", null);
        await player.SelectSubtitleTrackAsync(sub.Id); await Selected(player, "sub", sub.Id);
        await player.SetSpeedAsync(4); await player.PauseAsync(false); await End(player);
        Assert.InRange((await player.GetPositionAsync()) ?? 0, 3.8, 4.3);
        Assert.Equal(before, Hash(source)); Assert.Equal(subtitleBefore, Hash(subtitle));
    }

    [Fact]
    public async Task Mp4DisplayRotationProducesPortraitThumbnailAndDecodesWithoutChangingSource()
    {
        var original = Path.Combine(scratch, "landscape.mp4"); var source = Path.Combine(scratch, "portrait by metadata.mp4");
        await Generate(["-f", "lavfi", "-i", "testsrc2=size=160x90:rate=25:duration=2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", original]);
        var originalHash = Hash(original);
        await Generate(["-display_rotation", "90", "-i", original, "-map", "0:v:0", "-c", "copy", source]);
        var before = Hash(source); var probe = await tools.ProbeAsync(source); Assert.Equal(160, probe.Width); Assert.Equal(90, probe.Height);
        using (var raw = JsonDocument.Parse(await Run(ffprobe, ["-v", "error", "-show_streams", "-of", "json", source])))
        {
            var video = raw.RootElement.GetProperty("streams").EnumerateArray().Single(stream => stream.GetProperty("codec_type").GetString() == "video");
            Assert.Contains(video.GetProperty("side_data_list").EnumerateArray(), side => side.TryGetProperty("rotation", out var rotation) && Math.Abs(rotation.GetInt32()) == 90);
        }
        var preview = Path.Combine(scratch, "rotated.png"); await tools.CreateThumbnailAsync(source, preview, .5);
        var bytes = File.ReadAllBytes(preview); var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)); var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        Assert.True(height > width, $"Rotation must be applied to decoded thumbnail, got {width} x {height}.");
        await using var player = await tools.PlayAsync(source); await player.PauseAsync(true); await player.SeekAsync(.5); await At(player, .5);
        await player.SetSpeedAsync(4); await player.PauseAsync(false); await End(player);
        Assert.InRange((await player.GetPositionAsync()) ?? 0, 1.8, 2.2);
        Assert.Equal(before, Hash(source)); Assert.Equal(originalHash, Hash(original));
    }

    private static async Task<double> At(MpvPlayer player, double expected, double tolerance = .12)
    {
        double? position = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            position = await player.GetPositionAsync();
            if (position is { } value && Math.Abs(value - expected) <= tolerance) return value;
            await Task.Delay(20);
        }
        Assert.Fail($"Expected decoder position {expected}, observed {position}."); return 0;
    }
    private static async Task Selected(MpvPlayer player, string type, int? id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var selected = (await player.GetTracksAsync()).Where(track => track.Type == type && track.Selected).ToArray();
            if (id is null ? selected.Length == 0 : selected.Length == 1 && selected[0].Id == id) return;
            await Task.Delay(20);
        }
        Assert.Fail("Selected track did not settle.");
    }
    private static async Task End(MpvPlayer player)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); await player.WaitForEndAsync(timeout.Token);
    }
    private Task<string> Generate(IEnumerable<string> arguments) => Run(ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n" }.Concat(arguments));
    private static async Task<string> Run(string file, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); } catch { if (!process.HasExited) process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await stderr); return await stdout;
    }
    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));
    public void Dispose() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
}
