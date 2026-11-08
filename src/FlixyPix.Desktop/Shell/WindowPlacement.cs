using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FlixyPix.Desktop;

/// <summary>Where the main window stands, in the pixels of the screen. Pixels stay right across monitors with different
/// scaling, which window units do not. Applied before the window appears, so it never jumps, and always fitted to the
/// free part of a monitor, so that a window from a larger screen still fits.</summary>
internal static class WindowPlacement
{
    private const uint NoZOrderNoActivate = 0x0014, NearestMonitor = 2;
    private const int RestoreToMaximized = 2;

    /// <summary>Fits a place inside an area: not larger than the area and completely on it.</summary>
    internal static (int Left, int Top, int Width, int Height) Fit(int left, int top, int width, int height, int areaLeft, int areaTop, int areaRight, int areaBottom)
    {
        var fittedWidth = Math.Min(width, areaRight - areaLeft); var fittedHeight = Math.Min(height, areaBottom - areaTop);
        return (Math.Clamp(left, areaLeft, areaRight - fittedWidth), Math.Clamp(top, areaTop, areaBottom - fittedHeight), fittedWidth, fittedHeight);
    }

    /// <summary>Call from SourceInitialized: the window exists but is not visible yet.</summary>
    internal static void Apply(Window window, WindowPixels? stored, WindowPlace? older)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var current)) return;
        var place = stored;
        // Written by a version that kept window units for the scale of the main monitor.
        if (place == null && older != null && PresentationSource.FromVisual(window)?.CompositionTarget is { } target)
        {
            var scale = target.TransformToDevice;
            place = new WindowPixels((int)Math.Round(older.Left * scale.M11), (int)Math.Round(older.Top * scale.M22), (int)Math.Round(older.Width * scale.M11), (int)Math.Round(older.Height * scale.M22), older.Maximized);
        }
        var wanted = place != null
            ? new NativeRect { Left = place.Left, Top = place.Top, Right = place.Left + place.Width, Bottom = place.Top + place.Height }
            : current;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromRect(ref wanted, NearestMonitor), ref info)) return;
        var work = info.Work;
        var left = wanted.Left; var top = wanted.Top;
        if (place == null) { left = (work.Left + work.Right - (wanted.Right - wanted.Left)) / 2; top = (work.Top + work.Bottom - (wanted.Bottom - wanted.Top)) / 2; }
        var fit = Fit(left, top, wanted.Right - wanted.Left, wanted.Bottom - wanted.Top, work.Left, work.Top, work.Right, work.Bottom);
        SetWindowPos(handle, 0, fit.Left, fit.Top, fit.Width, fit.Height, NoZOrderNoActivate);
        // Arriving on a monitor with another scale, the window changes its size with it; the second call sets the wanted one.
        SetWindowPos(handle, 0, fit.Left, fit.Top, fit.Width, fit.Height, NoZOrderNoActivate);
        if (place is { Maximized: true }) window.WindowState = WindowState.Maximized;
    }

    /// <summary>The place to come back to; for a maximized or minimized window, where it stood before.</summary>
    internal static WindowPixels? Read(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return null;
        var placement = new NativePlacement { Length = Marshal.SizeOf<NativePlacement>() };
        if (!GetWindowPlacement(handle, ref placement)) return null;
        var maximized = window.WindowState == WindowState.Maximized || window.WindowState == WindowState.Minimized && (placement.Flags & RestoreToMaximized) != 0;
        NativeRect rect;
        if (window.WindowState == WindowState.Normal) { if (!GetWindowRect(handle, out rect)) return null; }
        else
        {
            // The placement counts from the free part of the monitor, which differs where the taskbar stands at the top or left.
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(handle, NearestMonitor), ref info)) return null;
            var (x, y) = (info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
            rect = placement.Normal;
            rect.Left += x; rect.Right += x; rect.Top += y; rect.Bottom += y;
        }
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        return width > 0 && height > 0 ? new WindowPixels(rect.Left, rect.Top, width, height, maximized) : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePlacement { public int Length, Flags, ShowCommand; public NativePoint Minimum, Maximum; public NativeRect Normal; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint window, ref NativePlacement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
