using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace FlixyPix.Desktop;

/// <summary>The size of text and controls in every window, popup and menu. Scaling the layout keeps every proportion the
/// interface was designed with, whichever way a size was set, and media keep their sharpness.
/// A larger interface also opens its windows larger, so that their content finds the room it was designed for.</summary>
internal static class InterfaceScaling
{
    internal static readonly double[] Steps = [.85, .9, 1, 1.1, 1.25, 1.4, 1.6];
    private sealed record Minimum(double Width, double Height);
    private static readonly ConditionalWeakTable<Window, Minimum> minimums = new();
    private static bool installed;
    internal static double Current { get; private set; } = 1;

    internal static double Valid(double scale) => double.IsFinite(scale) ? Steps.MinBy(step => Math.Abs(step - scale)) : 1;

    internal static void Install()
    {
        if (installed) return;
        installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Open((Window)sender)));
        // Menus and tooltips are roots of their own, which the Loaded of a class handler does not reach. Their submenus and
        // the popups inside a window follow by themselves; popups without a parent in the tree call Fit when they open.
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((sender, _) => Fit((FrameworkElement)sender)));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((sender, _) => Fit((FrameworkElement)sender)));
    }

    internal static void Apply(double scale)
    {
        Current = Valid(scale);
        foreach (Window window in Application.Current.Windows) { Fit(window.Content as FrameworkElement); KeepMinimum(window); }
    }

    /// <summary>One step larger or smaller, or back to normal with a direction of 0.</summary>
    internal static double Step(int direction)
    {
        if (direction == 0) return 1;
        var index = Array.IndexOf(Steps, Current);
        return Steps[Math.Clamp(index + direction, 0, Steps.Length - 1)];
    }

    internal static void Fit(FrameworkElement? element)
    {
        if (element == null) return;
        if (Current == 1) { if (element.LayoutTransform is ScaleTransform) element.LayoutTransform = Transform.Identity; return; }
        if (element.LayoutTransform is ScaleTransform { ScaleX: var x } && x == Current) return;
        var transform = new ScaleTransform(Current, Current); transform.Freeze();
        element.LayoutTransform = transform;
    }

    /// <summary>Applies the size before the window first appears, for a window that must not show a first layout at all: the player, whose video area keeps what stood on it.</summary>
    internal static void Prepare(Window window) => Open(window);

    private static void Open(Window window)
    {
        Fit(window.Content as FrameworkElement);
        if (minimums.TryGetValue(window, out _)) return;
        minimums.Add(window, new Minimum(window.MinWidth, window.MinHeight));
        // The main window keeps the size the user left it at; open windows do not jump when the size changes later.
        if (Current > 1 && window is not MainWindow && window.WindowState == WindowState.Normal) Enlarge(window);
        KeepMinimum(window);
    }

    /// <summary>The designed room at the current size, as far as the screen has it; a smaller interface keeps its window.</summary>
    private static double Room(double designed, double screen) => Math.Min(designed * Math.Max(1, Current), Math.Max(designed, screen));

    private static void Enlarge(Window window)
    {
        var area = WorkArea(window);
        if (window.MaxHeight < double.PositiveInfinity) window.MaxHeight = Room(window.MaxHeight, area.Height);
        if (window.MaxWidth < double.PositiveInfinity) window.MaxWidth = Room(window.MaxWidth, area.Width);
        if (!double.IsNaN(window.Width))
        {
            var width = Room(window.Width, area.Width); var growth = width - window.Width;
            window.Width = width;
            if (!double.IsNaN(window.Left)) window.Left = Math.Clamp(window.Left - growth / 2, area.Left, Math.Max(area.Left, area.Right - width));
        }
        if (!double.IsNaN(window.Height))
        {
            var height = Room(window.Height, area.Height); var growth = height - window.Height;
            window.Height = height;
            if (!double.IsNaN(window.Top)) window.Top = Math.Clamp(window.Top - growth / 2, area.Top, Math.Max(area.Top, area.Bottom - height));
        }
    }

    private static void KeepMinimum(Window window)
    {
        if (!minimums.TryGetValue(window, out var minimum)) return;
        var area = WorkArea(window);
        window.MinWidth = Room(minimum.Width, area.Width);
        window.MinHeight = Room(minimum.Height, area.Height);
    }

    /// <summary>The free part of the monitor the window is on, in the units of the window.</summary>
    private static Rect WorkArea(Window window)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        // Before it appears, a window opens on the monitor of its owner.
        var anchor = new WindowInteropHelper(window).Handle != 0 ? window : window.Owner ?? window;
        var handle = new WindowInteropHelper(anchor).Handle;
        if (handle == 0 || PresentationSource.FromVisual(anchor)?.CompositionTarget is not { } target
            || !GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) return SystemParameters.WorkArea;
        return new Rect(target.TransformFromDevice.Transform(new Point(info.Work.Left, info.Work.Top)), target.TransformFromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom)));
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
