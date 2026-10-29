using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace FlixyPix.Desktop;

/// <summary>Privacy on screen and in Windows (settings "Privatsphäre"): a neutral title and icon for windows, taskbar and
/// tray icon, protection against screen captures, no entries under "Recent", and blurred pictures until the pointer rests
/// on them. AppShell attaches every window; pictures opt in with <c>Privacy.Blur</c>.</summary>
public static class Privacy
{
 private static readonly ConditionalWeakTable<Window, WindowTraces> attached = new();
 private static readonly List<WeakReference<FrameworkElement>> blurred = [];
 private static readonly DispatcherTimer captureSweep = new() { Interval = TimeSpan.FromMilliseconds(400) };
 private static nint plainIcon;

 internal static PrivacySettings Settings { get; private set; } = new();
 /// <summary>Pictures are blurred right now (start value from the settings, toggled with the shortcut).</summary>
 internal static bool BlurOn { get; private set; }
 /// <summary>File dialogs add their files to "Recent" and the jump list.</summary>
 internal static bool AddToRecent => !Settings.HideRecent;
 internal static event Action? BlurChanged;

 static Privacy() => captureSweep.Tick += (_, _) => SweepCapture();

 /// <summary>Knows the settings before the main window exists, for windows shown while a library opens; no other effect.</summary>
 internal static void Prepare(PrivacySettings settings) => Settings = settings;

 /// <summary>Takes new settings; <paramref name="startup"/> also sets the blur to its start value.</summary>
 internal static void Apply(PrivacySettings settings, bool startup = false)
 {
  var before = Settings; Settings = settings;
  if (startup || before.BlurPictures != settings.BlurPictures) SwitchBlur(settings.BlurPictures);
  if (settings.HideRecent && (startup || !before.HideRecent)) ClearRecent();
  foreach (var window in Application.Current?.Windows.OfType<Window>() ?? []) Attach(window, refresh: true);
  if (settings.BlockCapture) { SweepCapture(); captureSweep.Start(); } else captureSweep.Stop();
  if (!settings.BlockCapture && before.BlockCapture) SweepCapture(); // takes the protection off popups too
 }

 /// <summary>Brings a window in line with the settings once it has a handle; called for every window by AppShell.</summary>
 internal static void Attach(Window window, bool refresh = false)
 {
  var handle = new WindowInteropHelper(window).Handle;
  if (handle == 0) return;
  if (!attached.TryGetValue(window, out var traces))
  {
   traces = new WindowTraces(window, handle); attached.Add(window, traces);
  }
  else if (!refresh) return;
  traces.Update();
 }

 /// <summary>The text Windows shows for the window (title bar, taskbar, Alt+Tab).</summary>
 internal static string ShownTitle(Window window)
 {
  var handle = new WindowInteropHelper(window).Handle; var text = new StringBuilder(512);
  return handle == 0 ? "" : GetWindowText(handle, text, text.Capacity) >= 0 ? text.ToString() : "";
 }

 internal static bool IsCaptureBlocked(Window window) =>
  GetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, out var affinity) && affinity != 0;

 /// <summary>Icon for neutral windows and the tray: the plain application icon of Windows.</summary>
 internal static nint PlainIcon => plainIcon != 0 ? plainIcon : plainIcon = LoadIcon(0, 32512);

 private sealed class WindowTraces
 {
  private readonly Window window;
  private readonly nint handle;
  private nint smallIcon, bigIcon;
  private bool neutral;
  private static readonly DependencyPropertyDescriptor TitleDescriptor = DependencyPropertyDescriptor.FromProperty(Window.TitleProperty, typeof(Window));

  internal WindowTraces(Window window, nint handle)
  {
   this.window = window; this.handle = handle;
   // WPF writes the title into the window on every change; the neutral one is put back right after.
   TitleDescriptor.AddValueChanged(window, TitleChanged);
   window.Closed += (_, _) => TitleDescriptor.RemoveValueChanged(window, TitleChanged);
  }

  private void TitleChanged(object? sender, EventArgs e) { if (neutral) SetWindowText(handle, Settings.NeutralText); }

  internal void Update()
  {
   var wanted = Settings.NeutralTitle;
   if (wanted) SetWindowText(handle, Settings.NeutralText);
   else if (neutral) SetWindowText(handle, window.Title);
   if (wanted && !neutral)
   {
    smallIcon = SendMessage(handle, SetIconMessage, 0, PlainIcon); bigIcon = SendMessage(handle, SetIconMessage, 1, PlainIcon);
   }
   else if (!wanted && neutral) { SendMessage(handle, SetIconMessage, 0, smallIcon); SendMessage(handle, SetIconMessage, 1, bigIcon); }
   neutral = wanted;
   Protect(handle, Settings.BlockCapture);
  }
 }

 /// <summary>Menus, tooltips and flyouts are windows of their own; while the protection is on they are covered as they open.</summary>
 private static void SweepCapture()
 {
  var on = Settings.BlockCapture;
  EnumThreadWindows(GetCurrentThreadId(), (handle, _) => { if (IsWindowVisible(handle)) Protect(handle, on); return true; }, 0);
 }

 private static void Protect(nint handle, bool on)
 {
  if (!GetWindowDisplayAffinity(handle, out var current)) return;
  var wanted = on ? ExcludeFromCapture : 0u;
  if (current == wanted || on && current == MonitorOnly) return;
  // Before Windows 10 2004 only "monitor" exists: captures show the window black instead of leaving it out.
  if (!SetWindowDisplayAffinity(handle, wanted) && on) SetWindowDisplayAffinity(handle, MonitorOnly);
 }

 /// <summary>Removes the entries of FlixyPix under "Recent" in its jump list (recent and frequent files).</summary>
 internal static bool ClearRecent()
 {
  try
  {
   var type = Type.GetTypeFromCLSID(new Guid("86c14003-4d6b-4ef3-a7b4-0506663b2e68"));
   if (type == null || Activator.CreateInstance(type) is not IApplicationDestinations destinations) return false;
   try { destinations.RemoveAllDestinations(); return true; }
   finally { Marshal.ReleaseComObject(destinations); }
  }
  catch (Exception error) when (error is COMException or InvalidCastException or UnauthorizedAccessException) { return false; }
 }

 // ---- Blurred pictures ----

 public static readonly DependencyProperty BlurProperty = DependencyProperty.RegisterAttached("Blur", typeof(bool), typeof(Privacy), new PropertyMetadata(false, BlurAttached));
 public static bool GetBlur(DependencyObject element) => (bool)element.GetValue(BlurProperty);
 public static void SetBlur(DependencyObject element, bool value) => element.SetValue(BlurProperty, value);

 private static void BlurAttached(DependencyObject target, DependencyPropertyChangedEventArgs e)
 {
  if (target is not FrameworkElement element || e.NewValue is not true) return;
  blurred.RemoveAll(reference => !reference.TryGetTarget(out _));
  blurred.Add(new WeakReference<FrameworkElement>(element));
  element.MouseEnter += (_, _) => Show(element, blur: false);
  element.MouseLeave += (_, _) => Show(element, BlurOn);
  element.SizeChanged += (_, _) => { if (element.Effect is BlurEffect) Show(element, blur: true); };
  Show(element, BlurOn && !element.IsMouseOver);
 }

 /// <summary>Switches the blur of all pictures; the shortcut toggles it.</summary>
 internal static void SwitchBlur(bool on)
 {
  BlurOn = on;
  foreach (var reference in blurred.ToArray())
   if (reference.TryGetTarget(out var element)) Show(element, on && !element.IsMouseOver); else blurred.Remove(reference);
  BlurChanged?.Invoke();
 }

 internal static bool IsBlurKey(Key key, ModifierKeys modifiers) =>
  PlayerHotkeys.TryParse(Settings.BlurKey, out var wantedModifiers, out var wanted) && key == wanted && modifiers == wantedModifiers;

 /// <summary>A picture that opted in is blurred right now (tests).</summary>
 internal static bool IsBlurred(FrameworkElement element) => element.Effect is BlurEffect;

 private static void Show(FrameworkElement element, bool blur)
 {
  if (!blur) { if (element.Effect is BlurEffect) element.Effect = null; return; }
  // Strong enough that faces cannot be made out, scaled with the picture.
  var size = Math.Min(element.ActualWidth, element.ActualHeight);
  element.Effect = new BlurEffect { Radius = Math.Clamp(size > 0 ? size / 4 : 32, 16, 80), RenderingBias = RenderingBias.Performance, KernelType = KernelType.Gaussian };
 }

 private const int SetIconMessage = 0x80;
 private const uint ExcludeFromCapture = 0x11, MonitorOnly = 0x1;
 private delegate bool EnumWindowsProc(nint handle, nint parameter);
 [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(nint handle, string text);
 [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int length);
 [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, int message, nint wParam, nint lParam);
 [DllImport("user32.dll")] private static extern nint LoadIcon(nint instance, nint name);
 [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint handle, uint affinity);
 [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint handle, out uint affinity);
 [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, nint parameter);
 [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
 [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

 [ComImport, Guid("12337d35-94c6-48a0-bce7-6a9c69d4d600"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 private interface IApplicationDestinations
 {
  void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
  void RemoveDestination([MarshalAs(UnmanagedType.IUnknown)] object item);
  void RemoveAllDestinations();
 }
}
