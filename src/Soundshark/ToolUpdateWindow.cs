using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Soundshark;
public sealed class ToolUpdateWindow : Window
{
    readonly MainWindow main;
    readonly CancellationTokenSource cancel = new();
    readonly TextBlock status = Ui.Text("Checking for import tool updates…", 12);
    readonly ProgressBar progress = new() { Name = "ImportToolsDownloadProgress", Height = 9, Maximum = 100, IsIndeterminate = true, Margin = new Thickness(0, 9, 0, 0) };
    readonly Button action;
    readonly Func<CancellationToken, IProgress<ToolUpdateProgress>, Task> update;
    bool busy = true, closeRequested;
    public ToolUpdateWindow(MainWindow owner, Func<CancellationToken, IProgress<ToolUpdateProgress>, Task>? operation = null)
    {
        main = owner; update = operation ?? ((ct, p) => ToolUpdater.Update(ct, p));
        Owner = owner; Title = "Update Import Tools"; Width = 590; Height = 300; MinWidth = 420; MinHeight = 270; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; SetResourceReference(StyleProperty, typeof(Window));
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var header = new StackPanel(); header.Children.Add(Ui.Text("Update Import Tools", 24)); header.Children.Add(Ui.Text("Update video importing without changing your audio or Library.", 13, true));
        header.Margin = new Thickness(0, 0, 0, 14); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        action = Ui.Button("Cancel", CancelOrClose); action.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(action, Dock.Bottom); root.Children.Add(action);
        progress.SetResourceReference(ProgressBar.ForegroundProperty, "Live"); progress.SetResourceReference(ProgressBar.BackgroundProperty, "SuccessSurface");
        progress.Effect = new DropShadowEffect { Color = Color.FromRgb(66, 222, 144), BlurRadius = 12, ShadowDepth = 0, Opacity = .4 };
        var download = new StackPanel(); download.Children.Add(status); download.Children.Add(progress);
        var panel = new Border { Child = download, CornerRadius = new CornerRadius(9), Padding = new Thickness(10), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 14) };
        panel.SetResourceReference(Border.BackgroundProperty, "SuccessSurface"); root.Children.Add(panel);
        Closing += (_, e) => { if (busy) { e.Cancel = true; closeRequested = true; cancel.Cancel(); status.Text = "Cancelling…"; } };
        Closed += (_, _) => cancel.Dispose(); Loaded += async (_, _) => await Start();
    }
    void CancelOrClose() { if (busy) { status.Text = "Cancelling…"; cancel.Cancel(); action.IsEnabled = false; } else Close(); }
    async Task Start()
    {
        var reports = new Progress<ToolUpdateProgress>(p => { if (cancel.IsCancellationRequested) return; status.Text = p.Status; progress.IsIndeterminate = p.Percent == null; progress.Value = p.Percent ?? 0; });
        bool success = false;
        await main.Run(async ct => {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancel.Token);
            try { await Task.Run(() => update(linked.Token, reports), linked.Token); success = true; }
            catch (OperationCanceledException) { status.Text = "Update cancelled · your previous tools are still available."; throw; }
            catch { status.Text = "Couldn't update import tools. Your previous tools are still available."; throw; }
        }, operation: "tools.update");
        busy = false; progress.IsIndeterminate = false;
        if (success) { progress.Value = 100; main.SetImportToolUpdateAvailable(false); }
        action.Content = "Close"; action.IsEnabled = true;
        if (closeRequested) Close();
    }
}
