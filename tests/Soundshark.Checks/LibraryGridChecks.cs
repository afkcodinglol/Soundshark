using System.IO;
using System.Windows;
using System.Windows.Controls;
using Soundshark;

internal static partial class Program
{
    static async Task LibraryGridChecks(MainWindow window)
    {
        ((TabControl)window.FindName("Tabs")).SelectedIndex = 1;
        window.UpdateLayout(); await Task.Delay(80);
        var pane = Descendants<LibraryPane>(window).Single();
        var list = Descendants<ListBox>(pane).Single();
        for (int i = 0; i < 600; i++) {
            using var writer = new NAudio.Wave.WaveFileWriter(Path.Combine(Paths.Library, $"Sound {i:000}.wav"), new NAudio.Wave.WaveFormat(48000, 16, 1));
            writer.Write(new byte[960], 0, 960);
        }
        await Task.Delay(500); await pane.Refresh(); await Task.Delay(100); window.UpdateLayout();
        Check(Descendants<ListBoxItem>(list).Count() < 80, "600 sounds generate only visible tiles");
        var cells = Descendants<ListBoxItem>(list).ToArray();
        var a = cells[0].TranslatePoint(new Point(), list); var b = cells[1].TranslatePoint(new Point(), list);
        Check(b.X > a.X && Math.Abs(a.Y - b.Y) < 1, "sounds fill multiple columns");
        list.ScrollIntoView(list.Items[599]); window.UpdateLayout(); await Task.Delay(100);
        Check(list.ItemContainerGenerator.ContainerFromIndex(599) is ListBoxItem last && last.TranslatePoint(new Point(), list).Y < list.ActualHeight, "last sound scrolls into view");
        Check(Descendants<ListBoxItem>(list).Count() < 80, "scrolling releases offscreen tiles");
        window.Width = 850; window.Height = 700; window.UpdateLayout();
        var original = Theme.Current;
        Theme.Apply(original with { FontSize = 19, CompactLibrary = false }); window.UpdateLayout();
        list.ScrollIntoView(list.Items[0]); window.UpdateLayout(); await Task.Delay(100);
        var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        foreach (var button in Descendants<Button>(first)) {
            var point = button.TranslatePoint(new Point(), first);
            Check(point.X >= 0 && point.X + button.ActualWidth <= first.ActualWidth + 1 && point.Y + button.ActualHeight <= first.ActualHeight + 1, "tile controls fit at maximum text size and minimum window size");
        }
        Snapshot(window, "library-grid-large-text.png");
        Theme.Apply(original); window.Width = 1920; window.Height = 1040; window.UpdateLayout();
        Snapshot(window, "library-grid-wide.png");
        var search = Descendants<TextBox>(pane).Single(t => t.Name == "LibrarySearch");
        search.Text = "Sound 599"; await pane.Refresh(); window.UpdateLayout();
        Check(list.Items.Count == 1 && Descendants<ListBoxItem>(list).Count() == 1, "filtering after scrolling resets the grid to the matching sound");
        search.Text = "no matching sound"; await pane.Refresh(); window.UpdateLayout();
        Check(list.Items.Count == 0, "empty search results clear realized tiles");
    }
}


