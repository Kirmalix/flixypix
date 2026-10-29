using System.Diagnostics;
using FlixyPix.Core;

namespace FlixyPix.MediaTools;

public sealed partial class MediaToolService
{
    /// <summary>Longest one FFmpeg run for fingerprint stills or a preview clip may take; past it the file counts as failed.
    /// Seeking in a large file without an index (old FLV) can read the whole file for every still.</summary>
    public TimeSpan PreviewTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The perceptual hashes of stills at the given places, in one FFmpeg run: every still has its own fast input
    /// seek, all are scaled to 32 × 32 grey and written as raw pixels. When that run fails (a broken spot), each still is
    /// tried alone and those without a picture are left out, so the result can be shorter than the request.</summary>
    public async Task<IReadOnlyList<ulong>> VideoHashesAsync(string input, IReadOnlyList<double> seconds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seconds);
        if (seconds.Count is 0 or > 32) throw new ArgumentOutOfRangeException(nameof(seconds), "1 to 32 stills per run.");
        foreach (var second in seconds) ValidateTime(second, nameof(seconds));
        var source = Input(input);
        try { return Hashes(await GrayFramesAsync(source, seconds, cancellationToken)); }
        catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested && seconds.Count > 1)
        {
            var hashes = new List<ulong>();
            foreach (var second in seconds)
            {
                try { hashes.AddRange(Hashes(await GrayFramesAsync(source, [second], cancellationToken))); }
                catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested) { }
            }
            return hashes;
        }
    }

    private static List<ulong> Hashes(byte[] pixels)
    {
        const int frame = PerceptualHash.Size * PerceptualHash.Size;
        var hashes = new List<ulong>();
        for (var offset = 0; offset + frame <= pixels.Length; offset += frame) hashes.Add(PerceptualHash.FromGray(pixels.AsSpan(offset, frame)));
        return hashes;
    }

    private async Task<byte[]> GrayFramesAsync(string source, IReadOnlyList<double> seconds, CancellationToken cancellationToken)
    {
        var size = PerceptualHash.Size;
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        foreach (var second in seconds) arguments.AddRange(["-ss", F(second), "-i", source]);
        var filters = seconds.Select((_, index) => $"[{index}:v:0]trim=end_frame=1,setpts=PTS-STARTPTS,scale={size}:{size}:flags=area,format=gray,setsar=1[f{index}]");
        var graph = string.Join(';', filters) + ";" + string.Concat(seconds.Select((_, index) => $"[f{index}]")) + $"concat=n={seconds.Count}:v=1:a=0[out]";
        // Without passthrough the raw muxer keeps a constant frame rate and drops stills that follow each other closely.
        arguments.AddRange(["-filter_complex", graph, "-map", "[out]", "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "gray", "-"]);
        var info = StartInfo(Resolve(paths.Ffmpeg, "ffmpeg"), arguments); info.RedirectStandardOutput = true; info.RedirectStandardError = true;
        using var process = new Process { StartInfo = info };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PreviewTimeout);
        using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            // The kill on time-out may end the process before the wait sees the token; both count as a time-out.
            try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new MediaToolTimeoutException($"ffmpeg brauchte für die Standbilder länger als {PreviewTimeout.TotalMinutes:0.#} Minuten und wurde beendet.");
            cancellationToken.ThrowIfCancellationRequested();
            await copy; var error = await errors;
            if (process.ExitCode != 0 || output.Length == 0)
                throw new InvalidOperationException($"ffmpeg wurde mit Fehler {process.ExitCode} beendet: {error[..Math.Min(error.Length, 1800)]}");
            return output.ToArray();
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(copy, errors); } catch (Exception error) when (error is OperationCanceledException or IOException) { }
        }
    }
}
