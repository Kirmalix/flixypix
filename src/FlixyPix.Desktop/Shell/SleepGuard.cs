using System.Runtime.InteropServices;

namespace FlixyPix.Desktop;

/// <summary>Keeps the display and system awake while videos or slideshows run, as the original's
/// "Disable screensaver when playing videos and slideshows". Nested users are counted.</summary>
internal static class SleepGuard
{
 private const uint Continuous = 0x80000000, SystemRequired = 0x00000001, DisplayRequired = 0x00000002;
 private static int users;
 internal static bool Active => users > 0;

 internal static IDisposable Acquire()
 {
  if (users++ == 0) _ = SetThreadExecutionState(Continuous | SystemRequired | DisplayRequired);
  return new Release();
 }

 private sealed class Release : IDisposable
 {
  private bool released;
  public void Dispose()
  {
   if (released) return; released = true;
   if (--users == 0) _ = SetThreadExecutionState(Continuous);
  }
 }

 [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}
