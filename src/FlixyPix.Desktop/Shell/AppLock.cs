using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>The lock of "Privatsphäre": after a time without input, on minimizing or when Windows locks, every window is
/// hidden and only a prompt remains. The password (or recovery key) of an encrypted library opens it, otherwise the PIN
/// of this computer; with both, either does. Running videos keep the idle lock away. The catalog stays open, so the lock
/// guards the screen, not the files; the encryption guards those.</summary>
internal static class AppLock
{
 private static readonly DispatcherTimer idleTimer = new() { Interval = TimeSpan.FromSeconds(10) };
 private static HwndSource? sessionWindow;
 private static LockWindow? prompt;
 private static PrivacySettings settings = new();

 /// <summary>Path of the open catalog when it is encrypted; null otherwise. Set by the main window.</summary>
 internal static Func<string?> EncryptedCatalog { get; set; } = () => null;
 internal static bool IsLocked { get; private set; }
 internal static bool PromptShown => prompt is { IsVisible: true };
 internal static LockWindow? Prompt => prompt;
 /// <summary>Something can open the lock: the library password or a PIN.</summary>
 internal static bool CanLock => EncryptedCatalog() != null || settings.PinHash != null;
 /// <summary>The message window that hears about Windows locking (tests send it the message).</summary>
 internal static nint SessionWindow => sessionWindow?.Handle ?? 0;
 /// <summary>Time without input; tests replace it.</summary>
 internal static Func<TimeSpan> IdleTime { get; set; } = SystemIdleTime;

 static AppLock() => idleTimer.Tick += (_, _) => CheckIdle();

 internal static void Configure(PrivacySettings privacy)
 {
  settings = privacy;
  if (privacy.LockAfterMinutes > 0) idleTimer.Start(); else idleTimer.Stop();
  if (privacy.LockWithWindows && sessionWindow == null)
  {
   sessionWindow = new HwndSource(new HwndSourceParameters("FlixyPix-Session") { ParentWindow = new nint(-3), Width = 0, Height = 0, WindowStyle = 0 });
   sessionWindow.AddHook(SessionHook);
   WTSRegisterSessionNotification(sessionWindow.Handle, 0); // NOTIFY_FOR_THIS_SESSION
   Application.Current.Exit += (_, _) => { if (sessionWindow != null) { WTSUnRegisterSessionNotification(sessionWindow.Handle); sessionWindow.Dispose(); } };
  }
 }

 /// <summary>Locks when the lock is set up and can be opened again; false otherwise.</summary>
 internal static bool Lock()
 {
  if (IsLocked) return true;
  if (!CanLock || AppShell.Root is not { IsLoaded: true }) return false;
  IsLocked = true;
  // Hidden by the boss key already: the prompt waits for the tray icon or the boss key.
  if (AppShell.IsHidden) return true;
  AppShell.HideAll(pause: true, trayIcon: false);
  ShowPrompt();
  return true;
 }

 /// <summary>A video plays (not paused) or a slideshow runs; the idle lock waits then.</summary>
 internal static bool Playing => SleepGuard.Active || Application.Current.Windows.OfType<VideoPlayerWindow>().Any(window => window.HasPlayer && !window.IsPaused);

 internal static void CheckIdle()
 {
  // Hidden by the boss key counts as well: the lock then waits behind the tray icon or the key.
  if (IsLocked || settings.LockAfterMinutes <= 0) return;
  if (IdleTime() >= TimeSpan.FromMinutes(settings.LockAfterMinutes) && !Playing) Lock();
 }

 internal static void ShowPrompt()
 {
  if (!IsLocked) return;
  prompt ??= new LockWindow();
  prompt.Reset();
  prompt.Show(); prompt.Activate();
 }

 internal static void HidePrompt() => prompt?.Hide();
 internal static bool Dismissing { get; private set; }

 /// <summary>The main window closed: the prompt goes with it, so it does not keep the application running.</summary>
 internal static void Dismiss()
 {
  Dismissing = true;
  try { prompt?.Close(); } finally { prompt = null; IsLocked = false; Dismissing = false; }
 }
 internal static bool IsPrompt(nint handle) => prompt != null && new WindowInteropHelper(prompt).Handle == handle;

 /// <summary>Checks a secret against the PIN and the library password; true opens the lock.</summary>
 internal static async Task<bool> TryUnlockAsync(string secret)
 {
  if (!IsLocked) return true;
  if (secret.Length == 0) return false;
  var catalog = EncryptedCatalog(); var pin = settings.PinHash;
  var right = await Task.Run(() =>
  {
   if (PinCode.Verify(secret, pin)) return true;
   if (catalog == null) return false;
   try { CatalogEncryption.Unlock(catalog, secret).Dispose(); return true; }
   catch (CryptographicException) { return false; }
  });
  if (!right) return false;
  IsLocked = false;
  prompt?.Hide();
  AppShell.ShowAll();
  return true;
 }

 /// <summary>What opens the lock, for the prompt and the settings.</summary>
 internal static string SecretLabel => EncryptedCatalog() != null
  ? settings.PinHash != null ? T("Password, recovery key or PIN") : T("Password or recovery key")
  : T("PIN");

 private static nint SessionHook(nint window, int message, nint wParam, nint lParam, ref bool handled)
 {
  // WM_WTSSESSION_CHANGE with WTS_SESSION_LOCK
  if (message == 0x2B1 && wParam == 7 && settings.LockWithWindows) Lock();
  return 0;
 }

 private static TimeSpan SystemIdleTime()
 {
  var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
  return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
 }

 [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size; public uint Time; }
 [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
 [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(nint handle, int flags);
 [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(nint handle);
}

/// <summary>The prompt of the lock: alone on screen, with its own taskbar button; wrong tries wait a little longer each time.</summary>
internal sealed class LockWindow : Window
{
 private readonly PasswordBox secret = new() { Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 4, 0, 4) };
 private readonly TextBlock label = new() { TextWrapping = TextWrapping.Wrap };
 private readonly WindowFrame frame;
 private readonly Button open;
 private int failures;
 internal Task Checking { get; private set; } = Task.CompletedTask;
 internal string Feedback => frame.Status.Text;

 internal LockWindow()
 {
  Title = Privacy.Settings.NeutralTitle ? Privacy.Settings.NeutralText : T("FlixyPix is locked");
  Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
  System.Windows.Automation.AutomationProperties.SetName(secret, T("Password or PIN"));
  var panel = new StackPanel { Children = { label, secret } };
  frame = new WindowFrame(T("Locked"), T("The windows come back once it is open again."), panel, "", scroll: false);
  open = frame.Add(T("Unlock"), () => Checking = TryAsync(), primary: true);
  frame.Add(T("Quit"), AppShell.Exit);
  Content = frame;
  // Closing the prompt does not open the lock; it hides like the boss key.
  Closing += (_, e) => { if (AppLock.IsLocked && !AppLock.Dismissing) { e.Cancel = true; Hide(); } };
  Activated += (_, _) => secret.Focus();
 }

 internal void Reset() { label.Text = AppLock.SecretLabel; secret.Clear(); frame.Status.Text = ""; }

 /// <summary>Types a secret and tries it (tests).</summary>
 internal Task EnterAsync(string text) { secret.Password = text; return Checking = TryAsync(); }

 private async Task TryAsync()
 {
  if (!open.IsEnabled) return;
  open.IsEnabled = false; frame.Status.Text = T("Checking …");
  try
  {
   if (await AppLock.TryUnlockAsync(secret.Password)) { failures = 0; secret.Clear(); frame.Status.Text = ""; return; }
   failures++; secret.Clear();
   // A wait that grows with each wrong try makes guessing a short PIN slow.
   var wait = TimeSpan.FromSeconds(Math.Min(30, failures <= 2 ? 0 : 1 << Math.Min(failures - 3, 5)));
   frame.Status.Text = wait > TimeSpan.Zero ? T("Wrong · try again in {0} s", (int)wait.TotalSeconds) : T("Wrong · please try again");
   if (wait > TimeSpan.Zero) await Task.Delay(wait);
  }
  finally { open.IsEnabled = true; secret.Focus(); }
 }
}
