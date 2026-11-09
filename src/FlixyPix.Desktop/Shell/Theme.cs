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
  ("ItemSelected", "#3E2238", "#FBDDE5"),
  ("ScrollThumb", "#3A4A63", "#C0C9D6"),
  ("ScrollThumbHover", "#5B7195", "#97A5BA"),
  ("SwitchKnob", "#C9D3E6", "#5B677D"),
  ("SwitchBorder", "#6E7F9C", "#8792A6"),
  ("Gold", "#F3C76B", "#A86F00"),
  ("StarEmpty", "#34435B", "#C3CCD9"),
  ("Error", "#FF8A80", "#B3261E"),
  ("Good", "#7ED39A", "#2B7A4B"),
  ("Chip", "#3E2238", "#FBDDE5"),
  ("Badge", "#C0131B29", "#C0131B29"),
  ("OnBadge", "#FFFFFF", "#FFFFFF"),
  ("TagIncluded", "#FF8FA8", "#B3164E"),
  ("TagExcluded", "#FFB074", "#A44A00"),
  ("TagUnused", "#7F8DA6", "#7C889C"),
  ("ChipFull", "#4A2440", "#F8D0DC"),
  ("ChipApplied", "#2E3A50", "#E2E7F0"),
  ("ChipPartial", "#1E2838", "#EDF1F7"),
  ("ChipExcluded", "#4A3220", "#FBE3CF"),
  ("ChipSuggestion", "#1F342E", "#D7EFE0"),
  ("TimelineTrack", "#34435B", "#C3CCD9"),
  ("TimelineMark", "#F3C76B", "#C98A00"),
  ("SceneMark", "#7ED39A", "#2B9A5B"),
  ("Skeleton", "#1E2839", "#E2E7EF"),
  ("SkeletonHighlight", "#2B3850", "#F2F5F9"),
 ];

 /// <summary>Text/background pairs that must stay readable in both schemes (WCAG AA: 4.5 for text, 3 for large text and icons).</summary>
 internal static readonly (string Foreground, string Background, double Minimum)[] ContrastPairs =
 [
  ("Text", "WindowBackground", 4.5), ("Text", "ChromeBackground", 4.5), ("Text", "CardBackground", 4.5), ("Text", "Field", 4.5),
  ("Text", "ButtonBackground", 4.5), ("Text", "SelectionBackground", 4.5), ("Text", "ItemSelected", 4.5), ("TextSoft", "SidebarBackground", 4.5),
  ("Muted", "WindowBackground", 4.5), ("Muted", "ChromeBackground", 4.5), ("Muted", "SidebarBackground", 4.5), ("Muted", "DetailsBackground", 4.5),
  ("Subtle", "Field", 4.5), ("Subtle", "CardBackground", 4.5), ("OnAccent", "Accent", 4.5), ("OnBrand", "Brand", 3),
  ("Gold", "CardBackground", 3), ("Error", "ChromeBackground", 4.5), ("Good", "ChromeBackground", 3), ("TagIncluded", "SidebarBackground", 4.5),
  ("TagExcluded", "SidebarBackground", 4.5), ("Text", "ChipFull", 4.5), ("Text", "ChipApplied", 4.5), ("Text", "ChipExcluded", 4.5), ("Text", "ChipSuggestion", 4.5), ("OnBadge", "Badge", 4.5),
  ("Brand", "ChromeBackground", 4.5), ("Brand", "DetailsBackground", 4.5), ("Brand", "CardBackground", 4.5),
 ];

 /// <summary>The colours a contrast theme of Windows prescribes.</summary>
 internal readonly record struct SystemTheme(Color Window, Color Text, Color Highlight, Color OnHighlight);

 /// <summary>Combinations of the kind Windows ships, for checking the derived palette.</summary>
 internal static readonly SystemTheme[] ContrastSamples =
 [
  new(Parse("#202020"), Parse("#FFFFFF"), Parse("#8EE3F0"), Parse("#263B50")),
  new(Parse("#FFFAEF"), Parse("#3D3D3D"), Parse("#903909"), Parse("#FFF5E3")),
  new(Parse("#2D3236"), Parse("#B6F6F0"), Parse("#ABCFF2"), Parse("#2D3236")),
  new(Parse("#000000"), Parse("#FFFFFF"), Parse("#D6B4FD"), Parse("#2B2B2B")),
  new(Parse("#000000"), Parse("#FFFFFF"), Parse("#1AEBFF"), Parse("#000000")),
  new(Parse("#FFFFFF"), Parse("#000000"), Parse("#37006E"), Parse("#FFFFFF")),
 ];

 /// <summary>The palette under a contrast theme: only its own colours, with a few tints where something must stand out from
 /// the window, such as buttons, hover and selection. Tints give way until the text on them stays readable.</summary>
 internal static Dictionary<string, Color> ContrastPalette(SystemTheme system)
 {
  var (window, text, highlight, onHighlight) = system;
  static Color Mix(Color from, Color to, double share)
  {
   byte Part(byte a, byte b) => (byte)Math.Round(a + (b - a) * share);
   return System.Windows.Media.Color.FromRgb(Part(from.R, to.R), Part(from.G, to.G), Part(from.B, to.B));
  }
  Color Tint(Color towards, double share)
  {
   while (share > 0 && Contrast(text, Mix(window, towards, share)) < 4.5) share -= .02;
   return Mix(window, towards, Math.Max(0, share));
  }
  var raised = Tint(text, .18); var chosen = Tint(highlight, .35);
  // Coloured text and marks need the window behind them; a highlight too close to it hands over to the text colour.
  var mark = Contrast(highlight, window) >= 4.5 ? highlight : text;
  var (accent, onAccent) = Contrast(onHighlight, highlight) >= 4.5 ? (highlight, onHighlight) : (text, window);
  return Palette.ToDictionary(entry => entry.Key, entry => entry.Key switch
  {
   "WindowBackground" or "ChromeBackground" or "SidebarBackground" or "DetailsBackground" or "CardBackground" or "MediaBackground" or "PreviewBackground" or "Field" or "PopupBackground" or "Badge" or "ChipPartial" or "ChipSuggestion" => window,
   "ButtonBackground" or "Skeleton" or "ChipApplied" or "ChipExcluded" => raised,
   "SelectionBackground" or "ItemSelected" or "Chip" or "ChipFull" or "CardHover" or "FieldHover" or "ButtonHover" or "ItemHighlight" or "SkeletonHighlight" => chosen,
   "Accent" or "AccentHover" => accent,
   "OnAccent" => onAccent,
   "Brand" or "Focus" or "SelectionBorder" or "ScrollThumbHover" or "TimelineMark" or "SceneMark" or "Gold" => mark,
   // A rating is information: given stars must differ from empty ones.
   "StarEmpty" => Mix(window, text, .35),
   "OnBrand" => window,
   _ => text,
  });
 }

 /// <summary>Under a contrast theme of Windows the system decides the colours, whatever scheme is chosen.</summary>
 internal static bool ContrastMode => SystemParameters.HighContrast || ContrastForTest != null;
 /// <summary>Tests cannot switch Windows; they hand in the colours instead.</summary>
 internal static SystemTheme? ContrastForTest { get; set; }
 private static SystemTheme CurrentSystemTheme() => ContrastForTest ?? new(SystemColors.WindowColor, SystemColors.WindowTextColor, SystemColors.HighlightColor, SystemColors.HighlightTextColor);

 /// <summary>The colour on screen right now, also under a contrast theme.</summary>
 internal static Color Current(string key) => slots[key].Color;

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
  // Items without a template of their own select like every other list, not in the blue of the system.
  application.Resources[SystemColors.HighlightBrushKey] = application.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = brushes["SelectionBackground"];
  application.Resources[SystemColors.HighlightTextBrushKey] = application.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = brushes["Text"];
  EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => ApplyTitleBar((Window)sender)));
  // Loaded only reaches windows with Loaded handlers of their own; the first size change reaches every window.
  EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((sender, _) =>
  {
   if (sender is Window window && titled.TryAdd(window, true)) { ApplyTitleBar(window); Motion.WindowOpens(window); }
  }));
  SystemEvents.UserPreferenceChanged += (_, e) =>
  {
   if (e.Category == UserPreferenceCategory.General && Mode == ColorScheme.System || e.Category == UserPreferenceCategory.Color && ContrastMode) application.Dispatcher.BeginInvoke(() => Apply(Mode));
  };
  SystemParameters.StaticPropertyChanged += (_, e) =>
  {
   if (e.PropertyName == nameof(SystemParameters.HighContrast)) application.Dispatcher.BeginInvoke(() => Apply(Mode));
  };
 }

 internal static Brush Brush(string key) => brushes.TryGetValue(key, out var brush) ? brush : throw new KeyNotFoundException("Unknown theme colour " + key);
 internal static Color Color(string key, bool dark) { var entry = Palette.First(p => p.Key == key); return Parse(dark ? entry.Dark : entry.Light); }

 internal static void Apply(ColorScheme mode)
 {
  var contrast = ContrastMode ? ContrastPalette(CurrentSystemTheme()) : null;
  Mode = mode; IsDark = contrast != null ? contrast["WindowBackground"] is var ground && ground.R + ground.G + ground.B < 384 : mode switch { ColorScheme.Light => false, ColorScheme.Dark => true, _ => !SystemUsesLightTheme() };
  foreach (var (key, dark, light) in Palette)
  {
   var color = contrast?[key] ?? Parse(IsDark ? dark : light);
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
  // Attributes 35 and 36 are the caption and its text (Windows 11); the bar then continues the window. Older systems ignore them.
  // A contrast theme draws its own bar (-1 is DWMWA_COLOR_DEFAULT).
  var caption = ContrastMode ? -1 : Colorref(Color("ChromeBackground", IsDark)); var text = ContrastMode ? -1 : Colorref(Color("Text", IsDark));
  _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
  _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
 }
 private static int Colorref(Color color) => color.R | color.G << 8 | color.B << 16;
 [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
