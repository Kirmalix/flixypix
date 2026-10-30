using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Settings of the "Video-Player" page for what the player shows and for screenshots: notices, hiding the
/// control bar, shortcuts, the jump mark list, and folder, name, format and numbering of screenshots.</summary>
internal sealed class PlayerDisplaySettings
{
 internal event Action? Changed;
 internal readonly CheckBox Messages = SettingsUi.Switch(), AutoHide = SettingsUi.Switch(), Shortcuts = SettingsUi.Switch(), Marks = SettingsUi.Switch(), NameBookmarks = SettingsUi.Switch(), Numbering = SettingsUi.Switch();
 internal readonly PathPicker Folder = new(true, null, DescribeFolder);
 internal readonly TextBox Prefix = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 80 };
 internal readonly SegmentedChoice Format = new([((int)ScreenshotFormat.Png, "PNG"), ((int)ScreenshotFormat.Jpg, "JPG")]);
 internal static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "FlixyPix");

 internal PlayerDisplaySettings()
 {
  AutomationProperties.SetName(Prefix, T("Screenshot name prefix"));
  foreach (var check in new[] { Messages, AutoHide, Shortcuts, Marks, NameBookmarks, Numbering }) { check.Checked += (_, _) => Changed?.Invoke(); check.Unchecked += (_, _) => Changed?.Invoke(); }
  Folder.Changed += () => Changed?.Invoke(); Prefix.TextChanged += (_, _) => Changed?.Invoke(); Format.Changed += () => Changed?.Invoke();
 }

 private static (bool? Ok, string Message) DescribeFolder(string value)
 {
  if (value.Length == 0) return (null, T("Leave empty for the default folder ") + DefaultFolder);
  if (!Path.IsPathFullyQualified(value)) return (false, T("Please enter a complete path with a drive."));
  return Directory.Exists(value) ? (true, T("Folder exists")) : (null, T("Created with the first screenshot."));
 }

 internal UIElement[] Cards() =>
 [
  SettingsUi.Card(T("Display in the player"), null,
   new SettingRow(T("Overlays"), T("Play/pause symbol over the video and short notes like “Bookmark set” in the status bar."), Messages),
   new SettingRow(T("Hide the control bar in full screen"), T("Without mouse movement the bar disappears after 2.5 seconds; when off it stays."), AutoHide),
   new SettingRow(T("Show jump mark panel"), T("Remembers whether the jump mark panel is shown, including after restarting the application."), Marks),
   new SettingRow(T("Name new bookmarks right away"), T("After “Set bookmark” a name field opens in the jump mark list. Enter keeps the name, Esc the time as name."), NameBookmarks)),
  SettingsUi.Card(T("Screenshots"), T("Applies to the key “Save screenshot” and the mouse action of the same name. Screenshots have the full resolution of the video."),
   new SettingRow(T("Folder"), null, Folder, wide: true),
   new SettingRow(T("Name prefix"), T("Empty: title of the video."), Prefix),
   new SettingRow(T("Format"), T("PNG is lossless, JPG much smaller."), Format),
   new SettingRow(T("Number consecutively"), T("“Name 0001.png”, “Name 0002.png” … instead of the position in the video."), Numbering)),
 ];

 internal void Load(PlaybackPreferences playback)
 {
  Messages.IsChecked = playback.ShowMessages; AutoHide.IsChecked = playback.AutoHideControls; Shortcuts.IsChecked = playback.KeyboardShortcuts; Marks.IsChecked = playback.ShowMarks; NameBookmarks.IsChecked = playback.NameNewBookmarks;
  Folder.Text = playback.Screenshots.Folder ?? ""; Prefix.Text = playback.Screenshots.Prefix; Format.Value = (int)playback.Screenshots.Format; Numbering.IsChecked = playback.Screenshots.Numbering;
 }

 internal (FrameworkElement Field, string Message)? FirstInvalid() =>
  Folder.IsValid ? null : (Folder, T("Screenshot folder: please enter a complete path or leave it empty."));

 internal PlaybackPreferences Apply(PlaybackPreferences playback) => playback with
 {
  ShowMessages = Messages.IsChecked == true, AutoHideControls = AutoHide.IsChecked == true, KeyboardShortcuts = Shortcuts.IsChecked == true, ShowMarks = Marks.IsChecked == true, NameNewBookmarks = NameBookmarks.IsChecked == true,
  Screenshots = new ScreenshotPreferences
  {
   Folder = Folder.Text.Length == 0 ? null : Folder.Text, Prefix = Prefix.Text.Trim(), Format = (ScreenshotFormat)Format.Value, Numbering = Numbering.IsChecked == true,
  },
 };
}
