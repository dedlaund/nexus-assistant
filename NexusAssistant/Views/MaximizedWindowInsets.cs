using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NexusAssistant.Views;

internal static class MaximizedWindowInsets
{
    public static Thickness Get(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetClientRect(handle, out var client)) return default;
        var origin = new NativePoint();
        var monitor = MonitorFromWindow(handle, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!ClientToScreen(handle, ref origin) || !GetMonitorInfo(monitor, ref info)) return default;

        // WindowChrome can put part of the client area inside the maximized resize frame.
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Thickness(
            Math.Max(0, info.Work.Left - origin.X) / dpi.DpiScaleX,
            Math.Max(0, info.Work.Top - origin.Y) / dpi.DpiScaleY,
            Math.Max(0, origin.X + client.Right - info.Work.Right) / dpi.DpiScaleX,
            Math.Max(0, origin.Y + client.Bottom - info.Work.Bottom) / dpi.DpiScaleY);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
