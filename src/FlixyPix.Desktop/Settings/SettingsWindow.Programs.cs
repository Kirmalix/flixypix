using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Programs of "Öffnen mit" in the tools rubric: add, edit, remove, and take over the original's custom media
/// players. They belong to this computer and are stored with the other desktop settings.</summary>
internal sealed partial class SettingsWindow
{
 private List<ExternalProgram> programs = [];
 private readonly StackPanel programRows = new() { Margin = new Thickness(16, 4, 16, 8) };
 private readonly Button adoptPrograms = SettingsUi.SmallButton("");
 private IReadOnlyList<ExternalProgram> originalPrograms = [];
 internal IReadOnlyList<ExternalProgram> Programs => programs;

 private Border ProgramsCard()
 {
  try { originalPrograms = repository.GetOriginalExternalPrograms(); } catch (Microsoft.Data.Sqlite.SqliteException) { originalPrograms = []; }
  var add = SettingsUi.SmallButton(T("＋ Add program …")); add.Click += (_, _) => EditProgram(null);
  adoptPrograms.Click += (_, _) => AdoptOriginalPrograms();
  ShowPrograms();
  return SettingsUi.Card(T("Open with"), T("Programs on this computer for “Open with” in an item’s menu (also available by right-clicking a tile). The Windows default program and program picker are always available there."),
   programRows, new SettingRow(T("Programs"), T("Name, program file, arguments with placeholders, media types and an optional shortcut."), SettingsUi.Buttons(adoptPrograms, add)));
 }

 private void ShowPrograms()
 {
  programRows.Children.Clear();
  if (programs.Count == 0) programRows.Children.Add(SettingsUi.Hint(T("No programs of your own yet.")));
  foreach (var program in programs.ToList())
  {
   var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
   text.Children.Add(new TextBlock { Text = program.Name + (program.Hotkey.Length > 0 ? "  ·  " + string.Join(" + ", program.Hotkey.Split('+').Select(SettingsUi.KeyName)) : ""), FontWeight = FontWeights.SemiBold });
   text.Children.Add(new TextBlock { Text = program.Path + "  " + program.Arguments, FontSize = 12, Foreground = SettingsUi.Muted, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = program.Path + " " + program.Arguments });
   if(ExternalProgramDialog.HotkeyProblem(program.Hotkey,[]) is {} conflict)text.Children.Add(SettingsUi.Hint(conflict));
   var edit = SettingsUi.SmallButton(T("Edit …")); edit.Click += (_, _) => EditProgram(program);
   var remove = SettingsUi.SmallButton(T("Remove")); remove.Click += (_, _) => { programs.Remove(program); ShowPrograms(); Touch(); };
   var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
   var buttons = SettingsUi.Buttons(edit, remove); DockPanel.SetDock(buttons, Dock.Right);
   row.Children.Add(buttons); row.Children.Add(text);
   programRows.Children.Add(row);
  }
  var missing = originalPrograms.Count(o => !programs.Any(p => string.Equals(p.Path, o.Path, StringComparison.OrdinalIgnoreCase)));
  adoptPrograms.Content = T("Import from EazyFlixPix 4 ({0:N0})", missing);
  adoptPrograms.Visibility = missing > 0 ? Visibility.Visible : Visibility.Collapsed;
 }

 private void EditProgram(ExternalProgram? program)
 {
  var dialog = new ExternalProgramDialog(this, program, programs.Where(p => !ReferenceEquals(p, program)).Select(p => p.Hotkey).Where(h => h.Length > 0).ToList());
  if (dialog.ShowDialog() != true || dialog.Result is not { } result) return;
  var index = program == null ? -1 : programs.IndexOf(program);
  if (index >= 0) programs[index] = result; else programs.Add(result);
  ShowPrograms(); Touch();
 }

 /// <summary>Takes over the original's custom players that are not in the list yet; a shortcut that is taken or
 /// unusable is left out.</summary>
 internal int AdoptOriginalPrograms()
 {
  var added = 0;
  foreach (var original in originalPrograms)
  {
   if (programs.Any(p => string.Equals(p.Path, original.Path, StringComparison.OrdinalIgnoreCase))) continue;
   var hotkey = ExternalProgramDialog.HotkeyProblem(original.Hotkey, programs.Select(p => p.Hotkey)) == null ? original.Hotkey : "";
   programs.Add(original with { Hotkey = hotkey }); added++;
  }
  ShowPrograms(); if (added > 0) Touch();
  note.Text = Tn(added, "1 program imported from EazyFlixPix 4 · not saved yet", "{0:N0} programs imported from EazyFlixPix 4 · not saved yet");
  return added;
 }

 internal void ScrollToPrograms() => programRows.BringIntoView();

 /// <summary>Adds a program directly (smoke test).</summary>
 internal void AddProgram(ExternalProgram program) { programs.Add(program); ShowPrograms(); Touch(); }
}
