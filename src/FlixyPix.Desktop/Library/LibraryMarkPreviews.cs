using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

/// <summary>Small picture of a bookmark or scene from its PreviewAssetId (lists in the details and the player).</summary>
public sealed class MarkThumbnailConverter : IValueConverter
{
 /// <summary>Set by the main window: the picture for an asset, or null.</summary>
 internal static Func<Guid, bool, ImageSource?>? Resolve { get; set; }
 /// <summary>Parameter "large" decodes a bigger picture (hover enlargement), "has" answers whether there is a picture at all.</summary>
 public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
 {
  var picture = value is Guid id ? Resolve?.Invoke(id, parameter as string == "large") : null;
  return parameter as string == "has" ? picture != null : picture;
 }
 public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Tags of a bookmark or scene after its time, as " · Kuss, Tanz"; empty without tags.</summary>
public sealed class TagLineConverter : IValueConverter
{
 public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is IEnumerable<string> tags && tags.Any() ? " · " + string.Join(", ", tags) : "";
 public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Seconds as "m:ss" or "h:mm:ss".</summary>
public sealed class ClockConverter : IValueConverter
{
 public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double seconds ? MediaCard.DurationText(seconds) : "";
 public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Pictures for bookmarks and scenes: taken-over and chosen pictures stay, missing ones are made from the video in the
/// background when a video is shown or played, and a moved mark gets a new one.</summary>
public partial class MainWindow
{
 private readonly Dictionary<(Guid, bool), ImageSource?> _markThumbnails = [];
 /// <summary>The latest job per video; a new request runs after it, so marks added meanwhile are not missed.</summary>
 private readonly Dictionary<Guid, Task> _markJobs = [];
 /// <summary>Marks FFmpeg found no picture for this session (unreadable file, broken stream); they are not tried and logged
 /// again on every selection.</summary>
 private readonly HashSet<Guid> _markFailures = [];

 private void InitializeMarkPreviews() => MarkThumbnailConverter.Resolve = MarkThumbnail;

 private ImageSource? MarkThumbnail(Guid assetId) => MarkThumbnail(assetId, false);

 private ImageSource? MarkThumbnail(Guid assetId, bool large)
 {
  if (_markThumbnails.TryGetValue((assetId, large), out var known)) return known;
  ImageSource? picture = null;
  try
  {
   if (_repository.GetMediaAsset(assetId) is { Data.Length: > 0 } asset)
   {
    using var stream = new MemoryStream(asset.Data);
    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = large ? 480 : 192; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
    picture = bitmap;
   }
  }
  catch (Exception error) when (error is NotSupportedException or IOException or InvalidOperationException or ObjectDisposedException) { picture = null; }
  _markThumbnails[(assetId, large)] = picture;
  return picture;
 }

 /// <summary>Makes the missing pictures of a video's marks; afterwards the lists that show the video are refreshed.</summary>
 internal Task EnsureMarkPreviewsAsync(MediaItem item)
 {
  if (item.Kind != MediaKind.Video || _closing) return Task.CompletedTask;
  var previous = _markJobs.GetValueOrDefault(item.Id) ?? Task.CompletedTask;
  var job = AfterAsync(previous, item);
  _markJobs[item.Id] = job;
  return job;

  async Task AfterAsync(Task before, MediaItem media)
  {
   try { await before; } catch (Exception error) when (error is OperationCanceledException) { }
   await MakeMarkPreviewsAsync(media);
   if (_markJobs.TryGetValue(media.Id, out var latest) && latest.IsCompleted) _markJobs.Remove(media.Id);
  }
 }

 private async Task MakeMarkPreviewsAsync(MediaItem item)
 {
  if (_closing || !ToolExists(_settings.Ffmpeg, "ffmpeg") || !File.Exists(item.Path)) return;
  {
   // Marks after the end have no picture (the player leaves them out as well).
   var needs = (await Task.Run(() => _repository.GetMarkPreviewNeeds(item.Id)))
    .Where(n => !_markFailures.Contains(n.Id) && n.PositionSeconds >= 0 && (item.DurationSeconds is not > 0 || n.PositionSeconds < item.DurationSeconds)).ToList();
   if (needs.Count == 0) return;
   var folder = Path.Combine(_settings.DataDirectory, "hover-cache"); Directory.CreateDirectory(folder);
   var made = 0;
   string PathOf(MarkPreviewNeed need) => Path.Combine(folder, $"{item.Id:N}-mark-{need.Id:N}.jpg");
   foreach (var need in needs) try { File.Delete(PathOf(need)); } catch (IOException) { }
   // All pictures of the video in one FFmpeg run; only those it could not make are tried singly afterwards.
   var batched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   try { foreach (var frame in await _tools.CreateThumbnailsAsync(item.Path, needs.Select(n => new FrameRequest(Math.Max(0, n.PositionSeconds), PathOf(n))).ToList())) batched.Add(frame.Output); }
   catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException) { ErrorLog.Write($"Bilder für Sprungmarken · {item.Path}", error); }
   foreach (var need in needs)
   {
    if (_closing) return;
    var path = PathOf(need);
    try
    {
     if (!batched.Contains(path)) await _tools.CreateThumbnailAsync(item.Path, path, Math.Max(0, need.PositionSeconds));
     var bytes = await File.ReadAllBytesAsync(path);
     if (await Task.Run(() => _repository.SetGeneratedMarkPreview(need, bytes))) made++;
    }
    catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException or ArgumentException)
    {
     ErrorLog.Write($"Bild für Sprungmarke · {item.Path} bei {need.PositionSeconds:0.###} s", error); _markFailures.Add(need.Id);
    }
    finally { try { File.Delete(path); } catch (IOException) { } }
   }
   if (made == 0 || _closing) return;
   if (_detailsShown?.Id == item.Id)
   {
    Scenes.ItemsSource = _repository.GetOrderedScenes(item.Id); Bookmarks.ItemsSource = _repository.GetOrderedBookmarks(item.Id);
    ShowMarkTagFilters();
   }
   if (_videoWindow is { } window && window.MediaId == item.Id) window.RefreshMarks();
  }
 }
}
