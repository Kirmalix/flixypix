using System.IO;
using System.Windows;
using FlixyPix.Archives;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private void CacheMenu() => Menu(new (string, Action)[] { (T("Restore missing archive images"), RestoreArchiveCache),
  (T("Create missing previews and metadata"), async () => await RunOperation(T("Prepare media"), PrepareMediaAsync)),
  (T("Adjust video thumbnails to the settings"), RegenerateAllThumbnails) }
  // A folder filter in the sidebar and each media folder offer to remake their videos' thumbnails.
  .Concat(_folder is { } folder ? [(T("Recreate thumbnails in “{0}”", Path.GetFileName(folder.TrimEnd('\\'))), (Action)(() => RegenerateFolderThumbnails(folder)))] : Array.Empty<(string, Action)>())
  .Concat(_repository.GetLibraryFolders().Select(f => (T("Recreate thumbnails in the media folder “{0}”", Path.GetFileName(f.Path.TrimEnd('\\'))), (Action)(() => RegenerateFolderThumbnails(f.Path)))))
  .Append((_settings.AutoGeneratePreviews ? T("Turn off automatic previews") : T("Turn on automatic previews"), (Action)(() =>
  { _settings = _settings with { AutoGeneratePreviews = !_settings.AutoGeneratePreviews }; SaveViewPreferences(); Status.Text = T("Preview setting saved"); })))
  .ToArray());

 private async void ImportImageArchive()
 {
  var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Import images from a ZIP/RAR archive"), Filter = T("Image archives|*.zip;*.rar"), Multiselect = true };
  if (picker.ShowDialog(this) != true) return;
  await RunOperation(T("Import image archives"), async token =>
  {
   var archives = new ImageArchiveService(Path.Combine(_settings.DataDirectory, "archive-cache"));
   var count = 0;
   foreach (var zip in picker.FileNames)
   {
    var entries = await Task.Run(() => archives.Inspect(zip), token);
    foreach (var entry in entries)
    {
     token.ThrowIfCancellationRequested(); Status.Text = T("Archive · {0} · {1}", ++count, entry.EntryName);
     var cachePath = await archives.ExtractAsync(entry, token);
     var existing = _repository.GetMediaByPath(cachePath);
     var extra = existing == null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(existing.Extra);
     extra["archivePath"] = entry.ArchivePath; extra["entryName"] = entry.EntryName; extra["sha256"] = entry.ArchiveSha256;
     var item = existing ?? new MediaItem { Path = cachePath, Kind = MediaKind.Image, Title = Path.GetFileNameWithoutExtension(entry.EntryName), FileSize = entry.UncompressedSize, Category = Path.GetFileNameWithoutExtension(zip) };
     _repository.Upsert(item with { Extra = extra });
    }
   }
   Status.Text = Tn(count, "1 archive image imported. The ZIP source files stay unchanged.", "{0} archive images imported. The ZIP source files stay unchanged.");
  });
 }
 private void ExportCsv()
 {
  var picker = new SaveFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Export catalog as CSV (new file)"), Filter = T("CSV catalog|*.csv"), FileName = $"FlixyPix-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
  if (picker.ShowDialog(this) == true) { CatalogInterchange.ExportCsv(_repository.Search(), picker.FileName); Status.Text = T("CSV catalog exported"); }
 }
 private async void ImportCsv()
 {
  var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Import CSV catalog"), Filter = T("CSV catalog|*.csv") };
  if (picker.ShowDialog(this) != true) return;
  await RunOperation(T("Import CSV catalog"), async token => { var result = await Task.Run(() => CatalogInterchange.ImportCsv(_repository, picker.FileName), token); ShowImportResult(result); });
 }
 private void ImportNfo()
 {
  if (Selected is not { } item) return;
  var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("NFO metadata for this item"), Filter = T("NFO / XML|*.nfo;*.xml") };
  if (picker.ShowDialog(this) != true) return;
  var metadata = CatalogInterchange.ReadNfo(picker.FileName);
  _repository.Upsert(CatalogInterchange.ApplyNfo(item, metadata)); Refresh(item.Id); Status.Text = T("NFO metadata imported");
 }
 /// <summary>Writes "&lt;video&gt;.nfo" beside the chosen videos (or all videos); existing NFO files are never replaced.</summary>
 private async void ExportNfoFiles()
 {
  var selection = SelectedMedia;
  await RunOperation(T("Write NFO files"), async token =>
  {
   var chosen = selection.Where(i => i.Kind == MediaKind.Video).ToList();
   var scope = chosen.Count > 0 ? Tn(chosen.Count, "1 selected video", "{0:N0} selected videos") : T("all videos in the library");
   if (chosen.Count == 0) await foreach (var item in StreamCatalog(new MediaQuery { Kind = MediaKind.Video }, token)) chosen.Add(item);
   if (chosen.Count == 0) { Status.Text = T("No videos in the library"); return; }
   if (Message.Show(this, T("Write NFO files for {0} next to the videos?\n\nExisting NFO files stay unchanged. The videos themselves are not changed.", scope),
    T("Write NFO files"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
   var preferences = _repository.GetPreferences(); Func<string, TagRole> roles = category => TagRoles.Of(preferences, category);
   var (written, kept, problems) = await Task.Run(() =>
   {
    int written = 0, kept = 0; var problems = new List<string>();
    foreach (var item in chosen)
    {
     token.ThrowIfCancellationRequested();
     var target = Path.ChangeExtension(item.Path, ".nfo");
     try
     {
      if (File.Exists(target)) { kept++; continue; }
      if (!Directory.Exists(Path.GetDirectoryName(target))) { problems.Add(T("Folder not reachable: {0}", item.Path)); continue; }
      FileAccessPolicy.Current.EnsureWritable(target); CatalogInterchange.WriteNfo(item, target, roles); written++;
     }
     catch (Exception error) when (error is IOException or UnauthorizedAccessException) { problems.Add(item.Path + ": " + error.Message); }
    }
    return (written, kept, problems);
   }, token);
   Status.Text = Tn(written, "1 NFO file written", "{0:N0} NFO files written") + (kept > 0 ? Tn(kept, " · 1 already existed", " · {0:N0} already existed") : "")
    + (problems.Count > 0 ? Tn(problems.Count, " · 1 error", " · {0:N0} errors") : "");
   if (problems.Count > 0) Message.Show(this, string.Join("\n", problems.Take(20)) + (problems.Count > 20 ? "\n…" : ""), T("Write NFO files"), MessageBoxButton.OK, MessageBoxImage.Warning);
  }, refresh: false);
 }

 /// <summary>Makes a collection from an M3U playlist; entries that are not in the catalog are named afterwards.</summary>
 private void ImportM3uPlaylist(string? path = null)
 {
  if (path == null)
  {
   var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Import playlist"), Filter = T("Playlists|*.m3u;*.m3u8|All files|*.*") };
   if (picker.ShowDialog(this) != true) return;
   path = picker.FileName;
  }
  try
  {
   var result = _repository.ImportM3u(path);
   if (result.Collection is not { } collection) { Message.Show(this, T("None of the {0} entries is in the catalog; no collection was created.", result.Entries), T("Import playlist"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
   // The new collection opens at once, as one made by hand does.
   _playlist = collection; _tag = null; ClearTagFilters(); _view = "all"; ViewTitle.Text = collection.Name; Refresh();
   Status.Text = T("Collection “{0}” created · {1}", collection.Name, Tn(collection.MediaIds.Count, "1 item", "{0:N0} items"))
    + (result.NotInCatalog.Count > 0 ? Tn(result.NotInCatalog.Count, " · 1 entry not in the catalog", " · {0:N0} entries not in the catalog") : "");
  }
  catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
  { Message.Show(this, error.Message, T("Import playlist"), MessageBoxButton.OK, MessageBoxImage.Warning); }
 }
 internal void ImportM3uForTest(string path) => ImportM3uPlaylist(path);

 private void ExportNfo()
 {
  if (Selected is not { } item) return;
  var picker = new SaveFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Export NFO metadata as a new file"), Filter = T("NFO|*.nfo"), FileName = Path.GetFileNameWithoutExtension(item.Path) + "-export.nfo" };
  if (picker.ShowDialog(this) == true) { var preferences = _repository.GetPreferences(); CatalogInterchange.WriteNfo(item, picker.FileName, c => TagRoles.Of(preferences, c)); Status.Text = T("NFO exported"); }
 }
}
