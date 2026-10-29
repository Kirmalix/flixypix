using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Shortcut cards of the player rubric and their conflict check.</summary>
internal sealed partial class SettingsWindow
{
 private UIElement HotkeyCards()
 {
  var panel = new StackPanel();
  var resetAll = SettingsUi.SmallButton(T("Reset all"), T("Reset all shortcuts to their defaults"));
  resetAll.Click += (_, _) => { foreach (var (action, key) in PlayerActions.Defaults()) hotkeys[action].Value = key; };
  var intro = SettingsUi.Card(T("Shortcuts"), T("Click the field and press the key you want, also with Ctrl, Shift or Alt. Backspace or Del removes the assignment. Esc and F11 stay reserved in the player for full screen and closing."),
   new SettingRow(T("Default assignment"), T("Space, F, arrow keys, Shift+arrow keys, comma/period, B, M and L; Page↑/Page↓ for the previous and next jump mark."), resetAll));
  ((StackPanel)intro.Child).Children.Insert(1, conflictNote);
  panel.Children.Add(intro);
  foreach (var (title, actions) in HotkeyGroups)
  {
   var rowsOfGroup = actions.Select(action =>
   {
    var label = PlayerActions.All.First(a => a.Action == action).Label; var box = hotkeys[action];
    var reset = SettingsUi.SmallButton("", T("Default: ") + Display(PlayerActions.Defaults()[action])); reset.Content = SettingsUi.Icon("", 12);
    System.Windows.Automation.AutomationProperties.SetName(reset, T("Reset {0}", label));
    reset.Click += (_, _) => box.Value = PlayerActions.Defaults()[action];
    var clear = SettingsUi.SmallButton("", T("Remove assignment")); clear.Content = SettingsUi.Icon("", 12);
    System.Windows.Automation.AutomationProperties.SetName(clear, T("Clear key assignment for {0}", label));
    clear.Click += (_, _) => box.Value = "";
    System.Windows.Automation.AutomationProperties.SetName(box, T("Keyboard shortcut for {0}", label));
    return (UIElement)new SettingRow(label, null, SettingsUi.Buttons(box, reset, clear));
   }).ToArray();
   panel.Children.Add(SettingsUi.Card(T("Shortcuts · ") + title, null, rowsOfGroup));
  }
  return panel;
 }
 private static string Display(string hotkey) => hotkey.Length == 0 ? T("not assigned") : string.Join(" + ", hotkey.Split('+').Select(SettingsUi.KeyName));
 private static string LabelOf(string action) => PlayerActions.All.First(a => a.Action == action).Label;
 private void UpdateConflicts()
 {
  // Actions and custom jumps share the keys; a jump is named by its distance.
  var boxes = hotkeys.Select(pair => (Label: LabelOf(pair.Key), Box: pair.Value))
   .Concat(Seek.Jumps.Select(j => (Label: T("Jump ") + Core.Jumps.Describe(j.Amount.Value ?? 0, (JumpUnit)j.Unit.Value), Box: j.Key))).ToList();
  var used = boxes.Where(b => b.Box.Value.Length > 0).GroupBy(b => b.Box.Value, StringComparer.OrdinalIgnoreCase)
   .ToDictionary(group => group.Key, group => group.Select(b => b.Label).ToList(), StringComparer.OrdinalIgnoreCase);
  foreach (var (label, box) in boxes)
   box.Conflict = box.Value.Length > 0 && used[box.Value].Count > 1 ? string.Join(", ", used[box.Value].Where(other => other != label)) : null;
  var clashes = used.Where(pair => pair.Value.Count > 1).Select(pair => T("{0} at {1}", Display(pair.Key), string.Join(", ", pair.Value))).ToList();
  conflictNote.Text = clashes.Count == 0 ? "" : T("⚠ Duplicate shortcuts: {0}. Please change one assignment.", string.Join(" · ", clashes));
  conflictNote.Visibility = clashes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
 }
}
