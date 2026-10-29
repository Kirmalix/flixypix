using System.Windows;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private async void ImportBinaryRule()
 {
  var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Read binary original tag rule structurally"), Filter = T("Binary rule files|*.bin;*.dat;*.blob|All files|*.*") };
  if (picker.ShowDialog(this) != true) return;
  await RunOperation(T("Import binary tag rule"),async token =>
  {
   var rule = await Task.Run(() => LegacyObjectReader.ReadTagRule(picker.FileName),token);
   _repository.UpsertFilenameTagRule(rule);
  });
 }
}
