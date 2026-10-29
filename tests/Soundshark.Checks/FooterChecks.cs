using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Soundshark;

internal static partial class Program
{
    static readonly string[] FooterNames = ["OpenLogs", "IssuesButton", "UpdateTools", "RollbackTools", "CancelJob", "UpdateAppButton", "RollbackAppButton", "UpdateLogButton"];
    static async Task FooterDiagnostic()
    {
        var window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20, WindowState = WindowState.Maximized };
        Theme.Apply(Theme.Current with { Font = "Abel", FontSize = 12 });
        window.Show(); await Task.Delay(150);
        string pcm = Paths.NewPcm(); using (var file = File.Create(pcm)) file.SetLength(21 * 48000 * 8);
        await window.Studio.LoadPcm(pcm, "Formatting preview"); await Task.Delay(150);
        Console.WriteLine($"Initial maximize screen: {window.PointToScreen(new Point())} to {window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight))}, actual={window.ActualWidth}x{window.ActualHeight}");
        foreach (string name in FooterNames) {
            var button = (Button)window.FindName(name);
            Console.WriteLine($"{name}: {button.PointToScreen(new Point())} to {button.PointToScreen(new Point(button.ActualWidth,button.ActualHeight))}");
        }
        Snapshot(window, "startup-loaded-formatting.png");
        await FooterChecks(window); window.Studio.Workspace.Dirty = false; window.Close();
    }
    static async Task FooterChecks(MainWindow window)
    {
        var appearance = Theme.Current;
        window.ShowActivated = true;
        window.WindowState = WindowState.Normal; window.Left = SystemParameters.WorkArea.Left + 20; window.Top = SystemParameters.WorkArea.Top + 20;
        window.WindowState = WindowState.Maximized;
        await Task.Delay(150);
        var top = window.PointToScreen(new Point());
        var bottom = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(window);
        var work = SystemParameters.WorkArea;
        Console.WriteLine($"Maximized: {top} to {bottom}, work={work}, DPI={dpi.DpiScaleX}");
        Check(top.X >= work.Left * dpi.DpiScaleX - 1 && top.Y >= work.Top * dpi.DpiScaleY - 1 && bottom.X <= work.Right * dpi.DpiScaleX + 1 && bottom.Y <= work.Bottom * dpi.DpiScaleY + 1, "maximized borderless window fits the monitor work area above the taskbar");
        foreach (bool large in new[] { false, true })
        {
            Theme.Apply(appearance with { FontSize = large ? 19 : 14 });
            foreach (bool maximized in new[] { true, false })
            {
                window.WindowState = maximized ? WindowState.Maximized : WindowState.Normal;
                if (!maximized) { window.Width = 850; window.Height = 700; }
                await Task.Delay(80);
                var bounds = FooterNames.Select(name => {
                    var button = (Button)window.FindName(name);
                    return button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                }).ToArray();
                Check(bounds.All(r => r.Left >= 0 && r.Top >= 0 && r.Right <= window.ActualWidth && r.Bottom <= window.ActualHeight), $"all footer buttons remain inside the window at {(maximized ? "maximized" : "minimum")} size, font {(large ? 19 : 14)}");
                Check(!bounds.Where((a, i) => bounds.Skip(i + 1).Any(b => a.IntersectsWith(b))).Any(), "footer button rows do not overlap");
                if (maximized && large) Snapshot(window, "footer-maximized-large.png");
            }
        }
        Theme.Apply(appearance);
        await StartupLayoutChecks();
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    static async Task StartupLayoutChecks()
    {
        var appearance = Theme.Current;
        var window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20, WindowState = WindowState.Maximized };
        Theme.Apply(appearance with { Font = "Abel", FontSize = 12 }); window.Show();
        string pcm = Paths.NewPcm(); using (var file = File.Create(pcm)) file.SetLength(21 * 48000 * 8);
        await window.Studio.LoadPcm(pcm, "Loaded formatting check"); await Task.Delay(120);
        var work = SystemParameters.WorkArea; var dpi = VisualTreeHelper.GetDpi(window);
        SetWindowPos(new WindowInteropHelper(window).Handle, 0, (int)(work.Left * dpi.DpiScaleX), (int)(work.Top * dpi.DpiScaleY), (int)(work.Width * dpi.DpiScaleX), (int)(work.Height * dpi.DpiScaleY) + 40, 0x0014);
        await Task.Delay(180);
        Check(window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight)).Y <= work.Bottom * dpi.DpiScaleY + 1, "maximized window corrects late oversized bounds after startup instead of extending behind the taskbar");
        Check(FooterNames.All(name => { var b = (Button)window.FindName(name); return b.PointToScreen(new Point(b.ActualWidth, b.ActualHeight)).Y <= work.Bottom * dpi.DpiScaleY; }), "every footer button stays above the taskbar with audio loaded and a condensed font");
        Descendants<Expander>(window.Studio).Single().IsExpanded = true; window.UpdateLayout();
        var device = Descendants<ComboBox>(window.Studio).Single(c => c.Name == "AudioDevice");
        var source = Descendants<ComboBox>(window.Studio).Single(c => c.Name == "RecordingSource");
        Check(Math.Abs(device.TransformToAncestor(window.Studio).Transform(new Point()).X - source.TransformToAncestor(window.Studio).Transform(new Point()).X) < 2 && device.ActualWidth > 420, "Audio Device aligns with Record From and fills its available row without a centered gap");
        Snapshot(window, "studio-formatting-fixed.png"); window.Studio.Workspace.Dirty = false; window.Close(); Theme.Apply(appearance);
    }
}
