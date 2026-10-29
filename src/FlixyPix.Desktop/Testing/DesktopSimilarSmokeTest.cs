using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlixyPix.Core;
using ImageMagick;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Similar items: a smaller, recompressed copy of a picture is found with the original, the original is suggested
 /// for keeping, and keeping it removes the copy from the catalog after carrying its tag over; the file stays.</summary>
 private async Task RunSimilarSmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  var folder = Path.Combine(directory, "similar"); Directory.CreateDirectory(folder);
  var original = Path.Combine(folder, "original.png"); var copy = Path.Combine(folder, "kopie.jpg"); var other = Path.Combine(folder, "anders.png");
  using (var image = new MagickImage("plasma:fractal", 640, 480)) image.Write(original);
  using (var image = new MagickImage(original)) { image.Resize(320, 240); image.Quality = 60; image.Write(copy, MagickFormat.Jpeg); }
  using (var image = new MagickImage("gradient:green-white", 640, 480)) { image.Rotate(45); image.Write(other); }
  MediaItem Add(string path, int width, int height, List<string> tags) =>
   _repository.Upsert(new MediaItem { Path = path, Title = Path.GetFileNameWithoutExtension(path), Kind = MediaKind.Image, Width = width, Height = height, FileSize = new FileInfo(path).Length, Tags = tags });
  var kept = Add(original, 640, 480, []); var duplicate = Add(copy, 320, 240, ["Ähnlich-Test"]); var different = Add(other, 640, 480, []);
  var window = new SimilarMediaWindow(this, _repository, ToolExists(_settings.Ffmpeg, "ffmpeg") ? _tools : null, RemoveDuplicate);
  window.Show();
  try
  {
   window.Choose(MediaKind.Image, SimilarityLevel.Similar);
   await window.SearchAsync(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   var group = window.Groups.SingleOrDefault(g => g.Items.Any(i => i.Id == kept.Id));
   Require(group != null && group.Items.Any(i => i.Id == duplicate.Id) && group.Items.All(i => i.Id != different.Id) && group.Keep == kept.Id,
    "The recompressed copy was not grouped with its original, or the original was not suggested for keeping.");
   Require(window.Computed >= 3 && _repository.GetFingerprintStamps().ContainsKey(different.Id), "Fingerprints were not computed and stored.");
   window.ShowGroup(group!); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Require(window.CardCount == group!.Items.Count, "The group is not shown side by side.");
   await window.PicturesReady; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Capture((FrameworkElement)window.Content, Path.Combine(directory, "similar-items.png"));
   window.Resolve(deleteFiles: false, ask: false);
   Require(_repository.GetMedia(duplicate.Id) == null && File.Exists(copy) && _repository.GetMedia(kept.Id)!.Tags.Contains("Ähnlich-Test"),
    "Keeping the original did not remove the copy from the catalog with its tag carried over, or touched the file.");
   // A second search computes nothing again.
   var before = window.Computed; await window.SearchAsync();
   Require(window.Computed == before, "Unchanged files were fingerprinted again.");
  }
  finally
  {
   window.Close();
   foreach (var id in new[] { kept.Id, different.Id }) _repository.Delete(id);
  }
  await RunFingerprintsOnImportSmokeTestAsync(folder);
 }

 /// <summary>"Find similar items on import": off, an import leaves the fingerprint to the next search; on, the import
 /// computes it.</summary>
 private async Task RunFingerprintsOnImportSmokeTestAsync(string folder)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  var before = _repository.GetPreferences().Previews; var settings = _settings;
  // Previews stay off, so only this option works after the import (and no preview report waits for a click).
  _settings = _settings with { AutoGeneratePreviews = false };
  try
  {
   foreach (var on in new[] { false, true })
   {
    _repository.SavePreviewPreferences(before with { FingerprintsOnImport = on });
    var sub = Path.Combine(folder, on ? "import-on" : "import-off"); Directory.CreateDirectory(sub);
    var path = Path.Combine(sub, "bild.png");
    using (var image = new MagickImage(on ? "plasma:red-blue" : "plasma:green-yellow", 200, 150)) image.Write(path);
    await ImportPaths([sub]);
    var item = _repository.Search().Single(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
    Require(_repository.GetFingerprintStamps().ContainsKey(item.Id) == on,
     on ? "The import did not compute the fingerprint although the setting is on." : "The import computed a fingerprint although the setting is off.");
    _repository.Delete(item.Id);
   }
  }
  finally { _repository.SavePreviewPreferences(before); _settings = settings; }
 }
}
