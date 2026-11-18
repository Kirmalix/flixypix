using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

/// <summary>The window follows the picture: landscape up to square videos fill the video area without bars above and
/// below, portrait videos keep the landscape window with side bars (or get a window of their shape when chosen). The
/// window opens where and as large as the user left it, keeps the shape of the picture while an edge is dragged (Shift
/// sizes freely) and grows or shrinks with the mark list and the strips of the control bar. Sizes are screen pixels.</summary>
internal sealed partial class VideoPlayerWindow
{
 /// <summary>The remembered place the window opens at; afterwards what the user made of it.</summary>
 internal PlayerWindowPlace? Place { get; set; }
 /// <summary>Raised when the user moved or sized the window, maximized it or switched full screen.</summary>
 internal event Action<PlayerWindowPlace>? PlaceChanged;
 /// <summary>Width / height of the picture as shown; from the catalog first, then from the player (rotation, pixel aspect).</summary>
 internal double? PictureAspect { get; private set; }
 private (int Width, int Height)? pictureSize;
 private int longSide, pictureReads;
 private (int Width, int Height)? chrome, normalSize;
 private bool followPicture = true, sizingLoop, sizedFreely, resizedInLoop, fitScheduled, fitting, pictureMeasured;
 /// <summary>The window keeps to the shape of the picture; off after the user sized it freely, on again with "Fit window to video".</summary>
 internal bool FollowsPicture => followPicture;

 /// <summary>Size of the video area in pixels (smoke test).</summary>
 internal (int Width, int Height) VideoAreaPixels => host.Handle != 0 && GetWindowRect(host.Handle, out var area) ? (area.Right - area.Left, area.Bottom - area.Top) : (0, 0);

 private nint WindowHandle => new WindowInteropHelper(this).Handle;
 private bool FitPortrait => playback.FitPortraitWindow;

 private void SetUpWindowFit()
 {
  if (media.Width is > 0 and var width && media.Height is > 0 and var height) { PictureAspect = (double)width / height; pictureSize = (width, height); }
  WindowStartupLocation = WindowStartupLocation.Manual;
  SourceInitialized += (_, _) => { HwndSource.FromHwnd(WindowHandle)?.AddHook(SizingHook); OpenAtPlace(); };
  Loaded += (_, _) => ScheduleFit();
  controlBar.SizeChanged += (_, e) => { if (e.HeightChanged) ScheduleFit(); };
  markPanel.IsVisibleChanged += (_, _) => ScheduleFit();
  StateChanged += (_, _) => { if (IsFullscreen) return; if (WindowState == WindowState.Normal) ScheduleFit(); Remember(false); };
 }

 /// <summary>Before the window shows: the remembered place, sized for this video with the chrome of last time.</summary>
 private void OpenAtPlace()
 {
  var handle = WindowHandle;
  if (handle == 0 || !GetWindowRect(handle, out var current) || !GetClientRect(handle, out var client)) return;
  var scale = DeviceScale();
  var frame = Place is { } known ? (known.ChromeWidth, known.ChromeHeight)
   : (current.Right - current.Left - client.Right + (marksWanted ? (int)Math.Round(290 * scale) : 0), current.Bottom - current.Top - client.Bottom + (int)Math.Round(110 * scale));
  int width = Place?.Width ?? current.Right - current.Left, height = Place?.Height ?? current.Bottom - current.Top;
  var wanted = Place is { } place ? new NativeRect { Left = place.Left, Top = place.Top, Right = place.Left + width, Bottom = place.Top + height } : current;
  if (Place == null && Owner != null && new WindowInteropHelper(Owner).Handle is not 0 and var owner && GetWindowRect(owner, out var ownerRect))
  {
   // The first player opens over the middle of the library.
   var (x, y) = ((ownerRect.Left + ownerRect.Right) / 2, (ownerRect.Top + ownerRect.Bottom) / 2);
   wanted = new NativeRect { Left = x - width / 2, Top = y - height / 2, Right = x + width / 2, Bottom = y + height / 2 };
  }
  if (WorkArea(wanted) is not { } work) return;
  if (PictureAspect is { } aspect)
  {
   var shape = PlayerWindowFit.Shape(aspect, FitPortrait);
   longSide = Place?.LongSide ?? PlayerWindowFit.LongSide(width - frame.Item1, height - frame.Item2, shape);
   (width, height) = PlayerWindowFit.Size(longSide, shape, frame.Item1, frame.Item2, MinPixels(MinWidth, scale), MinPixels(MinHeight, scale), work.Right - work.Left, work.Bottom - work.Top);
   // Without a place yet the window grows from its middle, later from its top edge.
   if (Place == null) wanted.Top = (wanted.Top + wanted.Bottom - height) / 2;
  }
  else longSide = Place?.LongSide ?? 0;
  var fit = WindowPlacement.Fit(wanted.Left, wanted.Top, width, height, work.Left, work.Top, work.Right, work.Bottom);
  fitting = true;
  try
  {
   SetWindowPos(handle, 0, fit.Left, fit.Top, fit.Width, fit.Height, NoZOrderNoActivate);
   // Arriving on a monitor with another scale, the window changes its size with it; the second call sets the wanted one.
   SetWindowPos(handle, 0, fit.Left, fit.Top, fit.Width, fit.Height, NoZOrderNoActivate);
  }
  finally { fitting = false; }
  if (Place is { Maximized: true, Fullscreen: false }) WindowState = WindowState.Maximized;
 }

 /// <summary>Fits the window once the layout settled; several reasons at once give one fit.</summary>
 private void ScheduleFit()
 {
  if (fitScheduled) return; fitScheduled = true;
  Dispatcher.BeginInvoke(() => { fitScheduled = false; FitToPicture(); }, DispatcherPriority.Loaded);
 }

 /// <summary>Gives the window the size the picture needs at the chosen long side; the top left corner stays unless the
 /// window would leave the screen.</summary>
 internal void FitToPicture()
 {
  if (closing || IsFullscreen || WindowState != WindowState.Normal || sizingLoop || !followPicture || PictureAspect is not { } aspect) return;
  var handle = WindowHandle;
  if (MeasureChrome() is not { } frame || !GetWindowRect(handle, out var rect) || WorkArea(rect) is not { } work) return;
  var shape = PlayerWindowFit.Shape(aspect, FitPortrait); var scale = DeviceScale();
  if (longSide <= 0) longSide = PlayerWindowFit.LongSide(rect.Right - rect.Left - frame.Width, rect.Bottom - rect.Top - frame.Height, shape);
  var (width, height) = PlayerWindowFit.Size(longSide, shape, frame.Width, frame.Height, MinPixels(MinWidth, scale), MinPixels(MinHeight, scale), work.Right - work.Left, work.Bottom - work.Top);
  var fit = WindowPlacement.Fit(rect.Left, rect.Top, width, height, work.Left, work.Top, work.Right, work.Bottom);
  if (Math.Abs(fit.Left - rect.Left) <= 1 && Math.Abs(fit.Top - rect.Top) <= 1 && Math.Abs(fit.Width - (rect.Right - rect.Left)) <= 1 && Math.Abs(fit.Height - (rect.Bottom - rect.Top)) <= 1) return;
  fitting = true;
  try { SetWindowPos(handle, 0, fit.Left, fit.Top, fit.Width, fit.Height, NoZOrderNoActivate); }
  finally { fitting = false; }
  Remember(false);
 }

 /// <summary>"Fit window to video": back to the shape of the picture after sizing freely or maximizing.</summary>
 internal void FitWindowToVideo()
 {
  if (IsFullscreen) return;
  followPicture = true;
  if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
  FitToPicture(); Remember(false);
 }

 /// <summary>Shows the picture at this share of its own size (1 = one screen pixel per video pixel), as far as the screen allows.</summary>
 internal void ScaleWindow(double scale)
 {
  if (IsFullscreen || PictureAspect is not { } aspect || pictureSize is not { } size) return;
  longSide = PlayerWindowFit.LongSideFor(size.Width, size.Height, scale, PlayerWindowFit.Shape(aspect, FitPortrait));
  followPicture = true;
  if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
  FitToPicture(); Remember(false);
 }

 /// <summary>Reads the size of the picture from the player: phone recordings turned by their rotation, DVDs with their
 /// pixel aspect. The first frames may not be decoded yet, so the poll asks again for a while.</summary>
 private async Task MeasurePictureAsync(MpvPlayer active)
 {
  if (pictureMeasured || ++pictureReads > 50) return;
  if (await active.GetDisplaySizeAsync() is not { } size) return;
  pictureMeasured = true; pictureSize = size;
  var aspect = (double)size.Width / size.Height;
  if (PictureAspect is { } known && Math.Abs(known - aspect) < .01) return;
  PictureAspect = aspect; ScheduleFit();
 }

 /// <summary>Keeps what the user did with the window for the next video and the next start.</summary>
 private void Remember(bool userSized)
 {
  if (fitting || closing) return;
  var handle = WindowHandle;
  if (handle == 0 || WindowPlacement.Read(this) is not { } normal) return;
  if (!IsFullscreen && WindowState == WindowState.Normal) MeasureChrome();
  var frame = chrome ?? (Place is { } known ? (known.ChromeWidth, known.ChromeHeight) : (0, 0));
  if (userSized || longSide <= 0)
  {
   var shape = PictureAspect is { } aspect ? PlayerWindowFit.Shape(aspect, FitPortrait) : (double)normal.Width / normal.Height;
   longSide = Math.Max(1, PlayerWindowFit.LongSide(normal.Width - frame.Item1, normal.Height - frame.Item2, shape));
  }
  Place = new PlayerWindowPlace(normal.Left, normal.Top, normal.Width, normal.Height, !IsFullscreen && WindowState == WindowState.Maximized, IsFullscreen, longSide, Math.Max(0, frame.Item1), Math.Max(0, frame.Item2));
  PlaceChanged?.Invoke(Place);
 }

 /// <summary>Window size minus the video area: frame, control bar and mark list, in pixels.</summary>
 private (int Width, int Height)? MeasureChrome()
 {
  var handle = WindowHandle;
  if (handle == 0 || host.Handle == 0 || IsFullscreen || WindowState != WindowState.Normal || !GetWindowRect(handle, out var outer) || !GetWindowRect(host.Handle, out var video)) return chrome;
  int videoWidth = video.Right - video.Left, videoHeight = video.Bottom - video.Top;
  if (videoWidth <= 1 || videoHeight <= 1) return chrome;
  return chrome = (outer.Right - outer.Left - videoWidth, outer.Bottom - outer.Top - videoHeight);
 }

 /// <summary>The sizing loop of Windows: the picture keeps its shape while an edge is dragged, Shift lets it go.</summary>
 private nint SizingHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
 {
  switch (message)
  {
   case 0x0231: // WM_ENTERSIZEMOVE
    sizingLoop = true; sizedFreely = resizedInLoop = false; MeasureChrome(); break;
   case 0x0214: // WM_SIZING
    resizedInLoop = true;
    if (GetKeyState(0x10) < 0) { sizedFreely = true; break; }
    if (PictureAspect is { } aspect && PlayerWindowFit.FollowsPicture(aspect, FitPortrait) && chrome is { } frame && wParam.ToInt64() is >= 1 and <= 8 and var edge)
    {
     var rect = Marshal.PtrToStructure<NativeRect>(lParam); var scale = DeviceScale();
     var (left, top, right, bottom) = PlayerWindowFit.Drag((rect.Left, rect.Top, rect.Right, rect.Bottom), (PlayerWindowFit.Edge)edge, aspect, frame.Width, frame.Height, MinPixels(MinWidth, scale), MinPixels(MinHeight, scale));
     Marshal.StructureToPtr(new NativeRect { Left = left, Top = top, Right = right, Bottom = bottom }, lParam, false);
     handled = true; return 1;
    }
    sizedFreely = true; break;
   case 0x0232: // WM_EXITSIZEMOVE
    sizingLoop = false;
    if (resizedInLoop) { followPicture = !sizedFreely; Remember(true); } else Remember(false);
    break;
   case 0x0005 when wParam == 0 && !IsFullscreen && GetWindowRect(hwnd, out var now): // WM_SIZE, restored
    // Sized from outside (snapped to half the screen, changed by the program or a new screen scale): the window keeps
    // that size until "Fit window to video" or the next video, so a mark list or strip does not undo it.
    var size = (now.Right - now.Left, now.Bottom - now.Top);
    if (!fitting && !sizingLoop && normalSize is { } before && (Math.Abs(before.Item1 - size.Item1) > 1 || Math.Abs(before.Item2 - size.Item2) > 1)) followPicture = false;
    normalSize = size;
    break;
  }
  return 0;
 }

 private double DeviceScale() => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
 private static int MinPixels(double units, double scale) => double.IsFinite(units) ? (int)Math.Round(units * scale) : 0;

 private static NativeRect? WorkArea(NativeRect around)
 {
  var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
  return GetMonitorInfo(MonitorFromRect(ref around, 2), ref info) ? info.Work : null;
 }

 private const uint NoZOrderNoActivate = 0x0014;
 [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
 [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
 [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
 [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rect);
 [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
 [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref NativeRect rect, uint flags);
 [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
 [DllImport("user32.dll")] private static extern short GetKeyState(int key);
}
