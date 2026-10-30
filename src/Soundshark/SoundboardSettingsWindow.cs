using System.Globalization;
using System.Diagnostics;
using System.Windows.Threading;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Data;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using Microsoft.Win32;
using Soundshark.Core;

namespace Soundshark;
public sealed class SoundboardSettingsWindow : Window
{
    readonly MainWindow main;
    readonly SoundboardConfiguration original;
    bool saving;
    readonly ComboBox microphone = new() { MinWidth = 400, DisplayMemberPath = "Name" }, speakers = new() { MinWidth = 400, DisplayMemberPath = "Name" };
    readonly CheckBox monitor, block, activation, autoStop, repeat;
    readonly ComboBox mode = new() { Name="MicrophoneMode",MinWidth=400,DisplayMemberPath="Name" };
    readonly ComboBox voice = new() {Name="VirtualVoiceSource",MinWidth=400,DisplayMemberPath="Name"}, cable = new(){Name="VirtualCablePlayback",MinWidth=400,DisplayMemberPath="Name"}, cableRecording = new(){Name="VirtualCableRecording",MinWidth=400,DisplayMemberPath="Name"};
    readonly CheckBox enableVirtual = new(){Name="EnableVirtualMic",Content="Enable Virtual Mic (starts voice passthrough when saved)",Margin=new Thickness(0,8,0,8)};
    readonly StackPanel virtualOptions=new(), physicalOptions=new();
    readonly TextBlock virtualStatus=Ui.Text("",12,true), destinationHint=Ui.Text("",13,true), protectionStatus=Ui.Text("",12,true);
    readonly DispatcherTimer statusTimer = new(){Interval=TimeSpan.FromSeconds(1)};
    sealed record Mode(MicrophoneBackend Backend,string Name) { public override string ToString() => Name; }
    readonly Slider soundVolume;
    readonly TextBox startDelay, relativeDelay, leadIn, releaseDelay, activationPath;
    readonly TextBlock error = Ui.Text("", 13), setupStatus = Ui.Text("", 12, true);
    readonly List<(SoundboardCommand Command, string File, HotkeyBox Key, ComboBox Route)> bindings = [];
    sealed record Device(string Id, string Name) { public override string ToString() => Name; }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    static CheckBox Check(string text, bool value) => new() { Content = text, IsChecked = value, Margin = new Thickness(0, 7, 0, 7) };
    static TextBox Number(double value) => new() { Text = value.ToString(CultureInfo.InvariantCulture), Width = 100 };
    sealed class Friendly : IValueConverter
    {
        public object Convert(object value, Type target, object parameter, CultureInfo culture) => Regex.Replace(value?.ToString() ?? "", "([a-z])([A-Z])", "$1 $2");
        public object ConvertBack(object value, Type target, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
    static ComboBox Choice<T>(T selected) where T : struct, Enum
    {
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding { Converter = new Friendly() });
        return new ComboBox { ItemsSource = Enum.GetValues<T>(), SelectedItem = selected, Width = 190, ItemTemplate = new DataTemplate { VisualTree = text } };
    }
    static void Field(Panel panel, string label, UIElement input) {
        var text = Ui.Text(label, 13, true); text.MinWidth = 235;
        if (input is Slider slider) {
            var amount = Ui.Text("", 13); amount.MinWidth = 45;
            amount.SetBinding(TextBlock.TextProperty, new Binding("Value") { Source = slider, StringFormat = "{0:0}%" });
            panel.Children.Add(Ui.Row(text, input, amount));
        } else panel.Children.Add(Ui.Row(text, input));
    }
    static void Heading(Panel panel, string label) { var text = Ui.Text(label, 18); text.Margin = new Thickness(0, 12, 0, 12); panel.Children.Add(text); }
    static Slider Volume(double value) => new() { Name = "SoundOutputVolume", Minimum = 0, Maximum = 100, Value = Math.Clamp(value, 0, 1) * 100, Width = 250, TickFrequency = 10, ToolTip = "Sound output volume: 0% mutes clips; 100% uses the automatically balanced level." };
    static TabItem Tab(string name, UIElement content) => new() { Header = name, Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
    public SoundboardSettingsWindow(MainWindow owner)
    {
        main = owner; original = owner.Settings.Soundboard; error.Foreground=System.Windows.Media.Brushes.IndianRed; SetResourceReference(StyleProperty, typeof(Window));
        Closing += (_, e) => { if (saving) e.Cancel = true; };
        Owner = owner; Title = "Soundboard Settings"; Width = 920; Height = 760; MinWidth = 780; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SourceInitialized += (_, _) => { int dark = 1, black = 0; var handle = new WindowInteropHelper(this).Handle; DwmSetWindowAttribute(handle, 20, ref dark, 4); DwmSetWindowAttribute(handle, 35, ref black, 4); };
        var root = new Grid { Margin = new Thickness(22) }; root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); Content = root;
        var tabs = new TabControl(); root.Children.Add(tabs);
        var audio = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        Heading(audio, "Microphone Setup");
        mode.ItemsSource = new[]{new Mode(MicrophoneBackend.VirtualCable,"Soundshark Virtual Mic — VB-CABLE"),new Mode(MicrophoneBackend.NativeMicrophone,"Physical Mic — Experimental")};
        mode.SelectedItem = mode.Items.Cast<Mode>().First(m=>m.Backend==(original.Backend==MicrophoneBackend.NativeMicrophone?MicrophoneBackend.NativeMicrophone:MicrophoneBackend.VirtualCable));
        Field(audio,"Microphone Mode",mode);
        audio.Children.Add(Ui.Text("VB-CABLE is recommended for new setups. Routing stays off until you explicitly enable it and save. The Soundshark option name does not create or rename a Windows device.",12,true));
        enableVirtual.IsChecked=original.VirtualRoutingEnabled;
        Field(virtualOptions,"Physical Voice Source",voice);
        Field(virtualOptions,"Cable Playback Endpoint",cable);
        Field(virtualOptions,"Cable Recording Endpoint",cableRecording);
        virtualOptions.Children.Add(enableVirtual); virtualOptions.Children.Add(destinationHint); virtualOptions.Children.Add(virtualStatus);
        virtualOptions.Children.Add(Ui.Text("Keep Soundshark running for voice passthrough. Closing its main window keeps it in the tray. Local clip monitoring never includes live voice.",12,true));
        virtualOptions.Children.Add(Ui.Row(Ui.Button("Get VB-CABLE (Official)",()=>Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/"){UseShellExecute=true})),Ui.Button("Refresh Devices",()=>RefreshDevices())));
        Field(physicalOptions,"Microphone",microphone); physicalOptions.Children.Add(setupStatus);
        physicalOptions.Children.Add(Ui.Text("Choose this same physical microphone in Discord / your game. Current unsigned experimental setup disables protected AudioDG system-wide. It requires administrator access and a Windows restart.",12,true));
        physicalOptions.Children.Add(Ui.Row(Ui.Button("Enable Microphone Playback",()=>Install(false)),Ui.Button("Remove Microphone Support",()=>Install(true))));
        audio.Children.Add(virtualOptions); audio.Children.Add(physicalOptions); audio.Children.Add(protectionStatus);
        audio.Children.Add(Ui.Button("Remove Experimental Support & Restore Previous Settings",()=>{
            if(MessageBox.Show(this,"Remove experimental microphone support from all configured microphones and restore backed-up endpoint effects and audio policy? This requires administrator approval and may require a Windows restart. Missing backups or conflicting changes will stop cleanup for repair.","Restore Previous Audio Settings",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            try{MicrophoneSetup.RemoveAll();}catch(Exception ex){error.Text=ex.Message;}
        }));
        mode.SelectionChanged+=(_,_)=>UpdateMode(); microphone.SelectionChanged+=(_,_)=>UpdateSetup(); cableRecording.SelectionChanged+=(_,_)=>UpdateMode();
        RefreshDevices(); UpdateMode(); UpdateSetup();
        statusTimer.Tick+=(_,_)=>{virtualStatus.Text=main.VirtualMicStatus;protectionStatus.Text=MicrophoneSetup.ProtectionStatus;};
        Loaded+=(_,_)=>statusTimer.Start(); Closed+=(_,_)=>statusTimer.Stop();
        Field(audio, "Preview / Monitor Output", speakers);
        monitor = Check("Also Hear Sounds I Send Through My Mic", original.HearTransmittedSounds); audio.Children.Add(monitor);
        block = Check("Block My Voice While A Sound Is Playing", original.BlockVoiceDuringSounds); audio.Children.Add(block);
        Heading(audio, "Sound Output"); soundVolume = Volume(original.TransmittedSoundVolume);
        Field(audio, "Sound Output Volume", soundVolume);
        audio.Children.Add(Ui.Text("Automatically balances loudness and limits peaks. 100% is the full controlled level; 0% mutes sound clips.", 13, true));
        tabs.Items.Add(Tab("Audio", audio));
        var shortcuts = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        shortcuts.Children.Add(Ui.Text("Click a shortcut box and press the combination. Use Clear to remove a binding. Shortcuts work while Soundshark is in the background. Keep an emergency Stop shortcut.", 13, true));
        shortcuts.Children.Add(Ui.Text("Set each sound's shortcut and Mic / Preview / Both route using its Shortcut button in Library.", 13, true));
        Heading(shortcuts, "Playback Controls");
        foreach (var command in Enum.GetValues<SoundboardCommand>().Where(c => c != SoundboardCommand.PlaySound && SoundboardConfiguration.PlaybackCommand(c))) AddBinding(shortcuts, command, "");
        tabs.Items.Add(Tab("Hotkeys", shortcuts));
        var advanced = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        Heading(advanced, "Playback Timing"); startDelay = Number(original.StartDelayMs); relativeDelay = Number(original.RelativeMicrophoneDelayMs); leadIn = Number(original.AutoKeyLeadInMs); releaseDelay = Number(original.AutoKeyReleaseDelayMs);
        Field(advanced, "Start Delay (0–5000 ms)", startDelay); Field(advanced, "Mic Vs Speaker Offset (−1000…1000 ms)", relativeDelay); Field(advanced, "Push-To-Talk Lead-In (ms)", leadIn); Field(advanced, "Push-To-Talk Release Delay (ms)", releaseDelay);
        advanced.Children.Add(Ui.Text("Positive timing offset sends the clip through the mic later than local monitoring. The start delay happens before automatic keys are pressed.", 12, true));
        activation = Check("Play An Activation Sound Before The Clip", original.UseActivationSound); advanced.Children.Add(activation);
        activationPath = new TextBox { Text = original.ActivationSoundPath, Width = 460 };
        advanced.Children.Add(Ui.Row(activationPath, Ui.Button("Browse", () => { var dialog = new OpenFileDialog { Filter = "Audio|*.mp3;*.wav;*.flac;*.m4a;*.ogg;*.opus;*.aiff" }; if (dialog.ShowDialog(this) == true) activationPath.Text = dialog.FileName; })));
        advanced.Children.Add(Ui.Text("The activation sound uses the same sound output volume and automatic loudness protection.", 12, true));
        repeat = Check("Repeat Sounds Until Stopped", original.RepeatSounds); autoStop = Check("Always Stop Hotkey Sounds At Their End", original.StopHotkeySoundsAtEnd); advanced.Children.Add(repeat); advanced.Children.Add(autoStop);
        tabs.Items.Add(Tab("Advanced", advanced));
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; footer.Children.Add(error);
        footer.Children.Add(Ui.Row(Ui.Button("Save Settings", () => _ = Save()), Ui.Button("Cancel", Close))); Grid.SetRow(footer, 1); root.Children.Add(footer);
    }
    void AddBinding(Panel parent, SoundboardCommand command, string file)
    {
        var previous = original.Bindings.FirstOrDefault(b => b.Command == command && string.Equals(b.SoundFilename, file, StringComparison.OrdinalIgnoreCase));
        var key = new HotkeyBox(previous?.Chord, () => true);
        var route = Choice(previous?.Route ?? SoundboardRoute.Microphone);
        var label = Ui.Text(file.Length > 0 ? Path.GetFileNameWithoutExtension(file) : Regex.Replace(command.ToString(), "([a-z])([A-Z])", "$1 $2"), 13); label.Width = 220;
        parent.Children.Add(Ui.Row(label, key, Ui.Button("Clear",key.ClearBinding), route)); route.Visibility = command == SoundboardCommand.PlaySound ? Visibility.Visible : Visibility.Collapsed;
        bindings.Add((command, file, key, route));
    }
    void RefreshDevices()
    {
        try {
            using var enumerator = new MMDeviceEnumerator();
            Device[] Devices(DataFlow flow)=>enumerator.EnumerateAudioEndPoints(flow,DeviceState.Active).Select(d=>{using(d)return new Device(d.ID,d.FriendlyName);}).ToArray();
            var captures=Devices(DataFlow.Capture);var renders=Devices(DataFlow.Render);
            void Fill(ComboBox control,Device[] items,string saved,Func<Device,bool>? preferred=null) {
                string id=(control.SelectedItem as Device)?.Id??saved;
                if(id.Length>0&&!items.Any(d=>d.Id==id))items=items.Append(new Device(id,"Unavailable — "+id)).ToArray();
                control.ItemsSource=items;
                control.SelectedItem=id.Length>0?items.FirstOrDefault(d=>d.Id==id):items.FirstOrDefault(preferred??(_=>true));
            }
            Fill(microphone,captures,original.MicrophoneEndpointId,d=>!d.Name.Contains("CABLE",StringComparison.OrdinalIgnoreCase));
            Fill(voice,captures,original.VirtualVoiceEndpointId,d=>!d.Name.Contains("CABLE",StringComparison.OrdinalIgnoreCase));
            Fill(cable,renders,original.TransmissionEndpointId,d=>d.Name.Contains("CABLE",StringComparison.OrdinalIgnoreCase));
            Fill(cableRecording,captures,original.CableRecordingEndpointId,d=>d.Name.Contains("CABLE",StringComparison.OrdinalIgnoreCase));
            Fill(speakers,new[]{new Device("","Windows Default Speakers / Headphones")}.Concat(renders).ToArray(),original.MonitorEndpointId);
            UpdateMode();
        }catch(Exception ex){error.Text="Could not refresh audio devices. "+ex.Message;}
    }
    void UpdateMode()
    {
        bool useVirtual=(mode.SelectedItem as Mode)?.Backend==MicrophoneBackend.VirtualCable;
        virtualOptions.Visibility=useVirtual?Visibility.Visible:Visibility.Collapsed;
        physicalOptions.Visibility=useVirtual?Visibility.Collapsed:Visibility.Visible;
        destinationHint.Text="In Discord / games, select the microphone: "+((cableRecording.SelectedItem as Device)?.Name??"CABLE Output (install VB-CABLE separately, then refresh devices)")+". Do not select the physical voice source for this mode.";
        virtualStatus.Text=main.VirtualMicStatus;protectionStatus.Text=MicrophoneSetup.ProtectionStatus;
    }
    void UpdateSetup() { setupStatus.Text = microphone.SelectedItem is Device device ? MicrophoneSetup.Status(device.Id) : "No microphone found. Connect a microphone and reopen Settings."; }
    void Install(bool remove)
    {
        try { if (microphone.SelectedItem is not Device device) throw new SoundboardException("Choose a microphone first."); MicrophoneSetup.Launch(device.Id, remove); }
        catch (Exception ex) { error.Text = UserErrors.Report(ex, "soundboard.setup").Inline; }
    }
    async Task Save()
    {
        if (saving) return;
        try {
            double Read(TextBox box) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : throw new SoundboardException("Enter a valid number for each timing setting.");
            int Milliseconds(TextBox box) { double value = Read(box); if (!double.IsFinite(value) || value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue) throw new SoundboardException("Enter whole milliseconds for timing settings."); return (int)value; }
            var candidate = original with {
                Backend = ((Mode)mode.SelectedItem).Backend, VirtualVoiceEndpointId=(voice.SelectedItem as Device)?.Id ?? "", TransmissionEndpointId=(cable.SelectedItem as Device)?.Id ?? "", CableRecordingEndpointId=(cableRecording.SelectedItem as Device)?.Id ?? "", VirtualRoutingEnabled=enableVirtual.IsChecked==true, MicrophoneEndpointId = (microphone.SelectedItem as Device)?.Id ?? "", MonitorEndpointId = (speakers.SelectedItem as Device)?.Id ?? "",
                TransmittedSoundVolume = soundVolume.Value / 100,
                HearTransmittedSounds = monitor.IsChecked == true, BlockVoiceDuringSounds = block.IsChecked == true,
                StartDelayMs = Milliseconds(startDelay), RelativeMicrophoneDelayMs = Milliseconds(relativeDelay), AutoKeyLeadInMs = Milliseconds(leadIn), AutoKeyReleaseDelayMs = Milliseconds(releaseDelay),
                UseActivationSound = activation.IsChecked == true, ActivationSoundPath = activationPath.Text.Trim(),
                HotkeysEnabled = true, PassHotkeysToOtherApps = true, AllowLeftRightModifiers = true,
                RepeatSounds = repeat.IsChecked == true, StopHotkeySoundsAtEnd = autoStop.IsChecked == true,
                Bindings = original.Bindings.Where(b => b.Command == SoundboardCommand.PlaySound).Concat(bindings.Where(b => b.Key.Chord != null).Select(b => new SoundboardBinding { Chord = b.Key.Chord!, Command = b.Command, SoundFilename = b.File, Route = (SoundboardRoute)b.Route.SelectedItem })).ToArray(),
                AutoKeys = [], AutoKeysEnabled = false
            };
            candidate = candidate.ForSoundOutput(); candidate.Validate(); saving = true; IsEnabled = false;
            await main.ApplySoundboardSettings(candidate); saving = false; Close();
        } catch (ArgumentException ex) { error.Text = ex.Message; AppLog.Error("soundboard.settings.validation", ex); }
        catch (Exception ex) { error.Text = UserErrors.Report(ex, "soundboard.settings").Inline; }
        finally { saving = false; IsEnabled = true; }
    }
}
