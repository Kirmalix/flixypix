using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

internal enum ColorScheme { System, Light, Dark }

/// <summary>Light and dark colour scheme. Every colour is an application brush whose colour is bound to a slot; a scheme
/// change only sets the slots, so XAML (via DynamicResource) and brushes captured in code switch together without
/// rebuilding windows. The binding matters: WPF freezes plain brushes held in application resources, and a frozen
/// brush can only be replaced, not recoloured. A bound brush cannot be frozen.</summary>
internal static class Theme
{
 /// <summary>Key, dark value, light value. The dark values are the original FlixyPix design; the accent family follows
 /// the raspberry of the logo (#F83058), darkened until white text on it stays readable.</summary>
 internal static readonly (string Key, string Dark, string Light)[] Palette =
 [
  ("WindowBackground", "#101621", "#F3F5F9"),
  ("ChromeBackground", "#171D29", "#FFFFFF"),
  ("SidebarBackground", "#141B28", "#EBEFF5"),
  ("DetailsBackground", "#171F2D", "#FAFBFD"),
  ("CardBackground", "#1A2332", "#FFFFFF"),
  ("CardHover", "#243047", "#E8EEF8"),
  ("SelectionBackground", "#3E2238", "#FBDDE5"),
  ("SelectionBorder", "#FF7A9A", "#C8194D"),
  ("MediaBackground", "#101722", "#E2E7EF"),
  ("PreviewBackground", "#0D1420", "#DCE2EB"),
  ("Field", "#222C3D", "#FFFFFF"),
  ("FieldHover", "#2E3C55", "#EDF1F7"),
  ("FieldBorder", "#34435B", "#C3CCD9"),
  ("PopupBackground", "#222C3D", "#FFFFFF"),
  ("PopupBorder", "#526482", "#AEB9CA"),
  ("ButtonBackground", "#2A3549", "#E3E8F0"),
  ("ButtonHover", "#405271", "#D2DAE6"),
  ("Divider", "#2C3546", "#DADFE7"),
  ("DividerSoft", "#283346", "#E2E6ED"),
  ("Text", "#F3F5FB", "#172033"),
  ("TextSoft", "#EAF0FA", "#1F2A3F"),
  ("Muted", "#A4B0C6", "#536078"),
  ("Subtle", "#8999B4", "#5F6C83"),
  ("IconMuted", "#667D9D", "#93A0B4"),
  ("Accent", "#D91E54", "#C8194D"),
  ("AccentHover", "#E0245A", "#A8124A"),
  ("OnAccent", "#FFFFFF", "#FFFFFF"),
  ("Focus", "#FF8FA8", "#C8194D"),
  ("Brand", "#FF6B8E", "#C8194D"),
  ("OnBrand", "#101621", "#FFFFFF"),
  ("ItemHighlight", "#55284A", "#FCE4EB"),
  ("ItemSelected", "#45233D", "#F8D0DC"),
  ("ScrollThumb", "#3A4A63", "#C0C9D6"),
  ("ScrollThumbHover", "#5B7195", "#97A5BA"),
  ("SwitchKnob", "#C9D3E6", "#5B677D"),
  ("SwitchBorder", "#6E7F9C", "#8792A6"),
  ("Gold", "#F3C76B", "#A86F00"),
  ("Error", "#FF8A80", "#B3261E"),
  ("Good", "#7ED39A", "#2B7A4B"),
  ("Chip", "#3E2238", "#FBDDE5"),
  ("Badge", "#C0131B29", "#C0131B29"),
  ("OnBadge", "#FFFFFF", "#FFFFFF"),
  ("TagIncluded", "#FF8FA8", "#B3164E"),
  ("TagExcluded", "#FFB074", "#A44A00"),
  ("TagUnused", "#7F8DA6", "#7C889C"),
  ("ChipFull", "#4A2440", "#F8D0DC"),
  ("ChipPartial", "#1E2838", "#EDF1F7"),
  ("ChipExcluded", "#4A3220", "#FBE3CF"),
  ("ChipSuggestion", "#1F342E", "#D7EFE0"),
  ("TimelineTrack", "#34435B", "#C3CCD9"),
  ("TimelineMark", "#F3C76B", "#C98A00"),
  ("SceneMark", "#7ED39A", "#2B9A5B"),
 ];

 /// <summary>Text/background pairs that must stay readable in both schemes (WCAG AA: 4.5 for text, 3 for large text and icons).</summary>
 internal static readonly (string Foreground, string Background, double Minimum)[] ContrastPairs =
 [
  ("Text", "WindowBackground", 4.5), ("Text", "ChromeBackground", 4.5), ("Text", "CardBackground", 4.5), ("Text", "Field", 4.5),
  ("Text", "ButtonBackground", 4.5), ("Text", "SelectionBackground", 4.5), ("Text", "ItemSelected", 4.5), ("TextSoft", "SidebarBackground", 4.5),
  ("Muted", "WindowBackground", 4.5), ("Muted", "ChromeBackground", 4.5), ("Muted", "SidebarBackground", 4.5), ("Muted", "DetailsBackground", 4.5),
  ("Subtle", "Field", 4.5), ("Subtle", "CardBackground", 4.5), ("OnAccent", "Accent", 4.5), ("OnBrand", "Brand", 3),
  ("Gold", "CardBackground", 3), ("Error", "ChromeBackground", 4.5), ("Good", "ChromeBackground", 3), ("TagIncluded", "SidebarBackground", 4.5),
  ("TagExcluded", "SidebarBackground", 4.5), ("Text", "ChipFull", 4.5), ("Text", "ChipExcluded", 4.5), ("Text", "ChipSuggestion", 4.5), ("OnBadge", "Badge", 4.5),
 ];

 private sealed class ColorSlot : INotifyPropertyChanged
 {
  private Color color;
  public Color Color { get => color; set { if (color == value) return; color = value; PropertyChanged?.Invoke(this, new(nameof(Color))); } }
  public event PropertyChangedEventHandler? PropertyChanged;
 }
 private static readonly Dictionary<string, SolidColorBrush> brushes = new(StringComparer.Ordinal);
 /// <summary>Windows whose title bar was set at their first size change.</summary>
 private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> titled = new();
 private static readonly Dictionary<string, ColorSlot> slots = new(StringComparer.Ordinal);
 internal static ColorScheme Mode { get; private set; } = ColorScheme.System;
 internal static bool IsDark { get; private set; } = true;
 internal static event Action? Changed;
 /// <summary>Keys whose brush was found frozen and had to be replaced; code holding the old brush no longer follows.</summary>
 internal static readonly SortedSet<string> ReplacedKeys = new(StringComparer.Ordinal);

 /// <summary>Adds the brushes to the application resources (dark until <see cref="Apply"/> runs) and keeps every window's title bar in the current scheme.</summary>
 internal static void Install(Application application)
 {
  foreach (var (key, dark, _) in Palette)
  {
   // A style with the same key would be silently replaced by the brush and fail far away when used.
   if (application.Resources.Contains(key)) throw new InvalidOperationException($"Resource key '{key}' is used by both App.xaml and the colour palette.");
   var slot = new ColorSlot { Color = Parse(dark) }; var brush = new SolidColorBrush();
   BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new Binding(nameof(ColorSlot.Color)) { Source = slot, Mode = BindingMode.OneWay });
   slots[key] = slot; brushes[key] = brush; application.Resources[key] = brush;
  }
  application.Resources[SystemColors.ControlBrushKey] = brushes["WindowBackground"];
  EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => ApplyTitleBar((Window)sender)));
  // Loaded only reaches windows with Loaded handlers of their own; the first size change reaches every window.
  EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((sender, _) =>
  {
   if (sender is Window window && titled.TryAdd(window, true)) ApplyTitleBar(window);
  }));
  SystemEvents.UserPreferenceChanged += (_, e) =>
  {
   if (e.Category == UserPreferenceCategory.General && Mode == ColorScheme.System) application.Dispatcher.BeginInvoke(() => Apply(ColorScheme.System));
  };
 }

 internal static Brush Brush(string key) => brushes.TryGetValue(key, out var brush) ? brush : throw new KeyNotFoundException("Unknown theme colour " + key);
 internal static Color Color(string key, bool dark) { var entry = Palette.First(p => p.Key == key); return Parse(dark ? entry.Dark : entry.Light); }

 internal static void Apply(ColorScheme mode)
 {
  Mode = mode; IsDark = mode switch { ColorScheme.Light => false, ColorScheme.Dark => true, _ => !SystemUsesLightTheme() };
  foreach (var (key, dark, light) in Palette)
  {
   var color = Parse(IsDark ? dark : light);
   if (!brushes[key].IsFrozen) { slots[key].Color = color; continue; }
   // Should not happen with bound brushes; replace it so DynamicResource users still follow.
   ReplacedKeys.Add(key);
   var replacement = new SolidColorBrush(color); brushes[key] = replacement; Application.Current.Resources[key] = replacement;
  }
  Application.Current.Resources[SystemColors.ControlBrushKey] = brushes["WindowBackground"];
  foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
  Changed?.Invoke();
 }

 internal static ColorScheme ParseMode(string? value) => value?.ToLowerInvariant() switch { "light" => ColorScheme.Light, "dark" => ColorScheme.Dark, _ => ColorScheme.System };
 internal static string ModeName(ColorScheme mode) => mode switch { ColorScheme.Light => "light", ColorScheme.Dark => "dark", _ => "system" };

 internal static double Contrast(Color foreground, Color background)
 {
  // Translucent colours (badges over thumbnails) are judged on the darkest and lightest image they may cover.
  static double Luminance(Color c)
  {
   static double Channel(byte v) { var s = v / 255d; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
   return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
  }
  static Color Over(Color top, Color bottom) { var a = top.A / 255d; byte Mix(byte t, byte b) => (byte)Math.Round(t * a + b * (1 - a)); return System.Windows.Media.Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B)); }
  double Ratio(Color f, Color b) { var (l1, l2) = (Luminance(f), Luminance(b)); return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05); }
  if (background.A == 255) return Ratio(foreground, background);
  return Math.Min(Ratio(foreground, Over(background, Colors.Black)), Ratio(foreground, Over(background, Colors.White)));
 }

 private static bool SystemUsesLightTheme()
 {
  try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is int value && value != 0; }
  catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return false; }
 }
 private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);

 private static void ApplyTitleBar(Window window)
 {
  var handle = new WindowInteropHelper(window).Handle; if (handle == 0) return;
  var dark = IsDark ? 1 : 0;
  // Attribute 20 is DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1 and later); older systems ignore it.
  _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
 }
 [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
