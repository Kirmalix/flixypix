using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace FlixyPix.Desktop;

/// <summary>The rubric "Privatsphäre": boss key, lock, blurred pictures and traces in Windows. All of it applies to this
/// computer (appsettings.json); the PIN is kept only as a hash.</summary>
internal sealed partial class SettingsWindow
{
 internal readonly SegmentedChoice BossActionChoice = new([((int)BossAction.Tray, T("Notification area")), ((int)BossAction.Minimize, T("Minimize")), ((int)BossAction.Vanish, T("Hide completely"))]);
 internal readonly SegmentedChoice BossPlaybackChoice = new([((int)BossPlayback.Pause, T("Pause")), ((int)BossPlayback.Mute, T("Mute")), ((int)BossPlayback.Continue, T("Keep playing"))]);
 internal readonly CheckBox BossGlobal = SettingsUi.Switch(), LockOnMinimize = SettingsUi.Switch(), LockWithWindows = SettingsUi.Switch(), BlurPictures = SettingsUi.Switch(),
  HideRecent = SettingsUi.Switch(), NeutralTitle = SettingsUi.Switch(), BlockCapture = SettingsUi.Switch();
 internal NumberBox LockMinutes = null!;
 internal readonly HotkeyBox BlurKey = new();
 internal readonly TextBox NeutralTitleText = new() { MinWidth = 260, MaxLength = 60 };
 private readonly TextBlock pinStatus = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
 private readonly Button setPin = SettingsUi.SmallButton(""), removePin = SettingsUi.SmallButton(T("Remove PIN"));
 private string? pinHash;
 private PrivacySettings privacyBefore = new();

 private void AddPrivacyPage(PrivacySettings privacy)
 {
  privacyBefore = privacy;
  LockMinutes = Number(0, PrivacySettings.MaximumLockMinutes, 1, "min", T("Lock after inactivity"));
  foreach (var check in new[] { BossGlobal, LockOnMinimize, LockWithWindows, BlurPictures, HideRecent, NeutralTitle, BlockCapture }) { check.Checked += (_, _) => Touch(); check.Unchecked += (_, _) => Touch(); }
  foreach (var choice in new[] { BossActionChoice, BossPlaybackChoice }) choice.Changed += Touch;
  BlurKey.Changed += Touch; NeutralTitleText.TextChanged += (_, _) => Touch();
  AutomationProperties.SetName(BlurKey, T("Shortcut for blurring")); AutomationProperties.SetName(NeutralTitleText, T("Neutral title"));
  foreach (var (check, name) in new[] { (BossGlobal, T("System-wide")), (LockOnMinimize, T("Lock on minimizing")), (LockWithWindows, T("Lock with Windows")),
   (BlurPictures, T("Blur pictures")), (HideRecent, T("No recent items")), (NeutralTitle, T("Neutral title")), (BlockCapture, T("Block screen captures")) }) AutomationProperties.SetName(check, name);
  setPin.Click += (_, _) => ChoosePin();
  removePin.Click += (_, _) => { pinHash = null; ShowPin(); Touch(); };
  LoadPrivacy(privacy);
  AddPage("Privacy",
   SettingsUi.Card(T("Boss key"),
    T("Hides FlixyPix with the player, dialogs and overlays at once. Click the field and press a key; Del turns it off. While Esc is the boss key, Esc no longer leaves full screen or closes dialogs (use F11, F and the buttons instead)."),
    new SettingRow(T("Key"), T("Applies on this computer, in all FlixyPix windows. Default: Esc."), BossKey),
    new SettingRow(T("Action"), T("Notification area: only its icon remains, a click brings everything back. Hide completely: nothing remains; the boss key again or starting FlixyPix brings it back."), BossActionChoice),
    new SettingRow(T("Playback"), T("What happens to a running video. Muted videos get their sound back when FlixyPix returns."), BossPlaybackChoice),
    new SettingRow(T("System-wide"), T("The boss key also works while another program is in front, and then brings FlixyPix back as well. Needs Ctrl, Alt or Win in the key, so Esc stays free for other programs."), BossGlobal),
    new SettingRow(T("Minimize to the notification area"), T("When minimized, FlixyPix and all its windows leave the taskbar; only the icon in the notification area remains, and a click on it brings everything back. Playback continues. Applies to this computer."), MinimizeToTray)),
   SettingsUi.Card(T("Lock"), T("Hides all windows behind a prompt. An encrypted library opens with its password or recovery key, otherwise with the PIN of this computer. The lock guards the screen; only the encryption protects the files."),
    new SettingRow(T("Lock after inactivity"), T("Minutes without mouse or keyboard input in Windows; 0 turns it off. A running video or slideshow keeps the lock away."), LockMinutes),
    new SettingRow(T("Lock on minimizing"), T("Also when the boss key minimizes."), LockOnMinimize),
    new SettingRow(T("Lock with Windows"), T("When Windows is locked (Win+L) or the computer goes to sleep with a lock."), LockWithWindows),
    new SettingRow(T("PIN"), T("At least four characters. Opens the lock also for an encrypted library; FlixyPix keeps only a check value of it."), SettingsUi.Buttons(pinStatus, setPin, removePin))),
   SettingsUi.Card(T("Blurred pictures"), T("Thumbnails, portraits and the pictures of the home page appear blurred until the pointer rests on them. Video and image viewer stay sharp."),
    new SettingRow(T("Blur pictures"), T("State at start. The shortcut switches at any time."), BlurPictures),
    new SettingRow(T("Shortcut"), T("Works in all FlixyPix windows. Default: Ctrl+Shift+U."), BlurKey)),
   SettingsUi.Card(T("Traces in Windows"), null,
    new SettingRow(T("No recent items"), T("File dialogs of FlixyPix add nothing to “Recent” and to the jump list of the taskbar; existing entries of FlixyPix are removed. Programs under “Open with” keep their own lists."), HideRecent),
    new SettingRow(T("Neutral title"), T("Title bar, taskbar, Alt+Tab and the icon in the notification area show this title and a plain icon instead of FlixyPix."), NeutralTitle),
    new SettingRow(T("Title"), T("Empty: “{0}”.", T("Media library")), NeutralTitleText),
    new SettingRow(T("Block screen captures"), T("Screenshots, screen recordings and screen sharing show FlixyPix windows black or leave them out."), BlockCapture)));
 }

 private void LoadPrivacy(PrivacySettings privacy)
 {
  BossActionChoice.Value = (int)privacy.BossAction; BossPlaybackChoice.Value = (int)privacy.BossPlayback; BossGlobal.IsChecked = privacy.BossGlobal;
  LockMinutes.Value = privacy.LockAfterMinutes; LockOnMinimize.IsChecked = privacy.LockOnMinimize; LockWithWindows.IsChecked = privacy.LockWithWindows;
  pinHash = privacy.PinHash; ShowPin();
  BlurPictures.IsChecked = privacy.BlurPictures; BlurKey.Value = privacy.BlurKey;
  HideRecent.IsChecked = privacy.HideRecent; NeutralTitle.IsChecked = privacy.NeutralTitle; NeutralTitleText.Text = privacy.NeutralTitleText; BlockCapture.IsChecked = privacy.BlockCapture;
 }

 private void ResetPrivacy()
 {
  BossKey.Value = "Escape"; MinimizeToTray.IsChecked = false;
  // The PIN is no option with a default; it stays until removed.
  var pin = pinHash; LoadPrivacy(new PrivacySettings()); pinHash = pin; ShowPin();
 }

 private void ShowPin()
 {
  pinStatus.Text = pinHash == null ? T("No PIN set") : T("PIN set");
  setPin.Content = pinHash == null ? T("Set PIN …") : T("Change PIN …");
  removePin.Visibility = pinHash == null ? Visibility.Collapsed : Visibility.Visible;
 }

 private void ChoosePin()
 {
  var hint = "";
  while (true)
  {
   var values = SecretDialogs.Passwords(this, T("PIN for the lock"), hint + T("The PIN opens the lock on this computer. Digits, letters or both."), T("New PIN"), T("Repeat the PIN"));
   if (values is null) return;
   if (values[0].Length < PrivacySettings.MinimumPinLength) { hint = T("The PIN needs at least {0} characters.\n\n", PrivacySettings.MinimumPinLength); continue; }
   if (values[0] != values[1]) { hint = T("The two entries differ.\n\n"); continue; }
   pinHash = PinCode.Hash(values[0]); ShowPin(); Touch(); return;
  }
 }

 /// <summary>The privacy of the page; throws <see cref="ArgumentException"/> with the reason when it cannot work.</summary>
 private PrivacySettings ChosenPrivacy()
 {
  var privacy = privacyBefore with
  {
   BossAction = (BossAction)BossActionChoice.Value, BossPlayback = (BossPlayback)BossPlaybackChoice.Value, BossGlobal = BossGlobal.IsChecked == true,
   LockAfterMinutes = (int)Math.Round(LockMinutes.Value ?? throw new ArgumentException(T("Please enter the minutes for the lock as a number from 0 to {0}.", PrivacySettings.MaximumLockMinutes))),
   LockOnMinimize = LockOnMinimize.IsChecked == true, LockWithWindows = LockWithWindows.IsChecked == true, PinHash = pinHash,
   BlurPictures = BlurPictures.IsChecked == true, BlurKey = BlurKey.Value,
   HideRecent = HideRecent.IsChecked == true, NeutralTitle = NeutralTitle.IsChecked == true, NeutralTitleText = NeutralTitleText.Text.Trim(), BlockCapture = BlockCapture.IsChecked == true,
  };
  var bossParsed = PlayerHotkeys.TryParse(BossKey.Value, out var bossModifiers, out _);
  if (privacy.BossGlobal && (!bossParsed || !AppShell.HasGlobalModifier(bossModifiers)))
   throw new ArgumentException(T("A system-wide boss key needs Ctrl, Alt or Win, for example Ctrl+Shift+H."));
  if (privacy.BossAction == BossAction.Vanish && (!privacy.BossGlobal || !bossParsed))
   throw new ArgumentException(T("“Hide completely” needs a system-wide boss key; otherwise only a new start of FlixyPix would bring it back."));
  if (privacy.BlurKey.Length > 0 && string.Equals(privacy.BlurKey, BossKey.Value, StringComparison.OrdinalIgnoreCase))
   throw new ArgumentException(T("Boss key and the shortcut for blurring must differ."));
  if (privacy.LocksAutomatically && privacy.PinHash == null && !repository.IsEncrypted)
   throw new ArgumentException(T("Set a PIN or encrypt the library, so the lock can be opened again."));
  return privacy;
 }
}
