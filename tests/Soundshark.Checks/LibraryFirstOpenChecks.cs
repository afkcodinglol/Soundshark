using System.IO;
using System.Windows;
using System.Windows.Controls;
using Soundshark;

internal static partial class Program
{
    static async Task LibraryFirstOpenChecks()
    {
        for (int i = 0; i < 14; i++) {
            using var writer = new NAudio.Wave.WaveFileWriter(Path.Combine(Paths.Library, $"Existing sound {i:00}.wav"), new NAudio.Wave.WaveFormat(48000, 16, 1));
            writer.Write(new byte[960], 0, 960);
        }
        int expectedCount = Directory.EnumerateFiles(Paths.Library).Count(AudioConversion.IsLibraryAudio);
        var window = new MainWindow { ShowActivated = false };
        window.Show(); await Task.Delay(500);
        // Match a user's first tab click. Do not traverse the Library visual tree first:
        // doing so initializes the panel generator and masks the startup bug.
        var tabs = (TabControl)window.FindName("Tabs");
        var libraryTab = (TabItem)tabs.Items[1];
        libraryTab.Focus(); window.UpdateLayout(); await Task.Delay(100);
        Check(tabs.SelectedIndex == 1, "keyboard focus opens Library from a fresh launch with existing sounds");
        var list = Descendants<ListBox>(window).Single(x => x.Name == "LibraryRows");
        Check(list.Items.Count == expectedCount && Descendants<ListBoxItem>(list).Any(), "first Library open realizes existing sound tiles without prewarming its visual tree");
        for (int i = 0; i < 10; i++) {
            ((TabItem)tabs.Items[0]).Focus(); window.UpdateLayout();
            libraryTab.Focus(); window.UpdateLayout();
        }
        Check(Descendants<ListBoxItem>(list).Any(), "repeated tab focus preserves working tile generation");
        Snapshot(window, "library-first-open.png");
        window.Close();
    }
}

