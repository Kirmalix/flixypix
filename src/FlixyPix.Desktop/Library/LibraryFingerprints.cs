using System.IO;
using FlixyPix.Core;
using FlixyPix.Imaging;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>After an import: previews when "Create on import" is on, then preview clips and similarity fingerprints when
 /// the library asks for them. All only fill in what is missing, so a cancelled run goes on next time.</summary>
 private async Task AfterImportAsync(CancellationToken token)
 {
  if (_settings.AutoGeneratePreviews) await PrepareMediaAsync(token);
  var previews = _repository.GetPreferences().Previews;
  if (previews.ClipsOnImport && ToolExists(_settings.Ffmpeg, "ffmpeg")) await CreatePreviewClipsAsync(null, token);
  if (previews.FingerprintsOnImport) await FingerprintMissingAsync(token);
 }

 private bool AfterImportWanted => _settings.AutoGeneratePreviews || _repository.GetPreferences().Previews is { FingerprintsOnImport: true } or { ClipsOnImport: true };

 private async Task FingerprintMissingAsync(CancellationToken token)
 {
  var run = new FingerprintRun(_repository, ToolExists(_settings.Ffmpeg, "ffmpeg") ? _tools : null);
  Status.Text = T("Checking which fingerprints are missing …");
  var work = await Task.Run(() => run.Missing(null, token), token);
  if (work.Count == 0) return;
  var started = DateTime.UtcNow;
  var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
  timer.Tick += (_, _) => Status.Text = run.Progress(started);
  timer.Start();
  try { await run.ComputeAsync(work, token); }
  finally { timer.Stop(); }
 }
}

/// <summary>Computes missing or outdated similarity fingerprints: images in this process on half the cores, videos in as
/// many FFmpeg processes as the preview generator may run. What is done stays done when the run is cancelled.</summary>
internal sealed class FingerprintRun(CatalogRepository repository, MediaToolService? tools)
{
 private int done, failed;
 internal int Done => Volatile.Read(ref done);
 internal int Failed => Volatile.Read(ref failed);
 internal int Total { get; private set; }

 /// <summary>Media of the given kind whose file exists and has no fingerprint for its current size and time. Videos only
 /// with FFmpeg.</summary>
 internal List<MediaItem> Missing(MediaKind? kind, CancellationToken token, IReadOnlyList<MediaItem>? scope = null)
 {
  var stamps = repository.GetFingerprintStamps();
  var result = new List<MediaItem>();
  foreach (var item in scope ?? repository.Search())
  {
   token.ThrowIfCancellationRequested();
   if (item.Kind == MediaKind.Audio || kind is { } only && item.Kind != only || item.Kind == MediaKind.Video && tools == null) continue;
   try
   {
    var file = new FileInfo(item.Path);
    if (file.Exists && (!stamps.TryGetValue(item.Id, out var stamp) || stamp != PerceptualHash.Stamp(file))) result.Add(item);
   }
   catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
  }
  return result;
 }

 internal async Task ComputeAsync(IReadOnlyList<MediaItem> work, CancellationToken token)
 {
  Total = work.Count;
  var processes = repository.GetPreferences().Previews.GeneratorProcesses;
  async Task One(MediaItem item, CancellationToken cancel)
  {
   try
   {
    var stamp = PerceptualHash.Stamp(new FileInfo(item.Path));
    IReadOnlyList<ulong> hashes;
    if (item.Kind == MediaKind.Image) hashes = [ImageFingerprints.Hash(item.Path)];
    else
    {
     var duration = item.DurationSeconds is > 0 and var known ? known : (await tools!.ProbeAsync(item.Path, cancel)).DurationSeconds ?? 0;
     hashes = duration > 0 ? await tools!.VideoHashesAsync(item.Path, PerceptualHash.VideoPositions(duration), cancel) : [];
    }
    if (hashes.Count > 0) repository.SaveFingerprint(new(item.Id, stamp, hashes)); else Interlocked.Increment(ref failed);
   }
   catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ImageMagick.MagickException or ArgumentException)
   { Interlocked.Increment(ref failed); }
   Interlocked.Increment(ref done);
  }
  await Task.Run(() => Parallel.ForEachAsync(work.Where(i => i.Kind == MediaKind.Image),
   new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = token }, async (item, cancel) => await One(item, cancel)), token);
  await Task.Run(() => Parallel.ForEachAsync(work.Where(i => i.Kind == MediaKind.Video),
   new ParallelOptions { MaxDegreeOfParallelism = processes, CancellationToken = token }, async (item, cancel) => await One(item, cancel)), token);
  if (Failed > 0) ErrorLog.Write("Ähnliche Medien", new IOException($"{Failed} Dateien ohne Fingerabdruck"));
 }

 internal string Progress(DateTime started)
 {
  var finished = Done;
  var left = finished == 0 ? "" : T(" · about {0} left", MediaCard.DurationText((DateTime.UtcNow - started).TotalSeconds / finished * (Total - finished)));
  return T("Fingerprints · {0:N0} of {1:N0}", finished, Total) + left;
 }
}
