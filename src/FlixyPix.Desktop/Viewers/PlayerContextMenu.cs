using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>The small player menu on a right click into the picture (UX plan U1.3): play state, marks, tracks, speed,
/// loop and shuffle, screenshot, O counter and full screen, each with its shortcut on the right.</summary>
internal sealed partial class VideoPlayerWindow
{
 internal IReadOnlyList<MenuSpec> PlayerMenu()
 {
  string? Shortcut(string action) => playback.Hotkeys.GetValueOrDefault(action, "") is { Length: > 0 } key ? DisplayKey(key) : null;
  MenuCommand Act(string label, string action, string? icon = null, bool enabled = true) =>
   new(label, () => _ = Command(() => RunActionAsync(action))) { Icon = icon, Shortcut = Shortcut(action), Enabled = enabled && player != null };
  return
  [
   Act(paused ? T("Play") : T("Pause"), PlayerActions.PlayPause, paused ? "" : ""),
   MenuSpec.Separator,
   Act(T("Set bookmark"), PlayerActions.CreateBookmark, ""),
   Act(T("Previous bookmark"), PlayerActions.PreviousBookmark, "", Marks.Count > 0),
   Act(T("Next bookmark"), PlayerActions.NextBookmark, "", Marks.Count > 0),
   // In full screen the list stays hidden; the bar's previous/next buttons stand in for it.
   new MenuCommand(T("Jump marks"), ToggleMarkPanel) { Checked = MarkPanelVisible, Shortcut = Shortcut(PlayerActions.ToggleMarks), Enabled = !IsFullscreen },
   MenuSpec.Separator,
   MenuSpec.Submenu(T("Tracks"), TrackEntries(), ""),
   MenuSpec.Submenu(T("Playback speed"), speed.Items.OfType<double>().Select(value =>
    (MenuSpec)new MenuCommand(value.ToString("0.##", CultureInfo.CurrentCulture) + "×", () => speed.SelectedItem = value) { Checked = speed.SelectedItem is double chosen && chosen == value })),
   new MenuCommand(T("Loop"), () => { repeat.IsChecked = repeat.IsChecked != true; PlaybackModeChanged?.Invoke(); }) { Icon = "", Checked = repeat.IsChecked == true },
   new MenuCommand(T("Shuffle"), () => { shuffle.IsChecked = shuffle.IsChecked != true; PlaybackModeChanged?.Invoke(); }) { Icon = "", Checked = shuffle.IsChecked == true },
   MenuSpec.Separator,
   Act(T("Save screenshot"), PlayerActions.Screenshot, ""),
   Act(T("O counter +1"), PlayerActions.AddO),
   new MenuCommand(T("Full screen"), ToggleFullscreen) { Icon = "", Shortcut = Shortcut(PlayerActions.Fullscreen), Checked = IsFullscreen },
   MenuSpec.Separator,
   MenuSpec.Command(T("Close"), Close, ""),
  ];
 }

 /// <summary>Opens the player menu at the mouse pointer. The video is a native window that WPF gets no mouse moves from,
 /// so the place comes from Windows.</summary>
 internal ContextMenu? OpenPlayerMenu { get; private set; }
 internal ContextMenu ShowPlayerMenu()
 {
  var menu = Menus.Create(PlayerMenu(), (_, error) => status.Text = error.Message);
  if (GetCursorPos(out var cursor) && PresentationSource.FromVisual(this) != null)
  {
   var point = PointFromScreen(new Point(cursor.X, cursor.Y));
   menu.PlacementTarget = this; menu.Placement = PlacementMode.Relative; menu.HorizontalOffset = point.X; menu.VerticalOffset = point.Y;
  }
  OpenPlayerMenu=menu; menu.IsOpen = true; return menu;
 }
}
