using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlixyPix.Core;
using FlixyPix.MediaTools;
using Xunit;

namespace FlixyPix.MediaTools.Tests;

public sealed class LegacyJobTranslationTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "flixypix-legacy-jobs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void OfflineCombinePreviewKeepsOrderRangesAndDoesNotRequireInputs()
    {
        var draft = List(@"U:\Filme\Eins.mp4", @"U:\Filme\Zwei.mkv");
        var preview = LegacyJobTranslator.PreviewCombine(draft, Path.Combine(scratch, "combined.mp4"));
        Assert.True(preview.CanEnqueue, string.Join("; ", preview.BlockingIssues));
        Assert.Equal(MediaJobKind.CombineSegments, preview.Job!.Kind);
        Assert.Equal(draft.Entries.Select(entry => entry.InputPath), preview.Job.Segments.Select(segment => segment.Input));
        Assert.Equal(37, preview.Job.Segments[0].StartSeconds); Assert.Equal(42, preview.Job.Segments[0].EndSeconds);
        Assert.Equal(preview.Job.Id, LegacyJobTranslator.PreviewCombine(draft, preview.Job.Output).Job!.Id);
        Assert.Contains(preview.Warnings, warning => warning.Contains("30fps"));
    }

    [Fact]
    public void ProtectedOutputOpaqueOrUntranslatedOptionCannotBeEnqueued()
    {
        Assert.False(LegacyJobTranslator.PreviewCombine(List(@"U:\A.mp4", @"U:\B.mp4"), @"U:\out.mp4").CanEnqueue);
        Assert.False(LegacyJobTranslator.PreviewCombine(List(@"U:\A.mp4", @"U:\B.mp4") with { Status = HistoricalProjectionStatus.Opaque }, Path.Combine(scratch, "out.mp4")).CanEnqueue);
        // The preview projects the preserved bytes again, so only issues found in the original payload count.
        var draft = List(@"U:\A.mp4", @"U:\B.mp4"); draft.Entries[0].BlockingIssues.Add("edited projection");
        Assert.True(LegacyJobTranslator.PreviewCombine(draft, Path.Combine(scratch, "out.mp4")).CanEnqueue);
        Assert.False(LegacyJobTranslator.PreviewCombine(List(@"U:\A.mp4", @"U:\B.mp4", index => index == 1), Path.Combine(scratch, "out.mp4")).CanEnqueue);
    }

    [Fact]
    public void KnownSqlClipMapsTimesAndNeverRetainsOriginalCommandAsExecutableArguments()
    {
        var draft = Sql(@"U:\München.mp4", @"U:\old.mp4", "-ss 00:00:37 -i \"U:\\München.mp4\" -t 00:00:05 -c:v libx264 -crf 15 -preset medium -c:a copy \"U:\\old.mp4\"");
        var preview = LegacyJobTranslator.PreviewSql(draft, Path.Combine(scratch, "clip.mp4"));
        Assert.True(preview.CanEnqueue, string.Join("; ", preview.BlockingIssues)); Assert.Equal(37, preview.Job!.StartSeconds); Assert.Equal(5, preview.Job.DurationSeconds);
        Assert.Equal(MediaJobKind.Clip, preview.Job.Kind); Assert.Equal(draft.InputPath, preview.Job.Input);
        Assert.DoesNotContain("crf", JsonSerializer.Serialize(preview.Job));
    }

    [Theory]
    [InlineData("-i \"U:\\A.mp4\" -t 5 -vf crop=10:10:0:0 \"U:\\old.mp4\"")]
    [InlineData("-i \"U:\\A.mp4\" -t 5 \"U:\\old.mp4\" & calc.exe")]
    [InlineData("-i \"U:\\Other.mp4\" -t 5 \"U:\\old.mp4\"")]
    [InlineData("-i \"U:\\A.mp4\" -t 6 \"U:\\old.mp4\"")]
    [InlineData("-i \"U:\\A.mp4\" -t 5 -ss 0 -ss 1 \"U:\\old.mp4\"")]
    public void UnknownSqlSyntaxAndInconsistentIdentityStayInactive(string command)
    {
        var preview = LegacyJobTranslator.PreviewSql(Sql(@"U:\A.mp4", @"U:\old.mp4", command), Path.Combine(scratch, "clip.mp4"));
        Assert.False(preview.CanEnqueue); Assert.NotEmpty(preview.BlockingIssues);
    }

    [Fact]
    public void EnqueueRequiresFreshPreviewAndIsIdempotentWithoutStartingAnything()
    {
        Directory.CreateDirectory(scratch); var first = Path.Combine(scratch, "one.mp4"); var second = Path.Combine(scratch, "two.mp4");
        File.WriteAllText(first, "synthetic media identity; no decoder starts"); File.WriteAllText(second, "second");
        var draft = List(first, second); var proposal = LegacyJobTranslator.PreviewCombine(draft, Path.Combine(scratch, "combined.mp4"));
        using (var queue = new MediaJobQueue(Path.Combine(scratch, "queue.json"), new(new())))
        {
            Assert.Throws<InvalidOperationException>(() => LegacyJobTranslator.Enqueue(queue, proposal, "changed"));
            Assert.Throws<InvalidOperationException>(() => LegacyJobTranslator.Enqueue(queue, proposal with { Job = proposal.Job! with { Output = Path.Combine(scratch, "tampered.mp4") } }, proposal.SourceSha256));
            var once = LegacyJobTranslator.Enqueue(queue, proposal, proposal.SourceSha256);
            Assert.Equal(once.Id, LegacyJobTranslator.Enqueue(queue, proposal, proposal.SourceSha256).Id);
            Assert.Single(queue.Jobs); Assert.Equal(MediaJobState.Pending, queue.Jobs[0].State); Assert.False(File.Exists(proposal.Job!.Output));
        }
        using var reopened = new MediaJobQueue(Path.Combine(scratch, "queue.json"), new(new()));
        Assert.Equal(2, Assert.Single(reopened.Jobs).Segments.Count); Assert.Equal(MediaJobState.Pending, reopened.Jobs[0].State);
    }

    [Fact]
    public async Task RealNormalizedCombinationHandlesDifferentCodecsDimensionsAudioAndClipRanges()
    {
        Directory.CreateDirectory(scratch); var (service, ffmpeg) = Tools();
        var first = Path.Combine(scratch, "first.mp4"); var second = Path.Combine(scratch, "zweite ü.avi");
        await Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=12:duration=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", first]);
        await Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=192x108:rate=15:duration=2", "-c:v", "mpeg4", "-an", second]);
        var hashes = new[] { SHA256.HashData(File.ReadAllBytes(first)), SHA256.HashData(File.ReadAllBytes(second)) };
        var output = Path.Combine(scratch, "normalized.mp4");
        await service.CombineSegmentsAsync([new(first, 0.4, 1.2), new(second, 0, 1)], output);
        var probe = await service.ProbeAsync(output); Assert.Equal("h264", probe.VideoCodec); Assert.Equal("aac", probe.AudioCodec);
        Assert.Equal(160, probe.Width); Assert.Equal(90, probe.Height); Assert.InRange(probe.DurationSeconds!.Value, 1.7, 2.15);
        Assert.Equal(hashes[0], SHA256.HashData(File.ReadAllBytes(first))); Assert.Equal(hashes[1], SHA256.HashData(File.ReadAllBytes(second)));
        Assert.Empty(Directory.GetDirectories(scratch, ".flixypix-normalize-*"));
        await Assert.ThrowsAsync<IOException>(() => service.CombineSegmentsAsync([new(first), new(second)], output));
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); var cancelled = Path.Combine(scratch, "cancelled.mp4");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CombineSegmentsAsync([new(first), new(second)], cancelled, cancel.Token));
        Assert.False(File.Exists(cancelled)); Assert.Empty(Directory.GetDirectories(scratch, ".flixypix-normalize-*"));
    }

    [Fact]
    public void OriginalCrossfadeAndMuteBecomeJobOptionsOnlyWhenTheWholeListAgrees()
    {
        var preview = LegacyJobTranslator.PreviewCombine(List(@"U:\A.mp4", @"U:\B.mp4", _ => true), Path.Combine(scratch, "faded.mp4"));
        Assert.True(preview.CanEnqueue, string.Join("; ", preview.BlockingIssues));
        Assert.Equal(1, preview.Job!.CrossfadeSeconds); Assert.False(preview.Job.MuteAudio);
        Assert.Equal(0, LegacyJobTranslator.PreviewCombine(List(@"U:\A.mp4", @"U:\B.mp4"), Path.Combine(scratch, "plain.mp4")).Job!.CrossfadeSeconds);
        var mixed = LegacyJobTranslator.PreviewCombine(List(@"U:\A.mp4", @"U:\B.mp4", index => index == 0), Path.Combine(scratch, "mixed.mp4"));
        Assert.False(mixed.CanEnqueue); Assert.Contains(mixed.BlockingIssues, issue => issue.Contains("same for all entries"));
    }

    private static LegacyCombineList List(string first, string second, Func<int, bool>? crossfade = null)
    {
        const string type = "MyMediaViewer.frmVideoTools+myVideos";
        const string args = "[[System.Guid, mscorlib],[MyMediaViewer.frmVideoTools+myVideos, Absent.Original.Assembly]]";
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((byte)0); writer.Write(1); writer.Write(-1); writer.Write(1); writer.Write(0);
        writer.Write((byte)12); writer.Write(99); writer.Write("Absent.Original.Assembly, Version=4.8.4.0, Culture=neutral, PublicKeyToken=null");
        Class(1, "System.Collections.Generic.Dictionary`2" + args, ["KeyValuePairs"], [2], []);
        writer.Write((byte)7); writer.Write(2); writer.Write((byte)0); writer.Write(1); writer.Write(2);
        writer.Write((byte)3); writer.Write("System.Collections.Generic.KeyValuePair`2" + args);
        var id = 10;
        foreach (var path in new[] { first, second })
        {
            var index = path == first ? 0 : 1;
            Class(id++, "System.Collections.Generic.KeyValuePair`2" + args, ["key", "value"], [2, 2], []);
            Class(id++, "System.Guid", ["_a", "_b", "_c", "_d", "_e", "_f", "_g", "_h", "_i", "_j", "_k"], new byte[11], [8, 7, 7, 2, 2, 2, 2, 2, 2, 2, 2]);
            writer.Write(new Guid(index == 0 ? "11111111-1111-1111-1111-111111111111" : "22222222-2222-2222-2222-222222222222").ToByteArray());
            if (crossfade is null) Class(id++, type, ["_id", "_starttime", "_endtime", "_isClip", "_video"], [0, 0, 0, 0, 2], [8, 8, 8, 1], true);
            else Class(id++, type, ["_id", "_starttime", "_endtime", "_isClip", "_video", "_crossfade"], [0, 0, 0, 0, 2, 0], [8, 8, 8, 1, 1], true);
            writer.Write(index + 1); writer.Write(index == 0 ? 37 : 0); writer.Write(index == 0 ? 42 : 15); writer.Write(index == 0);
            Class(id++, "MyMediaViewer.movies", ["_fullpath", "_filename"], [1, 1], [], true);
            String(id++, Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar); String(id++, Path.GetFileName(path));
            if (crossfade is not null) writer.Write(crossfade(index));
        }
        writer.Write((byte)11); writer.Flush();
        using var cipher = DES.Create(); cipher.Key = Convert.FromHexString("657A6670657A6670"); cipher.IV = cipher.Key;
        using var encrypt = cipher.CreateEncryptor(); var bytes = stream.ToArray();
        return LegacyCombineListReader.Read(encrypt.TransformFinalBlock(bytes, 0, bytes.Length), "fixture");

        void String(int record, string value) { writer.Write((byte)6); writer.Write(record); writer.Write(value); }
        void Class(int record, string name, string[] members, byte[] kinds, byte[] primitives, bool library = false)
        {
            writer.Write(library ? (byte)5 : (byte)4); writer.Write(record); writer.Write(name); writer.Write(members.Length);
            foreach (var member in members) writer.Write(member); writer.Write(kinds); writer.Write(primitives); if (library) writer.Write(99);
        }
    }
    private static LegacyJobDraft Sql(string input, string output, string command) => new()
    {
        SourceId = "fixture", LegacyId = "1", InputPath = input, OutputPath = output, OriginalCommand = command, DurationSeconds = 5, RawJson = "{}"
    };
    private static (MediaToolService Service, string Ffmpeg) Tools()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Cannot locate project toolchain.");
        var media = Path.Combine(root.FullName, ".tools", "media");
        var ffmpeg = Directory.GetFiles(Path.Combine(media, "ffmpeg"), "ffmpeg.exe", SearchOption.AllDirectories).Single();
        var ffprobe = Directory.GetFiles(Path.Combine(media, "ffmpeg"), "ffprobe.exe", SearchOption.AllDirectories).Single();
        return (new(new(ffmpeg, ffprobe)), ffmpeg);
    }
    private static async Task Run(string file, string[] arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!; var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await error); await output;
    }
    public void Dispose() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
}
