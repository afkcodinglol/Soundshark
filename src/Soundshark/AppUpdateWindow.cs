using System.Windows;
using System.Windows.Controls;

namespace Soundshark;
public sealed class AppUpdateWindow : Window
{
    readonly AppUpdates updates;
    readonly TextBlock status = Ui.Text("", 14), notes = Ui.Text("", 13, true);
    readonly ProgressBar progress = new() { Name = "AppDownloadProgress", Height = 9, Maximum = 100, Margin = new Thickness(0, 8, 0, 0) };
    readonly Border progressPanel;
    readonly Button check, install;
    public AppUpdateWindow(MainWindow owner, AppUpdates? service = null)
    {
        updates = service ?? owner.Updates; Owner = owner; Title = "Update Soundshark"; Width = 620; Height = 470; MinWidth = 460; MinHeight = 350;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; SetResourceReference(StyleProperty, typeof(Window));
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var header = new StackPanel(); header.Children.Add(Ui.Text("Update Soundshark", 24)); header.Children.Add(Ui.Text("Installed version " + AppUpdates.CurrentVersion.ToString(3), 13, true));
        header.Children.Add(Ui.Text("Updates download automatically and apply when Soundshark closes. Your sounds and preferences are kept.", 13, true));
        header.Margin = new Thickness(0, 0, 0, 18); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        check = Ui.Button("Check Now", () => _ = updates.Check(manual: true));
        install = Ui.Button("Install And Restart", () => { updates.RestartAfterInstall = true; Close(); owner.Close(); });
        var buttons = Ui.Row(check, install, Ui.Button("Close", Close)); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        progress.SetResourceReference(ProgressBar.ForegroundProperty, "Live"); progress.SetResourceReference(ProgressBar.BackgroundProperty, "SuccessSurface");
        progress.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Color.FromRgb(66, 222, 144), BlurRadius = 12, ShadowDepth = 0, Opacity = .4 };
        var download = new StackPanel(); download.Children.Add(status); download.Children.Add(progress);
        progressPanel = new Border { Child = download, CornerRadius = new CornerRadius(9), Padding = new Thickness(10), Margin = new Thickness(0, 10, 0, 14) };
        progressPanel.SetResourceReference(Border.BackgroundProperty, "SuccessSurface");
        var body = new StackPanel(); body.Children.Add(progressPanel); body.Children.Add(notes);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        updates.Changed += Refresh; Closed += (_, _) => updates.Changed -= Refresh;
        Refresh(); Loaded += (_, _) => _ = updates.Check(manual: true);
    }
    void Refresh() => Dispatcher.InvokeAsync(() => {
        status.Text = updates.Status; notes.Text = updates.Release?.Notes ?? "";
        check.IsEnabled = !updates.Busy; install.IsEnabled = updates.Ready;
        progress.Visibility = updates.Busy || updates.Ready ? Visibility.Visible : Visibility.Collapsed;
        progress.IsIndeterminate = updates.ProgressPercent == null; progress.Value = updates.ProgressPercent ?? 0;
    });
}
