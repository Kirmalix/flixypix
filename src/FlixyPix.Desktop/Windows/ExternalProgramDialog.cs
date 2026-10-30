using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>One program of "Öffnen mit": name, program file, argument template with placeholders, media kinds and an
/// optional shortcut in the main window.</summary>
internal sealed class ExternalProgramDialog : Window
{
 /// <summary>Shortcuts of the main window that a program must not take over.</summary>
 internal static IReadOnlyList<string> LibraryShortcuts { get; } =
  ["F1", "Ctrl+F1", "F6", "Shift+F6", "F10", "Ctrl+D", "Ctrl+I", "Ctrl+F", "Ctrl+T", "Ctrl+O", "Ctrl+S", "Ctrl+B", "Ctrl+G", "Ctrl+Home", "Ctrl+Shift+B", "Alt+Left", "Alt+Right",
   .. Enumerable.Range(1, 9).SelectMany(n => new[] { $"Ctrl+D{n}", $"Ctrl+Alt+D{n}" })];

 private readonly TextBox _name = new() { MinWidth = 420 };
 private readonly PathPicker _path;
 private readonly TextBox _arguments = new() { MinWidth = 420, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
 private readonly CheckBox _video = new() { Content = T("Videos"), Margin = new Thickness(0, 0, 16, 0) }, _image = new() { Content = T("Images"), Margin = new Thickness(0, 0, 16, 0) }, _audio = new() { Content = T("Audio") };
 private readonly HotkeyBox _hotkey = new();
 private readonly TextBlock _error = new() { Foreground = SettingsUi.Error, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
 private readonly IReadOnlyCollection<string> _taken;

 internal ExternalProgram? Result { get; private set; }

 /// <param name="taken">Shortcuts of the other programs.</param>
 internal ExternalProgramDialog(Window owner, ExternalProgram? program, IReadOnlyCollection<string> taken)
 {
  Owner = owner; Title = T("Open with program · FlixyPix"); Width = 620; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
  WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; FontSize = 14;
  _taken = taken;
  _path = new PathPicker(false, T("Programs (*.exe)|*.exe"), DescribeProgram);
  _path.Changed += () => { if (_name.Text.Trim().Length == 0 && File.Exists(_path.Text)) _name.Text = ProgramName(_path.Text); };
  _name.Text = program?.Name ?? ""; _path.Text = program?.Path ?? ""; _arguments.Text = program?.Arguments ?? ExternalPrograms.DefaultArguments;
  var kinds = program?.Kinds ?? [];
  _video.IsChecked = kinds.Count == 0 || kinds.Contains(MediaKind.Video); _image.IsChecked = kinds.Count == 0 || kinds.Contains(MediaKind.Image); _audio.IsChecked = kinds.Count == 0 || kinds.Contains(MediaKind.Audio);
  _hotkey.Value = program?.Hotkey ?? "";
  foreach (var (control, label) in new (UIElement, string)[] { (_name, T("Name")), (_arguments, T("Arguments")) }) System.Windows.Automation.AutomationProperties.SetName(control, label);

  var panel = new StackPanel();
  void Add(string label, UIElement control, string? hint = null)
  {
   panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 14, 0, 4) });
   panel.Children.Add(control);
   if (hint != null) panel.Children.Add(SettingsUi.Hint(hint));
  }
  Add(T("Program"), _path);
  Add(T("Name"), _name);
  Add(T("Arguments"), _arguments, T("Placeholders: {0} full path, {1} folder, {2} file name, {3} title, {4} remembered position in seconds. Add quotes yourself, such as \"{0}\".",
   "{path}", "{folder}", "{name}", "{title}", "{seconds}"));
  var kindPanel = new StackPanel { Orientation = Orientation.Horizontal }; kindPanel.Children.Add(_video); kindPanel.Children.Add(_image); kindPanel.Children.Add(_audio);
  Add(T("Offer for"), kindPanel);
  Add(T("Shortcuts in the main window"), _hotkey, T("Optional, with Ctrl or Alt. Opens the selected items directly with this program."));
  panel.Children.Add(_error);
  var frame = new WindowFrame(program == null ? T("Add program") : T("Edit program"), null, panel, "", scroll: false);
  frame.Add(T("Apply"), () => { if (TrySave()) DialogResult = true; }, primary: true);
  frame.Add(T("Cancel"), Close, cancel: true);
  Content = frame;
  Loaded += (_, _) => { if (_path.Text.Length == 0) _path.FocusInput(); else { _name.Focus(); _name.SelectAll(); } };
 }

 private static (bool? Ok, string Message) DescribeProgram(string value)
 {
  if (value.Length == 0) return (false, T("Please choose the program."));
  if (!Path.IsPathFullyQualified(value)) return (false, T("Please enter a complete path with a drive."));
  return File.Exists(value) ? (true, ProgramName(value)) : (null, T("The file cannot be reached at the moment."));
 }

 private static string ProgramName(string path)
 {
  try { if (FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description) return description.Trim(); }
  catch (FileNotFoundException) { }
  return Path.GetFileNameWithoutExtension(path);
 }

 /// <summary>Null when the shortcut is free and usable.</summary>
 internal static string? HotkeyProblem(string hotkey, IEnumerable<string> taken)
 {
  if(PlayerHotkeys.TryParse(hotkey,out var reservedModifiers,out var reservedKey)&&reservedModifiers==ModifierKeys.Control&&(reservedKey is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9))return T("Reserved for quick tags");

  if (hotkey.Length == 0) return null;
  if (!PlayerHotkeys.TryParse(hotkey, out var modifiers, out var key)) return T("The shortcut is not valid.");
  if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0) return T("Shortcuts need Ctrl or Alt.");
  bool Same(string other) => PlayerHotkeys.TryParse(other, out var m, out var k) && m == modifiers && k == key;
  if (LibraryShortcuts.Any(Same) || taken.Any(Same)) return T("The shortcut {0} is already taken.", Shortcut(hotkey));
  return AppShell.IsBossKey(key, modifiers) ? T("The shortcut {0} is already taken.", Shortcut(hotkey)) : null;
 }

 private static string Shortcut(string hotkey) => string.Join(" + ", hotkey.Split('+').Select(SettingsUi.KeyName));

 internal bool TrySave()
 {
  var kinds = new List<MediaKind>();
  if (_video.IsChecked == true) kinds.Add(MediaKind.Video);
  if (_image.IsChecked == true) kinds.Add(MediaKind.Image);
  if (_audio.IsChecked == true) kinds.Add(MediaKind.Audio);
  if (kinds.Count == 0) { _error.Text = T("Please choose at least one media type."); return false; }
  var program = new ExternalProgram { Name = _name.Text.Trim(), Path = _path.Text, Arguments = _arguments.Text.Trim(), Kinds = kinds.Count == 3 ? [] : kinds, Hotkey = _hotkey.Value };
  if ((ExternalPrograms.Problem(program) ?? HotkeyProblem(program.Hotkey, _taken)) is { } problem) { _error.Text = problem; return false; }
  Result = program; return true;
 }

 /// <summary>Fills the fields directly (smoke test).</summary>
 internal void Fill(string path, string name, string arguments, bool video, bool image, bool audio, string hotkey)
 {
  _path.Text = path; _name.Text = name; _arguments.Text = arguments; _video.IsChecked = video; _image.IsChecked = image; _audio.IsChecked = audio; _hotkey.Value = hotkey;
 }
 internal string Error => _error.Text;
}
