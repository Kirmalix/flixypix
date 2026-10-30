using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FlixyPix.Desktop;

/// <summary>Building blocks of the settings window: cards with divided rows, switches, number boxes with units,
/// segmented choices, star ratings, extension chips, path pickers and hotkey fields.</summary>
internal static class SettingsUi
{
 internal static Brush CardBackground => Theme.Brush("ChromeBackground");
 internal static Brush CardBorder => Theme.Brush("Divider");
 internal static Brush FieldBorder => Theme.Brush("FieldBorder");
 internal static Brush Accent => Theme.Brush("Accent");
 internal static Brush Focus => Theme.Brush("Focus");
 internal static Brush Error => Theme.Brush("Error");
 internal static Brush Good => Theme.Brush("Good");
 internal static Brush Chip => Theme.Brush("Chip");
 internal static Brush Gold => Theme.Brush("Gold");
 internal static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
 internal static Brush Muted => Theme.Brush("Muted");
 internal static Brush Field => Theme.Brush("Field");

 /// <summary>A rounded panel with a heading and its rows separated by thin dividers.</summary>
 internal static Border Card(string title, string? hint, params UIElement[] rows)
 {
  var panel = new StackPanel();
  var header = new StackPanel { Margin = new Thickness(16, 14, 16, rows.Length == 0 ? 14 : 10) };
  header.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
  if (hint != null) header.Children.Add(Hint(hint));
  panel.Children.Add(header);
  foreach (var row in rows) { panel.Children.Add(new Border { Height = 1, Background = CardBorder }); panel.Children.Add(row); }
  return new Border { Background = CardBackground, BorderBrush = CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 14), Child = panel };
 }
 internal static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 3, 0, 0) };
 internal static TextBlock Icon(string glyph, double size = 16) => new() { Text = glyph, FontFamily = Icons, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
 internal static CheckBox Switch() => new() { Style = (Style)Application.Current.Resources["Switch"], VerticalAlignment = VerticalAlignment.Center };
 internal static Button SmallButton(string text, string? tooltip = null) => new() { Content = text, ToolTip = tooltip, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
 internal static StackPanel Buttons(params UIElement[] buttons)
 {
  var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
  foreach (var button in buttons) panel.Children.Add(button);
  return panel;
 }

 /// <summary>Readable German names for shortcut parts such as "Ctrl", "Right" or "OemPeriod".</summary>
 internal static string KeyName(string part) => part switch
 {
  "Ctrl" => T("Ctrl"), "Shift" => T("Shift"), "Space" => T("Space"), "Right" => "→", "Left" => "←", "Up" => "↑", "Down" => "↓",
  "OemPeriod" => ".", "OemComma" => ",", "OemPlus" => "+", "OemMinus" => "−", "PageUp" => T("Page ↑"), "PageDown" => T("Page ↓"), "Enter" => T("Enter"),
  "Back" => T("Backspace"), "Delete" => T("Del"), "Insert" => T("Ins"), "Home" => Tc("key", "Home"), "End" => T("End"), "Add" => T("Num +"), "Subtract" => T("Num −"),
  "Multiply" => T("Num ×"), "Divide" => T("Num ÷"),
  _ when part.Length == 2 && part[0] == 'D' && char.IsDigit(part[1]) => part[1..],
  _ when part.StartsWith("NumPad", StringComparison.Ordinal) => T("Num ") + part[6..],
  _ => part,
 };
}

/// <summary>One setting: label and explanation on the left, its control on the right (or below when wide).</summary>
internal sealed class SettingRow : Grid
{
 internal string Label { get; }
 internal string? Explanation { get; }
 internal FrameworkElement Control { get; }
 /// <summary>Title of the card the row belongs to, shown in search results.</summary>
 internal string? Section { get; set; }

 internal SettingRow(string label, string? hint, FrameworkElement control, bool wide = false)
 {
  Label = label; Explanation = hint; Control = control; Background = Brushes.Transparent;
  var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 12, 16, wide ? 8 : 12) };
  text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
  if (hint != null) text.Children.Add(SettingsUi.Hint(hint));
  if (control is NumberBox number) number.SetLabel(label);
  else if (string.IsNullOrEmpty(AutomationProperties.GetName(control))) AutomationProperties.SetName(control, label);
  Children.Add(text);
  if (wide)
  {
   RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
   control.Margin = new Thickness(16, 0, 16, 14); SetRow(control, 1);
  }
  else
  {
   ColumnDefinitions.Add(new ColumnDefinition()); ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
   control.Margin = new Thickness(0, 8, 16, 8); control.VerticalAlignment = VerticalAlignment.Center; SetColumn(control, 1);
  }
  Children.Add(control);
 }

 /// <summary>Briefly highlights the row, e.g. after jumping to it from the settings search.</summary>
 internal void Flash()
 {
  var color = ((SolidColorBrush)Theme.Brush("ItemSelected")).Color; var brush = new SolidColorBrush(color); Background = brush;
  brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb(0, color.R, color.G, color.B), TimeSpan.FromSeconds(1.6)) { BeginTime = TimeSpan.FromSeconds(.4) });
 }
}

/// <summary>Numeric field with unit, arrow buttons, arrow keys and mouse wheel; out-of-range values are marked.</summary>
internal sealed class NumberBox : Border
{
 private readonly TextBox input = new() { BorderThickness = new Thickness(0), Margin = new Thickness(0), Padding = new Thickness(8, 6, 4, 6), Background = Brushes.Transparent, Width = 64, TextAlignment = TextAlignment.Right };
 private readonly double minimum, maximum, step;
 private readonly string unit;
 internal event Action? Changed;

 internal NumberBox(double minimum, double maximum, double step, string unit)
 {
  this.minimum = minimum; this.maximum = maximum; this.step = step; this.unit = unit;
  Background = SettingsUi.Field; BorderBrush = SettingsUi.FieldBorder; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(4); HorizontalAlignment = HorizontalAlignment.Left;
  var up = new RepeatButton { Content = "▴", Style = (Style)Application.Current.Resources["Spin"], Height = 15 }; up.Click += (_, _) => Nudge(1);
  var down = new RepeatButton { Content = "▾", Style = (Style)Application.Current.Resources["Spin"], Height = 15 }; down.Click += (_, _) => Nudge(-1);
  var panel = new StackPanel { Orientation = Orientation.Horizontal };
  panel.Children.Add(input);
  panel.Children.Add(new TextBlock { Text = unit, Foreground = SettingsUi.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), MinWidth = 16 });
  panel.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { up, down } });
  Child = panel;
  input.TextChanged += (_, _) => { Validate(); Changed?.Invoke(); };
  input.PreviewKeyDown += (_, e) => { if (e.Key is Key.Up or Key.Down) { Nudge(e.Key == Key.Up ? 1 : -1); e.Handled = true; } };
  input.LostKeyboardFocus += (_, _) => { if (IsValid) Value = Value!.Value; };
  input.GotKeyboardFocus += (_, _) => input.SelectAll();
  MouseWheel += (_, e) => { if (input.IsKeyboardFocusWithin) { Nudge(Math.Sign(e.Delta)); e.Handled = true; } };
  AutomationProperties.SetHelpText(input, T("{0} to {1} {2}", Format(minimum), Format(maximum), unit));
 }

 internal double? Value
 {
  get => double.TryParse(input.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
  set { if (value is { } number && Format(number) != input.Text) input.Text = Format(number); }
 }
 internal bool IsValid => Value is { } value && value >= minimum && value <= maximum;
 internal string Range => T("{0} to {1} {2}", Format(minimum), Format(maximum), unit).TrimEnd();
 internal void FocusInput() { input.Focus(); input.SelectAll(); }
 internal static string Format(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
 private void Nudge(int direction) { Value = Math.Clamp(Math.Round(((Value ?? minimum) + direction * step) / step) * step, minimum, maximum); input.CaretIndex = input.Text.Length; }
 private void Validate()
 {
  BorderBrush = IsValid ? SettingsUi.FieldBorder : SettingsUi.Error;
  ToolTip = IsValid ? null : T("Allowed: ") + Range;
 }
 internal void SetLabel(string label) => AutomationProperties.SetName(input, label);
}

/// <summary>A row of mutually exclusive buttons, e.g. 1 to 6 columns.</summary>
internal sealed class SegmentedChoice : Border
{
 private readonly List<RadioButton> options = [];
 internal event Action? Changed;

 internal SegmentedChoice(IEnumerable<(int Value, string Label)> values)
 {
  BorderBrush = SettingsUi.FieldBorder; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(5); ClipToBounds = true; HorizontalAlignment = HorizontalAlignment.Left;
  var group = Guid.NewGuid().ToString("N"); var panel = new StackPanel { Orientation = Orientation.Horizontal };
  foreach (var (value, label) in values)
  {
   var option = new RadioButton { Content = label, Tag = value, GroupName = group, Style = (Style)Application.Current.Resources["Segment"] };
   option.Checked += (_, _) => Changed?.Invoke(); options.Add(option); panel.Children.Add(option);
  }
  ((RadioButton)panel.Children[^1]).BorderThickness = new Thickness(0);
  Child = panel;
 }
 internal int Value
 {
  get => (int)(options.FirstOrDefault(o => o.IsChecked == true) ?? options[0]).Tag;
  set { foreach (var option in options) option.IsChecked = (int)option.Tag == value; }
 }
}

/// <summary>Minimum rating as five stars with half steps (0–10 like the catalog); 0 switches it off.</summary>
internal sealed class RatingPicker : StackPanel
{
 private readonly List<Border> fills = [];
 private readonly TextBlock caption = new() { Foreground = SettingsUi.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), MinWidth = 110 };
 private int value;
 internal event Action? Changed;

 public RatingPicker()
 {
  Orientation = Orientation.Horizontal; Focusable = true; FocusVisualStyle = null; Cursor = Cursors.Hand; Background = Brushes.Transparent;
  for (var index = 0; index < 5; index++)
  {
   var star = new Grid { Width = 24, Height = 24 };
   star.Children.Add(new TextBlock { Text = "★", FontSize = 20, Foreground = SettingsUi.FieldBorder, HorizontalAlignment = HorizontalAlignment.Center });
   var fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true, Width = 0, Child = new TextBlock { Text = "★", FontSize = 20, Foreground = SettingsUi.Gold, Width = 24, TextAlignment = TextAlignment.Center } };
   star.Children.Add(fill); fills.Add(fill);
   var position = index;
   star.MouseMove += (_, e) => Render(At(position, e.GetPosition(star).X));
   star.MouseLeftButtonUp += (_, e) => { var picked = At(position, e.GetPosition(star).X); Value = picked == value ? 0 : picked; Focus(); };
   Children.Add(star);
  }
  Children.Add(caption);
  MouseLeave += (_, _) => Render(value);
  KeyDown += (_, e) =>
  {
   var next = e.Key switch { Key.Right or Key.Up => value + 1, Key.Left or Key.Down => value - 1, Key.Home or Key.Back or Key.Delete => 0, Key.End => 10, _ => -1 };
   if (next >= 0) { Value = Math.Clamp(next, 0, 10); e.Handled = true; }
  };
  GotKeyboardFocus += (_, _) => caption.Foreground = SettingsUi.Focus;
  LostKeyboardFocus += (_, _) => caption.Foreground = SettingsUi.Muted;
  ToolTip = T("Click the left half of a star: half a star · click again: clear · use arrow keys to change");
  Render(0);
 }
 private static int At(int index, double x) => index * 2 + (x < 12 ? 1 : 2);
 internal int Value { get => value; set { var changed = this.value != value; this.value = value; Render(value); if (changed) Changed?.Invoke(); } }
 private Func<int, string> describe = shown => shown == 0 ? T("Off") : T("from {0:0.#} {1}", shown / 2d, (shown == 2 ? T("Star") : "Sternen"));
 /// <summary>Caption for a value; the settings show minimums ("ab 3 Sternen"), the details panel the rating itself.</summary>
 internal Func<int, string> Describe { get => describe; set { describe = value; Render(this.value); } }
 private void Render(int shown)
 {
  for (var index = 0; index < 5; index++) fills[index].Width = Math.Clamp(shown - index * 2, 0, 2) * 12;
  caption.Text = describe(shown);
  AutomationProperties.SetItemStatus(this, caption.Text);
 }
}

/// <summary>File extensions as removable chips with an input for new ones and a reset to the defaults.</summary>
internal sealed class ExtensionEditor : StackPanel
{
 private readonly WrapPanel chips = new();
 private readonly TextBox entry = new() { Width = 150, Margin = new Thickness(0) };
 private readonly TextBlock summary = new() { Foreground = SettingsUi.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
 private readonly IReadOnlyList<string> defaults;
 private List<string> values = [];
 internal event Action? Changed;

 internal ExtensionEditor(IReadOnlyList<string> defaults, string example)
 {
  this.defaults = defaults;
  AutomationProperties.SetName(entry, T("Add extension"));
  var placeholder = new TextBlock { Text = T("Extension, e.g. ") + example, Foreground = SettingsUi.Muted, IsHitTestVisible = false, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
  entry.TextChanged += (_, _) => placeholder.Visibility = entry.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
  entry.PreviewKeyDown += (_, e) => { if (e.Key is Key.Enter or Key.OemSemicolon or Key.OemComma or Key.Space) { Add(entry.Text); e.Handled = true; } };
  var add = SettingsUi.SmallButton(T("Add")); add.Click += (_, _) => { Add(entry.Text); entry.Focus(); };
  var reset = SettingsUi.SmallButton(T("Default"), T("Restore the bundled list")); reset.Click += (_, _) => { Values = [.. defaults]; Changed?.Invoke(); };
  var input = new Grid { Children = { entry, placeholder } };
  var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { input, add, reset, summary } };
  Children.Add(chips); Children.Add(bar);
 }

 internal List<string> Values { get => [.. values]; set { values = value.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(); Render(); } }

 private void Add(string text)
 {
  var added = text.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
   .Select(t => "." + t.TrimStart('.', '*').ToLowerInvariant()).Where(t => t.Length > 1 && t.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && t.IndexOf('.', 1) < 0).ToList();
  entry.Clear();
  if (added.Count == 0) return;
  Values = [.. values, .. added]; Changed?.Invoke();
 }
 private void Render()
 {
  chips.Children.Clear();
  foreach (var extension in values)
  {
   var remove = new Button { Content = SettingsUi.Icon("", 9), ToolTip = extension + " entfernen", Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(4, 0, 0, 0), Background = Brushes.Transparent };
   AutomationProperties.SetName(remove, extension + " entfernen");
   remove.Click += (_, _) => { values.Remove(extension); Render(); Changed?.Invoke(); };
   chips.Children.Add(new Border
   {
    Background = SettingsUi.Chip, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 1, 2, 1), Margin = new Thickness(0, 0, 6, 6),
    Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = extension, VerticalAlignment = VerticalAlignment.Center }, remove } },
   });
  }
  var custom = values.Count(v => !defaults.Contains(v, StringComparer.OrdinalIgnoreCase)); var missing = defaults.Count(d => !values.Contains(d, StringComparer.OrdinalIgnoreCase));
  summary.Text = Tn(values.Count, "1 extension", "{0} extensions") + (custom == 0 && missing == 0 ? T(" · Default") : T(" · {0} added, {1} removed", custom, missing));
 }
}

/// <summary>Text field with a browse button and a status line that explains what the current value means.</summary>
internal sealed class PathPicker : StackPanel
{
 private readonly TextBox box = new() { Margin = new Thickness(0) };
 private readonly TextBlock status = new() { FontSize = 12, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap };
 private readonly Func<string, (bool? Ok, string Message)> describe;
 internal event Action? Changed;

 internal PathPicker(bool folder, string? filter, Func<string, (bool? Ok, string Message)> describe)
 {
  this.describe = describe;
  var browse = SettingsUi.SmallButton(T("Browse …")); browse.Margin = new Thickness(8, 0, 0, 0);
  browse.Click += (_, _) =>
  {
   var current = box.Text.Trim(); var start = folder ? current : Path.GetDirectoryName(current);
   if (folder)
   {
    var dialog = new Microsoft.Win32.OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose folder") };
    if (Directory.Exists(start)) dialog.InitialDirectory = start;
    if (dialog.ShowDialog(Window.GetWindow(this)) == true) box.Text = dialog.FolderName;
   }
   else
   {
    var dialog = new Microsoft.Win32.OpenFileDialog { AddToRecent = Privacy.AddToRecent, Filter = filter ?? T("All files|*.*") };
    if (Directory.Exists(start)) dialog.InitialDirectory = start;
    if (dialog.ShowDialog(Window.GetWindow(this)) == true) box.Text = dialog.FileName;
   }
  };
  var line = new DockPanel(); DockPanel.SetDock(browse, Dock.Right); line.Children.Add(browse); line.Children.Add(box);
  Children.Add(line); Children.Add(status);
  box.TextChanged += (_, _) => { Describe(); Changed?.Invoke(); };
  Loaded += (_, _) => Describe();
 }
 internal string Text { get => box.Text.Trim(); set => box.Text = value; }
 internal void FocusInput() { box.Focus(); box.SelectAll(); }
 private void Describe()
 {
  var (ok, message) = describe(Text);
  status.Text = (ok switch { true => "✓ ", false => "⚠ ", _ => "" }) + message;
  status.Foreground = ok switch { true => SettingsUi.Good, false => SettingsUi.Error, _ => SettingsUi.Muted };
  box.BorderBrush = ok == false ? SettingsUi.Error : SettingsUi.FieldBorder;
 }
 internal bool IsValid => describe(Text).Ok != false;
}

/// <summary>Shortcut field: click, then press the keys; shows the result as key caps.</summary>
internal sealed class HotkeyBox : Border
{
 private readonly WrapPanel caps = new() { VerticalAlignment = VerticalAlignment.Center };
 private string value = "";
 private string? conflict;
 internal event Action? Changed;

 internal HotkeyBox()
 {
  Focusable = true; FocusVisualStyle = null; Cursor = Cursors.Hand; Width = 190; MinHeight = 34; Padding = new Thickness(6, 3, 6, 3);
  CornerRadius = new CornerRadius(4); Background = SettingsUi.Field; BorderThickness = new Thickness(1); Child = caps;
  MouseLeftButtonDown += (_, e) => { Focus(); e.Handled = true; };
  GotKeyboardFocus += (_, _) => Render(); LostKeyboardFocus += (_, _) => Render();
  PreviewKeyDown += Capture;
  Render();
 }
 internal string Value { get => value; set { var changed = this.value != value; this.value = value ?? ""; Render(); if (changed) Changed?.Invoke(); } }
 /// <summary>Esc is recorded as a key (boss key) instead of leaving the field; Tab still leaves.</summary>
 internal bool AcceptsEscape { get; init; }
 private bool shortcutActive=true;
 internal bool ShortcutActive { get=>shortcutActive; set { shortcutActive=value; Render(); } }
 internal string? Conflict { get => conflict; set { conflict = value; Render(); } }

 private void Capture(object sender, KeyEventArgs e)
 {
  var key = e.Key == Key.System ? e.SystemKey : e.Key;
  if (key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) return;
  e.Handled = true;
  if (key == Key.Escape && !AcceptsEscape) { MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); return; }
  if (key == Key.F11) return;
  if (key is Key.Back or Key.Delete) { Value = ""; return; }
  if (!PlayerHotkeys.IsModifierOnly(key)) Value = PlayerHotkeys.Format(Keyboard.Modifiers, key);
 }
 private void Render()
 {
  caps.Children.Clear();
  BorderBrush = IsKeyboardFocused ? SettingsUi.Focus : conflict != null ? SettingsUi.Error : SettingsUi.FieldBorder;
  ToolTip = conflict != null ? T("Also assigned to: {0}", conflict) : T("Click and press a key · Backspace/Del: remove assignment · Esc: cancel");
  if (value.Length == 0)
  {
   caps.Children.Add(new TextBlock { Text = IsKeyboardFocused ? T("Press a key …") : T("Not assigned"), Foreground = SettingsUi.Muted, FontStyle = IsKeyboardFocused ? FontStyles.Normal : FontStyles.Italic });
  }
  else foreach (var part in value.Split('+'))
  {
   caps.Children.Add(new Border
   {
    Background = SettingsUi.CardBackground, BorderBrush = SettingsUi.FieldBorder, BorderThickness = new Thickness(1, 1, 1, 2), CornerRadius = new CornerRadius(4),
    Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(0, 1, 4, 1), Child = new TextBlock { Text = SettingsUi.KeyName(part), FontSize = 12 },
   });
  }
  caps.Opacity=shortcutActive?1:.6;
  if(!shortcutActive)caps.Children.Add(new TextBlock { Text=T("Inactive"), Margin=new Thickness(6,0,0,0), Foreground=SettingsUi.Muted, VerticalAlignment=VerticalAlignment.Center });
  AutomationProperties.SetHelpText(this,shortcutActive?"":T("Player shortcuts are off. Assignments stay saved and work again when switched on."));
  AutomationProperties.SetItemStatus(this, (!shortcutActive?T("Inactive")+" · ":"") + (value.Length == 0 ? T("Not assigned") : string.Join(" + ", value.Split('+').Select(SettingsUi.KeyName))));
 }
}

/// <summary>Colour scheme choice as three tiles with a miniature window; "Wie Windows" shows a light and a dark half.</summary>
internal sealed class ThemePicker : StackPanel
{
 private readonly List<(ColorScheme Mode, RadioButton Tile)> tiles = [];
 internal event Action? Changed;

 internal ThemePicker()
 {
  Orientation = Orientation.Horizontal;
  var group = Guid.NewGuid().ToString("N");
  foreach (var (mode, label) in new[] { (ColorScheme.System, T("Follow Windows")), (ColorScheme.Light, T("Light")), (ColorScheme.Dark, T("Dark")) })
  {
   var preview = new Grid { Width = 148, Height = 92 };
   if (mode == ColorScheme.System)
   {
    preview.ColumnDefinitions.Add(new ColumnDefinition()); preview.ColumnDefinitions.Add(new ColumnDefinition());
    var light = Mockup(false); var dark = Mockup(true); Grid.SetColumn(dark, 1); preview.Children.Add(light); preview.Children.Add(dark);
   }
   else preview.Children.Add(Mockup(mode == ColorScheme.Dark));
   var frame = new Border { Child = preview, BorderBrush = SettingsUi.CardBorder, BorderThickness = new Thickness(1), ClipToBounds = true };
   var content = new StackPanel { Children = { frame, new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) } } };
   var tile = new RadioButton { GroupName = group, Content = content, Style = (Style)Application.Current.Resources["ThemeTile"], Margin = new Thickness(0, 0, 12, 0) };
   AutomationProperties.SetName(tile, T("Color scheme ") + label);
   tile.Checked += (_, _) => Changed?.Invoke();
   tiles.Add((mode, tile)); Children.Add(tile);
  }
 }

 internal ColorScheme Value
 {
  get => tiles.FirstOrDefault(t => t.Tile.IsChecked == true).Mode;
  set { foreach (var (mode, tile) in tiles) tile.IsChecked = mode == value; }
 }

 /// <summary>A tiny main window drawn with the fixed colours of one scheme, independent of the active scheme.</summary>
 private static Grid Mockup(bool dark)
 {
  Brush Paint(string key) { var brush = new SolidColorBrush(Theme.Color(key, dark)); brush.Freeze(); return brush; }
  var root = new Grid { Background = Paint("WindowBackground") };
  root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) }); root.RowDefinitions.Add(new RowDefinition());
  root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) }); root.ColumnDefinitions.Add(new ColumnDefinition());
  var bar = new Border { Background = Paint("ChromeBackground"), BorderBrush = Paint("Divider"), BorderThickness = new Thickness(0, 0, 0, 1) };
  bar.Child = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(2), Background = Paint("Brand"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(5, 0, 0, 0) };
  Grid.SetColumnSpan(bar, 2); root.Children.Add(bar);
  var side = new StackPanel { Background = Paint("SidebarBackground"), Margin = new Thickness(0) };
  foreach (var width in new[] { 18, 14, 16, 12 }) side.Children.Add(new Border { Width = width, Height = 3, CornerRadius = new CornerRadius(1.5), Background = Paint("Muted"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(5, 6, 0, 0) });
  Grid.SetRow(side, 1); root.Children.Add(side);
  var cards = new WrapPanel { Margin = new Thickness(5, 5, 0, 0) };
  for (var index = 0; index < 6; index++)
   cards.Children.Add(new Border { Width = 30, Height = 28, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 4, 4), Background = Paint("CardBackground"),
    BorderBrush = Paint(index == 0 ? "SelectionBorder" : "Divider"), BorderThickness = new Thickness(index == 0 ? 1.5 : .5),
    Child = new Border { Height = 16, VerticalAlignment = VerticalAlignment.Top, Background = Paint("MediaBackground"), Margin = new Thickness(2) } });
  Grid.SetRow(cards, 1); Grid.SetColumn(cards, 1); root.Children.Add(cards);
  return root;
 }
}

/// <summary>A #RRGGBB colour as text with a swatch and a few one-click presets.</summary>
internal sealed class ColorField : StackPanel
{
 private readonly TextBox input = new() { Width = 86, Padding = new Thickness(8, 5, 8, 5), VerticalAlignment = VerticalAlignment.Center };
 private readonly Border swatch = new() { Width = 26, Height = 26, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
 internal event Action? Changed;

 internal ColorField(string label, params string[] presets)
 {
  Orientation = Orientation.Horizontal; VerticalAlignment = VerticalAlignment.Center;
  swatch.BorderBrush = SettingsUi.FieldBorder;
  AutomationProperties.SetName(input, label);
  Children.Add(swatch); Children.Add(input);
  foreach (var preset in presets)
  {
   var button = new Button { Width = 22, Height = 22, Margin = new Thickness(6, 0, 0, 0), ToolTip = preset, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(preset)), BorderBrush = SettingsUi.FieldBorder };
   AutomationProperties.SetName(button, label + " " + preset);
   button.Click += (_, _) => Value = preset;
   Children.Add(button);
  }
  input.TextChanged += (_, _) => { Show(); Changed?.Invoke(); };
 }

 internal string Value
 {
  get => input.Text.Trim().ToUpperInvariant();
  set => input.Text = value;
 }
 internal bool IsValid => FlixyPix.Core.CatalogRepository.IsColor(Value);
 private void Show()
 {
  swatch.Background = IsValid ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(Value)) : Brushes.Transparent;
  input.BorderBrush = IsValid ? SettingsUi.FieldBorder : SettingsUi.Error;
 }
}
