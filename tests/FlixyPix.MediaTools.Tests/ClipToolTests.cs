using System.Diagnostics;
using FlixyPix.MediaTools;
using Xunit;

namespace FlixyPix.MediaTools.Tests;

public sealed class ClipToolTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "flixypix-clip-tools-" + Guid.NewGuid().ToString("N"));
    private readonly string ffmpeg;
    private readonly MediaToolService service;

    public ClipToolTests()
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
    public async Task AudioIsExtractedAsMp3OrM4aWithRangeAndNeverFromSilentVideos()
    {
        var source = await Video("mit Ton ü.mp4", 3, audio: true); var before = File.ReadAllBytes(source);
        var mp3 = Path.Combine(scratch, "ton.mp3"); await service.ExtractAudioAsync(source, mp3, 0.5, 1.5);
        var probe = await service.ProbeAsync(mp3);
        Assert.Equal("mp3", probe.AudioCodec); Assert.Null(probe.VideoCodec); Assert.InRange(probe.DurationSeconds!.Value, 1.3, 1.7);
        var m4a = Path.Combine(scratch, "ton.m4a"); await service.ExtractAudioAsync(source, m4a);
        var whole = await service.ProbeAsync(m4a);
        Assert.Equal("aac", whole.AudioCodec); Assert.Null(whole.VideoCodec); Assert.InRange(whole.DurationSeconds!.Value, 2.8, 3.2);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExtractAudioAsync(source, Path.Combine(scratch, "ton.wav")));
        var silent = await Video("stumm.mp4", 2, audio: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExtractAudioAsync(silent, Path.Combine(scratch, "nichts.mp3")));
        Assert.False(File.Exists(Path.Combine(scratch, "nichts.mp3"))); Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task CrossfadeOverlapsNeighboursAndMuteDropsTheAudioTrack()
    {
        var first = await Video("eins.mp4", 2, audio: true); var second = await Video("zwei.mp4", 2, audio: true, size: "192x108");
        var third = await Video("drei.mp4", 2, audio: false);
        var faded = Path.Combine(scratch, "faded.mp4");
        await service.CombineSegmentsAsync([new(first), new(second), new(third)], faded, options: new(CrossfadeSeconds: 0.5));
        var probe = await service.ProbeAsync(faded);
        // Three 2 s parts minus two 0.5 s overlaps.
        Assert.Equal("aac", probe.AudioCodec); Assert.Equal(160, probe.Width); Assert.InRange(probe.DurationSeconds!.Value, 4.8, 5.2);
        var muted = Path.Combine(scratch, "muted.mp4");
        await service.CombineSegmentsAsync([new(first, 0, 1.5), new(second)], muted, options: new(MuteAudio: true, CrossfadeSeconds: 0.5));
        var mutedProbe = await service.ProbeAsync(muted);
        Assert.Null(mutedProbe.AudioCodec); Assert.InRange(mutedProbe.DurationSeconds!.Value, 2.8, 3.2);
        var copied = Path.Combine(scratch, "copied.mp4"); await service.CombineAsync([first, first], copied, muteAudio: true);
        Assert.Null((await service.ProbeAsync(copied)).AudioCodec);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CombineSegmentsAsync([new(first, 0, 0.8), new(second)], Path.Combine(scratch, "short.mp4"), options: new(CrossfadeSeconds: 0.5)));
        Assert.False(File.Exists(Path.Combine(scratch, "short.mp4"))); Assert.Empty(Directory.GetDirectories(scratch, ".flixypix-normalize-*"));
    }

    [Fact]
    public async Task FramesOfOneVideoComeFromFewRunsAndMissingOnesAreReported()
    {
        var source = await Video("marken.mp4", 3, audio: false);
        // More frames than one run takes, plus one behind the end that has no picture.
        var frames = Enumerable.Range(0, MediaToolService.FramesPerRun + 4).Select(index => new FrameRequest(index * 0.1, Path.Combine(scratch, "marks", $"mark-{index}.jpg")))
            .Append(new FrameRequest(30, Path.Combine(scratch, "marks", "behind.jpg"))).ToList();
        var written = await service.CreateThumbnailsAsync(source, frames);
        // A run without such a frame delivers all of its frames. The frame without a picture ends its run with an error, and
        // depending on timing that can come before other frames of that run are written; the caller retries those singly.
        Assert.Equal(frames.Take(MediaToolService.FramesPerRun), written.Take(MediaToolService.FramesPerRun));
        Assert.Subset(frames.SkipLast(1).ToHashSet(), written.ToHashSet()); Assert.DoesNotContain(frames[^1], written);
        Assert.All(written, frame => Assert.Equal(new byte[] { 0xFF, 0xD8 }, File.ReadAllBytes(frame.Output)[..2]));
        Assert.False(File.Exists(frames[^1].Output)); Assert.Empty(Directory.GetFiles(Path.Combine(scratch, "marks"), ".*"));
        // Same size as a single thumbnail: fitted into 480 × 480.
        Assert.Equal((480, 270), ((await service.ProbeAsync(written[0].Output)).Width, (await service.ProbeAsync(written[0].Output)).Height));
        await Assert.ThrowsAsync<IOException>(() => service.CreateThumbnailsAsync(source, [frames[0]]));
        var twice = Path.Combine(scratch, "twice.jpg");
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateThumbnailsAsync(source, [new(0, twice), new(1, twice)]));
    }

    [Fact]
    public async Task MutedClipHasNoAudio()
    {
        var source = await Video("clip.mp4", 2, audio: true); var clip = Path.Combine(scratch, "clip-stumm.mp4");
        await service.CreateClipAsync(source, clip, 0.2, 1, muteAudio: true);
        var probe = await service.ProbeAsync(clip); Assert.Null(probe.AudioCodec); Assert.Equal("h264", probe.VideoCodec);
    }

    [Fact]
    public void CrossfadeFilterChainsOffsetsAndSkipsAudioWhenMuted()
    {
        var filter = MediaToolService.CrossfadeFilter([3, 4, 5], new(CrossfadeSeconds: 1));
        Assert.Equal("[0:v][1:v]xfade=transition=fade:duration=1:offset=2[v1];[0:a][1:a]acrossfade=d=1[a1];" +
            "[v1][2:v]xfade=transition=fade:duration=1:offset=5[v];[a1][2:a]acrossfade=d=1[a]", filter);
        Assert.DoesNotContain("acrossfade", MediaToolService.CrossfadeFilter([3, 4], new(MuteAudio: true, CrossfadeSeconds: 0.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CombineOptions(CrossfadeSeconds: 6).Validate(2));
        Assert.Throws<ArgumentException>(() => new CombineOptions(CrossfadeSeconds: 1).Validate(26));
        new CombineOptions(MuteAudio: true).Validate(128);
    }

    [Fact]
    public void RandomPlanIsReproducibleStaysInsideEachVideoAndKeepsShortVideosWhole()
    {
        (string, double)[] videos = [("a", 600), ("b", 30.5), ("c", 4)];
        var plan = ClipPlanner.Random(videos, 10, seed: 42);
        Assert.Equal(plan, ClipPlanner.Random(videos, 10, seed: 42));
        Assert.All(plan.Take(2).Zip(videos), pair =>
        {
            Assert.Equal(Math.Floor(pair.First.StartSeconds), pair.First.StartSeconds);
            Assert.InRange(pair.First.EndSeconds!.Value, 10, pair.Second.Item2); Assert.Equal(10, pair.First.EndSeconds!.Value - pair.First.StartSeconds);
        });
        Assert.Equal(new CombineSegment("c"), plan[2]);
        Assert.Contains(Enumerable.Range(1, 20), seed => !ClipPlanner.Random(videos, 10, seed).SequenceEqual(plan));
        var fixedPlan = ClipPlanner.Fixed(videos, 25, 10);
        Assert.Equal(new CombineSegment("a", 25, 35), fixedPlan[0]); Assert.Equal(new CombineSegment("b", 20.5, 30.5), fixedPlan[1]); Assert.Equal(new CombineSegment("c"), fixedPlan[2]);
        Assert.Throws<ArgumentException>(() => ClipPlanner.Random([("x", double.NaN)], 5, 1));
    }

    [Fact]
    public void QueueRejectsWrongAudioFormatCrossfadeOutsideSegmentsAndTooManyFadedParts()
    {
        var input = Path.Combine(scratch, "in.mp4"); File.WriteAllText(input, "identity only");
        using var queue = new MediaJobQueue(Path.Combine(scratch, "queue.json"), service);
        Assert.Throws<ArgumentException>(() => queue.Enqueue(new() { Kind = MediaJobKind.ExtractAudio, Input = input, Output = Path.Combine(scratch, "a.wav") }));
        Assert.Throws<ArgumentException>(() => queue.Enqueue(new() { Kind = MediaJobKind.Combine, Inputs = [input, input], Output = Path.Combine(scratch, "c.mp4"), CrossfadeSeconds = 1 }));
        Assert.Throws<ArgumentException>(() => queue.Enqueue(new()
        {
            Kind = MediaJobKind.CombineSegments, Segments = Enumerable.Repeat(new CombineSegment(input), 26).ToList(), Output = Path.Combine(scratch, "s.mp4"), CrossfadeSeconds = 1
        }));
        var audio = queue.Enqueue(new() { Kind = MediaJobKind.ExtractAudio, Input = input, Output = Path.Combine(scratch, "a.mp3"), StartSeconds = 2 });
        var muted = queue.Enqueue(new() { Kind = MediaJobKind.Combine, Inputs = [input, input], Output = Path.Combine(scratch, "m.mp4"), MuteAudio = true });
        Assert.Equal(MediaJobKind.ExtractAudio, queue.Jobs[0].Kind); Assert.True(queue.Jobs.Single(job => job.Id == muted.Id).MuteAudio);
        Assert.Equal(2, queue.Jobs.Single(job => job.Id == audio.Id).StartSeconds);
    }

    private async Task<string> Video(string name, double seconds, bool audio, string size = "160x90")
    {
        var path = Path.Combine(scratch, name);
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=15:duration={seconds}" };
        if (audio) arguments.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}", "-c:a", "aac"]);
        arguments.AddRange(["-c:v", "libx264", "-pix_fmt", "yuv420p", "-shortest", path]);
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await error); await stdout;
        return path;
    }

    public void Dispose()
    {
        var root = Path.GetFullPath(Path.GetTempPath()); var owned = Path.GetFullPath(scratch);
        if (owned.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(owned).StartsWith("flixypix-clip-tools-", StringComparison.Ordinal)) Directory.Delete(owned, recursive: true);
    }
}
