using System.Globalization;
using FlixyPix.Core;

namespace FlixyPix.MediaTools;

public sealed partial class MediaToolService
{
    /// <summary>Saves the single frame at this time in full resolution, as a range of pictures does: FFmpeg seeks to half a
    /// frame before it, so the first frame it decodes is the one shown there. Unlike a screenshot of the running player it
    /// needs no picture copied back from the graphics card and leaves playback alone. The file is written beside its
    /// target and moved into place when complete.</summary>
    /// <param name="framesPerSecond">Frame rate of the video, for the half frame of room before the time.</param>
    public async Task SaveStillAsync(string input, double seconds, double framesPerSecond, string path, CancellationToken cancellationToken = default)
    {
        ValidateTime(seconds, nameof(seconds));
        if (!double.IsFinite(framesPerSecond) || framesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg")) throw new ArgumentOutOfRangeException(nameof(path));
        var target = Path.GetFullPath(path); var folder = Path.GetDirectoryName(target)!;
        FileAccessPolicy.Current.EnsureWritable(folder); Directory.CreateDirectory(folder);
        var work = Path.Combine(folder, ".flixypix-still-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            string[] quality = extension == ".jpg" ? ["-q:v", "2"] : [];
            await RunAsync(Resolve(paths.Ffmpeg, "ffmpeg"), ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss", F(Math.Max(0, seconds - .5 / framesPerSecond)), "-i", Input(input),
                "-map", "0:v:0", "-an", "-sn", "-frames:v", "1", ..quality, "-update", "1", work], cancellationToken, TimeSpan.FromMinutes(2));
            if (!File.Exists(work) || new FileInfo(work).Length == 0) throw new NoRenderedMediaException();
            File.Move(work, target);
        }
        finally { try { if (File.Exists(work)) File.Delete(work); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>Saves the frames of a marked range as single pictures in full resolution: every frame, every n-th or one
    /// every so many seconds. FFmpeg seeks exactly, so the first picture is the frame at the start mark and the frame at
    /// the end mark is the last one that can be included. The pictures are written beside their target and moved into
    /// place when the run is complete; a cancelled run leaves nothing behind.</summary>
    /// <param name="framesPerSecond">Frame rate of the video; it tells the time of each picture and half a frame of room at both ends.</param>
    /// <param name="extension">".png" or ".jpg".</param>
    /// <param name="progress">The number of pictures written so far.</param>
    public async Task<IReadOnlyList<StillFile>> ExportStillsAsync(string input, string folder, string name, StillRange range, double framesPerSecond, string extension,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);
        range.Validate(framesPerSecond);
        if (extension is not (".png" or ".jpg")) throw new ArgumentOutOfRangeException(nameof(extension));
        var source = Input(input); var target = Path.GetFullPath(folder);
        FileAccessPolicy.Current.EnsureWritable(target); Directory.CreateDirectory(target);
        var work = Path.Combine(target, ".flixypix-stills-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            // FFmpeg seeks to half a frame before the start mark; from there the filter counts time. The frame at the end
            // mark lies half a frame before the limit, so neither rounding nor the length of a frame decides about it.
            var half = .5 / framesPerSecond; var start = Math.Max(0, range.StartSeconds - half);
            var limit = "lte(t\\," + F(range.EndSeconds + half - start) + ")";
            // Frames are chosen, never duplicated or blended: every one, every n-th, or the first one at each step of time.
            string[] choice = range.Spacing switch
            {
                StillSpacing.EveryNthFrame => ["-vf", "select='" + limit + "*not(mod(n\\," + ((int)Math.Max(1, Math.Round(range.Amount))).ToString(CultureInfo.InvariantCulture) + "))'", "-fps_mode", "vfr"],
                StillSpacing.EverySeconds => ["-vf", "select='" + limit + "*gte(t\\,selected_n*" + F(range.Amount) + ")'", "-fps_mode", "vfr"],
                _ => ["-vf", "select='" + limit + "'", "-fps_mode", "passthrough"],
            };
            string[] quality = extension == ".jpg" ? ["-q:v", "2"] : [];
            using var watching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var watch = progress == null ? Task.CompletedTask : Task.Run(async () =>
            {
                try { while (true) { await Task.Delay(400, watching.Token); progress.Report(Directory.EnumerateFiles(work).Count()); } }
                catch (OperationCanceledException) { }
            }, CancellationToken.None);
            try
            {
                // Reading ends a second after the range, so a long video is not decoded to its end.
                await RunAsync(Resolve(paths.Ffmpeg, "ffmpeg"), ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss", F(start), "-t", F(range.EndSeconds - start + 1), "-i", source,
                    "-map", "0:v:0", "-an", "-sn", ..choice, ..quality, "-start_number", "0", Path.Combine(work, "%06d" + extension)], cancellationToken, TimeSpan.FromHours(4));
            }
            finally { watching.Cancel(); await watch; }
            var rendered = Directory.EnumerateFiles(work, "*" + extension).Order(StringComparer.Ordinal).ToList();
            if (rendered.Count == 0) throw new NoRenderedMediaException();
            var step = range.Step(framesPerSecond); var stills = new List<StillFile>(rendered.Count);
            for (var index = 0; index < rendered.Count; index++)
            {
                var seconds = range.StartSeconds + index * step;
                var path = Path.Combine(target, name + " " + ScreenshotPreferences.Stamp(seconds, precise: true) + extension);
                for (var n = 2; File.Exists(path); n++) path = Path.Combine(target, name + " " + ScreenshotPreferences.Stamp(seconds, precise: true) + " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + extension);
                File.Move(rendered[index], path); stills.Add(new(path, seconds));
            }
            progress?.Report(stills.Count);
            return stills;
        }
        finally { try { Directory.Delete(work, recursive: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
}
