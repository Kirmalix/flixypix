using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;
using FlixyPix.MediaTools;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

/// <summary>Catalog and tools menus and the file actions on the selected medium.</summary>
public partial class MainWindow
{
 private ContextMenu Menu(params (string Label, Action Action)[] actions) => OpenMenu(null, actions);
 private ContextMenu OpenMenu(UIElement? target, (string Label, Action Action)[] actions) => Menus.Open(target, Specs(actions), MenuFailed);
 private static IEnumerable<MenuSpec> Specs(IEnumerable<(string Label, Action Action)> actions) => actions.Select(a => MenuSpec.Command(a.Label, a.Action));
 /// <summary>The catalog menu leads into the areas of the database management, the one place for its tools.</summary>
 private void CatalogMenu(object sender, RoutedEventArgs e) => MenuBelow(sender, (T("Database management …"), () => ShowDatabase()),
  (T("Libraries …"), ManageLibraries), (T("Check and repair …"), () => ShowDatabase(RepairSection)), (T("Backup …"), () => ShowDatabase(BackupSection)),
  (T("Import and export …"), () => ShowDatabase(TransferSection)), (T("Legacy data …"), () => ShowDatabase(LegacySection)), (T("About FlixyPix"), ShowAbout));
 private void ShowAbout()=>new HelpWindow(this,T("About FlixyPix"),[("FlixyPix",AppVersion),(T("Technical information"),TechnicalInformation()),(T("Licenses"),"GPL-3.0-or-later · THIRD-PARTY-NOTICES.md"),(T("Project website"),ProductIdentity.ProjectUrl)]).ShowDialog();
 private void ExportCatalog() { var picker = new SaveFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Export catalog"), Filter = T("JSON catalog|*.json"),
  FileName = "FlixyPix-Katalog.json" }; if (picker.ShowDialog(this) == true) { _repository.Export(picker.FileName);
  Status.Text = T("Catalog exported · ") + picker.FileName; } }
 private void BackupCatalog() { var picker = new SaveFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Back up catalog (without media files)"),
  Filter = T("JSON backup|*.json"), FileName = $"FlixyPix-Sicherung-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
  if (picker.ShowDialog(this) == true) { _repository.Export(picker.FileName); _repository.RecordBackup(picker.FileName, new FileInfo(picker.FileName).Length);
  Status.Text = T("Catalog backup created. Back up media files separately."); } }
 private async void ImportCatalog()
 {
  var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Import catalog (existing data is kept)"), Filter = T("Catalogs and legacy data|*.json;*.jsonl|JSON catalog|*.json|Legacy JSONL|*.jsonl") };
  if (picker.ShowDialog(this) != true) return;
  if(Path.GetExtension(picker.FileName).Equals(".jsonl",StringComparison.OrdinalIgnoreCase)){await ImportLegacyNewLibraryAsync(picker.FileName);return;}
  if (!SecretDialogs.TryAskContainerSecret(this, picker.FileName, T("Import encrypted catalog"), out var secret)) return;
  await RunOperation(T("Import catalog"), async token => { var result = await Task.Run(() =>
   _repository.ImportJson(picker.FileName,CatalogRepository.MaximumPortableEntityBytes,token,secret), token);
   if (result.Imported > 0 || result.Errors.Count == 0) await RestoreAssetPreviewsAsync(token); ShowImportResult(result); });
 }

 private void ToolsMenu(object sender, RoutedEventArgs e) => OpenMediaMenu(sender as UIElement);
 private async void ProbeSelected()
 {
  if (Selected is not { } item) return;
  await RunOperation(T("Media information"), async token =>
  {
   var info = await _tools.ProbeAsync(item.Path, token);
   _repository.Upsert(item with { DurationSeconds = info.DurationSeconds, Width = info.Width, Height = info.Height, Technical = TechnicalOf(item.Kind, info) ?? item.Technical });
   Dialogs.Text(this, item.Title + T(" · Media information"), T("Duration: {0:0.##} seconds\nSize: {1} × {2}\nVideo: {3}\nAudio: {4}\n\n", info.DurationSeconds, info.Width, info.Height, info.VideoCodec, info.AudioCodec) + info.RawJson);
  });
 }
 private async void ProcessMedia(string mode)
 {
  if (Selected is not { } item) return;
  double start = 0, duration = 5;
  if (mode != "convert")
  {
   var scene = Scenes.SelectedItem as Scene;
   var values = Dialogs.Fields(this, mode == "gif" ? T("Create GIF") : T("Export clip"), (T("Start in seconds"),
    (scene?.StartSeconds ?? 0).ToString(CultureInfo.InvariantCulture)), (T("Length in seconds"),
    (scene != null ? scene.EndSeconds - scene.StartSeconds : 5).ToString(CultureInfo.InvariantCulture)));
   if (values == null || !TrySeconds(values[0], out start) || !TrySeconds(values[1], out duration) || start < 0 || duration <= 0) { if (values != null) Message.Show(this, T("The start must be at least 0 and the length greater than 0.")); return; }
  }
  var picker = new SaveFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Save new media file"), Filter = mode == "gif" ? T("GIF image|*.gif") : T("MP4 video|*.mp4"), FileName = Path.GetFileNameWithoutExtension(item.Path) + (mode == "convert" ? "-konvertiert" : "-ausschnitt") };
  if (picker.ShowDialog(this) != true) return;
  if (string.Equals(Path.GetFullPath(picker.FileName), Path.GetFullPath(item.Path), StringComparison.OrdinalIgnoreCase)) { Message.Show(this, T("Please choose another name for the output file.")); return; }
  await RunOperation(T("Process media"), async token => { if (mode == "convert") await _tools.ConvertAsync(item.Path, picker.FileName, token);
   else if (mode == "gif") await _tools.CreateGifAsync(item.Path, picker.FileName, start, duration, token);
   else await _tools.CreateClipAsync(item.Path, picker.FileName, start, duration, token); });
 }
 private void Reveal() { if (Selected is { } item) Reveal(item); }
 private static void Reveal(MediaItem item) { var info = new ProcessStartInfo("explorer.exe"); info.ArgumentList.Add("/select,"); info.ArgumentList.Add(item.Path); Process.Start(info); }
 private void RenameFile()
 {
  if (Selected is not { } item) return;
  FileAccessPolicy.Current.EnsureWritable(item.Path);
  var input = Dialogs.Fields(this, T("Rename file"), (T("New file name including the extension"), Path.GetFileName(item.Path)));
  if (input == null) return;
  if (string.IsNullOrWhiteSpace(input[0]) || Path.GetFileName(input[0]) != input[0] || input[0].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidOperationException(T("Please enter a valid file name only."));
  var target = Path.Combine(Path.GetDirectoryName(item.Path)!, input[0]);
  if (target == item.Path) return;
  FileAccessPolicy.Current.EnsureWritable(item.Path); FileAccessPolicy.Current.EnsureWritable(target); File.Move(item.Path, target);
  try { _repository.Upsert(item with { Path = target }); } catch { File.Move(target, item.Path); throw; }
  Refresh(item.Id);
 }
 private void RelinkFile() { if (Selected is not { } item) return; var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, 
  Title = T("Assign new location"), FileName = Path.GetFileName(item.Path) };
  if (picker.ShowDialog(this) == true) { _repository.Upsert(item with { Path = picker.FileName }); Refresh(item.Id); } }
 private void RemoveSelected() => RemoveMedia(SelectedMedia);
 private void DeleteFile() { if (Selected is { } item) DeleteWithQuestion(item); }
 /// <summary>Asks, then deletes; true when the medium is gone.</summary>
 internal bool DeleteWithQuestion(MediaItem item, Window? owner = null)
 {
  FileAccessPolicy.Current.EnsureWritable(item.Path);
  // Into the recycle bin as in the original ("deletetorecyclebin"), or for good; asking stays in both cases.
  var recycle = _repository.GetPreferences().General.DeleteToRecycleBin;
  var question = recycle ? T("Move the file to the Recycle Bin?\n\n{0}", item.Path) : T("Permanently delete the file from the disk?\n\n{0}\n\nThis action cannot be undone.", item.Path);
  if (Message.Show(owner ?? this, question, recycle ? T("Delete file") : T("Permanently delete file"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
  DeleteConfirmed(item); return true;
 }
 /// <summary>Removes the file (recycle bin or for good, as the library says) and its catalog entry, after the question.</summary>
 internal void DeleteConfirmed(MediaItem item)
 {
  FileAccessPolicy.Current.EnsureWritable(item.Path); DeleteFromDisk(item.Path, _repository.GetPreferences().General.DeleteToRecycleBin); _repository.Delete(item.Id); Refresh();
 }
}
