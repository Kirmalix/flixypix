using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Player controls following frmEZFPPlayerOverlay and frmPlayPauseOverlay of EazyFlixPix 4.8.4: one bar with symbols, previous and next
/// in the current view, repeat and shuffle, one menu for tracks and one for the view. In full screen the bar floats over the
/// video and hides when the mouse rests. The video is a native window, so everything drawn over it lives in popups.</summary>
internal sealed partial class VideoPlayerWindow
{
 private readonly StackPanel controlBar = new() { Margin = new Thickness(8, 2, 8, 4) };
 private readonly Button previousButton = Symbol("", T("Previous video in the view"));
 private readonly Button nextButton = Symbol("", T("Next video in the view"));
 private readonly CheckBox repeat = Toggle("", T("Loop: continue with the next video at the end, start over after the last one"));
 private readonly CheckBox shuffle = Toggle("", T("Shuffle: pick the next video at random from the view"));
 private readonly TextBlock clock = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontSize = 12 };
 private readonly TextBlock clockTotal = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontSize = 12 };
 private readonly Button fullscreenButton = Symbol("", T("Full screen (F11)"));
 private readonly Popup overlay = new() { AllowsTransparency = true, Placement = PlacementMode.Relative, StaysOpen = true };
 private readonly Border overlayFrame = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(4, 6, 4, 2), Margin = new Thickness(24, 0, 24, 24) };
 private readonly Popup flash = new() { AllowsTransparency = true, Placement = PlacementMode.Relative, IsHitTestVisible = false };
 private readonly TextBlock flashGlyph = new() { FontFamily = SettingsUi.Icons, FontSize = 46, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
 private readonly Popup seekPreview = new() { AllowsTransparency = true, Placement = PlacementMode.Relative, IsHitTestVisible = false };
 private readonly Image seekImage = new() { Width = 240, Stretch = Stretch.Uniform };
 private readonly TextBlock seekTime = new() { Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 2), FontSize = 12 };
 private readonly Slider volumeSlider = new() { Minimum = 0, Maximum = 100, Value = 100, IsMoveToPointEnabled = true, Width = 96, VerticalAlignment = VerticalAlignment.Center, ToolTip = T("Volume") };
 private PlayerWheelAction? wheelOverride;
 internal double Volume => volumeSlider.Value;
 internal PlayerWheelAction WheelMode => wheelOverride ?? playback.Wheel;
 /// <summary>Asks the library for a random video of the current view (mouse action "play random").</summary>
 internal Func<Task>? RandomRequested { get; set; }

 /// <summary>Mouse input from the native host for the video area, handled with the mouse settings (original "ezfpleftclick" and its siblings, "ezfpwheelposition").</summary>
 internal async Task HandleMouseAsync(string input)
 {
  if(closing || player==null)return;
  switch (input)
  {
   case "MBTN_LEFT_DBL":
    clickTimer?.Stop(); suppressLeftRelease=true; ToggleFullscreen(); return;
   case "MBTN_LEFT":
    if(suppressLeftRelease) { suppressLeftRelease=false; return; }
    clickTimer?.Stop();
    clickTimer=new DispatcherTimerLike(TimeSpan.FromMilliseconds(GetDoubleClickTime()),()=>QueueMouseInput("LEFT_SINGLE")); return;
   case "LEFT_SINGLE": await RunMouseAsync(playback.LeftClick); return;
   case "MBTN_RIGHT": await RunMouseAsync(playback.RightClick); return;
   case "MBTN_MID": await RunMouseAsync(playback.MiddleClick); return;
   case "MBTN_BACK": await RunMouseAsync(playback.BackClick); return;
   case "MBTN_FORWARD": await RunMouseAsync(playback.ForwardClick); return;
  }
  var direction = input.EndsWith("WHEEL_UP", StringComparison.Ordinal) ? 1 : input.EndsWith("WHEEL_DOWN", StringComparison.Ordinal) ? -1 : 0;
  if (direction == 0 || player == null) return;
  var (mode, step, unit) = WheelBindingFor(input);
  switch (mode)
  {
   case PlayerWheelAction.Seek:
    if (playback.InvertWheel) direction = -direction;
    await Command(() => JumpByAsync(direction * step, unit)); break;
   // The volume step is in percent points; a step below one still moves one point.
   case PlayerWheelAction.Volume: volumeSlider.Value = Math.Clamp(volumeSlider.Value + Math.Max(1, step) * direction, 0, 100); Notify(T("Volume {0:0} %", volumeSlider.Value)); break;
   case PlayerWheelAction.FrameStep:
    if (playback.InvertWheel) direction = -direction;
    await Command(() => RunActionAsync(direction > 0 ? PlayerActions.FrameForward : PlayerActions.FrameBackward)); break;
  }
 }

 /// <summary>What a wheel input does: the plain wheel follows "Mausrad" (and the switch from a mouse button) with the wheel
 /// step; with Shift, Ctrl or Alt the binding of that key counts. "Seek and volume" keeps the original's Ctrl for volume.</summary>
 internal (PlayerWheelAction Action, double Step, JumpUnit Unit) WheelBindingFor(string input)
 {
  WheelModifier? modifier = input.StartsWith("Ctrl+", StringComparison.Ordinal) ? WheelModifier.Ctrl : input.StartsWith("Shift+", StringComparison.Ordinal) ? WheelModifier.Shift
   : input.StartsWith("Alt+", StringComparison.Ordinal) ? WheelModifier.Alt : null;
  if (modifier is null) return (WheelMode == PlayerWheelAction.SeekAndVolume ? PlayerWheelAction.Seek : WheelMode, playback.WheelStep, playback.WheelUnit);
  if (playback.WheelModifiers.FirstOrDefault(b => b.Modifier == modifier) is { } binding) return (binding.Action == PlayerWheelAction.SeekAndVolume ? PlayerWheelAction.Seek : binding.Action, binding.Step, binding.Unit);
  return (modifier == WheelModifier.Ctrl && WheelMode == PlayerWheelAction.SeekAndVolume ? PlayerWheelAction.Volume : PlayerWheelAction.Nothing, 5, JumpUnit.Seconds);
 }

 /// <summary>Jumps by seconds or a share of the length and shows how far, e.g. "+10 s · 12:34".</summary>
 internal async Task JumpByAsync(double amount, JumpUnit unit)
 {
  var seconds = Jumps.Seconds(amount, unit, durationSeconds);
  if (seconds == 0) { Notify(T("Percent jumps need the length of the video.")); return; }
  await JumpAsync(seconds);
  Notify(Jumps.Describe(amount, unit) + " · " + TimeSpan.FromSeconds(LastPositionSeconds).ToString(LastPositionSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss"));
 }
 private async Task RunMouseAsync(PlayerMouseAction action)
 {
  switch (action)
  {
   case PlayerMouseAction.Nothing: return;
   case PlayerMouseAction.PlayRandom: if (RandomRequested != null) await Command(RandomRequested); return;
   case PlayerMouseAction.Screenshot: await Command(SaveScreenshotAsync); return;
   case PlayerMouseAction.SwitchWheel: SwitchWheel(); return;
   case PlayerMouseAction.Mute: ToggleMute(); return;
   case PlayerMouseAction.ContextMenu: ShowPlayerMenu(); return;
  }
  var mapped = action switch
  {
   PlayerMouseAction.PlayPause => PlayerActions.PlayPause, PlayerMouseAction.PreviousVideo => PlayerActions.PreviousVideo, PlayerMouseAction.NextVideo => PlayerActions.NextVideo,
   PlayerMouseAction.PreviousBookmark => PlayerActions.PreviousBookmark, PlayerMouseAction.NextBookmark => PlayerActions.NextBookmark,
   PlayerMouseAction.SmallBackward => PlayerActions.SmallBackward, PlayerMouseAction.SmallForward => PlayerActions.SmallForward,
   PlayerMouseAction.FrameBackward => PlayerActions.FrameBackward, _ => PlayerActions.FrameForward,
  };
  await Command(() => RunActionAsync(mapped));
 }
 private DispatcherTimerLike? clickTimer;
 private bool suppressLeftRelease, processingMouse;
 private readonly Queue<string> mouseInputs=new();
 private void QueueMouseInput(string input)
 {
  if(closing || player==null)return;
  if(input.StartsWith("MBTN_",StringComparison.Ordinal)) { Focus(); Keyboard.Focus(this); }
  mouseInputs.Enqueue(input);
  if(!processingMouse)_=DrainMouseAsync();
 }
 private async Task DrainMouseAsync()
 {
  processingMouse=true;
  try { while(!closing && player!=null && mouseInputs.TryDequeue(out var input))await Command(()=>HandleMouseAsync(input)); }
  finally { processingMouse=false; }
 }
 private double lastPositiveVolume=100;
 private readonly Button volumeButton=Symbol("",T("Sound off"));
 internal Slider VolumeControl=>volumeSlider;
 internal Button MuteControl=>volumeButton;
 private void UpdateVolumeState()
 {
  if(volumeSlider.Value>0)lastPositiveVolume=volumeSlider.Value;
  volumeButton.Content=volumeSlider.Value==0?"":"";
  volumeButton.ToolTip=volumeSlider.Value==0?T("Sound on"):T("Sound off");
  AutomationProperties.SetName(volumeButton,(string)volumeButton.ToolTip);
 }
 [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
 private static TimeSpan DoubleClickTime => TimeSpan.FromMilliseconds(Math.Clamp(GetDoubleClickTime(), 200, 900) + 100);
 /// <summary>Sound off and back to the volume before; moving the volume in between ends the muting.</summary>
 internal void ToggleMute()
 {
  volumeSlider.Value=volumeSlider.Value>0?0:lastPositiveVolume;
  Notify(volumeSlider.Value==0?T("Sound off"):T("Volume {0:0} %",volumeSlider.Value));
 }
 /// <summary>The plain wheel switches between seeking and volume until the player closes (mouse button or shortcut).</summary>
 internal void SwitchWheel()
 {
  wheelOverride = WheelMode == PlayerWheelAction.Volume ? PlayerWheelAction.Seek : PlayerWheelAction.Volume;
  Notify(T("Mouse wheel: {0}", WheelMode == PlayerWheelAction.Volume ? T("Volume") : T("seek")));
 }
 private DispatcherTimerLike? flashTimer, messageTimer;
 private readonly Popup messagePopup = new() { AllowsTransparency = true, Placement = PlacementMode.Relative, IsHitTestVisible = false, HorizontalOffset = 12, VerticalOffset = 12 };
 internal bool MessageShown => messagePopup.IsOpen;
 internal string MessageText => status.Text;
 /// <summary>A popup does not follow its window by itself; opening it again places it anew.</summary>
 private void KeepMessageInPlace() { if (messagePopup.IsOpen) { messagePopup.IsOpen = false; messagePopup.IsOpen = true; } }
 /// <summary>A message over the video for a few seconds; the video keeps the room of a status line.</summary>
 private void ShowMessage(string text)
 {
  status.Text = text; if (closing || host.ActualWidth <= 0) return;
  InterfaceScaling.Fit((FrameworkElement)messagePopup.Child);
  messagePopup.IsOpen = true;
  messageTimer?.Stop(); messageTimer = new DispatcherTimerLike(TimeSpan.FromSeconds(4), () => messagePopup.IsOpen = false);
 }
 private (int X, int Y) lastCursor; private DateTime lastMovement = DateTime.MinValue;

 /// <summary>Asks the library for the previous (−1) or next (+1) video of the current view; the library honours shuffle.</summary>
 internal Func<int, Task>? StepRequested { get; set; }
 /// <summary>Picture of the video at a time, for the preview while hovering the time line.</summary>
 internal Func<double, CancellationToken, Task<FlixyPix.MediaTools.MemoryVideoFrame?>>? FrameAt { get; set; }
 internal event Action? PlaybackModeChanged;
 internal bool Repeat { get => repeat.IsChecked == true; set => repeat.IsChecked = value; }
 internal bool Shuffle { get => shuffle.IsChecked == true; set => shuffle.IsChecked = value; }
 internal bool OverlayShown => overlay.IsOpen;
 internal bool FlashShown => flash.IsOpen;
 internal bool SeekPreviewShown => seekPreview.IsOpen && seekImage.Source != null;
 /// <summary>Texts of the bar's buttons, wherever the bar currently lives (window or full-screen overlay).</summary>
 internal IEnumerable<string> ControlLabels
 {
  get
  {
   static IEnumerable<DependencyObject> All(DependencyObject parent) => LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>().SelectMany(c => All(c).Prepend(c));
   // Symbol buttons are known by their accessible name, text buttons by their text.
   return All(controlBar).OfType<Button>().SelectMany(b => new[] { b.Content as string, AutomationProperties.GetName(b) }).OfType<string>().Where(t => t.Length > 0).Distinct();
  }
 }
 internal string TrackMenuSummary => string.Join(" · ", (audio.SelectedItem as TrackChoice)?.Label, (subtitles.SelectedItem as TrackChoice)?.Label);

 private static Button Symbol(string glyph, string tip)
 {
  var button = new Button { Content = glyph, Style = (Style)Application.Current.Resources["IconButton"], ToolTip = tip, Margin = new Thickness(1, 0, 1, 0) };
  AutomationProperties.SetName(button, tip); return button;
 }
 private static CheckBox Toggle(string glyph, string tip)
 {
  var toggle = new CheckBox { Content = glyph, Tag = glyph, Style = (Style)Application.Current.Resources["IconToggle"], ToolTip = tip, Margin = new Thickness(2, 0, 2, 0) };
  AutomationProperties.SetName(toggle, tip); return toggle;
 }
 private static Button Labeled(string glyph, string text, string tip)
 {
  var content = new StackPanel { Orientation = Orientation.Horizontal };
  content.Children.Add(new TextBlock { Text = glyph, FontFamily = SettingsUi.Icons, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
  content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
  var button = new Button { Content = content, ToolTip = tip, Style = (Style)Application.Current.Resources["Quiet"], Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(2, 0, 2, 0) };
  AutomationProperties.SetName(button, text); return button;
 }

 private StackPanel BuildControlBar()
 {
  AutomationProperties.SetName(volumeSlider,T("Volume"));AutomationProperties.SetName(position,T("Position"));AutomationProperties.SetName(speed,T("Speed"));
  controlBar.Children.Add(BuildTimeline());
  var row = new PlayerControlRow { Margin = new Thickness(8, 0, 8, 2) };
  pause.Content = ""; pause.Style = (Style)Application.Current.Resources["PlayButton"]; pause.ToolTip = T("Play / Pause"); AutomationProperties.SetName(pause, T("Play or pause"));
  pause.Click += async (_, _) => await Command(TogglePauseAsync);
  previousButton.Click += async (_, _) => { if (StepRequested != null) await Command(() => StepRequested(-1)); };
  nextButton.Click += async (_, _) => { if (StepRequested != null) await Command(() => StepRequested(1)); };
  var jumps = new List<(Button Button, Func<IReadOnlyList<MenuSpec>> Entries)>();
  foreach (var (label, seconds) in new[] { ($"−{playback.SmallJumpBackwardSeconds:0.#} s", -playback.SmallJumpBackwardSeconds), ($"+{playback.SmallJumpForwardSeconds:0.#} s", playback.SmallJumpForwardSeconds) })
  {
   var jump = new Button { Content = label, Style = (Style)Application.Current.Resources["Quiet"], Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(2, 0, 2, 0), ToolTip = seconds < 0 ? T("Jump back") : T("Skip ahead") };
   jump.Click += async (_, _) => await Command(() => JumpAsync(seconds));
   var jumpLabel = jump.ToolTip + " (" + label + ")";
   jumps.Add((jump, () => [MenuSpec.Command(jumpLabel, () => _ = Command(() => JumpAsync(seconds)))]));
  }

  repeat.Click += (_, _) => PlaybackModeChanged?.Invoke(); shuffle.Click += (_, _) => PlaybackModeChanged?.Invoke();
  volumeButton.Margin=new Thickness(0,0,2,0);
  volumeButton.Click+=(_,_)=>ToggleMute();
  UpdateVolumeState();
  var volume = volumeSlider;
  AutomationProperties.SetName(volume, T("Volume"));
  volume.ValueChanged += async (_, _) => { UpdateVolumeState(); if (player != null) await Command(() => player.SetVolumeAsync(volume.Value)); };
  speed.Margin = new Thickness(0, 0, 2, 0); speed.VerticalAlignment = VerticalAlignment.Center; speed.ToolTip = T("Playback speed"); speed.ItemStringFormat = "0.##×";
  // xml:lang follows the UI language; numeric items follow the user's regional format.
  speed.Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag);
  speed.SelectionChanged += async (_, _) => { if (!syncing && player != null && speed.SelectedItem is double value) await TrackPreference(() => SaveSpeedAsync(value)); };
  var tracks = Symbol("", T("Choose audio and subtitle track")); AutomationProperties.SetName(tracks, T("Tracks")); tracks.Click += (_, _) => TrackMenu(tracks);
  var view = Symbol("", T("Time-based view: crop and position of the picture per point in time")); view.Click += (_, _) => ViewMenu(view);
  var marks = Pill(previousMarkButton, marksButton, nextMarkButton); marks.Margin = new Thickness(4, 0, 4, 0);
  var more = Symbol("\uE712", T("More actions")); more.Click += (_, _) => Menus.Open(more, controlRow.FoldedEntries(), (_, error) => ShowMessage(error.Message), PlacementMode.Top);
  fullscreenButton.Click += (_, _) => ToggleFullscreen();
  // Volume and rating on the left, playback in the middle, the tools on the right. The numbers say who gives way first.
  row.Add(volumeButton, RowZone.Left);
  row.Add(volume, RowZone.Left, 9, () => [MenuSpec.Submenu(T("Volume"), VolumeEntries())]);
  row.Add(BuildRating(), RowZone.Left, 1, compact: RatingChip());
  row.Add(BuildOCounter(), RowZone.Left, 3, OCounterEntries);
  row.Add(shuffle, RowZone.Center, 5, () => [ShuffleEntry()]);
  row.Add(jumps[0].Button, RowZone.Center, 4, jumps[0].Entries);
  row.Add(previousButton, RowZone.Center); row.Add(pause, RowZone.Center); row.Add(nextButton, RowZone.Center);
  row.Add(jumps[1].Button, RowZone.Center, 4, jumps[1].Entries);
  row.Add(repeat, RowZone.Center, 5, () => [LoopEntry()]);
  row.Add(speed, RowZone.Right, 7, () => [MenuSpec.Submenu(T("Playback speed"), SpeedEntries())]);
  row.Add(tracks, RowZone.Right, 8, () => [MenuSpec.Submenu(T("Tracks"), TrackEntries(), tracks.Content as string)]);
  row.Add(marks, RowZone.Right, 6, MarkEntries);
  row.Add(view, RowZone.Right, 2, () => [MenuSpec.Submenu(Tc("player", "View"), ViewEntries())]);
  row.AddMore(more);
  row.Add(fullscreenButton, RowZone.Right);
  controlRow = row; controlBar.Children.Add(row);
  controlBar.Children.Add(BuildRangeStrip());
  status.Margin = new Thickness(0); status.MaxWidth = 640; status.FontSize = 12; status.Foreground = Brushes.White;
  messagePopup.Child = new Border { Background = new SolidColorBrush(Color.FromArgb(200, 10, 12, 16)), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 10, 5), Child = status };
  messagePopup.PlacementTarget = host;

  flash.Child = new Border { Width = 96, Height = 96, CornerRadius = new CornerRadius(48), Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), Child = flashGlyph };
  flash.PlacementTarget = host;
  var seekBox = new StackPanel(); seekBox.Children.Add(seekImage); seekBox.Children.Add(seekTime);
  seekPreview.Child = new Border { Background = new SolidColorBrush(Color.FromArgb(225, 10, 12, 16)), CornerRadius = new CornerRadius(6), Padding = new Thickness(4), Child = seekBox };
  seekPreview.PlacementTarget = position;
  position.MouseMove += (_, e) => ShowSeekPreview(e.GetPosition(position).X);
  position.MouseLeave += (_, _) => HideSeekPreview();
  overlayFrame.Background = new SolidColorBrush(Color.FromArgb(218, 12, 15, 21));
  overlay.Child = new Decorator { Child = overlayFrame }; overlay.PlacementTarget = host;
  overlay.MouseMove += (_, _) => lastMovement = DateTime.UtcNow;
  // The bar lies outside mpv's picture, so its wheel arrives here: it does what the wheel over the video does,
  // except over the volume slider (volume) and the speed list (its own wheel).
  controlBar.PreviewMouseWheel += (_, e) =>
  {
   if (e.OriginalSource is not DependencyObject source || IsInside(source, speed)) return;
   e.Handled = true;
   if (IsInside(source, volumeSlider)) { volumeSlider.Value = Math.Clamp(volumeSlider.Value + Math.Sign(e.Delta) * 5, 0, 100); return; }
   host.Wheel(e.Delta,Keyboard.Modifiers);
  };
  return controlBar;
 }
 /// <summary>A WPF wheel turn in mpv's notation, e.g. "Ctrl+WHEEL_UP".</summary>
 internal static string WheelInput(int delta, ModifierKeys modifiers) =>
  ((modifiers & ModifierKeys.Control) != 0 ? "Ctrl+" : (modifiers & ModifierKeys.Shift) != 0 ? "Shift+" : (modifiers & ModifierKeys.Alt) != 0 ? "Alt+" : "") + (delta > 0 ? "WHEEL_UP" : "WHEEL_DOWN");
 private static bool IsInside(DependencyObject element, DependencyObject container)
 {
  for (var current = element; current != null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
   if (ReferenceEquals(current, container)) return true;
  return false;
 }

 /// <summary>Clock and play symbol follow each poll; previous and next only appear when the library offers them.</summary>
 private void UpdateControls(double seconds)
 {
  clock.Text = Time(seconds); clockTotal.Text = durationSeconds is double duration ? Time(duration) : "–"; clock.ToolTip = Source?.Path;
  previousButton.Visibility = nextButton.Visibility = StepRequested != null ? Visibility.Visible : Visibility.Collapsed;
  KeepOverlayFresh();
 }
 private static string Time(double seconds) { var time = TimeSpan.FromSeconds(Math.Max(0, seconds)); return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss"); }

 private void ShowPlayState()
 {
  pause.Content = paused ? "" : "";
  flashGlyph.Text = paused ? "" : "";
  if (host.ActualWidth <= 0 || !playback.ShowMessages) return;
  InterfaceScaling.Fit((FrameworkElement)flash.Child);
  flash.HorizontalOffset = host.ActualWidth / 2 - 48; flash.VerticalOffset = host.ActualHeight / 2 - 48;
  flash.IsOpen = true; flash.Child.Opacity = 1;
  flashTimer?.Stop();
  flashTimer = new DispatcherTimerLike(TimeSpan.FromMilliseconds(650), () =>
  {
   var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)); fade.Completed += (_, _) => flash.IsOpen = false;
   flash.Child.BeginAnimation(OpacityProperty, fade);
  });
 }

 private void ShowSeekPreview(double x)
 {
  if (durationSeconds is not (> 0 and double duration) || position.ActualWidth <= TimelineThumbWidth) return;
  var seconds = Math.Clamp((x - TimelineThumbWidth / 2) / (position.ActualWidth - TimelineThumbWidth), 0, 1) * duration;
  seekTime.Text = Time(seconds) + (JumpMarks.Current(Marks, seconds) is { } mark ? " · " + mark.Title : "");
  InterfaceScaling.Fit((FrameworkElement)seekPreview.Child);
  seekPreview.HorizontalOffset = Math.Clamp(x - 124, 0, Math.Max(0, position.ActualWidth - 248));
  seekPreview.VerticalOffset = -(seekImage.Source != null ? seekImage.ActualHeight + 52 : 44);
  seekPreview.IsOpen = true;
  _ = LoadSeekFrameAsync(seconds,CancellationToken.None);
 }

 private PlayerControlRow controlRow = null!;
 internal bool ControlsFolded => controlRow.AnythingFolded;
 internal bool MoreMenuShown => controlRow.MenuShown;
 internal IReadOnlyList<MenuSpec> FoldedControls => controlRow.FoldedEntries();
 private void TrackMenu(Button target) => Menus.Open(target, TrackEntries(), (_, error) => ShowMessage(error.Message), PlacementMode.Top);
 /// <summary>Audio and subtitle tracks with the current one checked, and the subtitle file and offset.</summary>
 private IReadOnlyList<MenuSpec> TrackEntries()
 {
  var entries = new List<MenuSpec>();
  void Section(string title, ComboBox source)
  {
   entries.Add(MenuSpec.Heading(title));
   foreach (var choice in source.Items.OfType<TrackChoice>())
    entries.Add(new MenuCommand(choice.Label, () => source.SelectedItem = choice) { Checked = ReferenceEquals(source.SelectedItem, choice) });
  }
  Section(Tc("player", "Audio"), audio); entries.Add(MenuSpec.Separator); Section(T("Subtitles"), subtitles);
  entries.Add(MenuSpec.Separator);
  entries.Add(MenuSpec.Command(T("Audio earlier (−0.1 s)"),async()=>await Command(()=>ShiftAudioAsync(-1))));
  entries.Add(MenuSpec.Command(T("Audio later (+0.1 s)"),async()=>await Command(()=>ShiftAudioAsync(1))));
  entries.Add(MenuSpec.Command(T("Reset offset ({0} s)",AudioDelay.ToString("+0.0;-0.0;0.0",System.Globalization.CultureInfo.CurrentCulture)),async()=>await Command(()=>ShiftAudioAsync(0)),enabled:AudioDelay!=0));
  entries.Add(MenuSpec.Separator);
  foreach (var (label, action) in new (string, Func<Task>)[] { (T("Load subtitle file …"), () => LoadSubtitleFileAsync()),
   (T("Subtitles earlier (−0.1 s)"), () => ShiftSubtitlesAsync(-1)), (T("Subtitles later (+0.1 s)"), () => ShiftSubtitlesAsync(1)) })
   entries.Add(MenuSpec.Command(label, async () => await Command(action)));
  entries.Add(MenuSpec.Command(T("Reset offset ({0} s)", SubtitleDelay.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.CurrentCulture)),
   async () => await Command(() => ShiftSubtitlesAsync(0)), enabled: SubtitleDelay != 0));
  return entries;
 }

 private void ViewMenu(Button target) => Menus.Open(target, ViewEntries(), (_, error) => ShowMessage(error.Message), PlacementMode.Top);
 private IReadOnlyList<MenuSpec> ViewEntries() =>
 [
  new MenuCommand(T("Use time-based view"), () => { transforms.IsChecked = transforms.IsChecked != true; _ = SaveTransformsEnabledAsync(); }) { Checked = transforms.IsChecked == true },
  MenuSpec.Separator,
  MenuSpec.Command(T("Edit view at time …"), () => _ = Command(EditTransformAsync)),
  MenuSpec.Command(T("Reset view"), () => { manualTransform = PlayerTransformTimeline.Identity; ApplyTransform(); }),
  MenuSpec.Command(T("Manage points in time …"), () => _ = Command(ManageTransformsAsync)),
 ];

 // Full screen: the bar moves into a popup over the video and shows while the mouse moves.
 private void EnterOverlayMode()
 {
  if (controlBar.Parent is Panel parent) parent.Children.Remove(controlBar);
  overlayFrame.Child = controlBar; fullscreenMarksWanted=false;ShowMarkPanel(false); marksButton.IsEnabled=true;
  fullscreenButton.Content = ""; fullscreenButton.ToolTip = T("Leave full screen (Esc)");
  ShowOverlay();
 }
 private void LeaveOverlayMode()
 {
  overlay.IsOpen = false; overlayFrame.Child = null;fullscreenMarksWanted=false; marksButton.IsEnabled=true; ShowMarkPanel(marksWanted);
  if (Content is DockPanel root && !root.Children.Contains(controlBar)) { DockPanel.SetDock(controlBar, Dock.Bottom); root.Children.Insert(0, controlBar); }
  fullscreenButton.Content = ""; fullscreenButton.ToolTip = T("Full screen (F11)");
 }
 internal void ShowOverlay()
 {
  if (overlayFrame.Child == null || host.ActualWidth <= 0) return;
  lastMovement = DateTime.UtcNow;
  // The frame is measured inside its scaled parent, so its size stays in the same units as the place over the video.
  InterfaceScaling.Fit((FrameworkElement)overlay.Child);
  overlayFrame.Width = Math.Max(300, host.ActualWidth - 48);
  overlay.HorizontalOffset = 0; overlayFrame.Measure(new Size(overlayFrame.Width + 48, double.PositiveInfinity));
  overlay.VerticalOffset = host.ActualHeight - overlayFrame.DesiredSize.Height;
  overlay.IsOpen = true;
 }
 /// <summary>Simulates idle pointer input without depending on the user's actual mouse position.</summary>
 internal void SimulateRest(bool mouseOverControls = false)
 {
  if (GetCursorPos(out var point)) lastCursor = (point.X, point.Y);
  lastMovement = DateTime.MinValue; HideIdleControls(mouseOverControls);
 }
 /// <summary>Runs with every poll: shows the floating bar on mouse movement and hides it after 2.5 s of rest, unless the
 /// settings keep it.</summary>
 private void KeepOverlayFresh()
 {
  if (overlayFrame.Child == null) return;
  if (GetCursorPos(out var point) && (point.X, point.Y) != lastCursor) { lastCursor = (point.X, point.Y); ShowOverlay(); return; }
  HideIdleControls(overlay.IsMouseOver);
 }

 private void HideIdleControls(bool mouseOverControls)
 {
  if (overlayFrame.Child != null && playback.AutoHideControls && overlay.IsOpen && !MarkPanelVisible && !mouseOverControls && !controlBar.IsKeyboardFocusWithin && !paused && DateTime.UtcNow - lastMovement > TimeSpan.FromSeconds(2.5)) overlay.IsOpen = false;
 }

 [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
 [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);

 /// <summary>One-shot timer on the UI thread.</summary>
 private sealed class DispatcherTimerLike
 {
  private readonly System.Windows.Threading.DispatcherTimer timer;
  internal DispatcherTimerLike(TimeSpan delay, Action action)
  {
   timer = new() { Interval = delay };
   timer.Tick += (_, _) => { timer.Stop(); action(); };
   timer.Start();
  }
  internal void Stop() => timer.Stop();
 }
}
