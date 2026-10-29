using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Soundshark;
public sealed record UpdateHistoryEntry(string Version, string Title, string[] Changes);
public sealed class UpdateLogWindow : Window
{
    public static UpdateHistoryEntry[] History {
        get {
            using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Soundshark;component/Assets/UpdateHistory.json")).Stream;
            return JsonSerializer.Deserialize<UpdateHistoryEntry[]>(resource) ?? [];
        }
    }
    public UpdateLogWindow(Window owner)
    {
        Owner = owner; Title = "Update Log"; Width = 760; Height = 700; MinWidth = 500; MinHeight = 400;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var header = new StackPanel(); header.Children.Add(Ui.Text("Update Log", 24));
        header.Children.Add(Ui.Text("Soundshark " + AppUpdates.CurrentVersion.ToString(3) + " · Newest first", 13, true));
        header.Margin = new Thickness(0, 0, 0, 16); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var close = Ui.Button("Close", Close); close.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        var list = new StackPanel();
        foreach (var entry in History) {
            var body = new StackPanel(); body.Children.Add(Ui.Text(entry.Version + "  ·  " + entry.Title, 18));
            foreach (string change in entry.Changes) { var line = Ui.Text("•  " + change, 13); line.Margin = new Thickness(0, 7, 0, 0); body.Children.Add(line); }
            var card = Ui.Card(body); card.Margin = new Thickness(0, 0, 0, 12); list.Children.Add(card);
        }
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
}
