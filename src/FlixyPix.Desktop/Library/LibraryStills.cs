using System.IO;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Pictures taken from videos in the player: each saved picture joins the library as an image that remembers its
/// video and the time in it, and a marked range is saved as a series of pictures by FFmpeg, as an operation of the
/// library with progress and cancel.</summary>
public partial class MainWindow
{
 private void ConnectStills(VideoPlayerWindow window, MediaItem item)
 {
  window.StillSaved = still => { ImportStills(item, [still]); return Task.CompletedTask; };
  if (ToolExists(_settings.Ffmpeg, "ffmpeg")) window.StillsExportRequested = (source, range, rate, folder) => ExportStillsAsync(item, source, range, rate, folder);
 }

 /// <summary>Takes saved pictures into the library (setting "Add pictures to the library") and prepares them like any
 /// new medium; the view shows them once that is done.</summary>
 internal IReadOnlyList<MediaItem> ImportStills(MediaItem video, IReadOnlyList<StillFile> stills)
 {
  if (_closing || stills.Count == 0 || !_repository.GetPreferences().Playback.Screenshots.AddToLibrary) return [];
  IReadOnlyList<MediaItem> added;
  try { added = _repository.AddStills(video.Id, stills); }
  catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { Status.Text = error.Message; return []; }
  if (added.Count > 0) QueueMediaPreparation(added);
  return added;
 }

 /// <summary>Saves the frames of a range as pictures and takes them into the library.</summary>
 /// <returns>What the player tells the user: how many pictures went where, or why none.</returns>
 internal async Task<string> ExportStillsAsync(MediaItem video, string source, StillRange range, double framesPerSecond, string folder)
 {
  if (_operation != null) return T("An operation is already running. You can cancel it below.");
  var title = T("Save pictures from the video"); var message = T("Operation cancelled");
  await RunOperation(title, async token =>
  {
   try
   {
    var screenshots = _repository.GetPreferences().Playback.Screenshots; var expected = range.Validate(framesPerSecond).Count(framesPerSecond);
    var progress = new Progress<int>(done => Status.Text = title + $" · {done:N0} / {expected:N0}");
    var stills = await _tools.ExportStillsAsync(source, folder, screenshots.NameFor(video.Title), range, framesPerSecond, screenshots.Extension, progress, token);
    ImportStills(video, stills);
    message = Tn(stills.Count, "1 picture saved · ", "{0:N0} pictures saved · ") + folder;
   }
   catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
   { ErrorLog.Write("Stills", error); message = error.Message; }
  }, refresh: false);
  return message;
 }

 /// <summary>The video an image was taken from, if the library still has it.</summary>
 private (MediaItem Video, double Seconds)? StillSource(MediaItem item) =>
  Stills.SourceOf(item) is { } origin && _repository.GetMedia(origin.VideoId) is { } video ? (video, origin.Seconds) : null;
}
