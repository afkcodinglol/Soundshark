using System.Windows;
using System.Windows.Controls;

namespace Soundshark;
public sealed class AppRollbackWindow : Window
{
    public AppRollbackWindow(MainWindow owner)
    {
        Owner = owner; Title = "Roll Back Soundshark"; Width = 560; Height = 310; MinWidth = 420; MinHeight = 280;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; SetResourceReference(StyleProperty, typeof(Window));
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var choice = AppRollback.Find(AppContext.BaseDirectory);
        var restore = Ui.Button("Restore And Restart", async () => {
            if (choice == null || !owner.Available) return;
            bool verified = false;
            await owner.Run(async ct => { await Task.Run(() => AppRollback.Verify(choice, ct), ct); verified = true; }, operation: "app.rollback.verify");
            if (!verified) return;
            owner.PendingRollback = choice; Close(); owner.Close();
        });
        restore.IsEnabled = choice != null;
        var buttons = Ui.Row(restore, Ui.Button("Cancel", Close)); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var text = new StackPanel(); text.Children.Add(Ui.Text("Roll Back Soundshark", 24));
        text.Children.Add(Ui.Text(choice == null ? "No previous version is available for this copy yet. A backup is created when Soundshark updates." : "Restore version " + choice.Backup.Version + " and restart Soundshark.", 14));
        text.Children.Add(Ui.Text("Your Library, settings and microphone setup are kept. Automatic updates pause after rollback. Use Update Soundshark whenever you want to update again.", 13, true));
        root.Children.Add(new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
}
