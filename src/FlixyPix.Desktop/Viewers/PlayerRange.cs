using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>One range with a start and an end, for saving a scene and for looping: both begin with the same two marks, only
/// the result differs. The tools stand above the jump marks; while that list is hidden, a small row under the controls
/// appears as soon as a range is begun. The range shows on the time line.</summary>
internal sealed partial class VideoPlayerWindow
{
 private sealed record RangeTools(Button Start, Button End, TextBlock Label, Button Save, Button Loop, Button Discard);
 private readonly List<RangeTools> rangeTools = [];
 private readonly Canvas rangeLayer = new() { IsHitTestVisible = false };
 private readonly DockPanel rangeStrip = new() { Margin = new Thickness(8, 4, 8, 2), Visibility = Visibility.Collapsed };
 private readonly StackPanel panelRangeResult = new() { Visibility = Visibility.Collapsed };
 private readonly SemaphoreSlim abLoopGate = new(1, 1);

 internal double? RangeStart { get; private set; }
 internal double? RangeEnd { get; private set; }
 /// <summary>What actually loops in mpv; empty until the loop is switched on.</summary>
 internal AbLoopState AbLoop { get; private set; } = new();
 internal Task AbLoopUpdate { get; private set; } = Task.CompletedTask;
 internal string RangeSummary => rangeTools.Count > 0 ? rangeTools[0].Label.Text : "";
 internal bool RangeStripVisible => rangeStrip.Visibility == Visibility.Visible;
 internal FrameworkElement ControlsSurface => controlBar;
 private bool RangeComplete => RangeStart.HasValue && RangeEnd.HasValue;

 private static string RangeTime(double seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff", System.Globalization.CultureInfo.CurrentCulture);

 /// <summary>Symbols that belong together in one rounded group.</summary>
 private static Border Pill(params UIElement[] parts)
 {
  var row = new StackPanel { Orientation = Orientation.Horizontal };
  foreach (var part in parts)
  {
   if (part is Button button) { button.Padding = new Thickness(7, 3, 7, 3); button.Margin = new Thickness(0); button.FontSize = 13; }
   row.Children.Add(part);
  }
  var pill = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(2), BorderThickness = new Thickness(1), Child = row, VerticalAlignment = VerticalAlignment.Center };
  pill.SetResourceReference(Border.BorderBrushProperty, "DividerSoft"); return pill;
 }

 private RangeTools NewRangeTools()
 {
  Button Mark(string glyph, string text, string name, string action)
  {
   var button = Labeled(glyph, text, name + KeyHint(action)); AutomationProperties.SetName(button, name);
   button.Click += async (_, _) => await Command(() => RunActionAsync(action));
   return button;
  }
  var save = new Button { Content = T("Save as scene"), Style = (Style)Application.Current.Resources["Primary"], Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(2, 0, 2, 0), ToolTip = T("Save as scene") + KeyHint(PlayerActions.ClipCreate) };
  AutomationProperties.SetName(save, T("Save as scene"));
  save.Click += async (_, _) => await Command(() => RunActionAsync(PlayerActions.ClipCreate));
  var loop = Labeled("", T("Loop range"), T("Loop range"));
  loop.Click += (_, _) => AbLoopUpdate = Command(ToggleRangeLoopAsync);
  var discard = Symbol("", T("Discard range"));
  discard.Click += (_, _) => AbLoopUpdate = Command(ClearRangeAsync);
  var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
  label.Typography.NumeralAlignment = FontNumeralAlignment.Tabular; label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
  var tools = new RangeTools(Mark("", Tc("range", "Start"), T("Set range start"), PlayerActions.ClipStart), Mark("", Tc("range", "End"), T("Set range end"), PlayerActions.ClipEnd), label, save, loop, discard);
  rangeTools.Add(tools); return tools;
 }

 /// <summary>The small row under the controls, for a hidden list and for full screen.</summary>
 private DockPanel BuildRangeStrip()
 {
  var tools = NewRangeTools();
  foreach (var (element, dock) in new (UIElement, Dock)[] { (tools.Start, Dock.Left), (tools.End, Dock.Left), (tools.Discard, Dock.Right), (tools.Loop, Dock.Right), (tools.Save, Dock.Right) })
  { DockPanel.SetDock(element, dock); rangeStrip.Children.Add(element); }
  rangeStrip.Children.Add(tools.Label);
  // A narrow row keeps its symbols with their tooltips; the times and the main action stay readable.
  rangeStrip.SizeChanged += (_, _) =>
  {
   var words = rangeStrip.ActualWidth >= 720;
   foreach (var button in new[] { tools.Start, tools.End, tools.Loop })
    if (button.Content is StackPanel { Children: [TextBlock glyph, TextBlock text] }) { text.Visibility = words ? Visibility.Visible : Visibility.Collapsed; glyph.Margin = new Thickness(0, 0, words ? 7 : 0, 0); }
  };
  return rangeStrip;
 }

 /// <summary>The range at the top of the jump mark list: start and end at rest, the results once a range is begun.</summary>
 private Border BuildPanelRange()
 {
  var tools = NewRangeTools();
  tools.Start.Margin = new Thickness(0, 0, 3, 0); tools.End.Margin = new Thickness(3, 0, 0, 0);
  var heading = new TextBlock { Text = T("RANGE"), FontSize = 11, Margin = new Thickness(0, 0, 0, 7) }; heading.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
  var marks = new Grid(); marks.ColumnDefinitions.Add(new()); marks.ColumnDefinitions.Add(new()); Grid.SetColumn(tools.End, 1); marks.Children.Add(tools.Start); marks.Children.Add(tools.End);
  tools.Label.Margin = new Thickness(0, 7, 0, 7); tools.Save.Margin = new Thickness(0, 0, 0, 5); tools.Loop.Margin = new Thickness(0, 0, 3, 0);
  var loopRow = new DockPanel(); DockPanel.SetDock(tools.Discard, Dock.Right); loopRow.Children.Add(tools.Discard); loopRow.Children.Add(tools.Loop);
  panelRangeResult.Children.Add(tools.Label); panelRangeResult.Children.Add(tools.Save); panelRangeResult.Children.Add(loopRow);
  var block = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(14, 0, 12, 10), Child = new StackPanel { Children = { heading, marks, panelRangeResult } } };
  block.SetResourceReference(Border.BorderBrushProperty, "DividerSoft"); return block;
 }

 private void UpdateRangeControls()
 {
  var running = AbLoop.Active;
  var length = RangeComplete ? " · " + Time(RangeEnd!.Value - RangeStart!.Value) : "";
  var summary = RangeStart is { } start ? RangeTime(start) + " → " + (RangeEnd is { } end ? RangeTime(end) : "…") + length : "";
  foreach (var tools in rangeTools)
  {
   tools.Start.IsEnabled = player != null; tools.End.IsEnabled = player != null && RangeStart.HasValue;
   tools.Label.Text = summary; tools.Label.Visibility = summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
   tools.Save.Visibility = tools.Loop.Visibility = RangeComplete ? Visibility.Visible : Visibility.Collapsed;
   tools.Save.IsEnabled = RangeComplete; tools.Loop.IsEnabled = RangeComplete && durationSeconds > 0;
   tools.Discard.Visibility = RangeStart.HasValue ? Visibility.Visible : Visibility.Collapsed;
   var name = running ? T("End loop") : T("Loop range");
   if (tools.Loop.Content is StackPanel { Children: [_, TextBlock text] }) text.Text = name;
   tools.Loop.ToolTip = name; AutomationProperties.SetName(tools.Loop, name);
  }
  panelRangeResult.Visibility = RangeStart.HasValue ? Visibility.Visible : Visibility.Collapsed;
  // Stills mode has the marks in its own row.
  rangeStrip.Visibility = RangeStart.HasValue && !MarkPanelVisible && !stillsMode ? Visibility.Visible : Visibility.Collapsed;
  if (exportRangeButton != null) exportRangeButton.IsEnabled = RangeComplete && StillsExportRequested != null;
  RenderRange();
  if (overlay.IsOpen) ShowOverlay();
 }

 private void RenderRange()
 {
  rangeLayer.Children.Clear();
  var width = rangeLayer.ActualWidth;
  if (RangeStart is not { } start || position.Maximum <= 0 || width <= TimelineThumbWidth) return;
  double Place(double seconds) => TimelineThumbWidth / 2 + Math.Clamp(seconds / position.Maximum, 0, 1) * (width - TimelineThumbWidth);
  var left = Place(start);
  var bar = new Border { Width = RangeEnd is { } end ? Math.Max(2, Place(end) - left) : 2, Height = 10, CornerRadius = new CornerRadius(2), Opacity = RangeEnd.HasValue ? .5 : 1, Background = Theme.Brush("Accent") };
  Canvas.SetLeft(bar, left); Canvas.SetTop(bar, 6); rangeLayer.Children.Add(bar);
 }

 private void SetRangeStart()
 {
  var seconds = LastPositionSeconds;
  RangeStart = seconds; if (RangeEnd <= seconds) RangeEnd = null;
  Notify(T("Range start · {0}", Time(seconds)));
  if (AbLoop.Active) AbLoopUpdate = Command(ApplyRangeToLoopAsync);
  UpdateRangeControls();
 }

 private void SetRangeEnd()
 {
  var seconds = LastPositionSeconds;
  if (RangeStart is not { } start || seconds <= start) { Notify(T("Set the start of the range first and then a later end.") + KeyHint(PlayerActions.ClipStart)); return; }
  RangeEnd = seconds;
  Notify(T("Range end · {0}", Time(seconds)));
  if (AbLoop.Active) AbLoopUpdate = Command(ApplyRangeToLoopAsync);
  UpdateRangeControls();
 }

 internal void CreateSceneFromMarks()
 {
  if (RangeStart is not { } start || RangeEnd is not { } end) { Notify(T("Set the start of the range first and then a later end.") + KeyHint(PlayerActions.ClipStart)); return; }
  repository.UpsertScene(new Scene { MediaId = media.Id, Title = T("Scene {0}–{1}", Time(start), Time(end)), StartSeconds = start, EndSeconds = end });
  AbLoopUpdate = Command(ClearRangeAsync);
  RefreshMarks(); Notify(T("Scene created · {0}–{1}", Time(start), Time(end)));
 }

 private async Task ToggleRangeLoopAsync()
 {
  if (AbLoop.Active) { await SetLoopAsync(new()); return; }
  await ApplyRangeToLoopAsync();
  if (RangeStart is { } start && RangeEnd is { } end) Notify(T("Looping {0}–{1}", Time(start), Time(end)));
 }

 /// <summary>A running loop follows the range; an unfinished range ends it.</summary>
 private Task ApplyRangeToLoopAsync() => SetLoopAsync(RangeComplete ? new AbLoopState { Start = RangeStart, End = RangeEnd } : new());

 internal async Task ClearRangeAsync()
 {
  RangeStart = RangeEnd = null;
  UpdateRangeControls();
  if (AbLoop.Active) await SetLoopAsync(new());
 }

 private async Task SetLoopAsync(AbLoopState next)
 {
  await abLoopGate.WaitAsync();
  try
  {
   if (closing) return;
   next = next.Validate(durationSeconds ?? throw new InvalidOperationException(T("The player is not ready yet.")));
   try { await RequirePlayer().SetAbLoopAsync(next); }
   catch { AbLoop = new(); UpdateRangeControls(); throw; }
   AbLoop = next; UpdateRangeControls();
  }
  finally { abLoopGate.Release(); }
 }
}
