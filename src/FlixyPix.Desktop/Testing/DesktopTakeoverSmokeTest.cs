using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Takeover assistant against a synthetic installation: it is found, a new library is built from its catalog,
 /// thumbnails, bookmark pictures and the chosen settings group are copied, and the installation stays byte for byte the same.</summary>
 private async Task RunTakeoverSmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  static byte[] Picture(Color color)
  {
   var pixels = Enumerable.Repeat(new[] { color.B, color.G, color.R, (byte)255 }, 160 * 90).SelectMany(p => p).ToArray();
   var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(160, 90, 96, 96, PixelFormats.Bgra32, null, pixels, 160 * 4)));
   using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
  }
  var root = Path.Combine(directory, "takeover");
  var program = Path.Combine(root, "Programme", LegacyInstallations.FolderName);
  var cache = Path.Combine(program, "cache", "mmvdb.sdf");
  foreach (var folder in new[] { "vcache", "bcache" }) Directory.CreateDirectory(Path.Combine(cache, folder));
  File.WriteAllBytes(Path.Combine(program, LegacyInstallations.ProgramFile), [0x4D, 0x5A]);
  File.WriteAllBytes(Path.Combine(program, "mmvdb.sdf"), new byte[4096]);
  File.WriteAllBytes(Path.Combine(cache, "vcache", "1.jpg"), Picture(Colors.SteelBlue));
  File.WriteAllBytes(Path.Combine(cache, "vcache", "2.jpg"), Picture(Colors.IndianRed));
  File.WriteAllBytes(Path.Combine(cache, "vcache", "7.jpg"), Picture(Colors.Gray));
  File.WriteAllBytes(Path.Combine(cache, "bcache", "1_10.jpg"), Picture(Colors.Goldenrod));
  string Hashes() => string.Join(",", Directory.EnumerateFiles(program, "*", SearchOption.AllDirectories).Order().Select(f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));
  var before = Hashes();

  object Schema(string table, params string[] names) => new { type = "schema", table, columns = names.Select(name => new { name }) };
  object Row(string table, object values) => new { type = "row", table, values };
  var exchange = Path.Combine(root, "catalog.jsonl");
  object[] records = [new { type = "header", format = "flixypix-legacy", version = 1, sourceSha256 = new string('c', 64) },
   Schema("movies", "ID", "fullpath", "filename", "title"), Row("movies", new { ID = 1, fullpath = @"U:\offline\", filename = "eins.mp4", title = "Eins" }),
   Row("movies", new { ID = 2, fullpath = @"U:\offline\", filename = "zwei.mp4", title = "Zwei" }),
   Schema("videobookmarks", "ID", "videoid", "bookmarktime", "description"), Row("videobookmarks", new { ID = 10, videoid = 1, bookmarktime = 12, description = "Anfang" }),
   Schema("configsettings", "ID", "parameter", "value1"), Row("configsettings", new { ID = 1, parameter = "fullscreen", value1 = "True" }),
   Row("configsettings", new { ID = 2, parameter = "searchhiddenfolders", value1 = "True" }),
   new { type = "footer", rows = 5, tables = 3, tableRows = new Dictionary<string, int> { ["movies"] = 2, ["videobookmarks"] = 1, ["configsettings"] = 2 } }];
  File.WriteAllLines(exchange, records.Select(r => JsonSerializer.Serialize(r)));
  var mediaBefore = _repository.QueryPage(new() { Limit = 1 }).TotalCount;

  var search = new LegacyInstallationSearch { ProgramFolders = [program], LocalAppData = Path.Combine(root, "AppData") };
  var assistant = new TakeoverAssistantWindow(this, search, _repository, ThumbnailFolder, root,
   (installation, target, progress, token) => MigrateLegacyToNewLibraryAsync(exchange, target, "takeover-smoke", token, adoptAppSettings: false));
  assistant.Show();
  try
  {
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Require(assistant.Installations.Count == 1 && assistant.Installation?.Folder(LegacyDataGroup.VideoThumbnails)?.Files == 3, "The assistant did not find the synthetic installation.");
   Capture((FrameworkElement)assistant.Content, Path.Combine(directory, "takeover-start.png"));
   await assistant.ContinueAsync();
   Require(assistant.Step == "choice", "The catalog step did not lead to the choice.");
   var plan = assistant.Plan!;
   Require(plan.Group(LegacyDataGroup.VideoThumbnails) is { Adopt: 2, Orphans: 1 } && plan.Group(LegacyDataGroup.BookmarkImages) is { Adopt: 1 }, "The plan did not match the cache files.");
   Require(assistant.SettingChoices.Keys.Order().SequenceEqual([LegacySettingGroup.Player, LegacySettingGroup.General]) && assistant.SettingChoices.Values.All(c => c.IsChecked == true), "Settings groups were not offered.");
   assistant.SettingChoices[LegacySettingGroup.General].IsChecked = false; assistant.ExpandValues();
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Capture((FrameworkElement)assistant.Content, Path.Combine(directory, "takeover-choice.png"));
   await assistant.ApplyAsync();
   Require(assistant.Step == "report" && assistant.Report!.Data.Single(r => r.Group == LegacyDataGroup.VideoThumbnails).Adopted == 2, "The takeover did not copy the thumbnails.");
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Capture((FrameworkElement)assistant.Content, Path.Combine(directory, "takeover-report.png"));
   var created = assistant.CreatedLibrary ?? throw new InvalidOperationException("The new library was not reported.");
   assistant.Close();
   using var library = new CatalogRepository(Path.Combine(created.DirectoryPath, "catalog.db"));
   var media = library.QueryPage(new() { Limit = 10 }).Items;
   Require(media.All(m => m.ThumbnailPath is { } path && path.StartsWith(Path.Combine(created.DirectoryPath, "thumbnails"), StringComparison.OrdinalIgnoreCase) && File.Exists(path)), "Thumbnails were not copied into the new library.");
   Require(library.GetBookmarks().Single().PreviewAssetId != null, "The bookmark picture was not linked.");
   Require(library.GetPreferences().Playback.AlwaysFullscreen && !library.GetPreferences().General.IncludeHiddenFolders, "The settings groups were not honoured.");
   Require(Hashes() == before, "The takeover changed the installation.");
   Require(_repository.QueryPage(new() { Limit = 1 }).TotalCount == mediaBefore, "The takeover changed the open library.");
  }
  finally { if (assistant.IsVisible) assistant.Close(); }
 }
}
