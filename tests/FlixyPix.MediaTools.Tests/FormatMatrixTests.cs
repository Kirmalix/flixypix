using System.Diagnostics;
using System.Security.Cryptography;
using FlixyPix.MediaTools;
using Xunit;

namespace FlixyPix.MediaTools.Tests;

/// <summary>Representative container/codec pairs, not an extension-only or Cartesian compatibility claim.
/// Slow: every pair is played in real time, and the end position depends on the audio device of the machine.</summary>
[Trait("Category", "Slow")]
public sealed class FormatMatrixTests : IDisposable
{
 private readonly string scratch = Path.Combine(Path.GetTempPath(),"flixypix-format-matrix-" + Guid.NewGuid().ToString("N"));
 private readonly string ffmpeg;
 private readonly MediaToolService tools;
 public FormatMatrixTests()
 {
  Directory.CreateDirectory(scratch); var root = new DirectoryInfo(AppContext.BaseDirectory);
  while (root is not null && !File.Exists(Path.Combine(root.FullName,"global.json"))) root = root.Parent;
  if (root is null) throw new InvalidOperationException("Cannot locate toolchain root.");
  var media = Path.Combine(root.FullName,".tools","media");
  ffmpeg = Directory.GetFiles(Path.Combine(media,"ffmpeg"),"ffmpeg.exe",SearchOption.AllDirectories).Single();
  tools = new(new(ffmpeg,Directory.GetFiles(Path.Combine(media,"ffmpeg"),"ffprobe.exe",SearchOption.AllDirectories).Single(),
   Path.Combine(media,"mpv","mpv.exe"),HeadlessPlayback:true));
 }

 [Theory]
 [InlineData("mp4","libx264","h264")]
 [InlineData("mp4","libx265","hevc")]
 [InlineData("mkv","libaom-av1","av1")]
 [InlineData("webm","libvpx-vp9","vp9")]
 [InlineData("avi","mpeg4","mpeg4")]
 [InlineData("mov","mjpeg","mjpeg")]
 [InlineData("mpg","mpeg2video","mpeg2video")]
 [InlineData("ts","libx264","h264")]
 [InlineData("m2ts","libx264","h264")]
 [InlineData("flv","flv","flv1")]
 [InlineData("wmv","wmv2","wmv2")]
 [InlineData("3gp","h263","h263")]
 public async Task RepresentativeVideoPairsProbeThumbnailAndDecodeInMpv(string extension,string encoder,string expectedCodec)
 {
  var path = Path.Combine(scratch,$"Bilder & Grüße-{encoder}.{extension}");
  var arguments = new List<string> { "-f","lavfi","-i","testsrc2=size=128x96:rate=25","-t","1","-an","-c:v",encoder,"-threads","1" };
  if (encoder == "libx264") arguments.AddRange(["-preset","ultrafast","-pix_fmt","yuv420p"]);
  if (encoder == "libx265") arguments.AddRange(["-preset","ultrafast","-x265-params","pools=none:frame-threads=1:log-level=error","-pix_fmt","yuv420p"]);
  if (encoder == "libaom-av1") arguments.AddRange(["-cpu-used","8","-crf","40","-row-mt","0","-pix_fmt","yuv420p"]);
  if (encoder == "libvpx-vp9") arguments.AddRange(["-deadline","realtime","-cpu-used","8","-b:v","100k","-pix_fmt","yuv420p"]);
  if (encoder == "mjpeg") arguments.AddRange(["-pix_fmt","yuvj420p"]);
  if (extension == "m2ts") arguments.AddRange(["-f","mpegts","-mpegts_m2ts_mode","1"]);
  arguments.Add(path); await Generate(arguments);
  var original = SHA256.HashData(File.ReadAllBytes(path)); var probe = await tools.ProbeAsync(path);
  Assert.Equal(expectedCodec,probe.VideoCodec); Assert.Equal(128,probe.Width); Assert.Equal(96,probe.Height);
  Assert.InRange(probe.DurationSeconds ?? 0,0.7,1.3);
  var thumb = Path.Combine(scratch,"preview.png"); await tools.CreateThumbnailAsync(path,thumb,0.3);
  Assert.Equal(new byte[] {137,80,78,71,13,10,26,10},File.ReadAllBytes(thumb)[..8]);
  await VerifyPlayback(path,"video");
  Assert.Equal(original,SHA256.HashData(File.ReadAllBytes(path)));
 }

 [Theory]
 [InlineData("mp3","libmp3lame","mp3")]
 [InlineData("flac","flac","flac")]
 [InlineData("wav","pcm_s16le","pcm_s16le")]
 [InlineData("ogg","libvorbis","vorbis")]
 [InlineData("m4a","aac","aac")]
 [InlineData("wma","wmav2","wmav2")]
 [InlineData("opus","libopus","opus")]
 [InlineData("aiff","pcm_s16be","pcm_s16be")]
 public async Task RepresentativeAudioPairsProbeAndDecodeInMpv(string extension,string encoder,string expectedCodec)
 {
  var path = Path.Combine(scratch,$"Klang & Grüße.{extension}");
  await Generate(["-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","1","-vn","-c:a",encoder,"-threads","1",path]);
  var original = SHA256.HashData(File.ReadAllBytes(path)); var probe = await tools.ProbeAsync(path);
  Assert.Equal(expectedCodec,probe.AudioCodec); Assert.Null(probe.VideoCodec); Assert.InRange(probe.DurationSeconds ?? 0,0.7,1.3);
  await VerifyPlayback(path,"audio"); Assert.Equal(original,SHA256.HashData(File.ReadAllBytes(path)));
 }

 private async Task VerifyPlayback(string path,string expectedTrack)
 {
  await using var player = await tools.PlayAsync(path);
  await player.PauseAsync(true); await player.SeekAsync(0.3);
  Assert.Contains(await player.GetTracksAsync(),track => track.Type == expectedTrack);
  await player.PauseAsync(false);
  var position = await player.GetPositionAsync();
  // mpv acknowledges seek before its decoder completes the asynchronous seek.
  for (var attempt = 0; attempt < 50 && (position is null || position < 0.15 || position > 0.65); attempt++)
  { await Task.Delay(20); position = await player.GetPositionAsync(); }
  Assert.NotNull(position); Assert.InRange(position.Value,0.15,0.65);
  using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); await player.WaitForEndAsync(timeout.Token);
  Assert.InRange((await player.GetPositionAsync()) ?? 0,0.7,1.4);
 }

 private async Task Generate(IEnumerable<string> arguments)
 {
  var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
  foreach (var argument in new[] {"-hide_banner","-loglevel","error","-nostdin","-n"}.Concat(arguments)) start.ArgumentList.Add(argument);
  using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
  using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
  try { await process.WaitForExitAsync(timeout.Token); }
  catch { if (!process.HasExited) process.Kill(entireProcessTree:true); throw; }
  Assert.True(process.ExitCode == 0,await stderr); await stdout;
 }

 public void Dispose() => Directory.Delete(scratch,true);
}
