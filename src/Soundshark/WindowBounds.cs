using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Soundshark;

// Borderless windows must use the current monitor's work area when maximized.
internal static class WindowBounds
{
    public static void Attach(Window window)
    {
        bool queued = false;
        void QueueFit()
        {
            if (queued || window.WindowState != WindowState.Maximized) return;
            queued = true;
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => {
                queued = false;
                if (!window.IsVisible || window.WindowState != WindowState.Maximized) return;
                nint hwnd = new WindowInteropHelper(window).Handle;
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info) || !GetWindowRect(hwnd, out var current)) return;
                var work = info.Work;
                if (current.Left != work.Left || current.Top != work.Top || current.Right != work.Right || current.Bottom != work.Bottom)
                    SetWindowPos(hwnd, 0, work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top, 0x0014); // no activation or z-order change
            });
        }
        nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message is 0x001A or 0x007E or 0x02E0) QueueFit(); // work area, display or DPI changed
            return HandleMessage(hwnd, message, wParam, lParam, ref handled);
        }
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(Hook);
        window.Loaded += (_, _) => QueueFit();
        window.StateChanged += (_, _) => QueueFit();
        window.SizeChanged += (_, _) => QueueFit();
        window.LocationChanged += (_, _) => QueueFit();
    }

    static nint HandleMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0024) return 0; // WM_GETMINMAXINFO
        var monitor = MonitorFromWindow(hwnd, 2); // nearest monitor, including secondary displays
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return 0;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition = new() { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
        limits.MaxSize = new() { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
        limits.MinTrackSize.X = Math.Min(limits.MinTrackSize.X, limits.MaxSize.X);
        limits.MinTrackSize.Y = Math.Min(limits.MinTrackSize.Y, limits.MaxSize.Y);
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
