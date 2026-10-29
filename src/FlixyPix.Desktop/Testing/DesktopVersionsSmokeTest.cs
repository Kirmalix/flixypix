using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Package P6: the import reads the technical data, badges show it, the filter bar and sorting use it, reading it
 /// later fills gaps; two videos become one item with two files, the details list them, the main file changes and a version
 /// becomes an item of its own again.</summary>
 private async Task RunVersionsSmokeTestAsync(string directory, Guid videoId)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  async Task Settled() { await _lastRefresh; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
  var video = _repository.GetMedia(videoId)!;
  Require(video.Technical is { VideoCodec: "h264" } technical && technical.FrameRate is > 9.9 and < 10.1, $"The import did not read the technical data ({video.Technical?.VideoCodec}, {video.Technical?.FrameRate}).");

  // A second, larger encoding of the same kind of picture, imported from a folder of its own.
  var folder = Path.Combine(directory, "versions"); Directory.CreateDirectory(folder);
  var larger = Path.Combine(folder, "synthetic-video-720p.mp4");
  var start = new System.Diagnostics.ProcessStartInfo(_settings.Ffmpeg ?? Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"))
  { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
  foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i", "testsrc=size=1280x720:rate=60", "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", larger })
   start.ArgumentList.Add(argument);
  using (var encoder = System.Diagnostics.Process.Start(start)!)
  {
   var error = encoder.StandardError.ReadToEndAsync(); var output = encoder.StandardOutput.ReadToEndAsync();
   await encoder.WaitForExitAsync(); Require(encoder.ExitCode == 0, await error); await output;
  }
  await ImportPaths([larger]);
  var second = _repository.GetMediaByPath(larger) ?? throw new InvalidOperationException("The second video was not imported.");
  Require(second.Technical?.FrameRate is > 59 and < 61 && second.Height == 720, "The second video lacks its technical data.");
  var badgesBefore = MediaCard.Display;
  try
  {
   MediaCard.Display = badgesBefore with { TileBadges = [TileBadge.Resolution, TileBadge.FrameRate, TileBadge.Codec, TileBadge.Versions] };
   var badges = new MediaCard(second, false).Badges;
   Require(badges.SequenceEqual(["720p", TechnicalLabels.FrameRate(60), "H.264"]), "The tile badges are wrong: " + string.Join(", ", badges));
   MediaCard.Display = badgesBefore with { TileBadges = [] };
   Require(new MediaCard(second, false).Badges.Count == 0, "Switched-off badges were shown.");
  }
  finally { MediaCard.Display = badgesBefore; }

  // Filter bar and sorting.
  ApplyFilter(new MediaFilter { MinFrameRate = 60 }); await Settled();
  Require(_total == 1, $"The frame rate filter found {_total} items.");
  ApplyFilter(new MediaFilter { VideoCodecs = ["h264"] }); await Settled();
  var h264 = _total;
  SortChoice.SelectedIndex = 9; await Settled();
  Require(MediaList.Items.Cast<MediaCard>().First().Item.Id == second.Id, "Sorting by resolution did not put the 720p video first.");
  SortChoice.SelectedIndex = 0; ApplyFilter(new MediaFilter()); await Settled();

  // Reading later fills what is missing.
  _repository.Upsert(_repository.GetMedia(second.Id)! with { Technical = null });
  var (read, errors) = await ReadTechnicalDataAsync(null, CancellationToken.None);
  Require(read >= 1 && errors.Count == 0 && _repository.GetMedia(second.Id)!.Technical?.VideoCodec == "h264", "Reading the technical data later did not fill the gap.");

  // Two files, one item: the details list both, the filters count one item.
  var merged = CombineAsVersions(video, [video, second]) ?? throw new InvalidOperationException("Combining as versions failed.");
  await RefreshAsync(video.Id); await Settled();
  Require(merged.Files.Count == 1 && _repository.GetMedia(second.Id) == null && VersionsSection.Visibility == Visibility.Visible && VersionRows.Children.Count == 2,
   "The details do not list the two files.");
  Require(new MediaCard(merged, false).Badges.Contains(Tn(2, "1 file", "{0} files")), "The tile does not show the two files.");
  ApplyFilter(new MediaFilter { VideoCodecs = ["h264"] }); await Settled();
  Require(_total == h264 - 1, "The combined item still counts twice.");
  ApplyFilter(new MediaFilter { MultipleFiles = true }); await Settled();
  Require(_total == 1, "The filter for several files did not find the item.");
  ApplyFilter(new MediaFilter()); await RefreshAsync(video.Id); await Settled();
  VersionsSection.BringIntoView(); await Settled();
  Capture(RootContent, Path.Combine(directory, "versions-details.png"));
  Require(MediaVersions.PlaybackPath(merged, preferBest: true, File.Exists) == larger, "The best version is not the 720p file.");

  // Main file and split.
  MakeMainVersion(merged, merged.Files[0]); await Settled();
  Require(_repository.GetMedia(video.Id)!.Path == larger, "The version did not become the main file.");
  MakeMainVersion(_repository.GetMedia(video.Id)!, _repository.GetMedia(video.Id)!.Files[0]); await Settled();
  var split = await SplitVersionAsync(_repository.GetMedia(video.Id)!, _repository.GetMedia(video.Id)!.Files[0]) ?? throw new InvalidOperationException("Splitting off failed.");
  Require(split.Path == larger && _repository.GetMedia(video.Id)!.Files.Count == 0 && File.Exists(_repository.GetMedia(split.Id)?.ThumbnailPath),
   "The split-off version is no item of its own with a thumbnail.");
  _repository.Delete(split.Id);
  await RefreshAsync(video.Id); await Settled();
 }
}
