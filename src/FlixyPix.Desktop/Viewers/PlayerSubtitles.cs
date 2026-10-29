using System.IO;
using FlixyPix.Core;
using FlixyPix.MediaTools;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

/// <summary>Subtitle files and their offset (original: "Select subtitle file", "Subtitle delay", "Remove subtitles").
/// mpv still loads a subtitle file of the same name by itself; a chosen file and the offset are remembered for the
/// medium and applied on the next playback.</summary>
internal sealed partial class VideoPlayerWindow
{
 internal const double SubtitleStep = 0.1;
 internal static readonly string SubtitleFilter = T("Subtitles|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.idx;*.sup|All files|*.*");
 internal double SubtitleDelay { get; private set; }

 /// <summary>Adds a subtitle file and selects it; without a path the user picks one beside the video.</summary>
 internal async Task LoadSubtitleFileAsync(string? path = null)
 {
  if (path == null)
  {
   var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose subtitle file"), Filter = SubtitleFilter, InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(media.Path)) };
   if (picker.ShowDialog(this) != true) return;
   path = picker.FileName;
  }
  await Command(async () =>
  {
   await RequirePlayer().AddSubtitleAsync(path, Path.GetFileName(path));
   await RefreshTracksAsync();
   RememberSubtitles(path, SubtitleDelay);
   Notify(T("Subtitles loaded: {0}", Path.GetFileName(path)));
  });
 }

 /// <summary>Moves the subtitles by <paramref name="steps"/> × 0.1 s (positive: later); 0 resets the offset.</summary>
 internal async Task ShiftSubtitlesAsync(int steps)
 {
  SubtitleDelay = steps == 0 ? 0 : Math.Round(Math.Clamp(SubtitleDelay + steps * SubtitleStep, -600, 600), 1);
  await Command(() => RequirePlayer().SetSubtitleDelayAsync(SubtitleDelay));
  RememberSubtitles(repository.GetMedia(media.Id)?.SubtitlePath, SubtitleDelay);
  Notify(T("Subtitle offset: {0} s", SubtitleDelay.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.CurrentCulture)));
 }

 private void RememberSubtitles(string? path, double delay)
 {
  if (repository.GetMedia(media.Id) is { } current && (current.SubtitlePath != path || current.SubtitleDelaySeconds != delay))
   repository.Upsert(current with { SubtitlePath = path, SubtitleDelaySeconds = delay });
 }

 /// <summary>Applies the remembered file (if it is still there) and offset when the player starts.</summary>
 private async Task RestoreSubtitlesAsync(MpvPlayer player)
 {
  if (repository.GetMedia(media.Id) is not { } current) return;
  if (current.SubtitlePath is { } path && File.Exists(path)) await player.AddSubtitleAsync(path, Path.GetFileName(path));
  if (current.SubtitleDelaySeconds != 0) { SubtitleDelay = current.SubtitleDelaySeconds; await player.SetSubtitleDelayAsync(SubtitleDelay); }
 }
}
