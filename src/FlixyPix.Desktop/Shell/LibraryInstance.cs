using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Several instances of FlixyPix may run, each with its own library, but a library is open in one instance only.
/// The instance holds a named mutex for its library folder; starting or choosing that library elsewhere signals the
/// instance, which brings its window to the front instead of opening the catalog a second time.</summary>
internal sealed class LibraryInstance : IDisposable
{
 private readonly Mutex _mutex;
 private readonly EventWaitHandle _activate;
 private readonly RegisteredWaitHandle _waiting;
 private bool _disposed;

 private LibraryInstance(Mutex mutex, EventWaitHandle activate, Action onActivate)
 {
  _mutex = mutex; _activate = activate;
  _waiting = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
 }

 private static string Key(string directory) =>
  Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(LibraryCatalogs.Normalize(directory).ToUpperInvariant())))[..32];
 private static string MutexName(string directory) => @"Local\FlixyPix-Library-" + Key(directory);
 private static string EventName(string directory) => @"Local\FlixyPix-Activate-" + Key(directory);

 /// <summary>Takes the library for this instance; null when another instance has it open.
 /// <paramref name="onActivate"/> runs on a pool thread when another start asks for this library.</summary>
 internal static LibraryInstance? Claim(string directory, Action onActivate)
 {
  var mutex = new Mutex(initiallyOwned: true, MutexName(directory), out var created);
  if (!created) { mutex.Dispose(); return null; }
  return new(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, EventName(directory)), onActivate);
 }

 /// <summary>True when another instance (or this one) has the library open.</summary>
 internal static bool IsOpen(string directory)
 {
  if (!Mutex.TryOpenExisting(MutexName(directory), out var mutex)) return false;
  mutex.Dispose(); return true;
 }

 /// <summary>Asks the instance that has the library open to come to the front; false when none has it.</summary>
 internal static bool Activate(string directory)
 {
  if (!EventWaitHandle.TryOpenExisting(EventName(directory), out var activate)) return false;
  // The other instance may only take the foreground if this one, which the user just used, allows it.
  AllowSetForegroundWindow(-1);
  using (activate) activate.Set();
  return true;
 }

 /// <summary>Lets go of the library; called on the thread that claimed it, when the catalog is closed.</summary>
 public void Dispose()
 {
  if (_disposed) return;
  _disposed = true;
  _waiting.Unregister(null); _activate.Dispose();
  try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } // Not owned by this thread (should not happen); disposing still frees it.
  _mutex.Dispose();
 }

 [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
