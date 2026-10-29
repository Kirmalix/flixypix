using System.Diagnostics;
using System.IO;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

/// <summary>Video thumbnails as contact sheets in the style chosen in the settings. Each generated thumbnail remembers the
/// style it was made with, so "regenerate" only redoes those that look different and can resume after a stop.</summary>
public partial class MainWindow
{
 internal const string SheetStyleKey = "thumbnail.style";

 /// <summary>Everything that changes how a sheet looks; header text comes from the medium itself.</summary>
 internal static string SheetSignature(PreviewPreferences p) => string.Join('|', "sheet-2", p.SheetColumns, p.SheetRows, p.SheetWidth, p.SheetFastSeek, p.SheetBorder,
  p.SheetHeader, p.SheetTimestamp, p.SheetBackground, p.SheetForeground, p.SheetFont, p.SheetSkipPercent);

 internal static ContactSheetStyle SheetStyle(PreviewPreferences p, MediaItem item) => new()
 {
  Columns = p.SheetColumns, Rows = p.SheetRows, Width = p.SheetWidth, FastSeek = p.SheetFastSeek, Border = p.SheetBorder,
  Background = p.SheetBackground, Foreground = p.SheetForeground, Timestamp = p.SheetTimestamp, SkipPercent = p.SheetSkipPercent,
  FontFile = p.SheetHeader || p.SheetTimestamp != SheetTimestamp.None ? FontFiles.FindOrDefault(p.SheetFont) : null,
  Header = p.SheetHeader ? HeaderLines(item) : [],
 };

 /// <summary>File name, then resolution, duration and size, as the original's header lines.</summary>
 private static IReadOnlyList<string> HeaderLines(MediaItem item)
 {
  var facts = new[]
  {
   item.Width is > 0 && item.Height is > 0 ? $"{item.Width} × {item.Height}" : null,
   item.DurationSeconds is > 0 and var seconds ? MediaCard.DurationText(seconds) : null,
   item.FileSize is { } bytes ? MediaCard.SizeText(bytes) : null,
  }.OfType<string>().ToList();
  return facts.Count == 0 ? [Path.GetFileName(item.Path)] : [Path.GetFileName(item.Path), string.Join(" · ", facts)];
 }

 private string ThumbnailFolder => Path.Combine(_settings.DataDirectory, "thumbnails");
 private bool IsGeneratedThumbnail(string? path) => path != null && path.StartsWith(ThumbnailFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

 /// <summary>Renders the sheet of one video in the current style. A stream whose keyframes cannot be reached falls back to an
 /// exact seek, and a video without a known duration gets a single early frame.</summary>
 private async Task<MediaItem> RenderVideoThumbnailAsync(MediaItem item, PreviewPreferences previews, CancellationToken token)
 {
  string Output() => Path.Combine(ThumbnailFolder, item.Id + "-" + Guid.NewGuid().ToString("N") + ".jpg");
  var duration = item.DurationSeconds.GetValueOrDefault();
  string path;
  if (duration > 0)
  {
   var aspect = item.Width is > 0 && item.Height is > 0 ? (double)item.Width.Value / item.Height.Value : 16d / 9;
   var style = SheetStyle(previews, item);
   try { path = await _tools.CreateContactSheetAsync(item.Path, Output(), duration, aspect, style, token); }
   catch (IOException) when (style.FastSeek)
   {
    try { path = await _tools.CreateContactSheetAsync(item.Path, Output(), duration, aspect, style with { FastSeek = false }, token); }
    catch (IOException) { path = await _tools.CreateThumbnailAsync(item.Path, Output(), Math.Min(duration / 10, 30), token); }
   }
   catch (IOException) { path = await _tools.CreateThumbnailAsync(item.Path, Output(), Math.Min(duration / 10, 30), token); }
  }
  else path = await _tools.CreateThumbnailAsync(item.Path, Output(), 0, token);
  return item with { ThumbnailPath = path, Extra = new(item.Extra) { [SheetStyleKey] = SheetSignature(previews) } };
 }

 /// <summary>Remakes video thumbnails in the background: the chosen media always, otherwise every video whose thumbnail was
 /// made in another style. Chosen thumbnails stay when the settings say so; a stopped run continues where it ended.</summary>
 /// <param name="folder">Only videos in this folder and its subfolders; like a selection, they are remade whatever their style.</param>
 internal async Task<(int Made, int Checked)> RegenerateThumbnailsAsync(IReadOnlyList<MediaItem>? chosen, CancellationToken token, string? folder = null)
 {
  var previews = _repository.GetPreferences().Previews; var signature = SheetSignature(previews);
  var canProbe = ToolExists(_settings.Ffprobe, "ffprobe");
  if (!ToolExists(_settings.Ffmpeg, "ffmpeg")) throw new InvalidOperationException(T("FFmpeg is missing. Its path can be set under Settings → Tools."));
  Directory.CreateDirectory(ThumbnailFolder);
  var scope = new MediaQuery { Kind = MediaKind.Video, Folder = folder, IncludeSubfolders = true };
  var force = chosen != null || folder != null;
  var total = chosen?.Count ?? (int)_repository.QueryPage(scope with { Limit = 1 }).TotalCount;
  int seen = 0, made = 0; var errors = new List<string>(); var watch = Stopwatch.StartNew();
  async IAsyncEnumerable<MediaItem> Candidates()
  {
   if (chosen != null) { foreach (var item in chosen) yield return item; yield break; }
   await foreach (var item in StreamCatalog(scope, token)) yield return item;
  }
  await Parallel.ForEachAsync(Candidates(), new ParallelOptions { MaxDegreeOfParallelism = previews.GeneratorProcesses, CancellationToken = token }, async (item, itemToken) =>
  {
   var number = Interlocked.Increment(ref seen);
   var current = _repository.GetMedia(item.Id) ?? item;
   var due = current.Kind == MediaKind.Video && File.Exists(current.Path)
    && !(previews.SkipWhenCustomThumbnail && current.Extra.ContainsKey("thumbnail.assetId"))
    && (force || !current.Extra.TryGetValue(SheetStyleKey, out var style) || style != signature || !File.Exists(current.ThumbnailPath));
   if (due)
   {
    try
    {
     if (current.DurationSeconds is null && canProbe)
     {
      var probe = await _tools.ProbeAsync(current.Path, itemToken);
      current = current with { DurationSeconds = probe.DurationSeconds, Width = current.Width ?? probe.Width, Height = current.Height ?? probe.Height };
     }
     var updated = await RenderVideoThumbnailAsync(current, previews, itemToken);
     _repository.Upsert(updated);
     if (IsGeneratedThumbnail(current.ThumbnailPath) && current.ThumbnailPath != updated.ThumbnailPath) File.Delete(current.ThumbnailPath!);
     Interlocked.Increment(ref made);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
    { lock (errors) errors.Add(Path.GetFileName(current.Path) + ": " + error.Message); }
   }
   var left = watch.Elapsed / number * Math.Max(0, total - number);
   _ = Dispatcher.InvokeAsync(() => Status.Text = T("Thumbnails · {0:N0} of {1:N0} checked · {2:N0} new", number, total, made) + (number > 8 && left > TimeSpan.FromSeconds(5) ? T(" · about {0} left", Remaining(left)) : ""));
  });
  if (errors.Count > 0) Dialogs.Text(this, T("Thumbnails"), string.Join(Environment.NewLine, errors.Take(30)) + (errors.Count > 30 ? Environment.NewLine + Tn(errors.Count - 30, "1 more file.", "{0} more files.") : ""));
  return (made, seen);
 }

 private static string Remaining(TimeSpan left) => left.TotalMinutes >= 1.5 ? T("{0:0} min", Math.Round(left.TotalMinutes)) : $"{Math.Round(left.TotalSeconds):0} s";

 private async void RegenerateSelectedThumbnails()
 {
  var items = SelectedMedia.Where(i => i.Kind == MediaKind.Video).ToList();
  if (items.Count == 0) { Status.Text = T("Please select one or more videos."); return; }
  await RunOperation(T("Recreate thumbnails"), async token => { var (made, _) = await RegenerateThumbnailsAsync(items, token); Status.Text = Tn(made, "1 thumbnail recreated", "{0:N0} thumbnails recreated"); });
 }

 private async void RegenerateFolderThumbnails(string folder)
 {
  await RunOperation(T("Recreate thumbnails in the folder"), async token =>
  {
   var (made, seen) = await RegenerateThumbnailsAsync(null, token, folder);
   Status.Text = T("{0:N0} of {1:N0} video thumbnails in “{2}” recreated", made, seen, Path.GetFileName(folder.TrimEnd('\\')));
  });
 }

 private async void RegenerateAllThumbnails()
 {
  await RunOperation(T("Adjust thumbnails"), async token =>
  {
   var (made, seen) = await RegenerateThumbnailsAsync(null, token);
   Status.Text = made == 0 ? Tn(seen, "The video thumbnail matches the settings", "All {0:N0} video thumbnails match the settings") : T("{0:N0} of {1:N0} video thumbnails recreated", made, seen);
  });
 }

 /// <summary>Renders a sample sheet for the settings preview from the selected video or the first one in the library.</summary>
 internal async Task<string?> RenderSampleSheetAsync(PreviewPreferences previews, CancellationToken token)
 {
  if (!ToolExists(_settings.Ffmpeg, "ffmpeg")) return null;
  var sample = Selected is { Kind: MediaKind.Video, DurationSeconds: > 0 } selected && File.Exists(selected.Path) ? selected : await Task.Run(() =>
   _repository.QueryPage(new MediaQuery { Kind = MediaKind.Video, Limit = 50 }, token).Items.FirstOrDefault(i => i.DurationSeconds > 0 && File.Exists(i.Path)), token);
  if (sample is null) return null;
  var output = Path.Combine(Path.GetTempPath(), "flixypix-sample-" + Guid.NewGuid().ToString("N") + ".jpg");
  var aspect = sample.Width is > 0 && sample.Height is > 0 ? (double)sample.Width.Value / sample.Height.Value : 16d / 9;
  return await _tools.CreateContactSheetAsync(sample.Path, output, sample.DurationSeconds!.Value, aspect, SheetStyle(previews, sample), token);
 }
}
