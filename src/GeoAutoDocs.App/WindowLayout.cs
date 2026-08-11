using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GeoAutoDocs.App;

internal static class WindowLayout
{
    private const uint MonitorDefaultToNearest = 2;
    private const double WorkAreaMargin = 8;

    public static void KeepInsideWorkArea(Window window, double preferredWidth, double preferredHeight)
    {
        window.SourceInitialized += (_, _) => Fit(window, preferredWidth, preferredHeight);
        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(() =>
            Fit(window, Math.Min(window.Width, preferredWidth), Math.Min(window.Height, preferredHeight)));
    }

    private static void Fit(Window window, double preferredWidth, double preferredHeight)
    {
        var windowHandle = new WindowInteropHelper(window).Handle;
        var referenceWindow = window.Owner is { IsVisible: true } ? window.Owner : window;
        var referenceHandle = new WindowInteropHelper(referenceWindow).Handle;
        if (referenceHandle == IntPtr.Zero) referenceHandle = windowHandle;

        var monitor = MonitorFromWindow(referenceHandle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            FitFallback(window, preferredWidth, preferredHeight);
            return;
        }

        var source = HwndSource.FromHwnd(referenceHandle) ?? HwndSource.FromHwnd(windowHandle);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = fromDevice.Transform(new Point(monitorInfo.WorkArea.Left, monitorInfo.WorkArea.Top));
        var bottomRight = fromDevice.Transform(new Point(monitorInfo.WorkArea.Right, monitorInfo.WorkArea.Bottom));
        var workLeft = Math.Min(topLeft.X, bottomRight.X) + WorkAreaMargin;
        var workTop = Math.Min(topLeft.Y, bottomRight.Y) + WorkAreaMargin;
        var workWidth = Math.Max(320, Math.Abs(bottomRight.X - topLeft.X) - WorkAreaMargin * 2);
        var workHeight = Math.Max(240, Math.Abs(bottomRight.Y - topLeft.Y) - WorkAreaMargin * 2);

        ApplyBounds(window, preferredWidth, preferredHeight, workLeft, workTop, workWidth, workHeight);
    }

    private static void FitFallback(Window window, double preferredWidth, double preferredHeight)
    {
        var workArea = SystemParameters.WorkArea;
        ApplyBounds(window, preferredWidth, preferredHeight,
            workArea.Left + WorkAreaMargin, workArea.Top + WorkAreaMargin,
            Math.Max(320, workArea.Width - WorkAreaMargin * 2),
            Math.Max(240, workArea.Height - WorkAreaMargin * 2));
    }

    private static void ApplyBounds(Window window, double preferredWidth, double preferredHeight,
        double workLeft, double workTop, double workWidth, double workHeight)
    {
        window.MinWidth = Math.Min(window.MinWidth, workWidth);
        window.MinHeight = Math.Min(window.MinHeight, workHeight);
        window.MaxWidth = workWidth;
        window.MaxHeight = workHeight;
        window.Width = Math.Min(preferredWidth, workWidth);
        window.Height = Math.Min(preferredHeight, workHeight);

        var centeredLeft = window.Owner is { IsVisible: true }
            ? window.Owner.Left + (window.Owner.ActualWidth - window.Width) / 2
            : workLeft + (workWidth - window.Width) / 2;
        var centeredTop = window.Owner is { IsVisible: true }
            ? window.Owner.Top + (window.Owner.ActualHeight - window.Height) / 2
            : workTop + (workHeight - window.Height) / 2;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Math.Clamp(centeredLeft, workLeft, workLeft + workWidth - window.Width);
        window.Top = Math.Clamp(centeredTop, workTop, workTop + workHeight - window.Height);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
