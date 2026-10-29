using System.IO;
using System.Windows;
using System.Windows.Controls;
using Soundshark.Core;

namespace Soundshark;

public sealed class SoundShortcutWindow : Window
{
    readonly MainWindow main;
    readonly string file;
    readonly CheckBox holdPtt;
    readonly HotkeyBox pttKey;
    readonly StackPanel entries = new();
    readonly TextBlock error = Ui.Text("", 12, true);
    readonly List<(HotkeyBox Key, ComboBox Route)> shortcuts = [];
    public sealed record RouteOption(string Label, SoundboardRoute Route) { public override string ToString() => Label; }
    static readonly RouteOption[] routes = [new("Mic", SoundboardRoute.Microphone), new("Preview", SoundboardRoute.Preview), new("Both", SoundboardRoute.Both)];
    public SoundShortcutWindow(MainWindow owner, string path)
    {
        main = owner; file = Path.GetFileName(path); error.Foreground=System.Windows.Media.Brushes.IndianRed; Owner = owner;
        Title = "Sound Shortcuts"; Width = 640; Height = 560; MinWidth = 560; MinHeight = 360;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; SetResourceReference(StyleProperty, typeof(Window));
        var root = new Grid { Margin = new Thickness(24) }; root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); Content = root;
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        body.Children.Add(Ui.Text(Path.GetFileNameWithoutExtension(file), 22));
        body.Children.Add(Ui.Text("Click a box to listen, then press a key, a key combination, any mouse button or the mouse wheel. Click again while listening to bind the left mouse button. Use Clear to remove a binding.", 13, true));

        var headings = Ui.Row(Ui.Text("Shortcut", 12, true), Ui.Text("Play To", 12, true)); headings.Margin = new Thickness(0, 16, 0, 4);
        ((FrameworkElement)headings.Children[0]).Width = 220; body.Children.Add(headings); body.Children.Add(entries);
        var existing = main.Settings.Soundboard.Bindings.Where(b => b.Command == SoundboardCommand.PlaySound && string.Equals(b.SoundFilename, file, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var binding in existing) Add(binding);
        if (existing.Length == 0) Add(null);
        body.Children.Add(Ui.Button("Add Shortcut", () => Add(null)));
        var ptt=main.Settings.Soundboard.SoundPtt.GetValueOrDefault(file);
        holdPtt=new CheckBox { Name="HoldSoundPtt",Content="Hold down PTT button",IsChecked=ptt?.Enabled==true,Margin=new Thickness(0,18,0,8) };
        pttKey=new HotkeyBox(ptt?.Chord,()=>true) { Name="SoundPttKey",Width=260,IsEnabled=ptt?.Enabled==true };
        holdPtt.Click+=(_,_)=>pttKey.IsEnabled=holdPtt.IsChecked==true;
        body.Children.Add(holdPtt); body.Children.Add(Ui.Row(pttKey,Ui.Button("Clear",pttKey.ClearBinding)));
        body.Children.Add(Ui.Text("This sound holds its PTT keys during playback and releases them when it finishes, stops or fails. A mouse wheel cannot be held.",12,true));
        var footer = new StackPanel(); footer.Children.Add(error); footer.Children.Add(Ui.Row(Ui.Button("Save", () => _ = Save()), Ui.Button("Cancel", Close))); Grid.SetRow(footer, 1); root.Children.Add(footer);
    }
    void Add(SoundboardBinding? binding)
    {
        var key = new HotkeyBox(binding?.Chord, () => main.Settings.Soundboard.AllowLeftRightModifiers) { Width = 220 };
        var route = new ComboBox { ItemsSource = routes, SelectedItem = routes.Single(r => r.Route == (binding?.Route ?? SoundboardRoute.Microphone)), Width = 120 };
        var state = (key, route); shortcuts.Add(state);
        var row = Ui.Row(key, route, Ui.Button("Clear",key.ClearBinding));
        row.Children.Add(Ui.Button("Remove", () => { shortcuts.Remove(state); entries.Children.Remove(row); })); entries.Children.Add(row);
    }
    async Task Save()
    {
        try {
            if (!File.Exists(Path.Combine(Paths.Library, file))) throw new SoundboardException("This sound was removed from Library. Close this window and choose another sound.");
            var current = main.Settings.Soundboard;
            var keep = current.Bindings.Where(b => b.Command != SoundboardCommand.PlaySound || !string.Equals(b.SoundFilename, file, StringComparison.OrdinalIgnoreCase));
            var assigned = shortcuts.Where(s => s.Key.Chord != null).Select(s => new SoundboardBinding { Chord = s.Key.Chord!, Command = SoundboardCommand.PlaySound, SoundFilename = file, Route = ((RouteOption)s.Route.SelectedItem).Route });
            var perSound=new Dictionary<string,SoundPttBinding>(current.SoundPtt,StringComparer.OrdinalIgnoreCase);
            if(holdPtt.IsChecked==true) perSound[file]=new(true,pttKey.Chord); else perSound.Remove(file);
            var candidate = current with { Bindings = keep.Concat(assigned).ToArray(), SoundPtt=perSound };
            candidate.Validate(); await main.ApplySoundboardSettings(candidate); Close();
        } catch (ArgumentException ex) { error.Text = ex.Message; }
        catch (Exception ex) { error.Text = UserErrors.Report(ex, "soundboard.shortcut.save").Inline; }
    }
}
