using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

/// <summary>Quick access folders in the general rubric (original "Quick access"): fixed targets for copying and moving
/// media files. They belong to this computer.</summary>
internal sealed partial class SettingsWindow
{
 private List<string> quickFolders = [];
 private readonly StackPanel quickRows = new() { Margin = new Thickness(16, 4, 16, 8) };
 internal IReadOnlyList<string> QuickFolders => quickFolders;

 private Border QuickAccessCard()
 {
  var add = SettingsUi.SmallButton(T("＋ Add folder …"));
  add.Click += (_, _) =>
  {
   var picker = new OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose a folder for quick access"), Multiselect = true };
   if (picker.ShowDialog(this) == true) foreach (var folder in picker.FolderNames) AddQuickFolder(folder);
  };
  ShowQuickFolders();
  return SettingsUi.Card(T("Quick access"), T("Preset target folders for “Copy to” and “Move to” in an item’s menu. FlixyPix shows a preview before applying changes; a number is added to names that are already in use."),
   quickRows, new SettingRow(T("Target folders"), T("Applies on this computer."), SettingsUi.Buttons(add)));
 }

 private void ShowQuickFolders()
 {
  quickRows.Children.Clear();
  if (quickFolders.Count == 0) quickRows.Children.Add(SettingsUi.Hint(T("No target folders yet.")));
  for (var i = 0; i < quickFolders.Count; i++)
  {
   var index = i; var folder = quickFolders[i];
   var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
   text.Children.Add(new TextBlock { Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder, FontWeight = FontWeights.SemiBold });
   text.Children.Add(new TextBlock { Text = folder, FontSize = 12, Foreground = Directory.Exists(folder) ? SettingsUi.Muted : SettingsUi.Error, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = folder });
   var up = SettingsUi.SmallButton("↑", T("Up")); up.IsEnabled = index > 0;
   up.Click += (_, _) => { (quickFolders[index - 1], quickFolders[index]) = (quickFolders[index], quickFolders[index - 1]); ShowQuickFolders(); Touch(); };
   var remove = SettingsUi.SmallButton(T("Remove")); remove.Click += (_, _) => { quickFolders.RemoveAt(index); ShowQuickFolders(); Touch(); };
   var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
   var buttons = SettingsUi.Buttons(up, remove); DockPanel.SetDock(buttons, Dock.Right);
   row.Children.Add(buttons); row.Children.Add(text);
   quickRows.Children.Add(row);
  }
 }

 /// <summary>Adds a folder once (also used by the smoke test).</summary>
 internal void AddQuickFolder(string folder)
 {
  var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
  if (quickFolders.Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase))) return;
  quickFolders.Add(full); ShowQuickFolders(); Touch();
 }
}
