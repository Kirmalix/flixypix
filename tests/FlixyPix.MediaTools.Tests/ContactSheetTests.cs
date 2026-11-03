using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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

    [Theory]
    [InlineData("jpg",640,8)]
    [InlineData("jpg",641,3)]
    [InlineData("png",641,3)]
    public async Task OddHeaderAndBordersRetainExactImageDimensionsAndCellPixels(string extension,int width,int border)
    {
        var source=await SecondsVideo();
        var result=await service.CreateContactSheetResultAsync(source,Path.Combine(scratch,"odd."+extension),30,64d/36,new(){Columns=2,Rows=2,Width=width,Border=border,FastSeek=false,Header=["Odd header"],FontFile=FontFiles.FindOrDefault("Segoe UI")});
        var start=new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(ffmpeg)!,"ffprobe.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-v","error","-show_entries","stream=width,height","-of","json",result.ImagePath})start.ArgumentList.Add(arg);
        using var probe=Process.Start(start)!;var output=probe.StandardOutput.ReadToEndAsync();var error=probe.StandardError.ReadToEndAsync();await probe.WaitForExitAsync();Assert.True(probe.ExitCode==0,await error);
        using var info=JsonDocument.Parse(await output);var stream=info.RootElement.GetProperty("streams")[0];Assert.Equal(result.Width,stream.GetProperty("width").GetInt32());Assert.Equal(result.Height,stream.GetProperty("height").GetInt32());
        foreach(var cell in result.Cells)
        {
            var bounds=cell.Bounds;var green=await CentreGreen(result.ImagePath,$"crop=8:8:{bounds.X+bounds.Width/2-4}:{bounds.Y+bounds.Height/2-4}");
            Assert.InRange(green,(int)Math.Floor(cell.Seconds)*8-10,(int)Math.Floor(cell.Seconds)*8+10);
        }
    }

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
        var fastResult = await service.CreateContactSheetResultAsync(source, Path.Combine(scratch, "fast.png"), 30, 64d / 36, new ContactSheetStyle { Width = 160, FastSeek = true });
        var exactResult = await service.CreateContactSheetResultAsync(source, Path.Combine(scratch, "exact.png"), 30, 64d / 36, new ContactSheetStyle { Width = 160, FastSeek = false });
        var fast=fastResult.ImagePath;var exact=exactResult.ImagePath;
        Assert.InRange(Assert.Single(fastResult.Cells).Seconds,9.999,10.001);Assert.InRange(Assert.Single(exactResult.Cells).Seconds,14.999,15.001);
        Assert.Equal(new SheetRectangle(0,0,160,90),fastResult.Cells[0].Bounds);Assert.Equal(fastResult.Cells[0],fastResult.HitTest(80,45));Assert.Null(fastResult.HitTest(160,45));Assert.Null(fastResult.HitTest(double.NaN,0));Assert.True(fastResult.SourceUnchanged());
        Assert.InRange(await CentreGreen(fast), 72, 88);
        Assert.InRange(await CentreGreen(exact), 112, 128);
    }

    [Fact]
    public async Task ResultRectanglesExcludeHeaderAndGapsAndSourceChangesInvalidate()
    {
        var source=await SecondsVideo();var style=new ContactSheetStyle{Columns=3,Rows=2,Width=640,Border=4,FastSeek=false,FontFile=FontFiles.FindOrDefault("Segoe UI"),Header=["Synthetic header","Second line"]};
        var result=await service.CreateContactSheetResultAsync(source,Path.Combine(scratch,"result.png"),30,64d/36,style);
        Assert.Equal(6,result.Cells.Count);Assert.Equal(640,result.Width);Assert.Equal(640,(await service.ProbeAsync(result.ImagePath)).Width);
        foreach(var cell in result.Cells)
        {
            Assert.Equal(cell,result.HitTest(cell.Bounds.X+cell.Bounds.Width/2d,cell.Bounds.Y+cell.Bounds.Height/2d));
            Assert.Null(result.HitTest(cell.Bounds.X-1,cell.Bounds.Y));Assert.InRange(cell.Seconds,0,30);
        }
        Assert.Null(result.HitTest(50,0));File.SetLastWriteTimeUtc(source,File.GetLastWriteTimeUtc(source).AddSeconds(2));Assert.False(result.SourceUnchanged());
    }
    [Fact]
    public async Task NonzeroStreamStartHasMediaRelativeFrameTimeMatchingPixels()
    {
        var source=await SecondsVideo();var shifted=Path.Combine(scratch,"shifted.mp4");await Run(ffmpeg,"-hide_banner","-loglevel","error","-i",source,"-c","copy","-output_ts_offset","5",shifted);
        var result=await service.CreateContactSheetResultAsync(shifted,Path.Combine(scratch,"shifted.png"),30,64d/36,new(){Width=160,FastSeek=false});
        Assert.InRange(Assert.Single(result.Cells).Seconds,14.999,15.001);Assert.InRange(await CentreGreen(result.ImagePath),112,128);
    }
    [Fact]
    public async Task VariableRateCellTimesAreDecodedFrameTimesRatherThanSeekTargets()
    {
        var source=Path.Combine(scratch,"variable.mp4");await Run(ffmpeg,"-hide_banner","-loglevel","error","-f","lavfi","-i","nullsrc=s=64x36:r=30:d=6,geq=r=0:g='T*40':b=0,format=yuv420p","-vf","select='if(lt(t,3),not(mod(n,3)),1)'","-fps_mode","vfr","-c:v","libx264","-preset","ultrafast",source);
        var info=new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(ffmpeg)!,"ffprobe.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-v","error","-select_streams","v:0","-show_frames","-show_entries","frame=best_effort_timestamp_time","-of","json",source})info.ArgumentList.Add(arg);
        using var probe=Process.Start(info)!;var output=probe.StandardOutput.ReadToEndAsync();var errors=probe.StandardError.ReadToEndAsync();await probe.WaitForExitAsync();Assert.True(probe.ExitCode==0,await errors);
        using var document=JsonDocument.Parse(await output);var available=document.RootElement.GetProperty("frames").EnumerateArray().Select(f=>double.Parse(f.GetProperty("best_effort_timestamp_time").GetString()!,CultureInfo.InvariantCulture)).ToArray();
        var result=await service.CreateContactSheetResultAsync(source,Path.Combine(scratch,"vfr.png"),6,64d/36,new(){Columns=3,Width=480,SkipPercent=7,FastSeek=false});var targets=MediaToolService.SheetTimes(6,3,7);
        Assert.Contains(result.Cells.Zip(targets),pair=>Math.Abs(pair.First.Seconds-pair.Second)>.01);
        foreach(var cell in result.Cells)
        {
            Assert.InRange(Math.Abs(cell.Seconds-available.First(time=>time>=targets[cell.Index]-.001)),0,.001);
            var green=await CentreGreen(result.ImagePath,$"crop=8:8:{cell.Bounds.X+cell.Bounds.Width/2-4}:{cell.Bounds.Y+cell.Bounds.Height/2-4}");
            Assert.InRange(Math.Abs(green-cell.Seconds*40),0,10);
        }
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
