using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

/// <summary>Stills mode: the player becomes a tool for taking pictures out of a video. The arrow keys step by single frames
/// (Shift ten, Ctrl one second), J K L play backward, stop and play forward, I and O mark a range, Enter saves the frame
/// shown in full resolution, and a marked range can be saved as a series of pictures. Pictures go into the screenshot
/// folder, one subfolder per video, and join the library at once.</summary>
internal sealed partial class VideoPlayerWindow
{
 private readonly DockPanel stillsStrip = new() { Margin = new Thickness(8, 4, 8, 2), Visibility = Visibility.Collapsed };
 private readonly TextBlock frameLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
 private readonly CheckBox stillsToggle = Toggle("", T("Stills mode: step frame by frame and save pictures"));
 private Button exportRangeButton = null!;
 private bool stillsMode, stepping, shuttling;
 private int pendingFrames;
 private double? frameRate;

 internal bool StillsMode => stillsMode;
 internal string FrameText => frameLabel.Text;
 /// <summary>Frames per second, as the player reports them; the catalog's value or 25 until then.</summary>
 internal double FrameRate => frameRate ?? (media.Technical?.FrameRate is > 0 and var known ? known : 25);
 /// <summary>The library takes a saved picture in.</summary>
 internal Func<StillFile, Task>? StillSaved { get; set; }
 /// <summary>The library saves the frames of a range from this file into this folder and says how it went.</summary>
 internal Func<string, StillRange, double, string, Task<string>>? StillsExportRequested { get; set; }

 private DockPanel BuildStillsStrip()
 {
  Button Step(string text, string tip, int frames)
  {
   var button = new Button { Content = text, ToolTip = tip, Style = (Style)Application.Current.Resources["Quiet"], Focusable = false, MinWidth = 38 };
   AutomationProperties.SetName(button, tip);
   button.Click += async (_, _) => await Command(() => StepFramesAsync(frames));
   return button;
  }
  var steps = Pill(Step("−10", T("{0} frames back", 10) + Keys("Shift+Left"), -10), Step("−1", T("Previous frame") + Keys("Left"), -1), Step("+1", T("Next frame") + Keys("Right"), 1), Step("+10", T("{0} frames forward", 10) + Keys("Shift+Right"), 10));
  var tools = NewRangeTools();
  tools.Start.Focusable = tools.End.Focusable = tools.Discard.Focusable = false;
  var save = new Button { Content = T("Save picture"), Style = (Style)Application.Current.Resources["Primary"], Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(2, 0, 2, 0), Focusable = false, ToolTip = T("Save the frame shown in full resolution") + Keys("Enter") };
  AutomationProperties.SetName(save, T("Save picture"));
  save.Click += async (_, _) => await Command(SaveStillAsync);
  exportRangeButton = Labeled("", T("Save range as pictures …"), T("Save the frames between the start and the end mark as pictures") + Keys("Ctrl+Enter")); exportRangeButton.Focusable = false;
  exportRangeButton.Click += async (_, _) => await Command(ExportRangeAsync);
  var folder = Symbol("", T("Open the folder with the pictures of this video")); folder.Focusable = false;
  folder.Click += (_, _) => OpenStillsFolder();
  var close = Symbol("", T("End stills mode") + Keys("Esc")); close.Focusable = false;
  close.Click += async (_, _) => await Command(ToggleStillsModeAsync);
  foreach (var (element, dock) in new (UIElement, Dock)[] { (steps, Dock.Left), (tools.Start, Dock.Left), (tools.End, Dock.Left), (close, Dock.Right), (folder, Dock.Right), (exportRangeButton, Dock.Right), (save, Dock.Right), (tools.Discard, Dock.Right) })
  { DockPanel.SetDock(element, dock); stillsStrip.Children.Add(element); }
  frameLabel.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
  stillsStrip.Children.Add(frameLabel);
  // A narrow row keeps its symbols with their tooltips.
  stillsStrip.SizeChanged += (_, _) =>
  {
   var words = stillsStrip.ActualWidth >= 900;
   foreach (var button in new[] { tools.Start, tools.End, exportRangeButton })
    if (button.Content is StackPanel { Children: [TextBlock glyph, TextBlock text] }) { text.Visibility = words ? Visibility.Visible : Visibility.Collapsed; glyph.Margin = new Thickness(0, 0, words ? 7 : 0, 0); }
  };
  stillsToggle.Click += async (_, _) => { stillsToggle.IsChecked = stillsMode; await Command(ToggleStillsModeAsync); };
  return stillsStrip;
 }

 /// <summary>A key as the settings name it, in brackets after a tooltip.</summary>
 private static string Keys(string keys) => " (" + DisplayKey(keys) + ")";

 private MenuCommand StillsEntry() => new(T("Stills mode"), () => _ = Command(ToggleStillsModeAsync)) { Checked = stillsMode, Icon = "", Shortcut = MenuShortcut(PlayerActions.StillsMode), Enabled = player != null };

 internal async Task ToggleStillsModeAsync()
 {
  var active = RequirePlayer();
  stillsMode = !stillsMode; stillsToggle.IsChecked = stillsMode;
  stillsStrip.Visibility = stillsMode ? Visibility.Visible : Visibility.Collapsed;
  UpdateRangeControls();
  if (overlay.IsOpen) ShowOverlay();
  if (!stillsMode) { await EndShuttleAsync(); return; }
  if (!paused) { paused = true; await active.PauseAsync(true); ShowPlayState(); }
  await RefreshFrameAsync();
  Focus(); Keyboard.Focus(this);
  Notify(T("Stills mode · ← →: one frame · {0}: 10 frames · {1}: 1 second · I / O: range · {2}: save picture", SettingsUi.KeyName("Shift"), SettingsUi.KeyName("Ctrl"), SettingsUi.KeyName("Enter")));
 }

 /// <summary>What a key does in stills mode; null leaves it to the usual shortcuts.</summary>
 private Func<Task>? StillsKey(Key key, ModifierKeys modifiers) => (key, modifiers) switch
 {
  (Key.Left, ModifierKeys.None) => () => StepFramesAsync(-1), (Key.Right, ModifierKeys.None) => () => StepFramesAsync(1),
  (Key.Left, ModifierKeys.Shift) => () => StepFramesAsync(-10), (Key.Right, ModifierKeys.Shift) => () => StepFramesAsync(10),
  (Key.Left, ModifierKeys.Control) => () => StepSecondsAsync(-1), (Key.Right, ModifierKeys.Control) => () => StepSecondsAsync(1),
  (Key.Enter, ModifierKeys.None) => SaveStillAsync, (Key.Enter, ModifierKeys.Control) => ExportRangeAsync,
  (Key.J, ModifierKeys.None) => () => ShuttleAsync(backward: true), (Key.L, ModifierKeys.None) => () => ShuttleAsync(backward: false), (Key.K, ModifierKeys.None) => StopShuttleAsync,
  (Key.I, ModifierKeys.Shift) when RangeStart.HasValue => () => GoToFrameTimeAsync(RangeStart!.Value), (Key.O, ModifierKeys.Shift) when RangeEnd.HasValue => () => GoToFrameTimeAsync(RangeEnd!.Value),
  (Key.Escape, ModifierKeys.None) => ToggleStillsModeAsync,
  _ => null,
 };

 /// <summary>Steps by frames. Keys held down arrive faster than the player steps: what came in meanwhile is taken in one go.</summary>
 internal async Task StepFramesAsync(int frames)
 {
  pendingFrames += frames; if (stepping) return; stepping = true;
  try
  {
   while (pendingFrames != 0)
   {
    var count = pendingFrames; pendingFrames = 0;
    var active = RequirePlayer(); await EndShuttleAsync();
    if (!paused) { paused = true; await active.PauseAsync(true); pause.Content = ""; }
    var before = await active.GetPositionAsync();
    if (count == 1) await active.FrameStepAsync(true); else await active.FrameStepAsync(count);
    await RefreshFrameAsync(before);
   }
  }
  finally { stepping = false; pendingFrames = 0; }
 }

 private async Task StepSecondsAsync(double seconds)
 {
  await EndShuttleAsync();
  await GoToFrameTimeAsync(Math.Max(0, LastPositionSeconds + seconds));
 }

 private async Task GoToFrameTimeAsync(double seconds)
 {
  await SeekToAsync(seconds); await RefreshFrameAsync();
 }

 /// <summary>Reads where the player stands once a step has arrived, so marks and pictures take the frame that is shown.</summary>
 private async Task RefreshFrameAsync(double? before = null)
 {
  var active = RequirePlayer(); await active.WaitForSeekAsync();
  double? seconds = null;
  for (var attempt = 0; attempt < 40; attempt++)
  {
   seconds = await active.GetPositionAsync();
   if (before == null || seconds == null || Math.Abs(seconds.Value - before.Value) > 1e-6) break;
   await Task.Delay(10);
  }
  if (closing) return;
  if ((await active.GetFrameAsync()).Rate is > 0 and var rate) frameRate = rate;
  if (seconds is not { } now) return;
  LastPositionSeconds = now; if (!seeking) position.Value = Math.Clamp(now, 0, position.Maximum);
  ApplyTransform(); UpdateCurrentMark(); UpdateControls(now);
 }

 private void UpdateFrameLabel(double seconds)
 {
  if (!stillsMode) return;
  var rate = FrameRate;
  frameLabel.Text = Stills.Position(seconds, (long)Math.Round(seconds * rate)) + " · " + rate.ToString("0.###", CultureInfo.CurrentCulture) + " fps";
 }

 /// <summary>J and L: play backward or forward, each further press doubles the pace up to four times.</summary>
 private async Task ShuttleAsync(bool backward)
 {
  var active = RequirePlayer();
  var pace = shuttling && shuttleBackward == backward && !paused ? Math.Min(4, shuttlePace * 2) : 1;
  var message = (backward ? "◀ " : "▶ ") + pace.ToString("0.##", CultureInfo.CurrentCulture) + "×";
  // Stepping back by itself already: a further J only changes the pace.
  if (steppingBack && backward && shuttling && !paused) { shuttlePace = pace; Notify(message); return; }
  var run = ++shuttleRun;
  // While stepping back the player itself stands still, whatever the button shows.
  if (steppingBack) { steppingBack = false; paused = true; }
  if (!shuttling || shuttleBackward != backward) await active.SetPlayDirectionAsync(backward);
  await active.SetSpeedAsync(pace);
  shuttling = true; shuttleBackward = backward; shuttlePace = pace;
  if (paused) { paused = false; await active.PauseAsync(false); pause.Content = ""; }
  Notify(message);
  if (backward) _ = Command(() => StepBackWhenStalledAsync(active, run));
 }
 private bool shuttleBackward, steppingBack; private double shuttlePace = 1; private int shuttleRun;

 /// <summary>The player cannot play backward from inside the last group of pictures of a file (short clips are one such
 /// group): it keeps seeking and shows nothing. Then the frames are stepped back one by one at the pace chosen.</summary>
 private async Task StepBackWhenStalledAsync(MpvPlayer active, int run)
 {
  bool Current() => run == shuttleRun && shuttling && shuttleBackward && !paused && !closing && ReferenceEquals(player, active);
  var from = await active.GetPositionAsync();
  await Task.Delay(1200);
  if (!Current() || !await active.IsSeekingAsync() || await active.GetPositionAsync() is { } now && from is { } start && Math.Abs(now - start) > 1e-6) return;
  await active.PauseAsync(true); await active.SetPlayDirectionAsync(false); await active.WaitForSeekAsync();
  if (!Current()) return;
  steppingBack = true;
  try
  {
   var clock = System.Diagnostics.Stopwatch.StartNew();
   while (Current())
   {
    var before = await active.GetPositionAsync();
    if (before is <= 0) break;
    await active.FrameStepAsync(false); await RefreshFrameAsync(before);
    if (!Current()) return;
    if (before is { } old && Math.Abs(LastPositionSeconds - old) < 1e-6) break;
    var wait = TimeSpan.FromSeconds(1 / (FrameRate * shuttlePace)) - clock.Elapsed; clock.Restart();
    if (wait > TimeSpan.Zero) { await Task.Delay(wait); clock.Restart(); }
   }
   // Arrived at the first frame: playback stands, as at the end when playing forward.
   if (Current()) { paused = true; ShowPlayState(); }
  }
  // Unless another J or L has taken over, the shuttle is over: the player stands and plays forward again.
  finally { if (run == shuttleRun || !shuttling) { steppingBack = false; if (!closing) await EndShuttleAsync(); } }
 }

 private async Task StopShuttleAsync()
 {
  var active = RequirePlayer();
  if (!paused) { paused = true; await active.PauseAsync(true); pause.Content = ""; }
  await EndShuttleAsync(); await RefreshFrameAsync();
 }

 /// <summary>Back to forward playback at the speed chosen in the bar.</summary>
 private async Task EndShuttleAsync()
 {
  if (!shuttling || player == null) return;
  shuttling = false; shuttlePace = 1;
  if (shuttleBackward) { shuttleBackward = false; await player.SetPlayDirectionAsync(false); }
  await player.SetSpeedAsync(speed.SelectedItem is double chosen ? chosen : 1);
 }

 /// <summary>Saves the frame shown; its name tells the time to the thousandth.</summary>
 internal async Task<string> SaveStillAsync()
 {
  var active = RequirePlayer(); await active.WaitForSeekAsync();
  if (await active.GetPositionAsync() is { } now) LastPositionSeconds = now;
  return await SaveScreenshotAsync(precise: true);
 }
 private Task SaveStillCommandAsync() => SaveStillAsync();

 /// <summary>The folder for the pictures of this video.</summary>
 internal string StillsFolder => playback.Screenshots.FolderFor(ScreenshotFolder, media.Title);

 private void OpenStillsFolder()
 {
  var folder = StillsFolder;
  if (!Directory.Exists(folder)) { Notify(T("No pictures of this video have been saved yet.")); return; }
  System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
 }

 /// <summary>Asks how densely the marked range is to be saved and hands it to the library.</summary>
 internal async Task ExportRangeAsync()
 {
  if (RangeStart is not { } start || RangeEnd is not { } end) { Notify(T("Set the start of the range first and then a later end.") + KeyHint(PlayerActions.ClipStart)); return; }
  if (StillsExportRequested == null || Source == null) { ShowMessage(T("The player is not ready yet.")); return; }
  var folder = playback.Screenshots.Subfolders ? Path.Combine(StillsFolder, ScreenshotPreferences.Stamp(start) + "_" + ScreenshotPreferences.Stamp(end)) : StillsFolder;
  var dialog = new StillExportWindow(this, start, end, FrameRate, folder);
  if (ExportChoice != null) dialog.Result = ExportChoice(dialog); else if (dialog.ShowDialog() != true) return;
  if (dialog.Result is not { } range) return;
  ShowMessage(T("Saving pictures …"));
  ShowMessage(await StillsExportRequested(Source.Path, range, FrameRate, folder));
 }
 /// <summary>Answers the export dialog instead of the user (smoke test).</summary>
 internal Func<StillExportWindow, StillRange?>? ExportChoice { get; set; }
}

/// <summary>How a marked range becomes pictures: every frame, every n-th frame or one every so many seconds, with the
/// number of pictures to expect.</summary>
internal sealed class StillExportWindow : Window
{
 private readonly RadioButton every = new() { Content = T("Every frame"), IsChecked = true, GroupName = "spacing", Margin = new Thickness(0, 0, 0, 8) };
 private readonly RadioButton nth = new() { Content = T("Every n-th frame, n ="), GroupName = "spacing", VerticalAlignment = VerticalAlignment.Center };
 private readonly RadioButton timed = new() { Content = T("One picture every so many seconds:"), GroupName = "spacing", VerticalAlignment = VerticalAlignment.Center };
 private readonly TextBox nthBox = new() { Text = "5", Width = 70, Margin = new Thickness(8, 0, 0, 0) }, secondsBox = new() { Text = "1", Width = 70, Margin = new Thickness(8, 0, 0, 0) };
 private readonly TextBlock estimate = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
 private readonly Button export;
 private readonly double start, end, rate;
 internal StillRange? Result { get; set; }
 internal string EstimateText => estimate.Text;

 internal StillExportWindow(Window owner, double start, double end, double rate, string folder)
 {
  this.start = start; this.end = end; this.rate = rate;
  Owner = owner; Title = T("Save range as pictures"); Width = 560; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
  AutomationProperties.SetName(nthBox, (string)nth.Content); AutomationProperties.SetName(secondsBox, (string)timed.Content);
  StackPanel Row(RadioButton choice, TextBox box) { var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) }; row.Children.Add(choice); row.Children.Add(box); return row; }
  var panel = new StackPanel();
  panel.Children.Add(every); panel.Children.Add(Row(nth, nthBox)); panel.Children.Add(Row(timed, secondsBox)); panel.Children.Add(estimate);
  var where = new TextBlock { Text = folder, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) }; where.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
  panel.Children.Add(where);
  var frame = new WindowFrame(T("Save range as pictures"), Stills.Position(start, null) + " → " + Stills.Position(end, null) + " · " + rate.ToString("0.###", CultureInfo.CurrentCulture) + " fps", panel, "");
  frame.Add(T("Cancel"), () => DialogResult = false, cancel: true);
  export = frame.Add(T("Save pictures"), () => { Result = Choice(); if (Result != null) DialogResult = true; }, primary: true);
  Content = frame;
  foreach (var choice in new[] { every, nth, timed }) choice.Checked += (_, _) => Update();
  nthBox.TextChanged += (_, _) => { nth.IsChecked = true; Update(); }; secondsBox.TextChanged += (_, _) => { timed.IsChecked = true; Update(); };
  every.IsChecked = true; Update();
 }

 /// <summary>Chooses a spacing as a click and typing would.</summary>
 internal StillRange? Choose(StillSpacing spacing, double amount)
 {
  if (spacing == StillSpacing.EveryNthFrame) nthBox.Text = amount.ToString(CultureInfo.CurrentCulture);
  else if (spacing == StillSpacing.EverySeconds) secondsBox.Text = amount.ToString(CultureInfo.CurrentCulture);
  else every.IsChecked = true;
  return Choice();
 }

 private StillRange? Choice()
 {
  var range = nth.IsChecked == true ? int.TryParse(nthBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) && n >= 1 ? new StillRange(start, end, StillSpacing.EveryNthFrame, n) : null
   : timed.IsChecked == true ? double.TryParse(secondsBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds) && seconds >= .01 ? new StillRange(start, end, StillSpacing.EverySeconds, seconds) : null
   : new StillRange(start, end);
  if (range == null) return null;
  try { return range.Validate(rate); } catch (ArgumentException) { return null; }
 }

 private void Update()
 {
  var range = Choice();
  export.IsEnabled = range != null;
  estimate.Text = range != null ? Tn(range.Count(rate), "About 1 picture in full resolution", "About {0:N0} pictures in full resolution")
   : T("Please enter a number. At most {0:N0} pictures are saved in one go.", StillRange.MaximumCount);
 }
}
