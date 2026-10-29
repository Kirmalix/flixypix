using System.Diagnostics;
using FlixyPix.Core;
using FlixyPix.MediaTools;
using Xunit;

namespace FlixyPix.MediaTools.Tests;

/// <summary>Video fingerprints with real FFmpeg: a re-encoded, scaled copy looks like the source, another video does not.</summary>
public sealed class VideoFingerprintTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "flixypix-fingerprints-" + Guid.NewGuid().ToString("N"));
    private readonly string ffmpeg;
    private readonly MediaToolService service;

    public VideoFingerprintTests()
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
    public async Task ReencodedCopyMatchesAndOtherVideoDoesNot()
    {
        var source = await Encode("quelle ü.mp4", ["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=20"], ["-c:v", "libx264", "-crf", "18", "-pix_fmt", "yuv420p"]);
        var copy = await Encode("kopie.mkv", ["-i", source], ["-vf", "scale=320:180", "-c:v", "mpeg4", "-q:v", "12"]);
        var other = await Encode("anderes.mp4", ["-f", "lavfi", "-i", "mandelbrot=size=640x360:rate=25", "-t", "20"], ["-c:v", "libx264", "-crf", "30", "-pix_fmt", "yuv420p"]);
        var positions = PerceptualHash.VideoPositions(20);
        var before = File.ReadAllBytes(source);
        var a = await service.VideoHashesAsync(source, positions);
        var b = await service.VideoHashesAsync(copy, positions);
        var c = await service.VideoHashesAsync(other, positions);
        Assert.Equal(PerceptualHash.VideoFrames, a.Count); Assert.Equal(PerceptualHash.VideoFrames, b.Count);
        Assert.InRange(PerceptualHash.Distance(a, b)!.Value, 0, PerceptualHash.Threshold(SimilarityLevel.Exact));
        Assert.True(PerceptualHash.Distance(a, c) > PerceptualHash.Threshold(SimilarityLevel.Loose), $"Different videos are {PerceptualHash.Distance(a, c)} bits apart.");
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task PositionsBeyondTheEndAreLeftOut()
    {
        var source = await Encode("kurz.mp4", ["-f", "lavfi", "-i", "testsrc2=size=160x90:rate=10:duration=2"], ["-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        var hashes = await service.VideoHashesAsync(source, [0.5, 1.5, 30]);
        Assert.Equal(2, hashes.Count);
    }

    [Fact]
    public async Task TimeOutIsAFileErrorNotACancellation()
    {
        var source = await Encode("langsam.mp4", ["-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=4"], ["-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        var impatient = new MediaToolService(new(ffmpeg, null, null, HeadlessPlayback: true)) { PreviewTimeout = TimeSpan.FromMilliseconds(1) };
        // A batch skips files with an IOException; a cancellation would stop the whole batch.
        var error = await Assert.ThrowsAsync<MediaToolService.MediaToolTimeoutException>(() => impatient.VideoHashesAsync(source, PerceptualHash.VideoPositions(4)));
        Assert.IsAssignableFrom<IOException>(error);
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
        if (owned.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(owned).StartsWith("flixypix-fingerprints-", StringComparison.Ordinal)) Directory.Delete(owned, recursive: true);
    }
}
