using System.IO;
using System.Windows;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private async void CreateBackupPackage()
 {
  var picker = new SaveFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Create complete backup package"), Filter = T("FlixyPix backup|*.flixypixbackup"), FileName = $"FlixyPix-{DateTime.Now:yyyyMMdd-HHmmss}.flixypixbackup" };
  if (picker.ShowDialog(this) != true) return;
  var choice = Message.Show(this,T("Also copy the external image, video and audio files into the backup?\n\nYes: catalog, stored artwork, previews and available media.\nNo: catalog, stored artwork and available previews; media remain external references.\n\nThe source files are only read."),T("Back up media too"),MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
  if (choice == MessageBoxResult.Cancel) return;
  await RunOperation(T("Create backup"),async token =>
  {
   var progress = new Progress<CatalogBackupProgress>(value => Status.Text = T("{0}: {1} files · {2}", value.Phase, value.CompletedFiles, Path.GetFileName(value.Name)));
   var result = await Task.Run(() => _repository.CreateBackupAsync(picker.FileName,choice == MessageBoxResult.Yes,token,progress),token);
   Dialogs.Text(this,T("Backup created"),Tn(result.FileCount, "1 file, {1:N0} bytes\n{2}", "{0} files, {1:N0} bytes\n{2}", result.TotalBytes, result.PackagePath) +
    (result.Warnings.Count == 0 ? "" : Tn(result.Warnings.Count, "\n\n1 note:\n", "\n\n{0} notes:\n") + string.Join('\n',result.Warnings.Take(40))));
  });
 }

 private async void RestoreBackupPackage(string? path = null)
 {
  var package = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Check and restore backup package"), Filter = T("FlixyPix backups|*.flixypixbackup;*.ezfpbackup|All files|*.*"), FileName = path ?? "" };
  if (path == null && package.ShowDialog(this) != true) return;
  var parent = new OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose the parent folder for the new library") };
  if (parent.ShowDialog(this) != true) return;
  var name = Dialogs.Fields(this,T("New library"),(T("Name of the new subfolder"),$"FlixyPix-wiederhergestellt-{DateTime.Now:yyyyMMdd-HHmmss}"));
  if (name == null) return;
  var folder = name[0].Trim();
  if (folder.Length == 0 || folder is "." or ".." || folder.EndsWith('.') || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
  { Message.Show(this,T("Please enter a valid new folder name.")); return; }
  var target = Path.Combine(parent.FolderName,folder);
  if (!SecretDialogs.TryAskContainerSecret(this,package.FileName,T("Restore encrypted backup"),out var secret)) return;
  string? restored = null;
  await RunOperation(T("Restore backup"),async token =>
  {
   var progress = new Progress<CatalogBackupProgress>(value => Status.Text = T("{0}: {1} files", value.Phase, value.CompletedFiles));
   var result = await Task.Run(() => CatalogRepository.RestoreBackupAsync(package.FileName,target,token,progress,secret),token);
   restored = result.DirectoryPath;
   Dialogs.Text(this,T("Restore completed"),T("New library:\n{0}\n\nMedia files restored: {1}.\nThe current catalog was not changed; the new library is listed under Libraries.",
    result.DirectoryPath, result.RestoredMediaFiles) +
    (result.Warnings.Count == 0 ? "" : Tn(result.Warnings.Count, "\n\n1 note:\n", "\n\n{0} notes:\n") + string.Join('\n',result.Warnings.Take(40))));
  });
  if (restored == null) return;
  // The restored library joins the list of this computer and can be opened at once.
  SaveLibraries(LibraryCatalogs.Rename(LibraryCatalogs.Remember(_settings.Libraries, restored, DateTimeOffset.Now), restored, folder), _settings.AskForLibraryAtStart);
  if (Message.Show(this, T("Open the restored library “{0}” now?", folder), T("Restore completed"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
   await SwitchLibraryAsync(restored);
 }
}
