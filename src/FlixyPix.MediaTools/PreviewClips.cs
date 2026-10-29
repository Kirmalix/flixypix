using System.Globalization;
using FlixyPix.Core;

namespace FlixyPix.MediaTools;

/// <summary>Shape of a preview clip: how many parts, how long each is and how wide the picture is. The defaults follow
/// Stash (12 parts of 0.75 s).</summary>
public sealed record PreviewClipSpec(int Segments = 12, double SegmentSeconds = 0.75, int Width = 320)
{
    public const int MaximumSegments = 32;

    public void Validate()
    {
        if (Segments is < 1 or > MaximumSegments) throw new ArgumentOutOfRangeException(nameof(Segments), $"1 bis {MaximumSegments} Abschnitte.");
        if (!double.IsFinite(SegmentSeconds) || SegmentSeconds < 0.25 || SegmentSeconds > 5) throw new ArgumentOutOfRangeException(nameof(SegmentSeconds), "Abschnitte sind 0,25 bis 5 Sekunden lang.");
        if (Width is < 120 or > 1280 || Width % 2 != 0) throw new ArgumentOutOfRangeException(nameof(Width), "Die Breite ist gerade und liegt zwischen 120 und 1280 Pixeln.");
    }

    /// <summary>Starts of the parts, spread evenly between 5 % and 95 % of the video. A video too short for all parts
    /// becomes one part from the start.</summary>
    public IReadOnlyList<double> Starts(double durationSeconds)
    {
        Validate();
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if (IsShort(durationSeconds)) return [0];
        var first = durationSeconds * 0.05; var last = durationSeconds * 0.95 - SegmentSeconds;
        return Segments == 1 ? [first] : Enumerable.Range(0, Segments).Select(i => first + (last - first) * i / (Segments - 1)).ToArray();
    }

    /// <summary>Too short for all parts: the clip is the whole video.</summary>
    public bool IsShort(double durationSeconds) => durationSeconds <= Segments * SegmentSeconds * 1.5;

    /// <summary>Part of the cache file name: another shape is another clip.</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Segments}x{SegmentSeconds * 1000:0}ms-{Width}");
}

public sealed partial class MediaToolService
{
    /// <summary>Writes a silent preview clip (H.264, ready for streaming) from short parts of the video, in one FFmpeg
    /// run with a fast seek per part. When that run fails at a broken spot, each part is tested alone and the broken
    /// ones are left out. The source is only read; the clip appears complete or not at all.</summary>
    public Task CreatePreviewClipAsync(string input, string output, double durationSeconds, PreviewClipSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return WriteClipAsync(input, output, spec.Starts(durationSeconds), spec.IsShort(durationSeconds) ? durationSeconds : spec.SegmentSeconds, spec.Width, cancellationToken);
    }

    /// <summary>A clip of one stretch from a place in the video, for a tile of the scene wall; it looks like a preview clip.</summary>
    public Task CreateMarkClipAsync(string input, string output, double startSeconds, double lengthSeconds, int width, CancellationToken cancellationToken = default)
    {
        ValidateTime(startSeconds, nameof(startSeconds)); ValidateTime(lengthSeconds, nameof(lengthSeconds), positive: true);
        new PreviewClipSpec(Width: width).Validate();
        return WriteClipAsync(input, output, [startSeconds], lengthSeconds, width, cancellationToken);
    }

    private async Task WriteClipAsync(string input, string output, IReadOnlyList<double> starts, double length, int width, CancellationToken cancellationToken)
    {
        var source = Input(input); var target = Path.GetFullPath(output);
        FileAccessPolicy.Current.EnsureWritable(target);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Eingabe und Ausgabe müssen verschieden sein.", nameof(output));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileNameWithoutExtension(target) + "." + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            try { await RunAsync(Resolve(paths.Ffmpeg, "ffmpeg"), PreviewClipArguments(source, temporary, starts, length, width), cancellationToken, PreviewTimeout); }
            catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested && starts.Count > 1)
            {
                var usable = new List<double>();
                foreach (var start in starts)
                {
                    try { await RunAsync(Resolve(paths.Ffmpeg, "ffmpeg"), ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss", F(start), "-t", F(length), "-i", source, "-map", "0:v:0", "-f", "null", "-"], cancellationToken, PreviewTimeout); usable.Add(start); }
                    catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested) { }
                }
                if (usable.Count == 0) throw new InvalidDataException("Kein Abschnitt des Videos ließ sich lesen.");
                if (File.Exists(temporary)) File.Delete(temporary);
                await RunAsync(Resolve(paths.Ffmpeg, "ffmpeg"), PreviewClipArguments(source, temporary, usable, length, width), cancellationToken, PreviewTimeout);
            }
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new NoRenderedMediaException();
            FileAccessPolicy.Current.EnsureWritable(target); File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static List<string> PreviewClipArguments(string source, string output, IReadOnlyList<double> starts, double length, int width)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
        // Each part starts at the keyframe before its place: decoding from there to the exact second cost several seconds per
        // part in long-GOP videos, and a preview does not need the exact frame.
        foreach (var start in starts) arguments.AddRange(["-noaccurate_seek", "-ss", F(start), "-t", F(length), "-i", source]);
        // Same size, frame rate and pixel format for every part, so concat accepts them; the height keeps the aspect ratio.
        var parts = starts.Select((_, index) => $"[{index}:v:0]setpts=PTS-STARTPTS,fps=24,scale={width}:-2:flags=bicubic,setsar=1,format=yuv420p[p{index}]");
        var graph = string.Join(';', parts) + ";" + string.Concat(starts.Select((_, index) => $"[p{index}]")) + $"concat=n={starts.Count}:v=1:a=0[out]";
        arguments.AddRange(["-filter_complex", graph, "-map", "[out]", "-an", "-sn", "-c:v", "libx264", "-preset", "veryfast", "-crf", "28",
            "-profile:v", "high", "-movflags", "+faststart", "-f", "mp4", output]);
        return arguments;
    }
}
