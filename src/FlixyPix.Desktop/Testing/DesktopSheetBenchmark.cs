using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Headless speed check of the thumbnail generator: the first 20 videos of a folder, made in the original's look
 /// (4 × 4, 1280 px, header and timestamps) with 1, 2, 4, 6 and 8 processes. Videos are only read; the sheets go to the
 /// output folder and the result file holds times and counts only.</summary>
 internal static async Task<int> RunSheetBenchmarkAsync(string videoFolder, string outputDirectory)
 {
  Directory.CreateDirectory(outputDirectory);
  var tools = new MediaToolService(new MediaToolPaths(null, null, null));
  var files = Directory.EnumerateFiles(videoFolder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
   .Where(f => MediaFormats.VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
  var videos = new List<MediaItem>();
  foreach (var file in files)
  {
   try { var probe = await tools.ProbeAsync(file); if (probe.DurationSeconds is > 0) videos.Add(new MediaItem { Path = file, Kind = MediaKind.Video, DurationSeconds = probe.DurationSeconds, Width = probe.Width, Height = probe.Height }); }
   catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException) { }
  }
  var style = new PreviewPreferences { SheetColumns = 4, SheetRows = 4, SheetWidth = 1280, SheetHeader = true, SheetTimestamp = SheetTimestamp.BottomRight, SheetFastSeek = true };
  var runs = new List<object>();
  foreach (var processes in new[] { 1, 2, 4, 6, 8 })
  {
   var folder = Path.Combine(outputDirectory, "p" + processes); Directory.CreateDirectory(folder);
   int made = 0, failed = 0; var watch = Stopwatch.StartNew();
   await Parallel.ForEachAsync(videos, new ParallelOptions { MaxDegreeOfParallelism = processes }, async (video, token) =>
   {
    var aspect = video.Width is > 0 && video.Height is > 0 ? (double)video.Width.Value / video.Height.Value : 16d / 9;
    try { await tools.CreateContactSheetAsync(video.Path, Path.Combine(folder, Guid.NewGuid().ToString("N") + ".jpg"), video.DurationSeconds!.Value, aspect, SheetStyle(style, video), token); Interlocked.Increment(ref made); }
    catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException) { Interlocked.Increment(ref failed); }
   });
   runs.Add(new { processes, seconds = Math.Round(watch.Elapsed.TotalSeconds, 1), secondsPerVideo = made == 0 ? 0 : Math.Round(watch.Elapsed.TotalSeconds / made, 2), made, failed });
  }
  File.WriteAllText(Path.Combine(outputDirectory, "sheet-benchmark.json"), JsonSerializer.Serialize(new
  {
   videos = videos.Count, totalHours = Math.Round(videos.Sum(v => v.DurationSeconds!.Value) / 3600, 1), logicalProcessors = Environment.ProcessorCount, runs,
  }, new JsonSerializerOptions { WriteIndented = true }));
  return 0;
 }
}
