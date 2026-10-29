using System.IO;
using System.Windows;
using FlixyPix.Archives;
using FlixyPix.Core;
using System.Runtime.CompilerServices;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private bool ToolExists(string? configured, string name) => File.Exists(string.IsNullOrWhiteSpace(configured)
  ? Path.Combine(AppContext.BaseDirectory, "tools", name + ".exe") : configured);

 private async Task PrepareMediaAsync(CancellationToken token)
 {
  var canProbe = ToolExists(_settings.Ffprobe, "ffprobe"); var canRender = ToolExists(_settings.Ffmpeg, "ffmpeg");
  var previews = _repository.GetPreferences().Previews;
  var errors = new List<string>();
  var toolsWarned = 0;
  var index = 0;
  // Up to the configured number of preview processes run side by side, as in the original thumbnail generator.
  await Parallel.ForEachAsync(StreamCatalogForImages(token), new ParallelOptions { MaxDegreeOfParallelism = previews.GeneratorProcesses, CancellationToken = token }, async (item, itemToken) =>
  {
   if(!File.Exists(item.Path) || !(item.Width==null || item.Kind!=MediaKind.Image && item.DurationSeconds==null ||
      item.Kind!=MediaKind.Audio && (string.IsNullOrEmpty(item.ThumbnailPath) || !File.Exists(item.ThumbnailPath))))return;
   if(item.Kind!=MediaKind.Image && (!canProbe || !canRender) && Interlocked.Exchange(ref toolsWarned,1)==0)
    lock(errors)errors.Add(T("Previews or media analysis are limited: FFmpeg/ffprobe are missing. The paths can be chosen in Settings."));
   var number = Interlocked.Increment(ref index);
   _ = Dispatcher.InvokeAsync(() => Status.Text = T("Preview · {0} · {1}", number, Path.GetFileName(item.Path)));
   try { _repository.Upsert(await PrepareItemAsync(item, previews, canProbe, canRender, itemToken)); }
   catch (OperationCanceledException) { throw; }
   catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
   { lock(errors)errors.Add(Path.GetFileName(item.Path) + ": " + error.Message); }
  });
  if (errors.Count > 0) Dialogs.Text(this, T("Preview and analysis report"), string.Join(Environment.NewLine, errors.Take(30)) +
   (errors.Count > 30 ? Environment.NewLine + Tn(errors.Count - 30, "1 more file could not be prepared.", "{0} more files could not be prepared.") : ""));
 }

 private async Task<MediaItem> PrepareItemAsync(MediaItem item, PreviewPreferences previews, bool canProbe, bool canRender, CancellationToken token)
 {
  var updated = item;
  if (item.Kind == MediaKind.Image)
  {
   var info = await DesktopImaging.Service.GetInfoAsync(item.Path, token);
   updated = updated with { Width = info.Width, Height = info.Height };
  }
  if (canProbe && (updated.Width == null || item.Kind != MediaKind.Image && item.DurationSeconds == null))
  {
   var probe = await _tools.ProbeAsync(item.Path, token);
   updated = updated with { Width = updated.Width ?? probe.Width, Height = updated.Height ?? probe.Height,
    DurationSeconds = updated.DurationSeconds ?? probe.DurationSeconds, Technical = item.Kind == MediaKind.Image ? null : TechnicalOf(item.Kind, probe) ?? updated.Technical };
   if (item.Kind == MediaKind.Audio && probe.VideoCodec != null && Path.GetExtension(item.Path).ToLowerInvariant() is ".ogg" or ".oga")
    updated = updated with { Kind = MediaKind.Video };
  }
  if (updated.Kind == MediaKind.Audio || !string.IsNullOrEmpty(item.ThumbnailPath) && File.Exists(item.ThumbnailPath)) return updated;
  // A chosen custom thumbnail is restored from its stored asset instead of being replaced.
  if (previews.SkipWhenCustomThumbnail && item.Extra.ContainsKey("thumbnail.assetId")) return updated;
  if (item.Kind == MediaKind.Image)
   return updated with { ThumbnailPath = await DesktopImaging.Service.GetPreviewPathAsync(item.Path,390,390,token) };
  if (!canRender) return updated;
  Directory.CreateDirectory(ThumbnailFolder);
  return await RenderVideoThumbnailAsync(updated, previews, token);
 }

 private IAsyncEnumerable<MediaItem> StreamCatalogForImages(CancellationToken token) => StreamCatalog(new MediaQuery(), token);

 private async IAsyncEnumerable<MediaItem> StreamCatalog(MediaQuery query, [EnumeratorCancellation] CancellationToken token)
 {
  for(var offset=0;;offset+=1000)
  {
   var page=await Task.Run(()=>_repository.QueryPage(query with { Offset=offset,Limit=1000 },token),token);
   foreach(var item in page.Items) { token.ThrowIfCancellationRequested(); yield return item; }
   if(offset+page.Items.Count>=page.TotalCount || page.Items.Count==0)yield break;
  }
 }

 private async Task<MediaItem> EnsureArchiveImageAsync(MediaItem item, CancellationToken token = default)
 {
  if (File.Exists(item.Path) || !item.Extra.TryGetValue("archivePath", out var archive) || string.IsNullOrWhiteSpace(archive) ||
   !item.Extra.TryGetValue("entryName", out var name) || string.IsNullOrWhiteSpace(name)) return item;
  var service = new ImageArchiveService(Path.Combine(_settings.DataDirectory, "archive-cache"));
  var entries = await Task.Run(() => service.Inspect(archive), token);
  var entry = entries.SingleOrDefault(e => e.EntryName.Equals(name, StringComparison.Ordinal))
   ?? throw new IOException(T("The image is missing from the original image archive: ") + name);
  if (item.Extra.TryGetValue("sha256", out var expected) && !string.IsNullOrEmpty(expected) && expected != entry.ArchiveSha256)
   throw new IOException(T("The image archive was changed. Please import it as a new image archive."));
  var restored = await service.ExtractAsync(entry, token);
  return _repository.Upsert(item with { Path = restored });
 }

 private async void RestoreArchiveCache()
 {
  await RunOperation(T("Restore image archive cache"), async token =>
  {
   await foreach (var item in StreamCatalogForImages(token))
   {
    if(File.Exists(item.Path) || !item.Extra.ContainsKey("archivePath"))continue;
    token.ThrowIfCancellationRequested(); Status.Text = T("Image archive · ") + item.Title;
    await EnsureArchiveImageAsync(item, token);
   }
  });
 }
}
