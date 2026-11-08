using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>The PIN before anything of a library can be seen: the choice of libraries shows names and paths already. Like
/// the lock it guards the screen; only the encryption protects the files.</summary>
internal static class StartLock
{
 private static bool passed;

 /// <summary>A library that opens directly and is encrypted asks for its password, which is the stronger question; one
 /// question is enough then.</summary>
 internal static bool Needed(PrivacySettings privacy, bool choosing, bool encrypted) => privacy.AskPinAtStart && privacy.PinHash != null && (choosing || !encrypted);

 /// <summary>False when the person quit at the question. Asks once per program start.</summary>
 internal static bool Pass(PrivacySettings privacy, bool choosing, string libraryDirectory)
 {
  if (passed || !Needed(privacy, choosing, !choosing && IsEncrypted(libraryDirectory))) return true;
  var window = new StartLockWindow(privacy.PinHash!);
  window.ShowDialog();
  return passed = window.Opened;
 }

 private static bool IsEncrypted(string libraryDirectory)
 {
  // What cannot be read here fails again when the library opens; until then the PIN is the safe answer.
  try { return CatalogEncryption.IsEncrypted(Path.Combine(libraryDirectory, "catalog.db")); }
  catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
 }
}

/// <summary>The time a wrong try costs; it grows, which makes guessing a short PIN slow.</summary>
internal static class LockWait
{
 internal static TimeSpan After(int failures) => TimeSpan.FromSeconds(Math.Min(30, failures <= 2 ? 0 : 1 << Math.Min(failures - 3, 5)));
}

/// <summary>The question at program start, alone on screen, before any other window exists.</summary>
internal sealed class StartLockWindow : Window
{
 private readonly string pinHash;
 private readonly PasswordBox secret = new() { Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 4, 0, 4) };
 private readonly WindowFrame frame;
 private readonly Button open;
 private int failures;
 internal bool Opened { get; private set; }
 internal Task Checking { get; private set; } = Task.CompletedTask;
 internal string Feedback => frame.Status.Text;

 internal StartLockWindow(string pinHash)
 {
  this.pinHash = pinHash;
  Title = Privacy.Settings.NeutralTitle ? Privacy.Settings.NeutralText : T("FlixyPix is locked");
  Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
  System.Windows.Automation.AutomationProperties.SetName(secret, T("PIN"));
  var panel = new StackPanel { Children = { new TextBlock { Text = T("PIN"), TextWrapping = TextWrapping.Wrap }, secret } };
  frame = new WindowFrame(T("Welcome back"), T("Enter your PIN to get started."), panel, ((char)0xE72E).ToString(), scroll: false);
  open = frame.Add(T("Unlock"), () => Checking = TryAsync(), primary: true);
  frame.Add(T("Quit"), Close, cancel: true);
  Content = frame;
  // Neutral title and capture protection apply here too, before the main window exists.
  SourceInitialized += (_, _) => Privacy.Attach(this);
  Activated += (_, _) => secret.Focus();
 }

 /// <summary>Types a PIN and tries it (tests).</summary>
 internal Task EnterAsync(string text) { secret.Password = text; return Checking = TryAsync(); }

 private async Task TryAsync()
 {
  if (!open.IsEnabled) return;
  open.IsEnabled = false; frame.Status.Text = T("Checking …");
  var entered = secret.Password;
  var right = await Task.Run(() => PinCode.Verify(entered, pinHash));
  secret.Clear();
  if (right) { Opened = true; Close(); return; }
  failures++;
  var wait = LockWait.After(failures);
  frame.Status.Text = wait > TimeSpan.Zero ? T("That was not right. Try again in {0} s.", (int)wait.TotalSeconds) : T("That was not right. Please try again.");
  if (wait > TimeSpan.Zero) await Task.Delay(wait);
  open.IsEnabled = true; secret.Focus();
 }
}
