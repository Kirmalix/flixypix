using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

/// <summary>Folders FlixyPix must never write to. They belong to this computer; none is protected by default.</summary>
internal sealed partial class SettingsWindow
{
 private List<string> protectedFolders = [];
 private IReadOnlyList<string> ownFolders = [];
 private readonly StackPanel protectedRows = new() { Margin = new Thickness(16, 4, 16, 8) };

 private Border ProtectedFoldersCard()
 {
  var add = SettingsUi.SmallButton(T("＋ Add folder …"));
  add.Click += (_, _) =>
  {
   var picker = new OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose a folder to protect"), Multiselect = true };
   if (picker.ShowDialog(this) == true) foreach (var folder in picker.FolderNames) if (!AddProtectedFolder(folder)) break;
  };
  ShowProtectedFolders();
  return SettingsUi.Card(T("Protected folders"), T("FlixyPix never writes to, changes or deletes files in these folders. Reading and importing them stays possible."),
   protectedRows, new SettingRow(T("Protected folders"), T("Applies on this computer."), SettingsUi.Buttons(add)));
 }

 private void ShowProtectedFolders()
 {
  protectedRows.Children.Clear();
  if (protectedFolders.Count == 0) protectedRows.Children.Add(SettingsUi.Hint(T("No protected folders yet.")));
  for (var i = 0; i < protectedFolders.Count; i++)
  {
   var index = i; var folder = protectedFolders[i];
   var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
   text.Children.Add(new TextBlock { Text = folder, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = folder });
   if (ProtectedFolders.CollidesWithOwnData(folder, ownFolders))
    text.Children.Add(new TextBlock { Text = T("Not active: FlixyPix keeps its own data or a library here."), FontSize = 12, Foreground = SettingsUi.Error, TextWrapping = TextWrapping.Wrap });
   var remove = SettingsUi.SmallButton(T("Remove")); remove.Click += (_, _) => { protectedFolders.RemoveAt(index); ShowProtectedFolders(); Touch(); };
   var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
   var buttons = SettingsUi.Buttons(remove); DockPanel.SetDock(buttons, Dock.Right);
   row.Children.Add(buttons); row.Children.Add(text);
   protectedRows.Children.Add(row);
  }
 }

 /// <summary>Adds a folder once; refuses folders FlixyPix needs for itself (also used by the smoke test).</summary>
 internal bool AddProtectedFolder(string folder)
 {
  var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
  if (protectedFolders.Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase))) return true;
  if (ProtectedFolders.CollidesWithOwnData(full, ownFolders))
  {
   error.Text = "⚠ " + T("This folder cannot be protected because FlixyPix keeps its own data or a library there: {0}", full); note.Text = "";
   return false;
  }
  protectedFolders.Add(full); ShowProtectedFolders(); Touch();
  return true;
 }
}
