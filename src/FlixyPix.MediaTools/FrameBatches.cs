using FlixyPix.Core;

namespace FlixyPix.MediaTools;

public sealed record FrameRequest(double Seconds, string Output);

public sealed partial class MediaToolService
{
    /// <summary>Frames per FFmpeg run; each one opens its own decoder, so very long lists are split.</summary>
    public const int FramesPerRun = 24;

    /// <summary>Several stills of one video in as few FFmpeg runs as possible: one accurate input seek per frame and one output
    /// per frame, scaled like <see cref="CreateThumbnailAsync"/>. Returns the frames that were written; a frame FFmpeg found no
    /// picture for is missing from the result, so the caller can retry it singly.</summary>
    public async Task<IReadOnlyList<FrameRequest>> CreateThumbnailsAsync(string input, IReadOnlyList<FrameRequest> frames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var source = Input(input);
        foreach (var frame in frames)
        {
            ValidateTime(frame.Seconds, nameof(frames));
            var target = Path.GetFullPath(frame.Output); FileAccessPolicy.Current.EnsureWritable(target);
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Eingabe und Ausgabe müssen verschieden sein.", nameof(frames));
            if (File.Exists(target)) throw new IOException("Die Ausgabedatei existiert bereits. Bitte einen neuen Namen wählen.");
        }
        if (frames.Select(frame => Path.GetFullPath(frame.Output)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != frames.Count)
            throw new ArgumentException("Jedes Bild braucht eine eigene Ausgabedatei.", nameof(frames));
        var written = new List<FrameRequest>();
        foreach (var batch in frames.Chunk(FramesPerRun))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-n" };
            foreach (var frame in batch) arguments.AddRange(["-ss", F(frame.Seconds), "-i", source]);
            var temporary = batch.Select(frame =>
            {
                var target = Path.GetFullPath(frame.Output); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                return Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileNameWithoutExtension(target) + "." + Guid.NewGuid().ToString("N") + Path.GetExtension(target));
            }).ToArray();
            for (var index = 0; index < batch.Length; index++)
                arguments.AddRange(["-map", $"{index}:v:0", "-frames:v", "1", "-vf", "scale=480:480:force_original_aspect_ratio=decrease", "-update", "1", temporary[index]]);
            try
            {
                // A broken frame fails the whole run; what was written still counts, the rest is retried singly by the caller.
                try { await RunAsync(Resolve(paths.Ffmpeg, "ffmpeg"), arguments, cancellationToken); }
                catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested) { }
                for (var index = 0; index < batch.Length; index++)
                {
                    if (!File.Exists(temporary[index]) || new FileInfo(temporary[index]).Length == 0) continue;
                    var target = Path.GetFullPath(batch[index].Output); FileAccessPolicy.Current.EnsureWritable(target);
                    File.Move(temporary[index], target, overwrite: false); written.Add(batch[index]);
                }
            }
            finally { foreach (var file in temporary) if (File.Exists(file)) File.Delete(file); }
        }
        return written;
    }
}
