using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Settings of the "Video-Player" page for seeking: the wheel step and direction, what the wheel does with Shift,
/// Ctrl and Alt, keys with jumps of one's own, and volume, resuming and staying on top.</summary>
internal sealed class PlayerSeekSettings
{
 internal event Action? Changed;
 internal readonly NumberBox WheelStep = new(0.1, 3600, 1, "");
 internal readonly SegmentedChoice WheelUnit = Units();
 internal readonly CheckBox InvertWheel = SettingsUi.Switch(), Resume = SettingsUi.Switch(), RememberVolume = SettingsUi.Switch(), OnTop = SettingsUi.Switch();
 internal readonly NumberBox StartVolume = new(0, 100, 5, "%");
 private readonly Dictionary<WheelModifier, (ComboBox Action, NumberBox Step, SegmentedChoice Unit)> modifiers = [];
 private readonly StackPanel jumpRows = new();
 internal readonly List<(HotkeyBox Key, NumberBox Amount, SegmentedChoice Unit, FrameworkElement Row)> Jumps = [];
 private static readonly PlayerWheelAction[] ModifierActions = [PlayerWheelAction.Nothing, PlayerWheelAction.Seek, PlayerWheelAction.Volume, PlayerWheelAction.FrameStep];

 internal PlayerSeekSettings()
 {
  WheelStep.SetLabel(T("Mouse wheel step")); StartVolume.SetLabel(T("Initial volume"));
  foreach (var modifier in Enum.GetValues<WheelModifier>())
  {
   var action = new ComboBox { Width = 150, ItemsSource = ModifierActions.Select(a => Capital(CatalogRepository.WheelLabel(a))).ToArray(), Margin = new Thickness(0, 0, 8, 0) };
   AutomationProperties.SetName(action, Name(modifier));
   var step = new NumberBox(0.1, 3600, 1, ""); step.SetLabel(T("Step size for {0}", Name(modifier)));
   var unit = Units();
   action.SelectionChanged += (_, _) => { UpdateEnabled(); Changed?.Invoke(); }; step.Changed += () => Changed?.Invoke(); unit.Changed += () => Changed?.Invoke();
   modifiers[modifier] = (action, step, unit);
  }
  WheelStep.Changed += () => Changed?.Invoke(); WheelUnit.Changed += () => Changed?.Invoke(); StartVolume.Changed += () => Changed?.Invoke();
  foreach (var check in new[] { InvertWheel, Resume, RememberVolume, OnTop }) { check.Checked += (_, _) => { UpdateEnabled(); Changed?.Invoke(); }; check.Unchecked += (_, _) => { UpdateEnabled(); Changed?.Invoke(); }; }
 }

 private static SegmentedChoice Units() => new([((int)JumpUnit.Seconds, T("Seconds")), ((int)JumpUnit.Percent, T("% of the length"))]);
 private static string Name(WheelModifier modifier) => modifier switch { WheelModifier.Shift => T("Shift+mouse wheel"), WheelModifier.Ctrl => T("Ctrl+mouse wheel"), _ => T("Alt+mouse wheel") };
 private static string Capital(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];

 private static StackPanel Line(params FrameworkElement[] parts)
 {
  var line = new StackPanel { Orientation = Orientation.Horizontal };
  foreach (var part in parts) { part.VerticalAlignment = VerticalAlignment.Center; if (part is not ComboBox) part.Margin = new Thickness(0, 0, 8, 0); line.Children.Add(part); }
  return line;
 }

 internal UIElement[] Cards()
 {
  var add = SettingsUi.SmallButton(T("Add jump"), T("Another key with its own jump distance")); add.Margin = new Thickness(0, 8, 0, 0); add.HorizontalAlignment = HorizontalAlignment.Left;
  add.Click += (_, _) => { AddJump(new CustomJump("", 60)); Changed?.Invoke(); };
  var jumps = new StackPanel(); jumps.Children.Add(jumpRows); jumps.Children.Add(add);
  return
  [
   SettingsUi.Card(T("Seek with the mouse wheel"), T("Applies when the mouse wheel over the video is set to seek. Modifier keys use their own assignments; percentages refer to the video’s duration."),
    [new SettingRow(T("Step"), T("How far one notch of the mouse wheel jumps."), Line(WheelStep, WheelUnit)),
     new SettingRow(T("Reverse direction"), T("Wheel up seeks back instead of forward."), InvertWheel),
     .. modifiers.Select(pair => (UIElement)new SettingRow(Name(pair.Key), null, Line(pair.Value.Action, pair.Value.Step, pair.Value.Unit)))]),
   SettingsUi.Card(T("Custom jumps"), T("More keys with their own jump distance, in addition to the small and large jump. Negative distances jump back, e.g. Ctrl+→ = 60, Ctrl+← = −60 or 9 = 90 % (almost to the end)."),
    new SettingRow(T("Jumps"), null, jumps, wide: true)),
   SettingsUi.Card(T("Volume and resume"), null,
    new SettingRow(T("Resume at the remembered position"), T("A video starts where it was last stopped."), Resume),
    new SettingRow(T("Remember volume"), T("Each video starts with the volume of the previous one."), RememberVolume),
    new SettingRow(T("Initial volume"), T("Applies when the volume is not remembered."), StartVolume),
    new SettingRow(T("Player always on top"), T("The player window stays above other windows."), OnTop)),
  ];
 }

 private void AddJump(CustomJump jump)
 {
  var key = new HotkeyBox { Value = jump.Key };
  var amount = new NumberBox(-36000, 36000, 5, ""); amount.Value = jump.Amount; amount.SetLabel(T("Jump distance"));
  var unit = Units(); unit.Value = (int)jump.Unit;
  var remove = SettingsUi.SmallButton(T("Remove")); remove.Margin = new Thickness(0);
  var row = Line(key, amount, unit, remove); row.Margin = new Thickness(0, 0, 0, 6);
  var entry = (key, amount, unit, (FrameworkElement)row);
  remove.Click += (_, _) => { Jumps.Remove(entry); jumpRows.Children.Remove(row); Changed?.Invoke(); };
  key.Changed += () => Changed?.Invoke(); amount.Changed += () => Changed?.Invoke(); unit.Changed += () => Changed?.Invoke();
  Jumps.Add(entry); jumpRows.Children.Add(row);
 }

 private void UpdateEnabled()
 {
  foreach (var (action, step, unit) in modifiers.Values)
  {
   var seeks = action.SelectedIndex >= 0 && ModifierActions[action.SelectedIndex] is PlayerWheelAction.Seek or PlayerWheelAction.Volume;
   step.IsEnabled = seeks; unit.IsEnabled = action.SelectedIndex >= 0 && ModifierActions[action.SelectedIndex] == PlayerWheelAction.Seek;
  }
  StartVolume.IsEnabled = RememberVolume.IsChecked != true;
 }

 internal void Load(PlaybackPreferences playback)
 {
  WheelStep.Value = playback.WheelStep; WheelUnit.Value = (int)playback.WheelUnit; InvertWheel.IsChecked = playback.InvertWheel;
  foreach (var (modifier, controls) in modifiers)
  {
   var binding = playback.WheelModifiers.FirstOrDefault(b => b.Modifier == modifier) ?? new WheelBinding(modifier, PlayerWheelAction.Nothing, 5);
   controls.Action.SelectedIndex = Math.Max(0, Array.IndexOf(ModifierActions, binding.Action)); controls.Step.Value = binding.Step; controls.Unit.Value = (int)binding.Unit;
  }
  Jumps.Clear(); jumpRows.Children.Clear();
  foreach (var jump in playback.CustomJumps) AddJump(jump);
  Resume.IsChecked = playback.ResumePlayback; RememberVolume.IsChecked = playback.RememberVolume; StartVolume.Value = playback.StartVolume; OnTop.IsChecked = playback.AlwaysOnTop;
  UpdateEnabled();
 }

 /// <summary>The first field that holds an invalid value, with what is wrong; null when everything is valid.</summary>
 internal (FrameworkElement Field, string Message)? FirstInvalid()
 {
  if (!WheelStep.IsValid) return (WheelStep, T("Mouse wheel step: ") + WheelStep.Range);
  foreach (var (modifier, controls) in modifiers) if (controls.Step.IsEnabled && !controls.Step.IsValid) return (controls.Step, Name(modifier) + ": " + controls.Step.Range);
  foreach (var (key, amount, _, _) in Jumps)
  {
   if (key.Value.Length == 0) return (key, T("Custom jump: please press a key or remove the row."));
   if (!amount.IsValid || amount.Value == 0) return (amount, T("Custom jump: a distance other than 0, ") + amount.Range);
  }
  if (RememberVolume.IsChecked != true && !StartVolume.IsValid) return (StartVolume, T("Initial volume: ") + StartVolume.Range);
  return null;
 }

 internal PlaybackPreferences Apply(PlaybackPreferences playback) => playback with
 {
  WheelStep = WheelStep.Value!.Value, WheelUnit = (JumpUnit)WheelUnit.Value, InvertWheel = InvertWheel.IsChecked == true,
  WheelModifiers = modifiers.Select(pair => new WheelBinding(pair.Key, ModifierActions[Math.Max(0, pair.Value.Action.SelectedIndex)],
   pair.Value.Step.IsValid ? pair.Value.Step.Value!.Value : 5, (JumpUnit)pair.Value.Unit.Value)).ToList(),
  CustomJumps = Jumps.Select(j => new CustomJump(j.Key.Value, j.Amount.Value!.Value, (JumpUnit)j.Unit.Value)).ToList(),
  ResumePlayback = Resume.IsChecked == true, RememberVolume = RememberVolume.IsChecked == true,
  StartVolume = StartVolume.IsValid ? (int)Math.Round(StartVolume.Value!.Value) : playback.StartVolume, AlwaysOnTop = OnTop.IsChecked == true,
 };
}
