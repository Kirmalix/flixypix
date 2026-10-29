using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Password prompts for encrypted libraries, backups and exports. Secrets are never stored.</summary>
internal static class SecretDialogs
{
 internal static string[]? Passwords(Window? owner, string title, string intro, params string[] labels)
 {
  var (dialog, inputs) = PasswordWindow(owner, title, intro, labels);
  try { return dialog.ShowDialog() == true ? inputs.Select(input => input.Password).ToArray() : null; }
  finally { foreach (var input in inputs) input.Clear(); }
 }

 /// <summary>The password dialog on the shared frame; shown modally by <see cref="Passwords"/>, non-modally by the smoke test.</summary>
 internal static (Window Dialog, IReadOnlyList<PasswordBox> Inputs) PasswordWindow(Window? owner, string title, string intro, params string[] labels)
 {
  var panel = new StackPanel();
  var dialog = new Window { Owner = owner, Title = title + T(" · FlixyPix"), Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = owner is null,
   WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner };
  var inputs = new List<PasswordBox>();
  foreach (var label in labels)
  {
   panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, inputs.Count == 0 ? 0 : 6, 0, 0) });
   var input = new PasswordBox { Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 4, 0, 4) };
   System.Windows.Automation.AutomationProperties.SetName(input, label);
   panel.Children.Add(input); inputs.Add(input);
  }
  var frame = new WindowFrame(title, intro, panel, "");
  frame.Add("OK", () => dialog.DialogResult = true, primary: true); frame.Add(T("Cancel"), dialog.Close, cancel: true);
  dialog.Content = frame;
  dialog.Loaded += (_, _) => inputs.FirstOrDefault()?.Focus();
  return (dialog, inputs);
 }

 /// <summary>Shows the recovery key once; closing requires confirming that it was stored.</summary>
 internal static void ShowRecoveryKey(Window owner, string recoveryKey) => RecoveryKeyWindow(owner, recoveryKey).Dialog.ShowDialog();

 internal static (Window Dialog, CheckBox Stored, Button Done) RecoveryKeyWindow(Window owner, string recoveryKey)
 {
  var panel = new StackPanel();
  var dialog = new Window { Owner = owner, Title = T("Recovery key · FlixyPix"), Width = 640, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
   WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
  panel.Children.Add(new TextBox { Text = recoveryKey, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 16, Padding = new Thickness(8) });
  var copy = SettingsUi.SmallButton(T("Copy to clipboard")); copy.Margin = new Thickness(0, 8, 0, 0); copy.HorizontalAlignment = HorizontalAlignment.Left;
  copy.Click += (_, _) => Clipboard.SetText(recoveryKey);
  panel.Children.Add(copy);
  var stored = new CheckBox { Content = T("I have stored the recovery key safely."), Margin = new Thickness(0, 14, 0, 0) };
  panel.Children.Add(stored);
  var frame = new WindowFrame(T("Recovery key"),
   T("The library is now encrypted. Keep this key separate from the computer, for example printed. It opens the library, backups and exports, ") +
   T("in case the password is forgotten. It will not be shown again and cannot be recovered."), panel, "");
  var close = frame.Add(T("Done"), () => dialog.DialogResult = true, primary: true); close.IsEnabled = false;
  stored.Checked += (_, _) => close.IsEnabled = true; stored.Unchecked += (_, _) => close.IsEnabled = false;
  dialog.Closing += (_, e) => e.Cancel = stored.IsChecked != true;
  dialog.Content = frame;
  return (dialog, stored, close);
 }

 /// <summary>Opens a library, asking for its password or recovery key until it unlocks or the user cancels.</summary>
 internal static CatalogRepository OpenLibrary(Window? owner, string databasePath)
 {
  if (!CatalogEncryption.IsEncrypted(databasePath)) return LibraryOpeningWindow.Open(owner, databasePath, null);
  var hint = "";
  while (true)
  {
   var values = Passwords(owner, T("Unlock library"), hint + T("This library is encrypted.\n") + databasePath, T("Password or recovery key"));
   if (values is null) throw new OperationCanceledException(T("The encrypted library was not unlocked."));
   CatalogKey key;
   try { key = CatalogEncryption.Unlock(databasePath, values[0]); }
   catch (CryptographicException) { hint = T("The password or recovery key is wrong.\n\n"); continue; }
   using (key) return LibraryOpeningWindow.Open(owner, databasePath, key);
  }
 }

 /// <summary>For an encrypted backup or export, asks until the secret unlocks it. Returns false when cancelled.</summary>
 internal static bool TryAskContainerSecret(Window owner, string path, string purpose, out string? secret)
 {
  secret = null;
  if (!EncryptedContainer.IsContainer(path)) return true;
  var slots = EncryptedContainer.ReadKeys(path);
  var hint = "";
  while (true)
  {
   var values = Passwords(owner, purpose, hint + T("The file is encrypted. Enter the password or recovery key of the library it comes from."), T("Password or recovery key"));
   if (values is null) return false;
   try { CatalogEncryption.Unlock(slots, values[0]).Dispose(); secret = values[0]; return true; }
   catch (CryptographicException) { hint = T("The password or recovery key is wrong.\n\n"); }
  }
 }
}

public partial class MainWindow
{
 private string CatalogPath => Path.Combine(_settings.DataDirectory, "catalog.db");

 private void EncryptionMenu()
 {
  if (_repository.IsEncrypted) Menu((T("Change password …"), ChangeLibraryPassword), (T("Remove encryption …"), DisableLibraryEncryption));
  else Menu((T("Encrypt library with a password …"), EnableLibraryEncryption));
 }

 /// <summary>Closes the catalog for a whole-file operation and reopens it afterwards, even on failure.</summary>
 private async Task ReplaceRepositoryAsync(string title, Func<CatalogKey?> change)
 {
  if (_operation != null) { Status.Text = T("Please finish the running operation first."); return; }
  await RunOperation(title, async _ =>
  {
   await _lastRefresh;
   _repository.Dispose();
   CatalogKey? key = null;
   try { key = await Task.Run(change); }
   finally
   {
    using (key) _repository = key is null && CatalogEncryption.IsEncrypted(CatalogPath)
     ? SecretDialogs.OpenLibrary(this, CatalogPath)
     : LibraryOpeningWindow.Open(this, CatalogPath, key);
   }
  });
 }

 private async void EnableLibraryEncryption()
 {
  var values = SecretDialogs.Passwords(this, T("Encrypt library"),
   T("The catalog, backup packages and JSON exports of this library are encrypted with a password of your own. ") +
   T("Media files and the preview cache stay unchanged. The password is asked for on every start and not stored."),
   T("New password (at least {0} characters)", CatalogEncryption.MinimumPasswordLength), T("Repeat password"));
  if (values is null) return;
  if (values[0] != values[1]) { Message.Show(this, T("The passwords do not match.")); return; }
  try { CatalogEncryption.ValidatePassword(values[0]); } catch (ArgumentException ex) { Message.Show(this, ex.Message); return; }
  string? recovery = null;
  await ReplaceRepositoryAsync(T("Encrypt library"), () =>
  {
   recovery = CatalogEncryption.EnableEncryption(CatalogPath, values[0], PictureProgress(T("Encrypting pictures … {0:N0} of {1:N0}"))).RecoveryKey;
   return CatalogEncryption.Unlock(CatalogPath, values[0]);
  });
  if (recovery is not null) SecretDialogs.ShowRecoveryKey(this, recovery);
 }

 private void ChangeLibraryPassword()
 {
  var values = SecretDialogs.Passwords(this, T("Change password"),
   T("Older backups and exports still open with the password that applied when they were created, or with the recovery key."),
   T("Current password or recovery key"), T("New password (at least {0} characters)", CatalogEncryption.MinimumPasswordLength), T("Repeat new password"));
  if (values is null) return;
  if (values[1] != values[2]) { Message.Show(this, T("The new passwords do not match.")); return; }
  try { CatalogEncryption.ChangePassword(CatalogPath, values[0], values[1]); Status.Text = T("Password changed."); }
  catch (Exception ex) when (ex is CryptographicException or ArgumentException) { Message.Show(this, ex.Message, T("Change password"), MessageBoxButton.OK, MessageBoxImage.Warning); }
 }

 /// <summary>Encrypts the picture files of the encrypted library or saves them as plain image files again. Runs to the end:
 /// an interrupted switch would only be completed on the next opening.</summary>
 private async void TogglePictureEncryption()
 {
  var encrypt = !_repository.EncryptsPictures;
  var progress = PictureProgress(encrypt ? T("Encrypting pictures … {0:N0} of {1:N0}") : T("Decrypting pictures … {0:N0} of {1:N0}"));
  await RunOperation(encrypt ? T("Encrypt pictures") : T("Store pictures unencrypted"),
   _ => Task.Run(() => _repository.SetPictureEncryption(encrypt, progress)), refresh: false);
 }

 /// <summary>Shows in the status line how many picture files are rewritten; <paramref name="format"/> takes done and total.</summary>
 private IProgress<CatalogOpenProgress> PictureProgress(string format) =>
  new Progress<CatalogOpenProgress>(step => { if (step.Total > 0) Status.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, format, step.Done, step.Total); });

 private async void DisableLibraryEncryption()
 {
  var values = SecretDialogs.Passwords(this, T("Remove encryption"),
   T("The catalog is then stored unencrypted. Encrypted backups and exports created earlier stay encrypted."),
   T("Password or recovery key"));
  if (values is null) return;
  try { CatalogEncryption.Unlock(CatalogPath, values[0]).Dispose(); }
  catch (CryptographicException ex) { Message.Show(this, ex.Message, T("Remove encryption"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
  var progress = PictureProgress(T("Decrypting pictures … {0:N0} of {1:N0}"));
  await ReplaceRepositoryAsync(T("Remove encryption"), () => { CatalogEncryption.DisableEncryption(CatalogPath, values[0], progress); return null; });
 }
}
