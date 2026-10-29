using System.IO;
using System.Windows;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private void MoveFile()
 {
  if (Selected is not { } item) return;
  FileAccessPolicy.Current.EnsureWritable(item.Path);
  var picker = new SaveFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Move media file to a new location"), FileName = Path.GetFileName(item.Path), Filter = T("All files|*.*") };
  if (picker.ShowDialog(this) != true) return;
  var target = Path.GetFullPath(picker.FileName);
  if (string.Equals(target, Path.GetFullPath(item.Path), StringComparison.OrdinalIgnoreCase)) return;
  MoveMediaFile(item, target); Refresh(item.Id); Status.Text = T("File moved and catalog updated");
 }

 private void MoveMediaFile(MediaItem item, string target)
 {
  FileAccessPolicy.Current.EnsureWritable(item.Path); FileAccessPolicy.Current.EnsureWritable(target);
  if (_repository.GetMediaByPath(target) is { } other && other.Id != item.Id)
   throw new IOException(T("Another catalog entry uses this target path."));
  File.Move(item.Path, target, overwrite: false);
  try { _repository.Upsert(item with { Path = target }); }
  catch (Exception catalogError)
  {
   try { File.Move(target, item.Path, overwrite: false); }
   catch (Exception rollbackError) { throw new IOException(T("The file is now at {0}. The catalog could not be updated and the move could not be undone.", target), new AggregateException(catalogError, rollbackError)); }
   throw;
  }
 }
}
