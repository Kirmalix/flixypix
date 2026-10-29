using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Library folder, tool paths, security status and caches.</summary>
internal sealed partial class SettingsWindow
{
 private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

 private static (bool? Ok, string Message) DescribeTool(string name, string value)
 {
  if (value.Length == 0)
   return File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", name + ".exe")) ? (true, T("The bundled version is used.")) : (null, T("The bundled version is used as soon as it is available."));
  if (!Path.IsPathFullyQualified(value)) return (false, T("Please enter a complete path with a drive."));
  return File.Exists(value) ? (true, T("Custom version found.")) : (false, T("File not found."));
 }

 private void UpdateSecurity()
 {
  bool encrypted;
  try { encrypted = CatalogEncryption.IsEncrypted(Path.Combine(libraryDirectory, "catalog.db")); } catch (IOException) { encrypted = repository.IsEncrypted; }
  securityIcon.Text = encrypted ? "" : ""; securityIcon.Foreground = encrypted ? SettingsUi.Good : SettingsUi.Muted;
  securityStatus.Text = encrypted ? T("This library is encrypted.") : T("This library is not encrypted.");
 }
 private static void OpenFolder(string folder) { if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } }); }

 private async Task UpdateCacheSummaryAsync()
 {
  static long Size(string path) => Directory.Exists(path) ? new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length) : 0;
  static string Readable(long bytes) => bytes < 1024 * 1024 ? (bytes / 1024d).ToString("N0",
   CultureInfo.CurrentCulture) + " KiB" : bytes < 1024L * 1024 * 1024 ? (bytes / 1024d / 1024d).ToString("N1",
   CultureInfo.CurrentCulture) + " MiB" : (bytes / 1024d / 1024d / 1024d).ToString("N2", CultureInfo.CurrentCulture) + " GiB";
  cacheTotal.Text = T("Calculating …");
  var sizes = await Task.Run(() => Caches.ToDictionary(cache => cache.Folder, cache => Size(Path.Combine(libraryDirectory, cache.Folder))));
  var largest = Math.Max(1, sizes.Values.Max());
  foreach (var (folder, (size, bar)) in cacheViews) { size.Text = Readable(sizes[folder]); bar.Width = 140d * sizes[folder] / largest; }
  cacheTotal.Text = T("Total {0} · can be cleared: {1}", Readable(sizes.Values.Sum()), Readable(Caches.Where(c => c.Clearable).Sum(c => sizes[c.Folder])));
 }

 private async void ClearCache(string name, string label)
 {
  var folder = Path.Combine(libraryDirectory, name);
  if (!Directory.Exists(folder)) return;
  if (Message.Show(this, T("Clear the {0}? It is recreated when needed.", label), T("Cache"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
  try
  {
   FileAccessPolicy.Current.EnsureWritable(folder);
   await Task.Run(() => { foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos()) { if (entry is DirectoryInfo directory) directory.Delete(true); else entry.Delete(); } });
  }
  catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { Message.Show(this, T("Some files are still in use: {0}", failure.Message), T("Cache")); }
  await UpdateCacheSummaryAsync();
 }
}
