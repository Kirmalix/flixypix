using System.Diagnostics;
using FlixyPix.Core;
using FlixyPix.MediaTools;
using Xunit;

namespace FlixyPix.MediaTools.Tests;

/// <summary>Contact sheets: keyframe seek takes the real keyframe, border, header and timestamps render in one FFmpeg run,
/// and file names with quote, colon, percent or brackets cannot break the filter graph.</summary>
public sealed class ContactSheetTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "flixypix-sheet-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string ffmpeg;
    private readonly MediaToolService service;

    public ContactSheetTests()
    {
        Directory.CreateDirectory(scratch);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        var tools = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Cannot locate preservation project."), ".tools", "media", "ffmpeg");
        ffmpeg = Directory.GetFiles(tools, "ffmpeg.exe", SearchOption.AllDirectories).Single();
        service = new(new(ffmpeg, Directory.GetFiles(tools, "ffprobe.exe", SearchOption.AllDirectories).Single()));
    }

    public void Dispose() { try { Directory.Delete(scratch, true); } catch (IOException) { } }

    /// <summary>30 s whose green channel shows the second (8 per second), with a keyframe only every 10 s.</summary>
    private async Task<string> SecondsVideo()
    {
        var path = Path.Combine(scratch, "sekunden.mp4");
        await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "nullsrc=s=64x36:r=25:d=30,geq=r=0:g='floor(T)*8':b=0,format=yuv420p",
            "-c:v", "libx264", "-g", "250", "-keyint_min", "250", "-sc_threshold", "0", "-x264-params", "scenecut=0", path);
        return path;
    }

    private async Task<int> CentreGreen(string image, string crop = "crop=8:8:(iw-8)/2:(ih-8)/2")
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-i", image, "-vf", crop + ",scale=1:1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var bytes = new MemoryStream(); await process.StandardOutput.BaseStream.CopyToAsync(bytes); await process.WaitForExitAsync();
        return bytes.ToArray()[1];
    }

    private static async Task Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; var error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public async Task FastSeekTakesTheKeyframeBeforeTheSceneAndExactSeekTheSceneItself()
    {
        var source = await SecondsVideo();
        // One scene at 15 s; the keyframe before it is at 10 s.
        var fast = await service.CreateContactSheetAsync(source, Path.Combine(scratch, "fast.png"), 30, 64d / 36, new ContactSheetStyle { Width = 160, FastSeek = true });
        var exact = await service.CreateContactSheetAsync(source, Path.Combine(scratch, "exact.png"), 30, 64d / 36, new ContactSheetStyle { Width = 160, FastSeek = false });
        Assert.InRange(await CentreGreen(fast), 72, 88);
        Assert.InRange(await CentreGreen(exact), 112, 128);
    }

    [Fact]
    public async Task ScenesCloserThanKeyframesUseTheExactSecond()
    {
        var source = await SecondsVideo();
        // 2 × 2 over 30 s puts scenes 6 s apart; with keyframes every 10 s they would repeat, so the exact seek is used.
        var sheet = await service.CreateContactSheetAsync(source, Path.Combine(scratch, "dense.png"), 30, 64d / 36, new ContactSheetStyle { Columns = 2, Rows = 2, Width = 160, FastSeek = true });
        Assert.InRange(await CentreGreen(sheet, "crop=8:8:36:18"), 40, 56);
    }

    [Fact]
    public async Task BorderHeaderAndTimestampsRenderAndTrickyNamesStayText()
    {
        var source = await SecondsVideo();
        var font = FontFiles.FindOrDefault("Segoe UI");
        Assert.NotNull(font);
        var plain = new ContactSheetStyle { Columns = 3, Rows = 2, Width = 640, Border = 4, Background = "#203040" };
        var styled = plain with
        {
            FontFile = font, Timestamp = SheetTimestamp.BottomRight,
            Header = ["it's 50%: a\\b [x],y;z 'quoted'", "64 × 36 · 0:30 · 12 KB"],
        };
        var plainSheet = await service.ProbeAsync(await service.CreateContactSheetAsync(source, Path.Combine(scratch, "plain.png"), 30, 64d / 36, plain));
        var styledSheet = await service.ProbeAsync(await service.CreateContactSheetAsync(source, Path.Combine(scratch, "styled.png"), 30, 64d / 36, styled));
        // Six cells of 208 × 116 with 4 px around and between them fill the 640 px exactly.
        Assert.Equal((4 + 3 * (208 + 4), 4 + 2 * (116 + 4)), (plainSheet.Width, plainSheet.Height));
        Assert.Equal(plainSheet.Width, styledSheet.Width);
        Assert.True(styledSheet.Height > plainSheet.Height + 30, "The header did not add its lines above the scenes.");
    }

    [Fact]
    public async Task TextNeedsAFontAndStyleIsChecked()
    {
        var source = await SecondsVideo();
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateContactSheetAsync(source, Path.Combine(scratch, "a.png"), 30, 1, new ContactSheetStyle { Timestamp = SheetTimestamp.TopLeft, FontFile = Path.Combine(scratch, "missing.ttf") }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateContactSheetAsync(source, Path.Combine(scratch, "b.png"), 30, 1, new ContactSheetStyle { Background = "red" }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CreateContactSheetAsync(source, Path.Combine(scratch, "c.png"), 30, 1, new ContactSheetStyle { Border = 21 }));
        Assert.False(File.Exists(Path.Combine(scratch, "a.png")));
    }

    [Fact]
    public void ScenesSpreadEvenlyWithoutTheSkippedStartAndEnd()
    {
        Assert.Equal([25d, 50, 75], MediaToolService.SheetTimes(100, 3, 0));
        Assert.Equal([30d, 50, 70], MediaToolService.SheetTimes(100, 3, 10));
        Assert.Null(FontFiles.Find("Keine Schrift mit diesem Namen"));
    }
}
