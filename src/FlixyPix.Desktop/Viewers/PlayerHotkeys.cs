using System.Windows.Input;

namespace FlixyPix.Desktop;

/// <summary>Shortcut text such as "Ctrl+Shift+Right" ↔ WPF keys; shared by the player and the settings window.</summary>
internal static class PlayerHotkeys
{
 internal static bool TryParse(string? text, out ModifierKeys modifiers, out Key key)
 {
  modifiers = ModifierKeys.None; key = Key.None;
  if (string.IsNullOrWhiteSpace(text)) return false;
  var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
  foreach (var part in parts[..^1])
   modifiers |= part.ToLowerInvariant() switch { "ctrl" or "control" => ModifierKeys.Control, "shift" => ModifierKeys.Shift, "alt" => ModifierKeys.Alt, _ => ModifierKeys.None };
  return Enum.TryParse(parts[^1], ignoreCase: true, out key) && key != Key.None;
 }

 internal static string Format(ModifierKeys modifiers, Key key) =>
  ((modifiers & ModifierKeys.Control) != 0 ? "Ctrl+" : "") + ((modifiers & ModifierKeys.Shift) != 0 ? "Shift+" : "") + ((modifiers & ModifierKeys.Alt) != 0 ? "Alt+" : "") + key;

 internal static string? Match(IReadOnlyDictionary<string, string> hotkeys, Key key, ModifierKeys modifiers) =>
  hotkeys.FirstOrDefault(pair => TryParse(pair.Value, out var m, out var k) && k == key && m == modifiers).Key;

 /// <summary>Modifier presses alone are not a shortcut; Back and Delete clear it, as in the original.</summary>
 internal static bool IsModifierOnly(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;
}
