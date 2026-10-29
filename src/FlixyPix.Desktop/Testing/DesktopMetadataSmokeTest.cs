using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlixyPix.Core;
using ImageMagick;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Metadata of the files: an image with IPTC data and a video with an XMP sidecar appear in the preview and are
 /// taken over; the sidecar export leaves the image untouched; the Windows properties of a real MP4 are read.</summary>
 private async Task RunMetadataSmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  var folder = Path.Combine(directory, "metadata"); Directory.CreateDirectory(folder);
  var imagePath = Path.Combine(folder, "abend.jpg");
  using (var picture = new MagickImage(new MagickColor("navy"), 40, 30))
  {
   var iptc = new IptcProfile(); iptc.SetValue(IptcTag.Title, "Abend am Meer"); iptc.SetValue(IptcTag.Keyword, "Meer"); picture.SetProfile(iptc);
   picture.Write(imagePath, MagickFormat.Jpeg);
  }
  var videoPath = Path.Combine(folder, "reise.mp4"); File.WriteAllBytes(videoPath, [0]);
  File.WriteAllText(videoPath + ".xmp", """
   <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
    <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmlns:lr="http://ns.adobe.com/lightroom/1.0/" xmp:Rating="5">
     <lr:hierarchicalSubject><rdf:Bag><rdf:li>Personen|Max Roth</rdf:li></rdf:Bag></lr:hierarchicalSubject>
    </rdf:Description></rdf:RDF></x:xmpmeta>
   """);
  var image = _repository.Upsert(new MediaItem { Path = imagePath, Title = "abend", Kind = MediaKind.Image });
  var video = _repository.Upsert(new MediaItem { Path = videoPath, Title = "reise", Kind = MediaKind.Video });
  try
  {
   var plan = _repository.PlanMetadataImport([image.Id, video.Id], new DesktopMetadataReader());
   var preview = new MetadataPreviewWindow(this, plan, "2 ausgewählte Medien"); preview.Show();
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Require(preview.RowCount == 2 && preview.Chosen.Single(c => c.MediaId == image.Id).Title == "Abend am Meer"
    && preview.Chosen.Single(c => c.MediaId == video.Id) is { Rating: 10 } change && change.Tags["Personen"].Single() == "Max Roth", "The metadata preview did not list both changes.");
   Capture((FrameworkElement)preview.Content, Path.Combine(directory, "metadata-preview.png"));
   var chosen = preview.Chosen; preview.Close();
   Require(_repository.ApplyMetadataImport(chosen) == 2 && _repository.GetMedia(image.Id)!.Tags.Contains("Meer"), "The metadata were not taken over.");

   var before = File.ReadAllBytes(imagePath);
   var export = _repository.ExportSidecars([image.Id]);
   Require(export.Written == 1 && File.Exists(imagePath + ".xmp") && before.AsSpan().SequenceEqual(File.ReadAllBytes(imagePath)), "The sidecar export failed or touched the image.");

   if (ToolExists(_settings.Ffmpeg, "ffmpeg"))
   {
    var tagged = Path.Combine(folder, "titel.mp4");
    var start = new System.Diagnostics.ProcessStartInfo(_settings.Ffmpeg ?? Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"))
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i", "testsrc=size=64x36:rate=5", "-t", "1", "-c:v", "libx264",
     "-pix_fmt", "yuv420p", "-metadata", "title=Titel aus den Eigenschaften", tagged }) start.ArgumentList.Add(argument);
    using var encoder = System.Diagnostics.Process.Start(start)!;
    var error = encoder.StandardError.ReadToEndAsync(); var output = encoder.StandardOutput.ReadToEndAsync();
    await encoder.WaitForExitAsync(); Require(encoder.ExitCode == 0, await error); await output;
    Require(new DesktopMetadataReader().Read(tagged, MediaKind.Video)?.Title == "Titel aus den Eigenschaften", "The Windows properties of an MP4 were not read.");
   }
  }
  finally { _repository.Delete(image.Id); _repository.Delete(video.Id); }
 }
}
