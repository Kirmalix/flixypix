using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace FlixyPix.Desktop;

/// <summary>What the boss key does with the windows.</summary>
internal enum BossAction
{
 /// <summary>Hide every window, leave an icon in the notification area.</summary>
 Tray,
 /// <summary>Minimize to the taskbar.</summary>
 Minimize,
 /// <summary>Hide every window without any icon; the separate restore shortcut brings it back.</summary>
 Vanish,
}

/// <summary>What the boss key does with a running playback.</summary>
internal enum BossPlayback { Pause, Mute, Continue }

/// <summary>Privacy of this computer (appsettings.json): boss key behavior, the lock, blurred pictures and traces in Windows.
/// The boss key itself and "minimize to the notification area" stay in <see cref="DesktopSettings"/> for older files.</summary>
internal sealed record PrivacySettings
{
 public const int MaximumLockMinutes = 240;
 public const int MinimumPinLength = 4;
 public BossAction BossAction { get; init; }
 public BossPlayback BossPlayback { get; init; }
 /// <summary>The boss key works also while another program has the focus; on by default. A key without Ctrl, Alt or
 /// Win is only listened to, so the other program still gets it. Stored under a new name: the old "BossGlobal" was off
 /// by default and could not be switched on for Esc, so it carries no deliberate choice.</summary>
 [JsonPropertyName("BossSystemWide")]
 public bool BossGlobal { get; init; } = true;
 /// <summary>Registered only while hidden or minimized by the boss key.</summary>
 public string RestoreKey { get; init; } = "Shift+Escape";
 /// <summary>Lock after this many minutes without input; 0 switches it off.</summary>
 public int LockAfterMinutes { get; init; }
 public bool LockOnMinimize { get; init; }
 public bool LockWithWindows { get; init; }
 /// <summary>PIN for the lock as "pbkdf2-sha256$iterations$salt$hash"; null when none is set.</summary>
 public string? PinHash { get; init; }
 /// <summary>The boss key also locks while a PIN is set; showing FlixyPix again then asks for it.</summary>
 public bool LockWithBossKey { get; init; } = true;
 /// <summary>The PIN is asked for before anything else at program start, as anyone setting a PIN expects. Without a PIN nothing is asked.</summary>
 public bool AskPinAtStart { get; init; } = true;
 /// <summary>Thumbnails, portraits and the home page start blurred.</summary>
 public bool BlurPictures { get; init; }
 public string BlurKey { get; init; } = "Ctrl+Shift+U";
 /// <summary>File dialogs add nothing to "Recent" and the jump list; existing entries of FlixyPix are removed.</summary>
 public bool HideRecent { get; init; } = FlixyPix.Core.AppStorage.Current.Mode == FlixyPix.Core.StorageMode.Portable;
 /// <summary>Windows, taskbar and the tray icon show <see cref="NeutralTitleText"/> and a plain icon instead of FlixyPix.</summary>
 public bool NeutralTitle { get; init; }
 public string NeutralTitleText { get; init; } = "";
 /// <summary>Screenshots, screen recordings and screen sharing show the windows as black or not at all.</summary>
 public bool BlockCapture { get; init; }

 internal bool LocksAutomatically => LockAfterMinutes > 0 || LockOnMinimize || LockWithWindows;
 /// <summary>The title in effect when the neutral title is on.</summary>
 internal string NeutralText => string.IsNullOrWhiteSpace(NeutralTitleText) ? T("Media library") : NeutralTitleText.Trim();

 internal PrivacySettings Validated(List<string> reset, string prefix)
 {
  var result = this;
  if (!Enum.IsDefined(BossAction)) { reset.Add(prefix + nameof(BossAction)); result = result with { BossAction = BossAction.Tray }; }
  if (!Enum.IsDefined(BossPlayback)) { reset.Add(prefix + nameof(BossPlayback)); result = result with { BossPlayback = BossPlayback.Pause }; }
  if (LockAfterMinutes is < 0 or > MaximumLockMinutes) { reset.Add(prefix + nameof(LockAfterMinutes)); result = result with { LockAfterMinutes = 0 }; }
  if (PinHash != null && !PinCode.IsHash(PinHash)) { reset.Add(prefix + nameof(PinHash)); result = result with { PinHash = null }; }
  return result with { AskPinAtStart = AskPinAtStart && result.PinHash != null, RestoreKey = RestoreKey ?? "Shift+Escape", BlurKey = BlurKey ?? "", NeutralTitleText = NeutralTitleText ?? "" };
 }
}

/// <summary>The lock PIN is kept only as a salted PBKDF2 hash.</summary>
internal static class PinCode
{
 private const string Scheme = "pbkdf2-sha256";
 private const int Iterations = 210_000;

 internal static string Hash(string pin)
 {
  var salt = RandomNumberGenerator.GetBytes(16);
  var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32);
  return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
 }

 internal static bool IsHash(string value) => Parse(value) != null;

 internal static bool Verify(string pin, string? stored)
 {
  if (stored == null || Parse(stored) is not { } parsed) return false;
  var (iterations, salt, expected) = parsed;
  var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
  return CryptographicOperations.FixedTimeEquals(actual, expected);
 }

 private static (int Iterations, byte[] Salt, byte[] Hash)? Parse(string value)
 {
  var parts = value.Split('$');
  if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations is < 10_000 or > 10_000_000) return null;
  try { var salt = Convert.FromBase64String(parts[2]); var hash = Convert.FromBase64String(parts[3]); return salt.Length >= 8 && hash.Length >= 16 ? (iterations, salt, hash) : null; }
  catch (FormatException) { return null; }
 }
}
