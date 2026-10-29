using System.Globalization;
using System.IO;
using System.Windows;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Several libraries, one open at a time. Opening another one closes this window as on exit (view, window place
/// and playback position are saved), then builds a main window for the other library in the same process. If the other
/// library cannot be opened (or its password prompt is cancelled), the previous one opens again.</summary>
public partial class MainWindow
{
 private string? _switchTo;
 private TaskCompletionSource<MainWindow?>? _switched;

 /// <summary>Completes once the start view is shown.</summary>
 internal Task Startup { get; private set; } = Task.CompletedTask;

 private string LibraryName => LibraryCatalogs.DisplayName(
  _settings.Libraries.FirstOrDefault(l => LibraryCatalogs.SameDirectory(l.Directory, _settings.DataDirectory)) ?? new KnownLibrary(_settings.DataDirectory));

 /// <summary>Notes the open library in this computer's list and shows its name once there are several.</summary>
 private void RememberOpenLibrary()
 {
  _settings = _settings with { Libraries = [.. LibraryCatalogs.Remember(_settings.Libraries, _settings.DataDirectory, DateTimeOffset.Now)] };
  if (_persistPreferences) { try { _settings.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorLog.Write("Bibliotheken", ex); } }
  UpdateLibraryName();
 }

 private void UpdateLibraryName()
 {
  var several = _settings.Libraries.Count > 1;
  LibraryHeader.Text = several ? LibraryName.ToUpper(CultureInfo.CurrentCulture) : T("LIBRARY");
  LibraryHeader.ToolTip = several ? _settings.DataDirectory : null;
  Title = several ? LibraryName + " · FlixyPix" : "FlixyPix";
  AppShell.LibraryName = several ? LibraryName : null;
 }

 private void SaveLibraries(IReadOnlyList<KnownLibrary> libraries, bool askAtStart)
 {
  _settings = _settings with { Libraries = [.. libraries], AskForLibraryAtStart = askAtStart };
  if (_persistPreferences) (_settings with { DataDirectory = _pendingDataDirectory ?? _settings.DataDirectory }).Save();
  UpdateLibraryName();
 }

 /// <summary>The database management at its list of libraries (button beside the library name, Ctrl+Shift+B).</summary>
 private void ManageLibraries(object sender, RoutedEventArgs e) => ManageLibraries();
 private void ManageLibraries() => ShowDatabase(DatabaseWindow.LibrariesSection);

 /// <summary>Closes this window and opens <paramref name="directory"/> in a new one; the task gives the new window, or
 /// null when neither library could be opened.</summary>
 internal Task<MainWindow?> SwitchLibraryAsync(string directory)
 {
  if (_switched != null) return _switched.Task;
  _switchTo = LibraryCatalogs.Normalize(directory); _pendingDataDirectory = _switchTo;
  _switched = new(TaskCreationOptions.RunContinuationsAsynchronously);
  Status.Text = T("Switching library …");
  Close();
  return _switched.Task;
 }

 /// <summary>Runs after this window let go of its catalog.</summary>
 private void OpenSwitchedLibrary(string target)
 {
  MainWindow? next = null;
  foreach (var directory in new[] { target, _settings.DataDirectory }.Distinct(StringComparer.OrdinalIgnoreCase))
  {
   try
   {
    var settings = (_persistPreferences ? DesktopSettings.Load() : _settings) with { DataDirectory = directory };
    if (_persistPreferences) settings.Save();
    next = new MainWindow(settings, _persistPreferences);
    Application.Current.MainWindow = next;
    next.Show();
    if (!LibraryCatalogs.SameDirectory(directory, target)) next.Status.Text = T("The other library was not opened; this one is open again.");
    break;
   }
   catch (OperationCanceledException) { next = null; }
   catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or Microsoft.Data.Sqlite.SqliteException)
   {
    next = null;
    ErrorLog.Write("Bibliothek wechseln", ex);
    Message.Show(T("The library could not be opened.\n{0}", ex.Message), "FlixyPix", MessageBoxButton.OK, MessageBoxImage.Error);
   }
  }
  _switched?.TrySetResult(next);
 }
}
