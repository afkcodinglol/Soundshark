using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Soundshark;

internal static partial class Program
{
    static async Task LibraryColorChecks(MainWindow window)
    {
        var tabs = (TabControl)window.FindName("Tabs"); tabs.SelectedIndex = 1;
        window.UpdateLayout(); await Task.Delay(80);
        var pane = Descendants<LibraryPane>(window).Single();
        var list = Descendants<ListBox>(pane).Single();
        string[] names = ["Color A.wav", "Color B.wav", "Color C.wav", "Color D.wav"];
        foreach (string name in names) {
            using var writer = new NAudio.Wave.WaveFileWriter(Path.Combine(Paths.Library, name), new NAudio.Wave.WaveFormat(48000, 16, 1)); writer.Write(new byte[960], 0, 960);
        }
        await window.RefreshLibrary(); await Task.Delay(300);
        string first = Path.Combine(Paths.Library, names[0]);
        await pane.SetButtonColor(first, "#E53935");
        await pane.SetButtonColor(Path.Combine(Paths.Library, names[1]), "#287BB8");
        await pane.SetButtonColor(Path.Combine(Paths.Library, names[2]), "#E53935");
        Check(Settings.Load().LibraryColors[names[0]] == "#E53935", "per-sound button colors persist independently of the application accent");
        async Task<ListBoxItem> Cell(string name) {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline) {
                var row = list.Items.OfType<LibraryPane.SoundRow>().Single(r => Path.GetFileName(r.Path) == name);
                list.ScrollIntoView(row); list.UpdateLayout(); await Task.Delay(30);
                if (list.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem cell) return cell;
            }
            throw new Exception("Library color row was not realized");
        }
        var firstCell = await Cell(names[0]);
        var preview = Descendants<Button>(firstCell).Single(b => b.Content is string s && s == "Preview");
        Check(preview.Background is SolidColorBrush red && ColorPickerWindow.Hex(red.Color) == "#E53935" && Descendants<TextBlock>(preview).Any(t => t.Text == "Preview"), "colored Library buttons show the exact chosen color and retain their labels");
        Check(Descendants<TextBlock>(preview).All(t => t.Foreground is SolidColorBrush ink && ink.Color == SoundColors.TextColor(((SolidColorBrush)preview.Background).Color)), "rendered button label uses contrast text instead of the global theme text color");
        var defaultCell = await Cell(names[3]);
        Check(Descendants<Button>(defaultCell).All(b => b.Background == window.FindResource("AccentSurface")), "recycled uncolored rows retain the normal theme button styling");
        async Task Pick(string color, string action) {
            var cell = await Cell(names[0]); var more = Descendants<Button>(cell).Single(b => b.Content is string s && s == "•••");
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(30);
            var menu = PresentationSource.CurrentSources.Cast<PresentationSource>().Where(p => p.RootVisual is Visual).SelectMany(p => Descendants<ContextMenu>((Visual)p.RootVisual)).Single(m => m.IsOpen && m.PlacementTarget == more);
            var item = menu.Items.OfType<MenuItem>().Single(m => m.Header is string s && s == "Button Color");
            bool opened = false;
            _ = window.Dispatcher.BeginInvoke(async () => {
                await Task.Delay(80); var picker = Application.Current.Windows.OfType<ColorPickerWindow>().Single();
                opened = picker.Title == "Button Color";
                Descendants<TextBox>(picker).Single(t => t.Name == "ColorHex").Text = color;
                Snapshot(picker, "library-button-color-wheel.png");
                Descendants<Button>(picker).Single(b => b.Content is string s && s == action).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false;
            var deadline = DateTime.UtcNow.AddSeconds(5); while (!opened && DateTime.UtcNow < deadline) await Task.Delay(20);
            Check(opened, "Button Color in the three-dot menu opens the color wheel");
            await Task.Delay(100);
        }
        await Pick("#00FF00", "Use Color");
        Check(Settings.Load().LibraryColors[names[0]] == "#00FF00", "Use Color saves the selected wheel color for the chosen sound");
        string settingsBefore = await File.ReadAllTextAsync(Path.Combine(Paths.Root, "settings.json"));
        await Pick("#000000", "Cancel");
        Check(await File.ReadAllTextAsync(Path.Combine(Paths.Root, "settings.json")) == settingsBefore && list.Items.OfType<LibraryPane.SoundRow>().Single(r => Path.GetFileName(r.Path) == names[0]).ColorHex == "#00FF00", "Cancel restores the sound color without writing preferences");
        await Pick("#FFFFFF", "Use Default");
        Check(!Settings.Load().LibraryColors.ContainsKey(names[0]), "Use Default clears the saved color and restores theme styling");
        await pane.SetButtonColor(first, "#E53935");
        var grouping = Descendants<CheckBox>(pane).Single(c => c.Name == "GroupLibraryByColor");
        grouping.IsChecked = true; grouping.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        await Task.Delay(250);
        var colored = list.Items.OfType<LibraryPane.SoundRow>().Where(r => r.ColorHex != null).ToArray();
        Check(colored.Select(r => r.ColorHex).SequenceEqual(colored.Select(r => r.ColorHex).Order(StringComparer.Ordinal)) && Settings.Load().GroupLibraryByColor, "Group By Color keeps matching sounds together and persists across settings reloads");
        var original = Theme.Current;
        foreach (bool light in new[] { false, true }) {
            Theme.Apply(original with { LightMode = light });
            firstCell = await Cell(names[0]);
            Check(Descendants<Button>(firstCell).All(b => b.Background is SolidColorBrush brush && ColorPickerWindow.Hex(brush.Color) == "#E53935"), "custom sound button colors survive dark and light theme changes");
            Snapshot(window, light ? "library-colors-light.png" : "library-colors-dark.png");
        }
        Theme.Apply(original);
        Check(SoundColors.TextColor(Colors.White) == Colors.Black && SoundColors.TextColor(Colors.Black) == Colors.White && SoundColors.TextColor(Colors.Yellow) == Colors.Black && SoundColors.TextColor(Colors.Blue) == Colors.White, "button labels choose contrasting text for very light and dark colors");
        var map = SoundColors.Normalize(new() { ["OLD.WAV"] = "#aabbcc", ["bad.wav"] = "invalid", ["../escape.wav"] = "#FFFFFF" });
        SoundColors.Rename(map, "old.wav", "new.wav");
        Check(map.Count == 1 && map["NEW.WAV"] == "#AABBCC", "sound colors validate inputs, ignore filename case and follow renamed sounds");
        foreach (string name in names) await pane.SetButtonColor(Path.Combine(Paths.Library, name), null);
        grouping.IsChecked = false; grouping.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); await Task.Delay(100);
    }
}
