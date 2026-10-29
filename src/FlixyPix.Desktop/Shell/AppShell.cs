using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace FlixyPix.Desktop;

/// <summary>FlixyPix acts as one application on screen. Every window without owner joins the main window's group, so
/// main window, player and secondary windows always come to the front or go to the back together. The boss key
/// (default Esc, see "Privatsphäre") hides all windows at once, behind an icon in the notification area, without any
/// trace or by minimizing; playback pauses, goes mute or runs on. Optionally it works system-wide. While the lock
/// (<see cref="AppLock"/>) is on, bringing the windows back shows its prompt instead.</summary>
internal static class AppShell
{
 private static Window? root;
 private static bool installed;
 private static TrayIcon? tray;
 private static readonly List<nint> hiddenWindows = [];
 private static nint lastActive;
 private static HwndSource? hotkeyWindow;
 private static bool hotkeyRegistered, mutedByBoss, minimizedByBoss;
 private const int HotkeyId = 0xB055;

 /// <summary>Shortcut such as "Escape" or "Ctrl+Shift+H"; empty switches the boss key off.</summary>
 internal static string BossKey { get; set; } = "Escape";
 internal static bool IsHidden => hiddenWindows.Count > 0;
 internal static bool TrayShown => tray?.IsShown == true;
 /// <summary>Whether this desktop has a taskbar with a notification area (not on a hidden test desktop).</summary>
 internal static bool HasTaskbar => FindWindow("Shell_TrayWnd", null) != 0;
 /// <summary>Raised before hiding with the boss key or the lock; the library pauses playback.</summary>
 internal static event Action? Hiding;
 /// <summary>The boss key mutes (true) or gives the sound back (false).</summary>
 internal static event Action<bool>? Muting;
 /// <summary>Minimizing the main window hides FlixyPix into the notification area (setting of this computer).</summary>
 internal static bool MinimizeToTray { get; set; }
 /// <summary>Name of the open library, shown in the tray tooltip once there are several; null shows none.</summary>
 internal static string? LibraryName { get; set; }
 /// <summary>State the main window had before it was minimized; restoring returns to it.</summary>
 internal static WindowState RestoreState { get; private set; } = WindowState.Normal;
 /// <summary>The boss key is registered with Windows and works while other programs have the focus.</summary>
 internal static bool GlobalHotkeyActive => hotkeyRegistered;
 internal static nint HotkeyWindow => hotkeyWindow?.Handle ?? 0;
 internal static Window? Root => root;

 /// <summary>Takes the settings of this computer: boss key, tray, privacy and lock.</summary>
 internal static void Configure(DesktopSettings settings)
 {
  BossKey = settings.BossKey; MinimizeToTray = settings.MinimizeToTray;
  Privacy.Apply(settings.Privacy, startup: !installedPrivacy); installedPrivacy = true;
  AppLock.Configure(settings.Privacy);
  // A tray icon made with the old title and icon is made again on the next hide.
  if (tray is { IsShown: false }) { tray.Dispose(); tray = null; }
  RegisterHotkey();
 }
 private static bool installedPrivacy;

 /// <summary>One taskbar button per instance: only the main window gets one. Secondary windows show none by default;
 /// a window that appears before the main window (library choice, password, error at start) sets it on its own.
 /// Must run before the first window is created, because WPF seals the metadata on first use.</summary>
 internal static void PrepareTaskbar()
 {
  foreach (var type in typeof(AppShell).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Window)) && t != typeof(MainWindow) && t != typeof(LockWindow) && !t.IsAbstract && !t.ContainsGenericParameters))
   Window.ShowInTaskbarProperty.OverrideMetadata(type, new FrameworkPropertyMetadata(false));
 }

 internal static void Install(Window main)
 {
  root = main;
  main.StateChanged += RootStateChanged;
  main.Closed += (_, _) => { if (ReferenceEquals(root, main)) AppLock.Dismiss(); };
  if (installed) return;
  installed = true;
  // WPF raises Loaded on a window only when it has Loaded handlers of its own; the first SizeChanged comes for every window.
  EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(JoinGroup));
  EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(JoinGroup));
  // A class handler runs before the windows' own key handlers, so Esc hides before it would leave full screen or close.
  EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(BossKeyPressed));
  Application.Current.Exit += (_, _) => { tray?.Dispose(); UnregisterHotkey(); hotkeyWindow?.Dispose(); };
 }

 private static void JoinGroup(object sender, RoutedEventArgs e)
 {
  if (sender is not Window window) return;
  Privacy.Attach(window);
  // The lock prompt stands alone while everything else is hidden.
  if (root is null || ReferenceEquals(window, root) || window is LockWindow || window.Owner != null || !root.IsLoaded || !root.IsVisible) return;
  // A window that owns the main window (none today) must not become its child.
  for (var owner = root.Owner; owner != null; owner = owner.Owner) if (ReferenceEquals(owner, window)) return;
  window.Owner = root;
  // Plain windows (new Window { … }) keep the WPF default; in the group they show no button of their own.
  window.ShowInTaskbar = false;
 }

 private static void RootStateChanged(object? sender, EventArgs e)
 {
  if (sender is not Window window || !ReferenceEquals(window, root)) return;
  if (window.WindowState != WindowState.Minimized)
  {
   RestoreState = window.WindowState;
   if (minimizedByBoss) { minimizedByBoss = false; GiveSoundBack(); }
   return;
  }
  if (IsHidden) return;
  if (Privacy.Settings.LockOnMinimize && AppLock.Lock()) return;
  if (MinimizeToTray && !minimizedByBoss) HideAll(pause: false);
 }

 /// <summary>True when the key of this event is the boss key.</summary>
 internal static bool IsBossKey(Key key, ModifierKeys modifiers) =>
  PlayerHotkeys.TryParse(BossKey, out var wantedModifiers, out var wanted) && key == wanted && modifiers == wantedModifiers;

 private static void BossKeyPressed(object sender, KeyEventArgs e)
 {
  if (e.Handled || Keyboard.FocusedElement is HotkeyBox) return; // the settings field records keys
  var key = e.Key == Key.System ? e.SystemKey : e.Key;
  if (!IsHidden && Privacy.IsBlurKey(key, Keyboard.Modifiers)) { e.Handled = true; Privacy.SwitchBlur(!Privacy.BlurOn); return; }
  if (!IsBossKey(key, Keyboard.Modifiers)) return;
  if (IsHidden && !AppLock.PromptShown) return;
  e.Handled = true;
  Boss();
 }

 /// <summary>What the boss key does, as set under "Privatsphäre". On the lock prompt it hides the prompt as well.</summary>
 internal static void Boss()
 {
  var privacy = Privacy.Settings;
  if (AppLock.PromptShown) { AppLock.HidePrompt(); if (privacy.BossAction != BossAction.Vanish) ShowTray(); return; }
  if (IsHidden) return;
  switch (privacy.BossPlayback)
  {
   case BossPlayback.Pause: Hiding?.Invoke(); break;
   case BossPlayback.Mute: mutedByBoss = true; Muting?.Invoke(true); break;
  }
  if (privacy.BossAction == BossAction.Minimize && root != null)
  {
   // Minimizing then neither goes into the tray on its own; the lock on minimizing still applies.
   minimizedByBoss = true; root.WindowState = WindowState.Minimized;
   return;
  }
  HideAll(pause: false, trayIcon: privacy.BossAction == BossAction.Tray);
 }

 /// <summary>Hides every window of the application (also the player, dialogs and popups) and shows the tray icon.
 /// The boss key pauses playback; minimizing into the tray lets it run on. Without <paramref name="trayIcon"/> nothing
 /// remains on screen; the system-wide boss key, the lock prompt or a new start bring the windows back.</summary>
 internal static void HideAll(bool pause = true, bool trayIcon = true)
 {
  if (IsHidden) return;
  if (pause) Hiding?.Invoke();
  var foreground = GetForegroundWindow();
  EnumThreadWindows(GetCurrentThreadId(), (handle, _) =>
  {
   if (IsWindowVisible(handle) && handle != tray?.Handle && handle != HotkeyWindow && !AppLock.IsPrompt(handle)) hiddenWindows.Add(handle);
   return true;
  }, 0);
  lastActive = hiddenWindows.Contains(foreground) ? foreground : root != null ? new WindowInteropHelper(root).Handle : 0;
  foreach (var handle in hiddenWindows) ShowWindow(handle, SwHide);
  if (!trayIcon) return;
  ShowTray();
  // Without a tray icon (no taskbar, for example while Explorer restarts) nothing would bring the windows back.
  if (!TrayShown && !AppLock.IsLocked) ShowAll();
 }

 private static void ShowTray() { tray ??= new TrayIcon(ShowAll, Exit); tray.Show(); }

 /// <summary>Shows the hidden windows again in their former order and activates the one that was active. While locked,
 /// only the lock prompt appears.</summary>
 internal static void ShowAll()
 {
  if (AppLock.IsLocked) { tray?.Hide(); AppLock.ShowPrompt(); return; }
  if (!IsHidden) { if (minimizedByBoss && root != null) root.WindowState = RestoreState; return; }
  // The list runs from front to back; showing from the back keeps the order.
  for (var i = hiddenWindows.Count - 1; i >= 0; i--) ShowWindow(hiddenWindows[i], SwShowNoActivate);
  hiddenWindows.Clear();
  tray?.Hide();
  if (root is { WindowState: WindowState.Minimized } main) main.WindowState = RestoreState;
  if (lastActive != 0) SetForegroundWindow(lastActive);
  GiveSoundBack();
 }

 private static void GiveSoundBack() { if (!mutedByBoss) return; mutedByBoss = false; Muting?.Invoke(false); }

 /// <summary>The window is shown on screen as far as Windows is concerned (tests).</summary>
 internal static bool IsOnScreen(Window window) => IsWindowVisible(new WindowInteropHelper(window).Handle);

 internal static void Exit()
 {
  // Quitting from the tray while locked closes without showing anything.
  if (!AppLock.IsLocked) ShowAll();
  root?.Close();
 }

 /// <summary>Registers the boss key with Windows while "system-wide" is on; a key without Ctrl, Alt or Win stays local,
 /// so Esc is never taken from other programs.</summary>
 private static void RegisterHotkey()
 {
  UnregisterHotkey();
  if (!Privacy.Settings.BossGlobal || !PlayerHotkeys.TryParse(BossKey, out var modifiers, out var key) || !HasGlobalModifier(modifiers)) return;
  if (hotkeyWindow == null)
  {
   hotkeyWindow = new HwndSource(new HwndSourceParameters("FlixyPix-Hotkey") { ParentWindow = new nint(-3), Width = 0, Height = 0, WindowStyle = 0 });
   hotkeyWindow.AddHook(HotkeyHook);
  }
  var flags = (modifiers.HasFlag(ModifierKeys.Alt) ? 1u : 0) | (modifiers.HasFlag(ModifierKeys.Control) ? 2u : 0) | (modifiers.HasFlag(ModifierKeys.Shift) ? 4u : 0)
   | (modifiers.HasFlag(ModifierKeys.Windows) ? 8u : 0) | 0x4000; // MOD_NOREPEAT
  hotkeyRegistered = RegisterHotKey(hotkeyWindow.Handle, HotkeyId, flags, (uint)KeyInterop.VirtualKeyFromKey(key));
 }

 internal static bool HasGlobalModifier(ModifierKeys modifiers) => (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;

 private static void UnregisterHotkey()
 {
  if (hotkeyRegistered && hotkeyWindow != null) UnregisterHotKey(hotkeyWindow.Handle, HotkeyId);
  hotkeyRegistered = false;
 }

 private static nint HotkeyHook(nint window, int message, nint wParam, nint lParam, ref bool handled)
 {
  if (message != 0x312 || wParam != HotkeyId) return 0; // WM_HOTKEY
  handled = true;
  // The same key hides and brings back; out of the hidden state it shows the windows or the lock prompt.
  if (IsHidden && !AppLock.PromptShown || minimizedByBoss) ShowAll();
  else Boss();
  return 0;
 }

 private const int SwHide = 0, SwShowNoActivate = 8;
 private delegate bool EnumWindowsProc(nint handle, nint parameter);
 [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, nint parameter);
 [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
 [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? windowName);
 [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
 [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
 [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint handle);
 [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint handle, int id, uint modifiers, uint key);
 [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint handle, int id);
}

/// <summary>The icon in the notification area while FlixyPix is hidden: click shows it again, the context menu also ends it.</summary>
internal sealed class TrayIcon : IDisposable
{
 private const int CallbackMessage = 0x8000 + 0x46; // WM_APP + n
 private const int LeftButtonUp = 0x202, RightButtonUp = 0x205;
 private readonly HwndSource source;
 private readonly Action open, exit;
 private readonly nint icon, ownIcon;
 internal bool IsShown { get; private set; }
 internal nint Handle => source.Handle;

 internal TrayIcon(Action open, Action exit)
 {
  this.open = open; this.exit = exit;
  // A message-only window receives the clicks.
  source = new HwndSource(new HwndSourceParameters("FlixyPix-Tray") { ParentWindow = new nint(-3), Width = 0, Height = 0, WindowStyle = 0 });
  source.AddHook(Hook);
  var size = GetSystemMetrics(49); // SM_CXSMICON, already scaled for the screen
  var file = Path.Combine(AppContext.BaseDirectory, "Assets", "flixypix.ico");
  // With the neutral title the icon is the plain one of Windows as well.
  ownIcon = !Privacy.Settings.NeutralTitle && File.Exists(file) ? LoadImage(0, file, 1, size, size, 0x10) : 0; // IMAGE_ICON, LR_LOADFROMFILE
  icon = ownIcon != 0 ? ownIcon : Privacy.PlainIcon;
 }

 private Data Notification(int flags) => new()
 {
  cbSize = Marshal.SizeOf<Data>(), hWnd = source.Handle, uID = 1, uFlags = flags, uCallbackMessage = CallbackMessage, hIcon = icon,
  szTip = Tip(), szInfo = "", szInfoTitle = "",
 };

 /// <summary>With several instances the library name tells the icons apart (the tooltip holds at most 127 characters).</summary>
 private static string Tip()
 {
  var tip = Privacy.Settings.NeutralTitle ? Privacy.Settings.NeutralText
   : (AppShell.LibraryName is { Length: > 0 } name ? name + " · " : "") + T("FlixyPix is hidden · click to show it again");
  return tip.Length <= 127 ? tip : tip[..126] + "…";
 }

 internal void Show()
 {
  if (IsShown) return;
  var data = Notification(0x1 | 0x2 | 0x4); // NIF_MESSAGE | NIF_ICON | NIF_TIP
  IsShown = Shell_NotifyIcon(0, ref data); // NIM_ADD
 }

 internal void Hide()
 {
  if (!IsShown) return;
  var data = Notification(0);
  Shell_NotifyIcon(2, ref data); // NIM_DELETE
  IsShown = false;
 }

 private nint Hook(nint window, int message, nint wParam, nint lParam, ref bool handled)
 {
  if (message != CallbackMessage) return 0;
  handled = true;
  switch ((int)lParam & 0xFFFF)
  {
   case LeftButtonUp: open(); break;
   case RightButtonUp: ShowMenu(); break;
  }
  return 0;
 }

 private void ShowMenu()
 {
  var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
  var show = new MenuItem { Header = Privacy.Settings.NeutralTitle ? T("Show") : T("Show FlixyPix"), FontWeight = FontWeights.SemiBold }; show.Click += (_, _) => open();
  var quit = new MenuItem { Header = T("Quit") }; quit.Click += (_, _) => exit();
  menu.Items.Add(show); menu.Items.Add(new Separator()); menu.Items.Add(quit);
  // The menu closes properly on a click elsewhere only when its owner is in the foreground.
  AppShell.SetForegroundWindow(source.Handle);
  menu.IsOpen = true;
 }

 public void Dispose() { Hide(); source.Dispose(); if (ownIcon != 0) DestroyIcon(ownIcon); }

 [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
 private struct Data
 {
  public int cbSize; public nint hWnd; public int uID; public int uFlags; public int uCallbackMessage; public nint hIcon;
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
  public int dwState; public int dwStateMask;
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
  public int uVersionOrTimeout;
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
  public int dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
 }
 [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref Data data);
 [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, int type, int width, int height, int load);
 [DllImport("user32.dll")] private static extern nint LoadIcon(nint instance, nint name);
 [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
 [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
