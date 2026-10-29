using System.Diagnostics;
using FlixyPix.MediaTools;
using Xunit;

namespace FlixyPix.MediaTools.Tests;

/// <summary>Preview clips with real FFmpeg: parts, length, width, no sound, source untouched, short videos whole.</summary>
public sealed class PreviewClipTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "flixypix-clips-" + Guid.NewGuid().ToString("N"));
    private readonly string ffmpeg;
    private readonly MediaToolService service;

    public PreviewClipTests()
    {
        Directory.CreateDirectory(scratch);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Cannot locate project toolchain.");
        var tools = Path.Combine(root.FullName, ".tools", "media");
        ffmpeg = Directory.GetFiles(Path.Combine(tools, "ffmpeg"), "ffmpeg.exe", SearchOption.AllDirectories).Single();
        var ffprobe = Directory.GetFiles(Path.Combine(tools, "ffmpeg"), "ffprobe.exe", SearchOption.AllDirectories).Single();
        service = new(new(ffmpeg, ffprobe, Path.Combine(tools, "mpv", "mpv.exe"), HeadlessPlayback: true));
    }

    [Fact]
    public void PartsSpreadBetweenFiveAndNinetyFivePercent()
    {
        var spec = new PreviewClipSpec();
        var starts = spec.Starts(100);
        Assert.Equal(12, starts.Count);
        Assert.Equal(5, starts[0], 3); Assert.Equal(95 - 0.75, starts[^1], 3);
        Assert.Equal([0], spec.Starts(10));
        Assert.NotEqual(spec.Key, (spec with { Width = 480 }).Key);
        Assert.Throws<ArgumentOutOfRangeException>(() => (spec with { Width = 321 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (spec with { Segments = 0 }).Validate());
    }

    [Fact]
    public async Task ClipHasThePartsTheWidthAndNoSound()
    {
        var source = await Encode("quelle ü.mp4", ["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=60", "-f", "lavfi", "-i", "sine=duration=60"],
            ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest"]);
        var before = File.ReadAllBytes(source);
        var clip = Path.Combine(scratch, "clips", "clip.mp4");
        await service.CreatePreviewClipAsync(source, clip, 60, new PreviewClipSpec(8, 0.5, 240));
        var probe = await service.ProbeAsync(clip);
        Assert.Equal("h264", probe.VideoCodec); Assert.Null(probe.AudioCodec);
        Assert.Equal(240, probe.Width); Assert.Equal(136, probe.Height);
        Assert.InRange(probe.DurationSeconds!.Value, 3.8, 4.3);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(clip)!));
    }

    [Fact]
    public async Task MarkClipStartsAtItsPlace()
    {
        var source = await Encode("marke.mp4", ["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=30"], ["-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        var clip = Path.Combine(scratch, "marke-clip.mp4");
        await service.CreateMarkClipAsync(source, clip, 12, 4, 320);
        var probe = await service.ProbeAsync(clip);
        Assert.Equal(320, probe.Width); Assert.InRange(probe.DurationSeconds!.Value, 3.8, 4.3);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CreateMarkClipAsync(source, clip, 12, 4, 333));
    }

    [Fact]
    public async Task ShortVideoBecomesTheWholeClip()
    {
        var source = await Encode("kurz.mp4", ["-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=3"], ["-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        var clip = Path.Combine(scratch, "kurz-clip.mp4");
        await service.CreatePreviewClipAsync(source, clip, 3, new PreviewClipSpec());
        Assert.InRange((await service.ProbeAsync(clip)).DurationSeconds!.Value, 2.8, 3.2);
    }

    private async Task<string> Encode(string name, string[] input, string[] output)
    {
        var path = Path.Combine(scratch, name);
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n" }.Concat(input).Concat(output).Append(path)) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await error); await stdout;
        return path;
    }

    public void Dispose()
    {
        var root = Path.GetFullPath(Path.GetTempPath()); var owned = Path.GetFullPath(scratch);
        if (owned.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(owned).StartsWith("flixypix-clips-", StringComparison.Ordinal)) Directory.Delete(owned, recursive: true);
    }
}
