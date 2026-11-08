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
  string? Shortcut(string action) => MenuShortcut(action);
  MenuCommand Act(string label, string action, string? icon = null, bool enabled = true) => ActionEntry(label, action, icon, enabled);
  return
  [
   Act(paused ? T("Play") : T("Pause"), PlayerActions.PlayPause, paused ? "" : ""),
   MenuSpec.Separator,
   Act(T("Set bookmark"), PlayerActions.CreateBookmark, ""),
   Act(T("Previous bookmark"), PlayerActions.PreviousBookmark, "", Marks.Count > 0),
   Act(T("Next bookmark"), PlayerActions.NextBookmark, "", Marks.Count > 0),
   new MenuCommand(T("Jump marks"), ToggleMarkPanel) { Checked = MarkPanelVisible, Shortcut = Shortcut(PlayerActions.ToggleMarks) },
   MenuSpec.Separator,
   MenuSpec.Submenu(T("Tracks"), TrackEntries(), ""),
   MenuSpec.Submenu(T("Playback speed"), SpeedEntries()),
   LoopEntry(),
   ShuffleEntry(),
   MenuSpec.Separator,
   MenuSpec.Submenu(T("Range"), RangeEntries()),
   Act(T("Save screenshot"), PlayerActions.Screenshot, ""),
   Act(T("O counter +1"), PlayerActions.AddO),
   new MenuCommand(T("Full screen"), ToggleFullscreen) { Icon = "", Shortcut = Shortcut(PlayerActions.Fullscreen), Checked = IsFullscreen },
   MenuSpec.Separator,
   MenuSpec.Command(T("Copy path"), () => Clipboard.SetText(Source!.Path), enabled: Source != null),
   MenuSpec.Separator,
   MenuSpec.Command(T("Close"), Close, ""),
  ];
 }

 private string? MenuShortcut(string action) => playback.Hotkeys.GetValueOrDefault(action, "") is { Length: > 0 } key ? DisplayKey(key) : null;
 private MenuCommand ActionEntry(string label, string action, string? icon = null, bool enabled = true) =>
  new(label, () => _ = Command(() => RunActionAsync(action))) { Icon = icon, Shortcut = MenuShortcut(action), Enabled = enabled && player != null };
 private IEnumerable<MenuSpec> RangeEntries() =>
 [
  ActionEntry(T("Set range start"), PlayerActions.ClipStart),
  ActionEntry(T("Set range end"), PlayerActions.ClipEnd, null, RangeStart.HasValue),
  ActionEntry(T("Save as scene"), PlayerActions.ClipCreate, null, RangeStart.HasValue && RangeEnd.HasValue),
  MenuSpec.Command(AbLoop.Active ? T("End loop") : T("Loop range"), () => AbLoopUpdate = Command(ToggleRangeLoopAsync), enabled: RangeStart.HasValue && RangeEnd.HasValue && durationSeconds > 0),
  MenuSpec.Command(T("Discard range"), () => AbLoopUpdate = Command(ClearRangeAsync), enabled: RangeStart.HasValue),
 ];
 private MenuCommand LoopEntry() => new(T("Loop"), () => { repeat.IsChecked = repeat.IsChecked != true; PlaybackModeChanged?.Invoke(); }) { Icon = "", Checked = repeat.IsChecked == true };
 private MenuCommand ShuffleEntry() => new(T("Shuffle"), () => { shuffle.IsChecked = shuffle.IsChecked != true; PlaybackModeChanged?.Invoke(); }) { Icon = "", Checked = shuffle.IsChecked == true };
 private IEnumerable<MenuSpec> SpeedEntries() => speed.Items.OfType<double>().Select(value =>
  (MenuSpec)new MenuCommand(value.ToString("0.##", CultureInfo.CurrentCulture) + "×", () => speed.SelectedItem = value) { Checked = speed.SelectedItem is double chosen && chosen == value });
 private IEnumerable<MenuSpec> RatingEntries() => Enumerable.Range(0, 11).Reverse().Select(value =>
  (MenuSpec)new MenuCommand(value == 0 ? T("Unrated") : T("{0:0.#} out of 5 stars", value / 2d), () => Rate(value)) { Checked = ratingPicker.Value == value });
 private IReadOnlyList<MenuSpec> OCounterEntries() => OCounterShown
  ? [ActionEntry(T("O counter +1"), PlayerActions.AddO), MenuSpec.Command(T("Take back the last O"), () => ChangeO(add: false), enabled: OCount > 0)] : [];
 private IReadOnlyList<MenuSpec> MarkEntries() =>
 [
  ActionEntry(T("Previous bookmark"), PlayerActions.PreviousBookmark, null, Marks.Count > 0),
  ActionEntry(T("Next bookmark"), PlayerActions.NextBookmark, null, Marks.Count > 0),
  new MenuCommand(T("Jump marks"), ToggleMarkPanel) { Checked = MarkPanelVisible, Shortcut = MenuShortcut(PlayerActions.ToggleMarks) },
 ];

 /// <summary>Opens the player menu at the mouse pointer. The video is a native window that WPF gets no mouse moves from,
 /// so the place comes from Windows.</summary>
 internal ContextMenu? OpenPlayerMenu { get; private set; }
 internal ContextMenu ShowPlayerMenu()
 {
  var menu = Menus.Create(PlayerMenu(), (_, error) => ShowMessage(error.Message));
  if (GetCursorPos(out var cursor) && PresentationSource.FromVisual(this) != null)
  {
   var point = PointFromScreen(new Point(cursor.X, cursor.Y));
   menu.PlacementTarget = this; menu.Placement = PlacementMode.Relative; menu.HorizontalOffset = point.X; menu.VerticalOffset = point.Y;
  }
  OpenPlayerMenu=menu; menu.IsOpen = true; return menu;
 }
}
