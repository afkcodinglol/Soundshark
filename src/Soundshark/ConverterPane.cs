using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Soundshark;
public sealed class ConverterPane : StackPanel
{
    readonly MainWindow main;
    readonly StackPanel items = new();
    readonly TextBlock status = Ui.Text("Add audio, choose a format, and convert. We handle the settings.", 13, true);
    readonly TextBlock formatHint = Ui.Text("", 12, true);
    readonly ComboBox format = new() { Name = "OutputFormat", Width = 175, ItemsSource = AudioOutputFormat.All, SelectedItem = AudioOutputFormat.Mp3 };
    readonly Button add, convert, clear;
    readonly List<Item> queue = [];
    AudioOutputFormat studioFormat = AudioOutputFormat.Mp3;
    sealed record Item(string Path, TextBlock Status);

    public ConverterPane(MainWindow owner)
    {
        main = owner; Children.Add(Ui.Text("Convert Your Audio", 28)); Children.Add(new Border { Height = 12 });
        Children.Add(Ui.Text("1. Add files     2. Choose a format     3. Convert", 14, true)); Children.Add(new Border { Height = 20 });
        add = Ui.Button("Add Files", Pick); convert = Ui.Button("Convert", () => _ = Convert());
        clear = Ui.Button("Clear List", () => { if (main.Available) { queue.Clear(); items.Children.Clear(); } });
        var selector = new StackPanel(); var label = Ui.Text("Convert To", 12, true); label.Margin = new Thickness(0, 0, 0, 4); selector.Children.Add(label); selector.Children.Add(format);
        System.Windows.Automation.AutomationProperties.SetName(format, "Convert To");
        var actions = Ui.Row(add, selector, convert, clear, Ui.Button("Open Output Folder", () => Process.Start(new ProcessStartInfo(Paths.Library) { UseShellExecute = true })));
        foreach (FrameworkElement child in actions.Children) child.VerticalAlignment = VerticalAlignment.Bottom;
        Children.Add(actions); Children.Add(formatHint); Children.Add(new Border { Height = 14 }); Children.Add(status); Children.Add(new Border { Height = 14 });
        Children.Add(new ScrollViewer { Content = items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420 });
        Children.Add(Ui.Text("Converted files appear in Library. Originals stay untouched; duplicate names get a numbered suffix.", 12, true));
        format.SelectionChanged += (_, _) => { if (!main.Settings.Discord && format.SelectedItem is AudioOutputFormat choice) studioFormat = choice; };
        main.Settings.DiscordChanged += ApplyDiscordMode; ApplyDiscordMode();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; timer.Tick += (_, _) => UpdateControls(); timer.Start(); UpdateControls();
    }
    void ApplyDiscordMode()
    {
        format.SelectedItem = main.Settings.Discord ? AudioOutputFormat.Mp3 : studioFormat;
        formatHint.Text = main.Settings.Discord ? "Discord Soundboard is on · MP3 selected automatically · up to 5 seconds and 512,000 bytes" : "Choose MP3, WAV, FLAC, M4A, OGG, Opus, or AIFF. Audio settings are automatic.";
        UpdateControls();
    }
    void UpdateControls()
    {
        bool available = main.Available;
        add.IsEnabled = available; convert.IsEnabled = clear.IsEnabled = available && queue.Count > 0;
        format.IsEnabled = available && !main.Settings.Discord;
    }
    void Pick()
    {
        if (!main.Available) return;
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "Audio and video|*.*", Title = "Add Files Containing Audio" };
        if (dialog.ShowDialog(main) == true) Add(dialog.FileNames);
    }
    public void Add(IEnumerable<string> paths)
    {
        if (!main.Available) return;
        foreach (string path in paths.Where(File.Exists))
        {
            var message = Ui.Text("Ready to convert", 12, true); message.MaxWidth = 650;
            var item = new Item(path, message); queue.Add(item);
            var body = new StackPanel(); body.Children.Add(Ui.Text(Path.GetFileName(path), 15)); body.Children.Add(new Border { Height = 8 });
            var card = Ui.Card(body); card.Margin = new Thickness(0, 0, 0, 10);
            body.Children.Add(Ui.Row(message, Ui.Button("Open In Studio", () => _ = main.OpenStudio(path)), Ui.Button("Remove", () => { if (main.Available) { queue.Remove(item); items.Children.Remove(card); UpdateControls(); } })));
            items.Children.Add(card);
        }
        UpdateControls();
    }
    async Task Convert()
    {
        if (!main.Available || queue.Count == 0) return;
        bool discord = main.Settings.Discord;
        var target = discord ? AudioOutputFormat.Mp3 : (AudioOutputFormat)format.SelectedItem;
        var work = queue.ToArray();
        await main.Run(async ct =>
        {
            int success = 0, index = 0;
            foreach (var item in work)
            {
                ct.ThrowIfCancellationRequested(); item.Status.Text = "Converting…"; item.Status.ToolTip = null;
                bool itemDone = false;
                status.Text = $"Converting {Path.GetFileName(item.Path)} to {target.Label} · {++index} of {work.Length}";
                try
                {
                    string destination = AudioConversion.NewDestination(item.Path, target);
                    var result = await AudioConversion.Convert(item.Path, destination, target, discord, ct, s =>
                    {
                        if (s.StartsWith("Checking ")) _ = Dispatcher.BeginInvoke(() => { if (!itemDone) item.Status.Text = s; });
                        if (s.StartsWith("out_time_us=") && long.TryParse(s[12..], out long us))
                            _ = Dispatcher.BeginInvoke(() => { if (!itemDone) item.Status.Text = $"Converting · {us / 1000000.0:F1}s processed"; });
                    });
                    itemDone = true; item.Status.Text = $"Saved {Path.GetFileName(destination)} · {result.Duration:F2}s"; item.Status.ToolTip = destination; success++;
                    await main.RefreshLibrary();
                }
                catch (OperationCanceledException) { itemDone = true; item.Status.Text = "Cancelled · original preserved"; status.Text = "Conversion cancelled. Completed files are in Library."; throw; }
                catch (Exception ex)
                {
                    itemDone = true;
                    var error = UserErrors.Report(ex, "conversion", new { input = item.Path, outputFormat = target.Label });
                    item.Status.ToolTip = error.Display;
                    item.Status.Text = "Needs attention · " + error.Inline;
                }
            }
            status.Text = $"Finished · {success} of {work.Length} saved to Library. Audio needing trimming can open in The Studio.";
        }, operation: "conversion.batch");
        UpdateControls();
    }
}
