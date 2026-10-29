using System.Diagnostics;
using System.IO;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

/// <summary>Preview clips (feature plan P3): short silent clips from parts of a video for the hover preview and the
/// walls. They live in the library's clip cache under the media id and the clip shape, so other settings make new clips
/// and the old ones count as unused.</summary>
public partial class MainWindow
{
 private string ClipFolder => Path.Combine(_settings.DataDirectory, "clip-cache");

 internal static PreviewClipSpec ClipSpec(PreviewPreferences previews) => new(previews.ClipSegments, previews.ClipSegmentMilliseconds / 1000.0, previews.ClipWidth);

 internal string PreviewClipPath(MediaItem item, PreviewClipSpec spec) => Path.Combine(ClipFolder, item.Id.ToString("N") + "-" + spec.Key + ".mp4");

 /// <summary>Makes the clips that are missing: for the chosen videos, otherwise for every video of the library. A
 /// stopped run goes on where it ended, because finished clips stay.</summary>
 internal async Task<(int Made, int Checked)> CreatePreviewClipsAsync(IReadOnlyList<MediaItem>? chosen, CancellationToken token)
 {
  var previews = _repository.GetPreferences().Previews; var spec = ClipSpec(previews);
  if (!ToolExists(_settings.Ffmpeg, "ffmpeg")) throw new InvalidOperationException(T("FFmpeg is missing. Its path can be set under Settings → Tools."));
  var canProbe = ToolExists(_settings.Ffprobe, "ffprobe");
  FileAccessPolicy.Current.EnsureWritable(ClipFolder); Directory.CreateDirectory(ClipFolder);
  var scope = new MediaQuery { Kind = MediaKind.Video };
  var total = chosen?.Count ?? (int)_repository.QueryPage(scope with { Limit = 1 }).TotalCount;
  int seen = 0, made = 0, failed = 0; var watch = Stopwatch.StartNew();
  async IAsyncEnumerable<MediaItem> Candidates()
  {
   if (chosen != null) { foreach (var item in chosen) yield return item; yield break; }
   await foreach (var item in StreamCatalog(scope, token)) yield return item;
  }
  await Parallel.ForEachAsync(Candidates(), new ParallelOptions { MaxDegreeOfParallelism = previews.GeneratorProcesses, CancellationToken = token }, async (item, itemToken) =>
  {
   var number = Interlocked.Increment(ref seen);
   var clip = PreviewClipPath(item, spec);
   if (item.Kind == MediaKind.Video && !File.Exists(clip) && File.Exists(item.Path))
   {
    try
    {
     var duration = item.DurationSeconds is > 0 and var known ? known : canProbe ? (await _tools.ProbeAsync(item.Path, itemToken)).DurationSeconds ?? 0 : 0;
     if (duration > 0) { await _tools.CreatePreviewClipAsync(item.Path, clip, duration, spec, itemToken); Interlocked.Increment(ref made); }
     else Interlocked.Increment(ref failed);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
    { Interlocked.Increment(ref failed); ErrorLog.Write("Vorschauclip", error); }
   }
   var left = watch.Elapsed / number * Math.Max(0, total - number);
   _ = Dispatcher.InvokeAsync(() => Status.Text = T("Preview clips · {0:N0} of {1:N0} checked · {2:N0} new", number, total, made) + (number > 8 && left > TimeSpan.FromSeconds(5) ? T(" · about {0} left", Remaining(left)) : ""));
  });
  if (failed > 0) Status.Text = Tn(failed, "1 video had no preview clip made; details are in the error log.", "{0:N0} videos had no preview clip made; details are in the error log.");
  return (made, seen);
 }

 private async void CreateSelectedPreviewClips()
 {
  var items = SelectedMedia.Where(i => i.Kind == MediaKind.Video).ToList();
  if (items.Count == 0) { Status.Text = T("Please select one or more videos."); return; }
  await RunOperation(T("Create preview clips"), async token => { var (made, _) = await CreatePreviewClipsAsync(items, token); Status.Text = Tn(made, "1 preview clip created", "{0:N0} preview clips created"); });
 }

 private async void CreateAllPreviewClips() =>
  await RunOperation(T("Create preview clips"), async token => { var (made, _) = await CreatePreviewClipsAsync(null, token); Status.Text = Tn(made, "1 preview clip created", "{0:N0} preview clips created"); });
}
