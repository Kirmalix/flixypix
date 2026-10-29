using System.Globalization;

namespace FlixyPix.MediaTools;

/// <summary>How the clip tool joins videos: without sound and/or with a crossfade between neighbouring parts.
/// The original (frmVideoTools) used a fixed one-second crossfade and allowed at most 25 videos.</summary>
public sealed record CombineOptions(bool MuteAudio = false, double CrossfadeSeconds = 0)
{
    public const int MaximumCrossfadeParts = 25;
    public const double MaximumCrossfadeSeconds = 5;
    public bool Crossfade => CrossfadeSeconds > 0;

    public void Validate(int parts)
    {
        if (!double.IsFinite(CrossfadeSeconds) || CrossfadeSeconds < 0 || CrossfadeSeconds > MaximumCrossfadeSeconds)
            throw new ArgumentOutOfRangeException(nameof(CrossfadeSeconds), "Die Überblendung muss zwischen 0 und 5 Sekunden liegen.");
        if (Crossfade && parts > MaximumCrossfadeParts)
            throw new ArgumentException($"Mit Überblendung lassen sich höchstens {MaximumCrossfadeParts} Videos verbinden.");
    }
}

/// <summary>Plans clip ranges for many videos at once, like the original's "Random start time" for the combine list.
/// A seed makes the plan reproducible, so a queued job keeps exactly the ranges the user saw.</summary>
public static class ClipPlanner
{
    public static IReadOnlyList<CombineSegment> Random(IReadOnlyList<(string Input, double DurationSeconds)> videos, double lengthSeconds, int seed)
    {
        ArgumentNullException.ThrowIfNull(videos);
        if (!double.IsFinite(lengthSeconds) || lengthSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(lengthSeconds));
        var random = new Random(seed);
        return videos.Select(video =>
        {
            if (!double.IsFinite(video.DurationSeconds) || video.DurationSeconds <= 0) throw new ArgumentException("Die Länge eines Videos ist unbekannt.", nameof(videos));
            if (video.DurationSeconds <= lengthSeconds) return new CombineSegment(video.Input);
            // Whole seconds like the original; the last possible start still leaves the full length.
            var start = random.Next(0, (int)Math.Floor(video.DurationSeconds - lengthSeconds) + 1);
            return new CombineSegment(video.Input, start, start + lengthSeconds);
        }).ToList();
    }

    public static IReadOnlyList<CombineSegment> Fixed(IReadOnlyList<(string Input, double DurationSeconds)> videos, double startSeconds, double lengthSeconds)
    {
        ArgumentNullException.ThrowIfNull(videos);
        if (!double.IsFinite(startSeconds) || startSeconds < 0) throw new ArgumentOutOfRangeException(nameof(startSeconds));
        if (!double.IsFinite(lengthSeconds) || lengthSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(lengthSeconds));
        return videos.Select(video =>
        {
            // Videos shorter than the start keep their ending instead of failing the whole list.
            var start = Math.Max(0, Math.Min(startSeconds, video.DurationSeconds - lengthSeconds));
            return video.DurationSeconds <= lengthSeconds ? new CombineSegment(video.Input) : new CombineSegment(video.Input, start, start + lengthSeconds);
        }).ToList();
    }
}

public sealed partial class MediaToolService
{
    public static readonly string[] AudioExtensions = [".mp3", ".m4a"];

    /// <summary>Extracts the first audio track as MP3 (VBR quality 0, like the original's <c>-q:a 0</c>) or M4A (AAC).
    /// A duration of 0 takes everything from the start position to the end.</summary>
    public async Task ExtractAudioAsync(string input, string output, double startSeconds = 0, double durationSeconds = 0, CancellationToken cancellationToken = default)
    {
        ValidateTime(startSeconds, nameof(startSeconds)); ValidateTime(durationSeconds, nameof(durationSeconds));
        var extension = Path.GetExtension(output).ToLowerInvariant();
        if (!AudioExtensions.Contains(extension)) throw new ArgumentException("Ton wird als .mp3 oder .m4a gespeichert.", nameof(output));
        var probe = await ProbeAsync(input, cancellationToken);
        if (probe.AudioCodec is null) throw new InvalidDataException("Die Datei hat keine Tonspur.");
        var after = new List<string>();
        if (durationSeconds > 0) after.AddRange(["-t", F(durationSeconds)]);
        after.AddRange(["-vn", "-sn", "-map", "0:a:0"]);
        after.AddRange(extension == ".mp3" ? ["-c:a", "libmp3lame", "-q:a", "0"] : ["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
        _ = await RenderAsync(input, output, startSeconds > 0 ? ["-ss", F(startSeconds)] : [], after.ToArray(), cancellationToken);
    }

    /// <summary>Joins normalized parts (same size, frame rate and audio layout) with <c>xfade</c> and <c>acrossfade</c>.
    /// Transition k starts where the previous parts end minus k overlaps, so the result is shorter by one overlap per transition.</summary>
    private async Task CrossfadeAsync(IReadOnlyList<string> parts, string output, CombineOptions options, CancellationToken cancellationToken)
    {
        var durations = new List<double>();
        foreach (var part in parts)
        {
            var duration = (await ProbeAsync(part, cancellationToken)).DurationSeconds ?? 0;
            if (duration <= options.CrossfadeSeconds * 2)
                throw new InvalidDataException($"Jeder Abschnitt muss für die Überblendung länger als {F(options.CrossfadeSeconds * 2)} Sekunden sein.");
            durations.Add(duration);
        }
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-n" };
        foreach (var part in parts) arguments.AddRange(["-i", part]);
        arguments.AddRange(["-filter_complex", CrossfadeFilter(durations, options), "-map", "[v]"]);
        if (!options.MuteAudio) arguments.AddRange(["-map", "[a]"]);
        arguments.AddRange(["-pix_fmt", "yuv420p", "-c:v", "libx264", "-preset", "medium", "-crf", "20"]);
        arguments.AddRange(options.MuteAudio ? ["-an"] : ["-c:a", "aac", "-ar", "48000", "-ac", "2"]);
        arguments.AddRange(["-movflags", "+faststart", output]);
        await RunAsync(Resolve(paths.Ffmpeg, "ffmpeg"), arguments, cancellationToken);
        if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new IOException("Keine überblendete Datei erzeugt.");
    }

    public static string CrossfadeFilter(IReadOnlyList<double> durations, CombineOptions options)
    {
        if (durations.Count < 2) throw new ArgumentException("Zum Überblenden mindestens zwei Abschnitte wählen.", nameof(durations));
        var d = options.CrossfadeSeconds; var filter = new List<string>(); var elapsed = 0d;
        for (var index = 1; index < durations.Count; index++)
        {
            elapsed += durations[index - 1];
            var offset = elapsed - index * d;
            var previous = index == 1 ? "[0:v]" : $"[v{index - 1}]"; var next = index == durations.Count - 1 ? "[v]" : $"[v{index}]";
            filter.Add($"{previous}[{index}:v]xfade=transition=fade:duration={F(d)}:offset={offset.ToString("0.###", CultureInfo.InvariantCulture)}{next}");
            if (options.MuteAudio) continue;
            var previousAudio = index == 1 ? "[0:a]" : $"[a{index - 1}]"; var nextAudio = index == durations.Count - 1 ? "[a]" : $"[a{index}]";
            filter.Add($"{previousAudio}[{index}:a]acrossfade=d={F(d)}{nextAudio}");
        }
        return string.Join(';', filter);
    }
}
