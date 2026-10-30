using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace Soundshark;

public sealed class VirtualMicTray : IDisposable
{
    const int Callback = 0x8001;
    readonly MainWindow owner;
    readonly HwndSource source;
    readonly uint taskbarCreated;
    IconData data;
    bool disposed;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct IconData
    {
        public int Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    public VirtualMicTray(MainWindow window)
    {
        owner = window; nint handle = new WindowInteropHelper(window).EnsureHandle(); source = HwndSource.FromHwnd(handle)!;
        ExtractIconEx(Environment.ProcessPath!, 0, out var large, out var small, 1); if (large != 0) DestroyIcon(large);
        data = new() { Size = Marshal.SizeOf<IconData>(), Window = handle, Id = 1, Flags = 7, Callback = Callback, Icon = small, Tip = "Soundshark Virtual Mic", Info = "", InfoTitle = "" };
        taskbarCreated = RegisterWindowMessage("TaskbarCreated"); source.AddHook(Hook);
        if (!ShellNotifyIcon(0, ref data)) { Dispose(); throw new InvalidOperationException("Windows could not create the Soundshark tray icon. The main window will stay open."); }
    }
    nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (disposed) return 0;
        if ((uint)message == taskbarCreated) ShellNotifyIcon(0, ref data);
        if (message != Callback) return 0;
        if ((int)lParam == 0x0203) { owner.OpenFromTray(); handled = true; }
        if ((int)lParam is 0x0205 or 0x007B) {
            SetForegroundWindow(data.Window);
            var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
            void Item(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
            Item("Open Soundshark", owner.OpenFromTray);
            Item(owner.Settings.Soundboard.UsesContinuousVirtualMic ? "Disable Virtual Mic" : "Enable Virtual Mic", () => _ = owner.ToggleVirtualMic());
            Item("Stop All Sounds", () => { owner.Player.Stop(); _ = owner.Soundboard.StopAsync(); });
            Item("Exit", owner.ExitFromTray);
            menu.Closed += (_, _) => PostMessage(data.Window, 0, 0, 0); menu.IsOpen = true; handled = true;
        }
        return 0;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; ShellNotifyIcon(2, ref data); source.RemoveHook(Hook);
        if (data.Icon != 0) DestroyIcon(data.Icon);
    }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ShellNotifyIcon(uint message, ref IconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
