using System.IO;
using System.Windows;
namespace Soundshark;
public partial class App : Application
{
    Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        SliderInput.Register();
        if (Environment.GetEnvironmentVariable("SOUNDSHARK_TEST_ROOT") == null)
        {
            instance = new Mutex(true, "Local\\Soundshark-" + Environment.UserName, out bool first);
            if (!first) { MessageBox.Show("Soundshark is already running.", "Soundshark"); Shutdown(); return; }
        }
        DispatcherUnhandledException += (_, a) =>
        {
            var error = UserErrors.Report(a.Exception, "ui.unhandled");
            MessageBox.Show(error.Display, "Soundshark", MessageBoxButton.OK, MessageBoxImage.Error); a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { AppLog.Error("process.unhandled", args.ExceptionObject as Exception ?? new Exception("Unknown fatal failure.")); AppLog.FlushOnExit(); };
        TaskScheduler.UnobservedTaskException += (_, args) => { AppLog.Error("task.unobserved", args.Exception); args.SetObserved(); };
        AppLog.Info("session.start", new { version = typeof(App).Assembly.GetName().Version?.ToString(), runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString(), is64Bit = Environment.Is64BitProcess });
        try { Paths.Init(); }
        catch (Exception ex) { MessageBox.Show(UserErrors.Report(ex, "startup").Display, "Soundshark", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(); return; }
        base.OnStartup(e);
        if (Environment.GetEnvironmentVariable("SOUNDSHARK_TEST_ROOT") == null) new MainWindow { WindowState = WindowState.Maximized }.Show();
    }
    protected override void OnExit(ExitEventArgs e) { AppLog.Info("session.exit", new { e.ApplicationExitCode }); AppLog.FlushOnExit(); instance?.Dispose(); base.OnExit(e); }
}
