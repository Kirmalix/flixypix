using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Privacy: the settings page refuses what cannot work; each boss key action and playback choice, the
 /// system-wide key, the lock (idle, Windows lock, minimizing, PIN), blurred tiles, the neutral title, the capture
 /// protection and the recent items each do what they say.</summary>
 private async Task RunPrivacySmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  async Task Settled() { await _lastRefresh; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
  const string pin = "2468";
  var pinHash = PinCode.Hash(pin);
  Require(PinCode.Verify(pin, pinHash) && !PinCode.Verify("2469", pinHash) && !PinCode.Verify(pin, null) && PinCode.IsHash(pinHash) && !PinCode.IsHash("2468"),
   "The PIN check value does not tell right from wrong.");
  var reset = new List<string>();
  var repaired = new PrivacySettings { PinHash = "plain", LockAfterMinutes = 9999, BossAction = (BossAction)7 }.Validated(reset, "Privacy.");
  Require(repaired.PinHash == null && repaired.LockAfterMinutes == 0 && repaired.BossAction == BossAction.Tray && reset.Count == 3, "Damaged privacy values were not reset.");

  // The settings page: impossible combinations are refused with their reason, a valid one reaches the result.
  var window = new SettingsWindow(this, _repository, _settings, _settings.DataDirectory, () => { }, () => { }); window.Show();
  try
  {
   window.SelectPage("Privacy"); await Settled();
   Capture((FrameworkElement)window.Content, Path.Combine(directory, "settings-privacy.png"));
   window.BossKey.Value = "Escape"; window.BossGlobal.IsChecked = true;
   Require(!window.TrySave() && window.LastError?.Contains(T("A system-wide boss key needs Ctrl, Alt or Win, for example Ctrl+Shift+H.")) == true, "A system-wide Esc was not refused.");
   window.BossKey.Value = "Ctrl+Shift+H"; window.BossGlobal.IsChecked = false; window.BossActionChoice.Value = (int)BossAction.Vanish;
   Require(!window.TrySave() && window.LastError != null, "Hiding completely without a system-wide key was not refused.");
   window.BossActionChoice.Value = (int)BossAction.Tray; window.LockOnMinimize.IsChecked = true;
   Require(_repository.IsEncrypted || !window.TrySave() && window.LastError != null, "A lock that nothing can open was not refused.");
   window.LockOnMinimize.IsChecked = false; window.BlurKey.Value = "Ctrl+Shift+H";
   Require(!window.TrySave(), "The same key for boss and blur was not refused.");
   window.BlurKey.Value = "Ctrl+Shift+U"; window.BossActionChoice.Value = (int)BossAction.Minimize; window.BossPlaybackChoice.Value = (int)BossPlayback.Mute;
   window.NeutralTitle.IsChecked = true; window.NeutralTitleText.Text = "Notizen"; window.HideRecent.IsChecked = true;
   Require(window.TrySave() && window.Result is { BossKey: "Ctrl+Shift+H", Privacy: { BossAction: BossAction.Minimize, BossPlayback: BossPlayback.Mute, NeutralTitle: true, NeutralTitleText: "Notizen", HideRecent: true } },
    "Valid privacy options did not reach the result.");
  }
  finally { window.CloseDiscarding(); }
  window = new SettingsWindow(this, _repository, _settings with { Privacy = new PrivacySettings { PinHash = pinHash } }, _settings.DataDirectory, () => { }, () => { }); window.Show();
  try { window.SelectPage("Privacy"); window.LockOnMinimize.IsChecked = true; Require(window.TrySave() && window.Result?.Privacy?.PinHash == pinHash, "A lock with a PIN was refused or lost the PIN."); }
  finally { window.CloseDiscarding(); }

  var idleBefore = AppLock.IdleTime; var muted = new List<bool>(); var paused = 0;
  void Muted(bool on) => muted.Add(on); void Paused() => paused++;
  AppShell.Muting += Muted; AppShell.Hiding += Paused;
  void Use(PrivacySettings privacy, string bossKey = "Escape") => AppShell.Configure(_settings with { BossKey = bossKey, Privacy = privacy });
  try
  {
   // Hide completely and mute: nothing stays on screen, no tray icon; showing gives the sound back.
   Use(new PrivacySettings { BossAction = BossAction.Vanish, BossPlayback = BossPlayback.Mute });
   AppShell.Boss(); await Settled();
   Require(AppShell.IsHidden && !AppShell.TrayShown && !AppShell.IsOnScreen(this) && muted.SequenceEqual([true]) && paused == 0, "Hiding completely left something on screen or did not mute.");
   AppShell.ShowAll(); await Settled();
   Require(!AppShell.IsHidden && AppShell.IsOnScreen(this) && muted.SequenceEqual([true, false]), "Coming back did not show the windows or give the sound back.");
   // Minimize and pause.
   Use(new PrivacySettings { BossAction = BossAction.Minimize });
   AppShell.Boss(); await Settled();
   Require(WindowState == WindowState.Minimized && paused == 1 && !AppShell.IsHidden, "The boss key did not minimize and pause.");
   AppShell.ShowAll(); await Settled();
   Require(WindowState == WindowState.Normal && AppShell.IsOnScreen(this), "Coming back from minimizing failed.");
   // System-wide: Windows hands the key to FlixyPix, the same key brings it back.
   Use(new PrivacySettings { BossAction = BossAction.Vanish, BossPlayback = BossPlayback.Continue, BossGlobal = true }, "Ctrl+Alt+Shift+F12");
   Require(AppShell.GlobalHotkeyActive, "The system-wide boss key was not registered.");
   SendMessage(AppShell.HotkeyWindow, 0x312, 0xB055, 0); await Settled();
   Require(AppShell.IsHidden && !AppShell.IsOnScreen(this), "The system-wide boss key did not hide.");
   SendMessage(AppShell.HotkeyWindow, 0x312, 0xB055, 0); await Settled();
   Require(!AppShell.IsHidden && AppShell.IsOnScreen(this), "The system-wide boss key did not bring FlixyPix back.");
   Use(new PrivacySettings { BossGlobal = true }, "Escape");
   Require(!AppShell.GlobalHotkeyActive, "Esc was taken from other programs.");

   // The lock: nothing to open it with means no lock.
   Use(new PrivacySettings { LockAfterMinutes = 1 });
   AppLock.IdleTime = () => TimeSpan.FromMinutes(2);
   if (!_repository.IsEncrypted) { AppLock.CheckIdle(); Require(!AppLock.IsLocked, "The lock closed without a PIN or password to open it."); }
   // Idle with a PIN: everything hides behind the prompt; a wrong PIN keeps it locked, the right one opens.
   Use(new PrivacySettings { LockAfterMinutes = 1, PinHash = pinHash, LockWithWindows = true, LockOnMinimize = true });
   AppLock.IdleTime = () => TimeSpan.FromSeconds(30); AppLock.CheckIdle();
   Require(!AppLock.IsLocked, "The lock closed before the idle time was over.");
   AppLock.IdleTime = () => TimeSpan.FromMinutes(2); AppLock.CheckIdle(); await Settled();
   var prompt = AppLock.Prompt;
   Require(AppLock.IsLocked && AppLock.PromptShown && prompt != null && !AppShell.IsOnScreen(this), "Idle time did not lock FlixyPix behind the prompt.");
   Capture((FrameworkElement)prompt!.Content, Path.Combine(directory, "privacy-lock.png"));
   AppShell.ShowAll(); await Settled();
   Require(AppLock.IsLocked && !AppShell.IsOnScreen(this), "Showing the windows went past the lock.");
   await prompt.EnterAsync("1357");
   Require(AppLock.IsLocked && prompt.Feedback.Length > 0 && !AppShell.IsOnScreen(this), "A wrong PIN opened the lock.");
   AppShell.Boss(); await Settled();
   Require(!AppLock.PromptShown && AppLock.IsLocked, "The boss key did not hide the lock prompt.");
   AppShell.ShowAll(); await Settled();
   Require(AppLock.PromptShown && !AppShell.IsOnScreen(this), "The prompt did not come back.");
   await prompt.EnterAsync(pin); await Settled();
   Require(!AppLock.IsLocked && !AppLock.PromptShown && AppShell.IsOnScreen(this), "The right PIN did not open the lock.");
   AppLock.IdleTime = () => TimeSpan.Zero;
   // Windows locks: the session message locks FlixyPix as well.
   SendMessage(AppLock.SessionWindow, 0x2B1, 7, 0); await Settled();
   Require(AppLock.IsLocked && !AppShell.IsOnScreen(this), "Locking Windows did not lock FlixyPix.");
   await AppLock.Prompt!.EnterAsync(pin); await Settled();
   // Minimizing locks; unlocking restores the window.
   WindowState = WindowState.Minimized; await Settled();
   Require(AppLock.IsLocked, "Minimizing did not lock.");
   await AppLock.Prompt!.EnterAsync(pin); await Settled();
   Require(!AppLock.IsLocked && WindowState == WindowState.Normal && AppShell.IsOnScreen(this), "Unlocking after minimizing did not restore the window.");

   // Blurred pictures: every tile picture is blurred, the pointer on one shows it, the shortcut switches all.
   Use(new PrivacySettings { BlurPictures = true }); Refresh(); await Settled();
   await Task.WhenAll(MediaList.Items.Cast<MediaCard>().Select(card => card.EnsureLoaded())); await Settled();
   var pictures = VisualDescendants(MediaList).OfType<FrameworkElement>().Where(Privacy.GetBlur).ToList();
   Require(pictures.Count > 0 && pictures.All(Privacy.IsBlurred), "The tile pictures were not blurred.");
   Capture(RootContent, Path.Combine(directory, "privacy-blur.png"));
   pictures[0].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
   Require(!Privacy.IsBlurred(pictures[0]), "The pointer on a picture did not show it.");
   pictures[0].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
   Require(Privacy.IsBlurred(pictures[0]), "Leaving a picture did not blur it again.");
   Require(Privacy.IsBlurKey(Key.U, ModifierKeys.Control | ModifierKeys.Shift), "The blur shortcut is not Ctrl+Shift+U.");
   Privacy.SwitchBlur(false); Require(pictures.All(p => !Privacy.IsBlurred(p)), "Switching the blur off left pictures blurred.");

   // Traces: neutral title in Windows while WPF keeps its own, protection against captures, no recent items.
   Use(new PrivacySettings { NeutralTitle = true, NeutralTitleText = "Notizen", BlockCapture = true, HideRecent = true });
   var loose = new Window { Title = "FlixyPix · Test", Width = 200, Height = 120, ShowActivated = false }; loose.Show(); await Settled();
   Require(Privacy.ShownTitle(this) == "Notizen" && Privacy.ShownTitle(loose) == "Notizen" && Title == ProductIdentity.Name, "The neutral title was not shown in Windows.");
   loose.Title = "FlixyPix · geändert"; await Settled();
   Require(Privacy.ShownTitle(loose) == "Notizen", "A title change brought the name back.");
   Require(Privacy.IsCaptureBlocked(this) && Privacy.IsCaptureBlocked(loose), "The windows were not protected against screen captures.");
   Require(!Privacy.AddToRecent && Privacy.ClearRecent(), "The recent items of FlixyPix were not removed.");
   Use(new PrivacySettings()); await Settled();
   Require(Privacy.ShownTitle(this) == Title && Privacy.ShownTitle(loose) == loose.Title && !Privacy.IsCaptureBlocked(this) && Privacy.AddToRecent, "Switching off did not restore title, capture and recent items.");
   loose.Close();
  }
  finally
  {
   AppLock.IdleTime = idleBefore; AppShell.Muting -= Muted; AppShell.Hiding -= Paused;
   if (AppLock.IsLocked && AppLock.Prompt is { } open) await open.EnterAsync(pin);
   AppShell.Configure(_settings); Privacy.SwitchBlur(_settings.Privacy.BlurPictures);
   if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
  }
 }

 private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
 {
  for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
  {
   var child = VisualTreeHelper.GetChild(root, i);
   yield return child;
   foreach (var nested in VisualDescendants(child)) yield return nested;
  }
 }

 [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, int message, nint wParam, nint lParam);
}
