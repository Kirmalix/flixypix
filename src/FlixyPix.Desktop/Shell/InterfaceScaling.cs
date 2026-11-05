using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FlixyPix.Desktop;

/// <summary>The size of text and controls in every window, popup and menu. Scaling the layout keeps every proportion the
/// interface was designed with, whichever way a size was set, and media keep their sharpness.</summary>
internal static class InterfaceScaling
{
    internal static readonly double[] Steps = [.85, .9, 1, 1.1, 1.25, 1.4, 1.6];
    private static readonly ConditionalWeakTable<Popup, object> watched = new();
    private static bool installed;
    internal static double Current { get; private set; } = 1;

    internal static double Valid(double scale) => double.IsFinite(scale) ? Steps.MinBy(step => Math.Abs(step - scale)) : 1;

    internal static void Install()
    {
        if (installed) return;
        installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Fit(((Window)sender).Content as FrameworkElement)));
        EventManager.RegisterClassHandler(typeof(ContextMenu), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Fit((FrameworkElement)sender)));
        EventManager.RegisterClassHandler(typeof(ToolTip), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Fit((FrameworkElement)sender)));
        EventManager.RegisterClassHandler(typeof(Popup), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            var popup = (Popup)sender;
            if (watched.TryGetValue(popup, out var _)) return;
            watched.Add(popup, new object());
            popup.Opened += (_, _) => Fit(popup.Child as FrameworkElement);
            if (popup.IsOpen) Fit(popup.Child as FrameworkElement);
        }));
    }

    internal static void Apply(double scale)
    {
        Current = Valid(scale);
        foreach (Window window in Application.Current.Windows) Fit(window.Content as FrameworkElement);
    }

    /// <summary>One step larger or smaller, or back to normal with a direction of 0.</summary>
    internal static double Step(int direction)
    {
        if (direction == 0) return 1;
        var index = Array.IndexOf(Steps, Current);
        return Steps[Math.Clamp(index + direction, 0, Steps.Length - 1)];
    }

    private static void Fit(FrameworkElement? element)
    {
        if (element == null) return;
        if (Current == 1) { if (element.LayoutTransform is ScaleTransform) element.LayoutTransform = Transform.Identity; return; }
        if (element.LayoutTransform is ScaleTransform { ScaleX: var x } && x == Current) return;
        var transform = new ScaleTransform(Current, Current); transform.Freeze();
        element.LayoutTransform = transform;
    }
}
