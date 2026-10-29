using System.Diagnostics;
using FlixyPix.MediaTools;
using Xunit;

namespace FlixyPix.MediaTools.Tests;

public sealed class MediaToolsTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "flixypix-media-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string ffmpeg;
    private readonly MediaToolService service;

    public MediaToolsTests()
    {
        Directory.CreateDirectory(scratch);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Cannot locate preservation project.");
        var tools = Path.Combine(root.FullName, ".tools", "media");
        ffmpeg = Directory.GetFiles(Path.Combine(tools, "ffmpeg"), "ffmpeg.exe", SearchOption.AllDirectories).Single();
        var ffprobe = Directory.GetFiles(Path.Combine(tools, "ffmpeg"), "ffprobe.exe", SearchOption.AllDirectories).Single();
        var mpv = Path.Combine(tools, "mpv", "mpv.exe");
        service = new(new(ffmpeg, ffprobe, mpv, HeadlessPlayback: true));
    }

    [Fact]
    public void ProbeParserHandlesUnknownDurationAndAudioOnly()
    {
        var parsed = MediaToolService.ParseProbe("""{"format":{"duration":"N/A"},"streams":[{"codec_type":"audio","codec_name":"flac","duration":"1.25"}]}""");
        Assert.Equal(1.25, parsed.DurationSeconds); Assert.Equal("flac", parsed.AudioCodec); Assert.Null(parsed.Width); Assert.Null(parsed.VideoCodec);
        Assert.Null(MediaToolService.ParseProbe("""{"format":{"duration":"NaN"},"streams":[]}""").DurationSeconds);
    }

    [Fact]
    public void ProbeParserReadsTechnicalData()
    {
        var parsed = MediaToolService.ParseProbe("""
            {"format":{"duration":"10.0","bit_rate":"25000000","format_name":"matroska,webm"},
             "streams":[{"codec_type":"video","codec_name":"hevc","width":3840,"height":2160,"avg_frame_rate":"60000/1001","color_transfer":"smpte2084"},
                        {"codec_type":"audio","codec_name":"eac3"}]}
            """);
        var technical = Assert.IsType<FlixyPix.Core.TechnicalInfo>(parsed.Technical);
        Assert.Equal("hevc", technical.VideoCodec); Assert.Equal("eac3", technical.AudioCodec); Assert.Equal(59.94, technical.FrameRate);
        Assert.Equal(25_000_000, technical.BitRate); Assert.True(technical.Hdr); Assert.Equal("matroska,webm", technical.Container);
        var plain = MediaToolService.ParseProbe("""{"format":{},"streams":[{"codec_type":"video","codec_name":"h264","avg_frame_rate":"0/0","r_frame_rate":"25/1","bit_rate":"900000","color_transfer":"bt709"}]}""");
        Assert.Equal(25, plain.Technical?.FrameRate); Assert.Equal(900_000, plain.Technical?.BitRate); Assert.False(plain.Technical?.Hdr);
        Assert.Null(MediaToolService.ParseProbe("""{"format":{},"streams":[]}""").Technical);
    }

    [Fact]
    public async Task RealMediaPipelineHandlesUnicodeAndNeverOverwritesInput()
    {
        var source = await MakeFixture(); var before = File.ReadAllBytes(source);
        var probe = await service.ProbeAsync(source);
        Assert.Equal(160, probe.Width); Assert.Equal(90, probe.Height); Assert.InRange(probe.DurationSeconds!.Value, 1.9, 2.1);
        var thumbnail = Path.Combine(scratch, "Vorschau ü.png");
        Assert.Equal(thumbnail, await service.CreateThumbnailAsync(source, thumbnail, 0.5));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, File.ReadAllBytes(thumbnail)[..8]);
        var sheet = Path.Combine(scratch, "raster.png");
        await service.CreateContactSheetAsync(source, sheet, 2, columns: 3, rows: 2, width: 480, aspectRatio: 160d / 90);
        var sheetProbe = await service.ProbeAsync(sheet);
        Assert.Equal((480, 2 * 90), (sheetProbe.Width, sheetProbe.Height));
        var clip = Path.Combine(scratch, "clip.mp4"); await service.CreateClipAsync(source, clip, 0.4, 0.8);
        var clipProbe = await service.ProbeAsync(clip); Assert.InRange(clipProbe.DurationSeconds!.Value, 0.7, 1.0);
        var converted = Path.Combine(scratch, "converted.mp4"); await service.ConvertAsync(source, converted);
        Assert.Equal("h264", (await service.ProbeAsync(converted)).VideoCodec);
        var gif = Path.Combine(scratch, "animated.gif"); await service.CreateGifAsync(source, gif, 0, 0.7);
        Assert.StartsWith("GIF8", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(gif), 0, 6));
        await Assert.ThrowsAsync<IOException>(() => service.ConvertAsync(source, converted));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ConvertAsync(source, source));
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task CancellationDoesNotPublishPartialOutput()
    {
        var source = await MakeFixture(); var output = Path.Combine(scratch, "cancelled.mp4");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ConvertAsync(source, output, cancellation.Token));
        Assert.False(File.Exists(output)); Assert.Empty(Directory.GetFiles(scratch, ".cancelled.*"));
    }

    [Fact]
    public async Task MpvNamedPipeSupportsPauseSeekAndRealEndDetection()
    {
        var source = await MakeFixture();
        await using var player = await service.PlayAsync(source);
        await player.PauseAsync(true); await Task.Delay(200);
        var first = await player.GetPositionAsync(); await Task.Delay(250); var second = await player.GetPositionAsync();
        Assert.NotNull(first); Assert.NotNull(second); Assert.InRange(Math.Abs(second.Value - first.Value), 0, 0.15);
        await player.SeekAsync(1.5); await player.SetVolumeAsync(25); await player.SetSpeedAsync(1.5);
        Assert.Contains(await player.GetTracksAsync(), track => track.Type == "video");
        await player.SelectSubtitleTrackAsync(null); await player.SelectAudioTrackAsync(null);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => player.SetSpeedAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => player.SelectAudioTrackAsync(0));
        await player.PauseAsync(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await player.WaitForEndAsync(timeout.Token);
        Assert.True((await player.GetPositionAsync()) >= 1.5);
    }

    [Fact]
    public async Task MouseInputIsReportedInsteadOfMpvBindings()
    {
        var source = await MakeFixture();
        await using var player = await service.PlayAsync(source, reportMouse: true);
        await player.PauseAsync(true);
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        player.ClientMessage += args => { if (args is [MediaToolService.MouseMessage, var input]) received.Enqueue(input); };
        foreach (var key in new[] { "MBTN_LEFT", "WHEEL_UP", "MBTN_BACK" }) await player.PressAsync(key);
        for (var wait = 0; received.Count < 3 && wait < 40; wait++) { await player.GetPositionAsync(); await Task.Delay(50); }
        Assert.Equal(["MBTN_LEFT", "WHEEL_UP", "MBTN_BACK"], received.ToArray());
        // mpv's own left-click binding would have toggled pause; with the report it stays paused.
        var before = await player.GetPositionAsync(); await Task.Delay(300); var after = await player.GetPositionAsync();
        Assert.NotNull(before); Assert.InRange(Math.Abs(after!.Value - before.Value), 0, 0.05);
    }

    [Fact]
    public async Task SceneGenerationPublishesOnlyCompleteVerifiedResultsAndResumesIdempotently()
    {
        var source = await MakeFixture(); var before = File.ReadAllBytes(source);
        var request = new ScenePreviewRequest(Guid.NewGuid(),0.2,1.8,Count:4); var target = Path.Combine(scratch,"previews");
        var generated = await service.GenerateScenePreviewsAsync(request,target,source);
        Assert.Equal(new[] { 0.2,0.6,1.0,1.4 },generated.Frames.Select(frame => Math.Round(frame.Seconds,6)));
        Assert.Equal(4,MediaToolService.ReadScenePreviewManifest(target).Frames.Count);
        Assert.Equal(generated.SourceSha256,(await service.GenerateScenePreviewsAsync(request,target,source)).SourceSha256);
        await Assert.ThrowsAsync<IOException>(() => service.GenerateScenePreviewsAsync(request with { JobId = Guid.NewGuid() },target,source));
        File.WriteAllText(Path.Combine(target,generated.Frames[0].FileName),"corrupt");
        Assert.Throws<InvalidDataException>(() => MediaToolService.ReadScenePreviewManifest(target));
        var cancelled = Path.Combine(scratch,"cancelled-previews"); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateScenePreviewsAsync(request,cancelled,source,cancellationToken:cts.Token));
        Assert.False(Directory.Exists(cancelled)); Assert.Empty(Directory.GetDirectories(scratch,".scene-previews-*"));
        Assert.Equal(before,File.ReadAllBytes(source));
    }

    [Fact]
    public async Task QueuedSceneGenerationPersistsCompletionAndCatalogAcknowledgement()
    {
        var source = await MakeFixture(); var queuePath = Path.Combine(scratch,"scene-queue.json"); Guid id;
        using (var queue = new MediaJobQueue(queuePath,service))
        {
            var job = queue.Enqueue(new MediaJob { Kind = MediaJobKind.ScenePreviews, Input = source,
                Output = Path.Combine(scratch,"queue-previews"), EndSeconds = 1.8, PreviewIntervalSeconds = 0.6 }); id = job.Id;
            await queue.RunAsync(); var finished = Assert.Single(queue.Jobs); Assert.Equal(MediaJobState.Completed,finished.State);
            Assert.False(finished.ResultsApplied); queue.MarkResultsApplied(id);
        }
        using var restored = new MediaJobQueue(queuePath,service);
        Assert.True(Assert.Single(restored.Jobs).ResultsApplied);
        Assert.Equal(3,MediaToolService.ReadScenePreviewManifest(restored.Jobs[0].Output).Frames.Count);
    }

    [Fact]
    public void ScenePositionModesAreBoundedAndHalfOpen()
    {
        Assert.Equal(new[] { 1d,3d,5d },new ScenePreviewRequest(Guid.NewGuid(),1,6,IntervalSeconds:2).GetPositions());
        Assert.Equal(new[] { 1d },new ScenePreviewRequest(Guid.NewGuid(),1,6,Count:1).GetPositions());
        Assert.Throws<ArgumentException>(() => new ScenePreviewRequest(Guid.NewGuid(),0,1,Count:1,IntervalSeconds:1).Validate());
        Assert.Throws<ArgumentException>(() => new ScenePreviewRequest(Guid.NewGuid(),0,1,IntervalSeconds:0.0001).Validate());
        Assert.Throws<ArgumentException>(() => new ScenePreviewRequest(Guid.NewGuid(),0,double.NaN,Count:1).Validate());
    }

    [Fact]
    public async Task InterruptedSceneGenerationCleansPartialFramesAndRecoversPublishedResults()
    {
        var source = await MakeFixture(); var path = Path.Combine(scratch,"recovery-queue.json"); var output = Path.Combine(scratch,"recovered");
        var request = new ScenePreviewRequest(Guid.NewGuid(),0,1.8,Count:3);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateScenePreviewsAsync(request,output,source,
            new ImmediateProgress<int>(_ => cancel.Cancel()),cancel.Token));
        Assert.False(Directory.Exists(output)); Assert.Empty(Directory.GetDirectories(scratch,".scene-previews-*"));
        using (var queue = new MediaJobQueue(path,service))
        {
            queue.Enqueue(new() { Id = request.JobId, Kind = MediaJobKind.ScenePreviews, Input = source, Output = output, EndSeconds = 1.8, PreviewCount = 3 });
            await queue.RunAsync(); Assert.Equal(MediaJobState.Completed,Assert.Single(queue.Jobs).State);
        }
        // Emulate a crash after output publication but before the final queue-state flush.
        File.WriteAllText(path,File.ReadAllText(path).Replace("\"Completed\"","\"Running\"",StringComparison.Ordinal));
        using var recovered = new MediaJobQueue(path,service);
        Assert.Equal(MediaJobState.Pending,Assert.Single(recovered.Jobs).State);
        await recovered.RunAsync(); Assert.Equal(MediaJobState.Completed,Assert.Single(recovered.Jobs).State);
        Assert.Equal(3,MediaToolService.ReadScenePreviewManifest(output).Frames.Count);
    }

    private sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }

    [Fact]
    public async Task CropAndCombineProduceMeasuredResults()
    {
        var source = await MakeFixture(); var cropped = Path.Combine(scratch, "crop.mp4");
        await service.CropAsync(source, cropped, 10, 10, 100, 60);
        var dimensions = await service.ProbeAsync(cropped); Assert.Equal(100, dimensions.Width); Assert.Equal(60, dimensions.Height);
        var combined = Path.Combine(scratch, "combined.mp4");
        // Apostrophes and spaces in paths must remain ffconcat data, never commands.
        var quoted = Path.Combine(scratch, "clip's & ü.mp4"); File.Copy(source, quoted);
        await service.CombineAsync([source, quoted], combined);
        Assert.InRange((await service.ProbeAsync(combined)).DurationSeconds!.Value, 3.9, 4.2);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CombineAsync([source, cropped], Path.Combine(scratch, "mismatch.mp4")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CropAsync(source, Path.Combine(scratch, "invalid.mp4"), 140, 10, 100, 60));
        Assert.Empty(Directory.GetDirectories(scratch, ".flixypix-combine-*"));
    }

    [Fact]
    public async Task DurableQueueRunsRealJobsRetainsFailuresAndSurvivesReopen()
    {
        var source = await MakeFixture(); var file = Path.Combine(scratch, "jobs.json");
        using (var queue = new MediaJobQueue(file, service))
        {
            queue.Enqueue(new() { Kind = MediaJobKind.Clip, Input = source, Output = Path.Combine(scratch, "queued.mp4"), DurationSeconds = 0.6 });
            queue.Enqueue(new() { Kind = MediaJobKind.Crop, Input = source, Output = Path.Combine(scratch, "failed.mp4"), Width = 999, Height = 99 });
            Assert.Throws<IOException>(() => new MediaJobQueue(file, service));
            await queue.RunAsync();
            Assert.Equal(MediaJobState.Completed, queue.Jobs[0].State); Assert.Equal(MediaJobState.Failed, queue.Jobs[1].State);
            Assert.NotEmpty(queue.Jobs[1].Error!); Assert.False(File.Exists(Path.Combine(scratch, "failed.mp4")));
            queue.Retry(queue.Jobs[1].Id); Assert.Equal(MediaJobState.Pending, queue.Jobs[1].State);
        }
        using (var reopened = new MediaJobQueue(file, service))
        {
            Assert.Equal(2, reopened.Jobs.Count); Assert.Equal(MediaJobState.Completed, reopened.Jobs[0].State);
            reopened.Remove(reopened.Jobs[1].Id); Assert.Single(reopened.Jobs);
        }
    }

    [Theory]
    [InlineData(-1, 1)] [InlineData(0, 0)] [InlineData(double.NaN, 2)] [InlineData(0, double.PositiveInfinity)]
    public async Task InvalidClipTimesAreRejected(double start, double duration)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CreateClipAsync("not-needed", "out.mp4", start, duration));
    }

    private async Task<string> MakeFixture()
    {
        var path = Path.Combine(scratch, "Test $(ignored) & Grüße.mp4");
        if (File.Exists(path)) return path;
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=10", "-t", "2", "-pix_fmt", "yuv420p", "-c:v", "libx264", path }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await error); await stdout;
        return path;
    }

    public void Dispose()
    {
        var root = Path.GetFullPath(Path.GetTempPath()); var owned = Path.GetFullPath(scratch);
        if (owned.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(owned).StartsWith("flixypix-media-tests-", StringComparison.Ordinal)) Directory.Delete(owned, recursive: true);
    }
}
