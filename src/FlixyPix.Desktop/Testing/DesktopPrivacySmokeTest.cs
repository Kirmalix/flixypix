using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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
  Require(System.Text.Json.JsonSerializer.Deserialize<PrivacySettings>("{}")!.RestoreKey == "Shift+Escape", "Old settings lost the restore default.");
  var custom = new PrivacySettings { RestoreKey = "Ctrl+Alt+F11" };
  Require(System.Text.Json.JsonSerializer.Deserialize<PrivacySettings>(System.Text.Json.JsonSerializer.Serialize(custom))!.RestoreKey == custom.RestoreKey, "Custom restore shortcut did not persist.");
  var reset = new List<string>();
  var repaired = new PrivacySettings { PinHash = "plain", LockAfterMinutes = 9999, BossAction = (BossAction)7 }.Validated(reset, "Privacy.");
  Require(repaired.PinHash == null && repaired.LockAfterMinutes == 0 && repaired.BossAction == BossAction.Tray && reset.Count == 3, "Damaged privacy values were not reset.");

  // The settings page: impossible combinations are refused with their reason, a valid one reaches the result.
  var window = new SettingsWindow(this, _repository, _settings, _settings.DataDirectory, () => { }, () => { }); window.Show();
  try
  {
   window.SelectPage("Privacy"); await Settled();
   Capture((FrameworkElement)window.Content, Path.Combine(directory, "settings-privacy.png"));
   window.BossKey.Value = "Escape"; window.BossGlobal.IsChecked = true; window.RestoreKey.Value = "";
   Require(!window.TrySave() && window.LastError?.Contains(T("Choose a shortcut with a modifier to show FlixyPix again, for example Shift+Esc.")) == true, "A system-wide Esc was refused.");
   window.BossKey.Value = "Ctrl+Shift+H"; window.BossGlobal.IsChecked = false; window.BossActionChoice.Value = (int)BossAction.Vanish; window.RestoreKey.Value = "";
   Require(!window.TrySave() && window.LastError != null, "An empty restore shortcut was not refused.");
   window.RestoreKey.Value = "Ctrl+Shift+H";
   Require(!window.TrySave(), "Identical boss and restore shortcuts were accepted.");
   window.RestoreKey.Value = "Shift+Escape";
   window.BossActionChoice.Value = (int)BossAction.Tray; window.LockOnMinimize.IsChecked = true;
   Require(_repository.IsEncrypted || !window.TrySave() && window.LastError != null, "A lock that nothing can open was not refused.");
   window.LockOnMinimize.IsChecked = false; window.BlurKey.Value = "Ctrl+Shift+H";
   Require(!window.TrySave(), "The same key for boss and blur was not refused.");
   window.BlurKey.Value = "Ctrl+Shift+U"; window.BossActionChoice.Value = (int)BossAction.Minimize; window.BossPlaybackChoice.Value = (int)BossPlayback.Mute;
   window.NeutralTitle.IsChecked = true; window.NeutralTitleText.Text = "Notizen"; window.HideRecent.IsChecked = true;
   Require(window.TrySave() && window.Result is { BossKey: "Ctrl+Shift+H", Privacy: { BossAction: BossAction.Minimize, BossPlayback: BossPlayback.Mute, NeutralTitle: true, NeutralTitleText: "Notizen", HideRecent: true, RestoreKey: "Shift+Escape" } },
    "Valid privacy options did not reach the result.");
  }
  finally { window.CloseDiscarding(); }
  window = new SettingsWindow(this, _repository, _settings with { Privacy = new PrivacySettings { PinHash = pinHash } }, _settings.DataDirectory, () => { }, () => { }); window.Show();
  try { window.SelectPage("Privacy"); window.LockOnMinimize.IsChecked = true; Require(window.TrySave() && window.Result?.Privacy?.PinHash == pinHash, "A lock with a PIN was refused or lost the PIN."); }
  finally { window.CloseDiscarding(); }

  // Setting a first PIN also asks at start; a deliberate "off" stays off.
  window = new SettingsWindow(this, _repository, _settings with { Privacy = new PrivacySettings() }, _settings.DataDirectory, () => { }, () => { }); window.Show();
  try
  {
   window.SelectPage("Privacy");
   Require(window.AskPinAtStart.IsChecked == false && !window.AskPinAtStart.IsEnabled, "The start question was offered without a PIN.");
   window.SetPin(pinHash);
   Require(window.AskPinAtStart.IsChecked == true && window.AskPinAtStart.IsEnabled, "Setting a PIN did not switch on the start question.");
   window.RemovePin();
   Require(window.AskPinAtStart.IsChecked == false && !window.AskPinAtStart.IsEnabled, "Removing the PIN kept the start question.");
   window.SetPin(pinHash);
   Require(window.TrySave() && window.Result?.Privacy is { AskPinAtStart: true } && window.Result.Privacy.PinHash == pinHash, "The start question did not reach the result.");
  }
  finally { window.CloseDiscarding(); }
  window = new SettingsWindow(this, _repository, _settings with { Privacy = new PrivacySettings { PinHash = pinHash, AskPinAtStart = false } }, _settings.DataDirectory, () => { }, () => { }); window.Show();
  try
  {
   window.SelectPage("Privacy");
   window.SetPin(PinCode.Hash("1357"));
   Require(window.AskPinAtStart.IsChecked == false, "Changing a PIN overruled the choice not to be asked at start.");
  }
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
   Require(AppShell.RestoreHotkeyActive && !AppShell.GlobalHotkeyActive, "Minimizing did not register the return key.");
   SendMessage(AppShell.HotkeyWindow, 0x312, 0xB056, 0); await Settled();
   Require(WindowState == WindowState.Normal && AppShell.IsOnScreen(this), "Coming back from minimizing failed.");
   // Hiding releases the boss shortcut and registers only the separate restore shortcut.
   Use(new PrivacySettings { BossAction = BossAction.Vanish, BossPlayback = BossPlayback.Continue, BossGlobal = true, RestoreKey = "Ctrl+Alt+F11" }, "Ctrl+Alt+Shift+F12");
   Require(AppShell.GlobalHotkeyActive, "The system-wide boss key was not registered.");
   SendMessage(AppShell.HotkeyWindow, 0x312, 0xB055, 0); await Settled();
   Require(AppShell.IsHidden && !AppShell.IsOnScreen(this), "The system-wide boss key did not hide.");
   Require(!AppShell.GlobalHotkeyActive && AppShell.RestoreHotkeyActive, "Hidden app still reserved the boss key.");
   var handle = AppShell.HotkeyWindow;
   Require(ReservePrivacyKey(handle, 0xB057, 1|2|4, (uint)KeyInterop.VirtualKeyFromKey(Key.F12)), "Another consumer could not reserve the released boss key.");
   try { SendMessage(handle, 0x312, 0xB055, 0); Require(AppShell.IsHidden, "Stale boss message restored the app."); }
   finally { ReleasePrivacyKey(handle, 0xB057); }
   SendMessage(handle, 0x312, 0xB056, 0); await Settled();
   Require(AppShell.GlobalHotkeyActive && !AppShell.RestoreHotkeyActive, "Showing did not restore shortcut ownership.");
   Require(!AppShell.IsHidden && AppShell.IsOnScreen(this), "The system-wide boss key did not bring FlixyPix back.");
   // An occupied return key must never strand an invisible instance.
   Require(ReservePrivacyKey(handle, 0xB057, 1|2, (uint)KeyInterop.VirtualKeyFromKey(Key.F11)), "Could not reserve conflict fixture.");
   try { AppShell.Boss(); await Settled(); Require(!AppShell.IsHidden && AppShell.IsOnScreen(this), "Restore conflict stranded an invisible app."); }
   finally { ReleasePrivacyKey(handle, 0xB057); }
   Use(new PrivacySettings { BossGlobal = true }, "Escape");
   Require(!AppShell.GlobalHotkeyActive && AppShell.KeyListenerActive, "Esc was taken from other programs or not listened to.");
   Use(new PrivacySettings { BossGlobal = false }, "Escape");
   Require(!AppShell.KeyListenerActive, "Esc was listened to although system-wide is off.");

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
   Require(ReservePrivacyKey(AppShell.HotkeyWindow, 0xB057, 4, (uint)KeyInterop.VirtualKeyFromKey(Key.Escape)), "Could not reserve locked-return conflict fixture.");
   try
   {
    var checkpointItem = _repository.Search().First();
    await RefreshAsync(checkpointItem.Id);
    EditDescription.Text = "Synthetic privacy-lock checkpoint";
    _draftTimer.Stop(); // Lock must checkpoint even before the debounce timer runs.
    Require(AppLock.Lock(), "Conflict fixture could not lock."); await Settled();
    Require(_repository.GetMetadataDrafts().Any(d => d.MediaId == checkpointItem.Id && d.Description == "Synthetic privacy-lock checkpoint"), "Privacy lock did not checkpoint the last edit.");
    AppLock.HidePrompt(); await Settled();
    Require(AppLock.PromptShown || AppShell.TrayShown, "Return conflict stranded a hidden lock prompt.");
    await AppLock.Prompt!.EnterAsync(pin); await Settled();
    DiscardDetails(this, new RoutedEventArgs()); await Settled();
   }
   finally { ReleasePrivacyKey(AppShell.HotkeyWindow, 0xB057); }
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

   // The boss key with a PIN locks, leaves nothing on screen, and only the way back asks.
   muted.Clear();
   Use(new PrivacySettings { BossAction = BossAction.Vanish, BossPlayback = BossPlayback.Mute, PinHash = pinHash });
   AppShell.Boss(); await Settled();
   Require(AppLock.IsLocked && !AppLock.PromptShown && AppShell.IsHidden && !AppShell.TrayShown && !AppShell.IsOnScreen(this), "The boss key did not lock without showing anything.");
   SendMessage(AppShell.HotkeyWindow, 0x312, 0xB056, 0); await Settled();
   Require(AppLock.PromptShown && !AppShell.IsOnScreen(this), "The return shortcut went past the PIN.");
   await AppLock.Prompt!.EnterAsync("1357");
   Require(AppLock.IsLocked && !AppShell.IsOnScreen(this), "A wrong PIN brought the windows back.");
   await AppLock.Prompt!.EnterAsync(pin); await Settled();
   Require(!AppLock.IsLocked && AppShell.IsOnScreen(this) && muted.SequenceEqual([true, false]), "The right PIN did not bring windows and sound back.");
   Use(new PrivacySettings { BossAction = BossAction.Tray, PinHash = pinHash });
   AppShell.Boss(); await Settled();
   // Where Windows has no notification area (a hidden test desktop, a restarting Explorer), the prompt is the way back.
   Require(AppLock.IsLocked && AppShell.TrayShown != AppLock.PromptShown && !AppShell.IsOnScreen(this), $"The boss key with the tray icon did not lock quietly (tray {AppShell.TrayShown}, prompt {AppLock.PromptShown}).");
   File.WriteAllText(Path.Combine(directory, "privacy-boss-tray.txt"), AppShell.TrayShown ? "tray icon" : "no notification area, prompt shown");
   AppShell.ShowAll(); await Settled();
   Require(AppLock.PromptShown && !AppShell.IsOnScreen(this), "The tray icon went past the PIN.");
   await AppLock.Prompt!.EnterAsync(pin); await Settled();
   Use(new PrivacySettings { BossAction = BossAction.Minimize, PinHash = pinHash });
   AppShell.Boss(); await Settled();
   Require(AppLock.IsLocked && AppLock.Prompt is { IsVisible: true, WindowState: WindowState.Minimized } && !AppShell.IsOnScreen(this), "The boss key with minimizing left no way back or showed the prompt.");
   AppShell.ShowAll(); await Settled();
   Require(AppLock.Prompt is { WindowState: WindowState.Normal, ActualHeight: > 100 } && !AppShell.IsOnScreen(this), "The prompt did not come up from the taskbar in its full size.");
   Capture((FrameworkElement)AppLock.Prompt!.Content, Path.Combine(directory, "privacy-lock-boss.png"));
   await AppLock.Prompt!.EnterAsync(pin); await Settled();
   Require(!AppLock.IsLocked && AppShell.IsOnScreen(this), "Unlocking after the minimizing boss key did not bring the window back.");
   // The setting: switched off, and without a PIN, the boss key only hides.
   Use(new PrivacySettings { BossAction = BossAction.Vanish, PinHash = pinHash, LockWithBossKey = false });
   AppShell.Boss(); await Settled();
   Require(!AppLock.IsLocked && AppShell.IsHidden, "The boss key locked although the setting is off.");
   AppShell.ShowAll(); await Settled();
   Require(AppShell.IsOnScreen(this), "Switched off, coming back asked for something.");
   Require(new PrivacySettings().LockWithBossKey && System.Text.Json.JsonSerializer.Deserialize<PrivacySettings>("{}")!.LockWithBossKey && new PrivacySettings().AskPinAtStart && System.Text.Json.JsonSerializer.Deserialize<PrivacySettings>("{}")!.AskPinAtStart, "The defaults of the lock options changed.");
   Require(!new PrivacySettings { AskPinAtStart = true }.Validated([], "").AskPinAtStart && new PrivacySettings { AskPinAtStart = true, PinHash = pinHash }.Validated([], "").AskPinAtStart, "A start question without a PIN was kept.");

   // The question at program start: when it comes, and that only the right PIN passes.
   var asked = new PrivacySettings { AskPinAtStart = true, PinHash = pinHash };
   Require(StartLock.Needed(asked, choosing: false, encrypted: false) && StartLock.Needed(asked, choosing: true, encrypted: false) && StartLock.Needed(asked, choosing: true, encrypted: true), "The start question was missing.");
   Require(!StartLock.Needed(asked, choosing: false, encrypted: true) && StartLock.Needed(new PrivacySettings { PinHash = pinHash }, false, false) && !StartLock.Needed(new PrivacySettings { PinHash = pinHash, AskPinAtStart = false }, false, false) && !StartLock.Needed(new PrivacySettings { AskPinAtStart = true }, false, false), "The start question came without need.");
   Privacy.Prepare(new PrivacySettings { NeutralTitle = true, NeutralTitleText = "Notizen", BlockCapture = true });
   var question = new StartLockWindow(pinHash); question.Show(); await Settled();
   Privacy.Attach(question);
   Require(Privacy.ShownTitle(question) == "Notizen" && Privacy.IsCaptureBlocked(question), "The start question ignored the neutral title or the capture protection.");
   Capture((FrameworkElement)question.Content, Path.Combine(directory, "privacy-start-question.png"));
   await question.EnterAsync("1357");
   Require(!question.Opened && question.IsVisible && question.Feedback.Length > 0, "A wrong PIN passed the start question.");
   await question.EnterAsync(pin); await Settled();
   Require(question.Opened && !question.IsVisible, "The right PIN did not pass the start question.");
   Privacy.Prepare(_settings.Privacy);

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
   Use(new PrivacySettings { HideRecent = false }); await Settled();
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

 [DllImport("user32.dll", EntryPoint="RegisterHotKey")] private static extern bool ReservePrivacyKey(nint handle,int id,uint modifiers,uint key);
 [DllImport("user32.dll", EntryPoint="UnregisterHotKey")] private static extern bool ReleasePrivacyKey(nint handle,int id);
 [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, int message, nint wParam, nint lParam);
}
