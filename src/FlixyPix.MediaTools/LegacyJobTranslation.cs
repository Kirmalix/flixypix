using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlixyPix.Core;

namespace FlixyPix.MediaTools;

public sealed record CombineSegment(string Input, double StartSeconds = 0, double? EndSeconds = null);

public sealed record LegacyJobProposal(string SourceSha256, MediaJob? Job, IReadOnlyList<string> BlockingIssues,
    IReadOnlyList<string> Warnings, string ProposalSha256)
{
    public bool CanEnqueue => Job is not null && BlockingIssues.Count == 0;
}

/// <summary>Explicit data translation. Original command strings are never passed to any process.</summary>
public static class LegacyJobTranslator
{
    public static LegacyJobProposal PreviewCombine(LegacyCombineList draft, string output)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var issues = new List<string>();
        try { draft = LegacyCombineListReader.Refresh(draft); } catch (InvalidDataException error) { issues.Add(error.Message); }
        if (draft.Status == HistoricalProjectionStatus.Opaque) issues.Add("The list is opaque and has no executable projection.");
        if (draft.Entries.Count is < 2 or > 128) issues.Add("Normalized combination requires 2 to 128 entries.");
        var segments = draft.Entries.OrderBy(entry => entry.Order).Select(entry => new CombineSegment(entry.InputPath, entry.StartSeconds, entry.EndSeconds)).ToList();
        foreach (var entry in draft.Entries) issues.AddRange(entry.BlockingIssues.Select(issue => $"Entry {entry.Order + 1}: {issue}"));
        if (!draft.Entries.Select(entry => entry.Order).Order().SequenceEqual(Enumerable.Range(0, draft.Entries.Count))) issues.Add("Entry order must be unique and contiguous.");
        var target = CheckOutput(output, issues);
        foreach (var segment in segments) CheckSegment(segment, issues);
        if (segments.Any(segment => SamePath(segment.Input, target))) issues.Add("Output must differ from all sources.");
        // The original stored both options on every entry but applied them to the whole list.
        if (draft.Entries.Select(entry => entry.Crossfade).Distinct().Count() > 1 || draft.Entries.Select(entry => entry.MuteAudio).Distinct().Count() > 1)
            issues.Add("Crossfade and mute must be the same for all entries.");
        var options = new CombineOptions(draft.Entries.Any(entry => entry.MuteAudio), draft.Entries.Any(entry => entry.Crossfade) ? 1 : 0);
        try { options.Validate(segments.Count); } catch (ArgumentException error) { issues.Add(error.Message); }
        var job = new MediaJob
        {
            Id = StableId(draft.PayloadSha256 + ":normalized-combine-v1:" + target.ToUpperInvariant()), Kind = MediaJobKind.CombineSegments,
            Input = segments.FirstOrDefault()?.Input ?? "", Inputs = segments.Select(segment => segment.Input).ToList(), Segments = segments,
            Output = target, MuteAudio = options.MuteAudio, CrossfadeSeconds = options.CrossfadeSeconds, CreatedAt = DateTimeOffset.UnixEpoch
        };
        return Proposal(draft.PayloadSha256, job, issues,
            ["Explicit modern profile: H.264 CRF20, AAC stereo 48kHz, 30fps; even canvas of first video, aspect-preserving fit and padding.",
                "Source order and selected second ranges are retained. Crossfade (1 s, as in the original) and mute are applied; original commands and metadata-copy actions are not executed."]);
    }

    public static LegacyJobProposal PreviewSql(LegacyJobDraft draft, string output)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var sourceHash = Hash(JsonSerializer.Serialize(draft)); var issues = new List<string>();
        var target = CheckOutput(output, issues); var start = 0d; double? duration = null;
        try
        {
            var tokens = Tokens(draft.OriginalCommand ?? "");
            string? input = null; string? originalOutput = null; var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < tokens.Count; index++)
            {
                var token = tokens[index];
                if (token is "-y" or "-n" or "-hide_banner" or "-nostdin") continue;
                if (!token.StartsWith('-'))
                {
                    if (index != tokens.Count - 1 || originalOutput is not null) throw new InvalidDataException("Unexpected positional argument.");
                    originalOutput = token; continue;
                }
                if (!seen.Add(token) || ++index == tokens.Count) throw new InvalidDataException("Repeated option or missing option value.");
                var value = tokens[index];
                switch (token)
                {
                    case "-i": input = value; break;
                    case "-ss": start = Seconds(value); break;
                    case "-t": duration = Seconds(value); break;
                    case "-c:v": case "-vcodec":
                        if (value is not ("libx264" or "h264" or "copy")) throw new InvalidDataException("Unsupported historical video encoding option."); break;
                    case "-c:a": case "-acodec":
                        if (value is not ("aac" or "copy")) throw new InvalidDataException("Unsupported historical audio encoding option."); break;
                    case "-crf": if (!int.TryParse(value, out var crf) || crf is < 0 or > 51) throw new InvalidDataException("Invalid CRF."); break;
                    case "-preset": if (value is not ("ultrafast" or "superfast" or "veryfast" or "faster" or "fast" or "medium" or "slow" or "slower" or "veryslow")) throw new InvalidDataException("Unsupported preset."); break;
                    case "-loglevel": if (value is not ("error" or "warning")) throw new InvalidDataException("Unsupported log level."); break;
                    default: throw new InvalidDataException("Historical option has no complete translation: " + token);
                }
            }
            if (input is null || !SamePath(input, draft.InputPath ?? "")) issues.Add("Command input does not match the recorded source path.");
            if (originalOutput is null || !SamePath(originalOutput, draft.OutputPath ?? "")) issues.Add("Command output does not match the recorded output path.");
            if (duration is null || duration <= 0) issues.Add("A positive explicit clip duration is required.");
            if (draft.DurationSeconds is { } expected && duration is { } actual && Math.Abs(actual - expected) > 0.05) issues.Add("Recorded and command durations disagree.");
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        { issues.Add(error.Message); }
        var segment = new CombineSegment(draft.InputPath ?? "", start, duration is { } length ? start + length : null);
        CheckSegment(segment, issues);
        if (SamePath(segment.Input, target)) issues.Add("Output must differ from the source.");
        var job = new MediaJob
        {
            Id = StableId(sourceHash + ":modern-clip-v1:" + target.ToUpperInvariant()), Kind = MediaJobKind.Clip,
            Input = segment.Input, Output = target, StartSeconds = start, DurationSeconds = duration ?? 0, CreatedAt = DateTimeOffset.UnixEpoch
        };
        return Proposal(sourceHash, job, issues,
            ["Explicit modern clip profile replaces original encoder choices: H.264 CRF20 and AAC. Original command is retained as data and never executed."]);
    }

    public static string Fingerprint(LegacyJobDraft draft) => Hash(JsonSerializer.Serialize(draft));

    public static MediaJob Enqueue(MediaJobQueue queue, LegacyJobProposal proposal, string currentSourceSha256)
    {
        ArgumentNullException.ThrowIfNull(queue); ArgumentNullException.ThrowIfNull(proposal);
        if (!proposal.CanEnqueue) throw new InvalidOperationException("The historical job has unresolved translation issues.");
        if (proposal.SourceSha256 != currentSourceSha256 || proposal.ProposalSha256 != ProposalHash(proposal.SourceSha256, proposal.Job!, proposal.BlockingIssues, proposal.Warnings))
            throw new InvalidOperationException("The source or proposal changed; create a new preview.");
        var existing = queue.Jobs.FirstOrDefault(job => job.Id == proposal.Job!.Id);
        if (existing is not null)
        {
            var requested = proposal.Job!;
            if (existing.Kind != requested.Kind || !SamePath(existing.Input, requested.Input) || !SamePath(existing.Output, requested.Output) ||
                existing.StartSeconds != requested.StartSeconds || existing.DurationSeconds != requested.DurationSeconds ||
                existing.MuteAudio != requested.MuteAudio || existing.CrossfadeSeconds != requested.CrossfadeSeconds ||
                existing.Segments.Count != requested.Segments.Count || existing.Segments.Where((segment, index) =>
                    !SamePath(segment.Input, requested.Segments[index].Input) || segment.StartSeconds != requested.Segments[index].StartSeconds || segment.EndSeconds != requested.Segments[index].EndSeconds).Any())
                throw new InvalidOperationException("An existing job with this identity has a different definition.");
            return existing;
        }
        return queue.Enqueue(proposal.Job!);
    }

    private static LegacyJobProposal Proposal(string source, MediaJob job, List<string> issues, IReadOnlyList<string> warnings) =>
        new(source, job, issues.Distinct(StringComparer.Ordinal).ToArray(), warnings, ProposalHash(source, job, issues.Distinct(StringComparer.Ordinal).ToArray(), warnings));
    private static string ProposalHash(string source, MediaJob job, IReadOnlyList<string> issues, IReadOnlyList<string> warnings) => Hash(JsonSerializer.Serialize(new { source, job, issues, warnings }));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    private static bool SamePath(string first, string second) => !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private static string CheckOutput(string output, List<string> issues)
    {
        if (!Path.IsPathFullyQualified(output)) { issues.Add("Choose an absolute new output path."); return output; }
        var target = Path.GetFullPath(output);
        try { FileAccessPolicy.Current.EnsureWritable(target); } catch (UnauthorizedAccessException error) { issues.Add(error.Message); }
        if (!Path.GetExtension(target).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) issues.Add("The explicit modern output profile requires .mp4.");
        return target;
    }
    private static void CheckSegment(CombineSegment segment, List<string> issues)
    {
        if (!Path.IsPathFullyQualified(segment.Input)) issues.Add("Source path must be absolute.");
        if (!double.IsFinite(segment.StartSeconds) || segment.StartSeconds < 0 || segment.EndSeconds is { } end && (!double.IsFinite(end) || end <= segment.StartSeconds))
            issues.Add("Invalid segment time range.");
    }
    private static double Seconds(string value)
    {
        var result = value.Contains(':') && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span) ? span.TotalSeconds :
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : double.NaN;
        if (!double.IsFinite(result) || result < 0) throw new InvalidDataException("Invalid command time value."); return result;
    }
    private static List<string> Tokens(string value)
    {
        if (value.Length is < 1 or > 65536) throw new InvalidDataException("Missing or oversized historical command.");
        var result = new List<string>(); var text = new StringBuilder(); var quoted = false;
        foreach (var character in value)
        {
            if (character == '"') { quoted = !quoted; continue; }
            if (character is '\r' or '\n' or '\0' || !quoted && character is '&' or '|' or '<' or '>') throw new InvalidDataException("Shell or multiline command syntax is not translated.");
            if (!quoted && char.IsWhiteSpace(character)) { if (text.Length > 0) { result.Add(text.ToString()); text.Clear(); } }
            else text.Append(character);
        }
        if (quoted) throw new InvalidDataException("Unclosed command quote."); if (text.Length > 0) result.Add(text.ToString());
        if (result.Count > 128) throw new InvalidDataException("Too many command tokens."); return result;
    }
}

public sealed partial class MediaToolService
{
    public async Task CombineSegmentsAsync(IReadOnlyList<CombineSegment> segments, string output, CancellationToken cancellationToken = default, CombineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(segments); FileAccessPolicy.Current.EnsureWritable(output);
        if (segments.Count is < 2 or > 128) throw new ArgumentException("Choose 2 to 128 segments.", nameof(segments)); options ??= new(); options.Validate(segments.Count);
        var target = Path.GetFullPath(output);
        if (!Path.GetExtension(target).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Normalized combination requires MP4 output.");
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Output already exists.");
        var sources = segments.Select(segment => Input(segment.Input)).ToArray();
        if (sources.Any(source => source.Equals(target, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Output must differ from sources.");
        foreach (var segment in segments)
        {
            ValidateTime(segment.StartSeconds, nameof(segment.StartSeconds));
            if (segment.EndSeconds is { } end && (!double.IsFinite(end) || end <= segment.StartSeconds)) throw new ArgumentException("Invalid segment range.");
        }
        var probes = new List<MediaProbe>();
        for (var index = 0; index < sources.Length; index++)
        {
            var probe = await ProbeAsync(sources[index], cancellationToken);
            if (probe.VideoCodec is null || probe.Width is not > 0 || probe.Height is not > 0 || probe.DurationSeconds is not > 0)
                throw new InvalidDataException("A finite video duration and dimensions are required.");
            var segment = segments[index];
            if (segment.StartSeconds >= probe.DurationSeconds || segment.EndSeconds > probe.DurationSeconds + 0.1)
                throw new InvalidDataException("Selected range exceeds the source duration.");
            probes.Add(probe);
        }
        var width = Math.Max(2, probes[0].Width!.Value / 2 * 2); var height = Math.Max(2, probes[0].Height!.Value / 2 * 2);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var work = Path.Combine(Path.GetDirectoryName(target)!, ".flixypix-normalize-" + Guid.NewGuid().ToString("N"));
        FileAccessPolicy.Current.EnsureWritable(work); Directory.CreateDirectory(work);
        var created = new List<string>();
        try
        {
            for (var index = 0; index < sources.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); var segment = segments[index];
                var duration = (segment.EndSeconds ?? probes[index].DurationSeconds!.Value) - segment.StartSeconds;
                var part = Path.Combine(work, index.ToString("D4", CultureInfo.InvariantCulture) + ".mp4"); created.Add(part);
                var before = new List<string> { "-ss", F(segment.StartSeconds) };
                var after = new List<string>();
                if (probes[index].AudioCodec is null) after.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-map", "0:v:0", "-map", "1:a:0"]);
                else after.AddRange(["-map", "0:v:0", "-map", "0:a:0"]);
                after.AddRange(["-t", F(duration), "-vf", $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30", "-pix_fmt", "yuv420p",
                    "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-c:a", "aac", "-ar", "48000", "-ac", "2", "-shortest", "-movflags", "+faststart"]);
                _ = await RenderAsync(sources[index], part, before.ToArray(), after.ToArray(), cancellationToken);
            }
            var combined = Path.Combine(work, "combined.mp4"); created.Add(combined);
            if (options.Crossfade) await CrossfadeAsync(created.Take(sources.Length).ToList(), combined, options, cancellationToken);
            else await CombineAsync(created.Take(sources.Length), combined, cancellationToken, options.MuteAudio);
            cancellationToken.ThrowIfCancellationRequested(); FileAccessPolicy.Current.EnsureWritable(target); File.Move(combined, target, false);
        }
        finally
        {
            FileAccessPolicy.Current.EnsureWritable(work);
            foreach (var file in created) { FileAccessPolicy.Current.EnsureWritable(file); if (File.Exists(file)) File.Delete(file); }
            Directory.Delete(work, false);
        }
    }
}
