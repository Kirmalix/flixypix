using System.IO;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Watches the library folders marked for watching and reports once the file system has been quiet for a
/// moment, so a copy of many or large files leads to a single check with final sizes.</summary>
internal sealed class LibraryFolderWatcher : IDisposable
{
 private readonly List<FileSystemWatcher> _watchers = [];
 private readonly DispatcherTimer _settle;
 private readonly string _library;
 private bool _disposed;
 internal event Action? Changed;
 internal IReadOnlyList<string> Watched => _watchers.Select(w => w.Path).ToList();

 internal LibraryFolderWatcher(IEnumerable<LibraryFolder> folders, string libraryDirectory, TimeSpan settle)
 {
  _library = Path.GetFullPath(libraryDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
  _settle = new DispatcherTimer { Interval = settle };
  _settle.Tick += (_, _) => { _settle.Stop(); if (!_disposed) Changed?.Invoke(); };
  foreach (var folder in folders.Where(f => f.Watch))
  {
   try
   {
    if (!Directory.Exists(folder.Path)) continue;
    var watcher = new FileSystemWatcher(folder.Path) { IncludeSubdirectories = folder.IncludeSubfolders, InternalBufferSize = 64 * 1024,
     NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
    watcher.Created += Noticed; watcher.Deleted += Noticed; watcher.Changed += Noticed; watcher.Renamed += Noticed;
    // A lost buffer or a vanished share means unknown changes: check everything.
    watcher.Error += (_, _) => Poke();
    watcher.EnableRaisingEvents = true;
    _watchers.Add(watcher);
   }
   catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
  }
 }

 private void Noticed(object sender, FileSystemEventArgs e)
 {
  if (CatalogRepository.LibraryWorkFolders.Any(name => e.FullPath.StartsWith(Path.Combine(_library, name) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return;
  if (e.FullPath.StartsWith(Path.Combine(_library, LibraryCatalogs.CatalogFileName), StringComparison.OrdinalIgnoreCase)) return;
  // Media files and anything without an extension, which is usually a folder being moved or deleted.
  Poke();
 }

 private static bool Relevant(string path) => Path.GetExtension(path).Length == 0 || MediaFormats.GetKind(path) != null;

 private void Poke()
 {
  if (_disposed) return;
  _settle.Dispatcher.BeginInvoke(() => { if (_disposed) return; _settle.Stop(); _settle.Start(); });
 }

 public void Dispose()
 {
  _disposed = true;
  _settle.Stop();
  foreach (var watcher in _watchers) watcher.Dispose();
  _watchers.Clear();
 }
}
