using System.Globalization;
using System.Diagnostics;
using System.Windows.Threading;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Data;
using System.Windows.Media;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using Soundshark.Core;

namespace Soundshark;
public sealed class SoundboardSettingsWindow : Window
{
    readonly MainWindow main;
    readonly SoundboardConfiguration original;
    bool saving;
    readonly ComboBox microphone = new() { MinWidth = 320, DisplayMemberPath = "Name" }, speakers = new() { MinWidth = 320, DisplayMemberPath = "Name" };
    readonly CheckBox monitor, block, autoStop, repeat;
    readonly ComboBox mode = new() { Name="MicrophoneMode",MinWidth=320,DisplayMemberPath="Name" };
    readonly ComboBox voice = new() {Name="VirtualVoiceSource",MinWidth=320,DisplayMemberPath="Name"}, cable = new(){Name="VirtualCablePlayback",MinWidth=320,DisplayMemberPath="Name"}, cableRecording = new(){Name="VirtualCableRecording",MinWidth=320,DisplayMemberPath="Name"};
    readonly CheckBox enableVirtual = new(){Name="EnableVirtualMic",Content="Enable Virtual Mic",Margin=new Thickness(0)};
    readonly StackPanel virtualOptions=new(), physicalOptions=new();
    readonly TextBlock virtualStatus=Ui.Text("",12,true), destinationHint=Ui.Text("",13,true), protectionStatus=Ui.Text("",12,true);
    readonly DispatcherTimer statusTimer = new(){Interval=TimeSpan.FromSeconds(1)};
    sealed record Mode(MicrophoneBackend Backend,string Name) { public override string ToString() => Name; }
    readonly Slider soundVolume;
    readonly TextBlock error = Ui.Text("", 13), setupStatus = Ui.Text("", 12, true);
    readonly List<(SoundboardCommand Command, string File, HotkeyBox Key, ComboBox Route)> bindings = [];
    sealed record Device(string Id, string Name) { public override string ToString() => Name; }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    static CheckBox Check(string text, bool value) => new() { Content = text, IsChecked = value, Margin = new Thickness(0) };
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
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(205) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var text = Ui.Text(label, 13, true); text.Margin = new Thickness(0, 0, 18, 0);
        row.Children.Add(text); Grid.SetColumn(input, 1);
        if (input is FrameworkElement element) { element.HorizontalAlignment = HorizontalAlignment.Stretch; element.Margin = new Thickness(0); }
        row.Children.Add(input);
        if (input is Slider slider) {
            slider.Width = double.NaN; slider.Style = (Style)Application.Current.FindResource("WaveSlider");
            var amount = Ui.Text("", 13); amount.MinWidth = 52; amount.Margin = new Thickness(12, 0, 0, 0); amount.TextAlignment = TextAlignment.Right;
            amount.SetBinding(TextBlock.TextProperty, new Binding("Value") { Source = slider, StringFormat = "{0:0}%" });
            row.Children.Add(amount); Grid.SetColumn(amount, 2);
        }
        panel.Children.Add(row);
    }
    static Border Gap(double height) => new() { Height = height };
    static Border Card(string title, string subtitle, params UIElement[] children)
    {
        var body = new StackPanel();
        var heading = Ui.Text(title, 18); heading.FontWeight = FontWeights.SemiBold; body.Children.Add(heading);
        if (!string.IsNullOrWhiteSpace(subtitle)) { var hint = Ui.Text(subtitle, 12, true); hint.Margin = new Thickness(0, 4, 0, 16); body.Children.Add(hint); }
        else body.Children.Add(Gap(14));
        foreach (var child in children) body.Children.Add(child);
        var card = Ui.Card(body); card.Margin = new Thickness(0, 0, 0, 14); return card;
    }
    static Border Callout(string title, UIElement content, string background = "AccentSurface")
    {
        var body = new StackPanel(); var heading = Ui.Text(title, 13); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 0, 0, 4);
        body.Children.Add(heading); body.Children.Add(content);
        var border = new Border { BorderThickness = new Thickness(3, 1, 1, 1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 4, 0, 12), Child = body };
        border.SetResourceReference(Border.BackgroundProperty, background); border.SetResourceReference(Border.BorderBrushProperty, "AccentBorder"); return border;
    }
    static Border Option(CheckBox check, string description)
    {
        var body = new StackPanel(); body.Children.Add(check); var hint = Ui.Text(description, 12, true); hint.Margin = new Thickness(24, 4, 0, 0); body.Children.Add(hint);
        var border = new Border { Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(8), Child = body };
        border.SetResourceReference(Border.BackgroundProperty, "Raised"); border.SetResourceReference(Border.BorderBrushProperty, "Border"); border.BorderThickness = new Thickness(1); return border;
    }
    static Button Action(string text, Action click, bool quiet = false)
    {
        var button = Ui.Button(text, click); button.MinWidth = 120; button.Padding = new Thickness(16, 10, 16, 10);
        if (quiet) button.SetResourceReference(Control.BackgroundProperty, "Raised"); return button;
    }
    static Slider Volume(double value) => new() { Name = "SoundOutputVolume", Minimum = 0, Maximum = 100, Value = Math.Clamp(value, 0, 1) * 100, TickFrequency = 10, ToolTip = "Sound output volume: 0% mutes clips; 100% uses the automatically balanced level." };
    static TabItem Tab(string name, UIElement content) => new() { Header = name, Content = new ScrollViewer { Content = content, Padding = new Thickness(0, 18, 10, 8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
    public SoundboardSettingsWindow(MainWindow owner)
    {
        main = owner; original = owner.Settings.Soundboard; error.Foreground=System.Windows.Media.Brushes.IndianRed; SetResourceReference(StyleProperty, typeof(Window));
        Closing += (_, e) => { if (saving) e.Cancel = true; };
        Owner = owner; Title = "Soundboard Settings"; Width = 960; Height = 820; MinWidth = 780; MinHeight = 640; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SourceInitialized += (_, _) => { int dark = 1, black = 0; var handle = new WindowInteropHelper(this).Handle; DwmSetWindowAttribute(handle, 20, ref dark, 4); DwmSetWindowAttribute(handle, 35, ref black, 4); };
        var root = new Grid { Margin = new Thickness(24, 20, 24, 18) }; root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); Content = root;
        var header = new StackPanel(); var title = Ui.Text("Soundboard settings", 26); title.FontWeight = FontWeights.SemiBold; header.Children.Add(title);
        var subtitle = Ui.Text("Choose how your microphone, monitoring, and playback controls work.", 13, true); subtitle.Margin = new Thickness(0, 4, 0, 0); header.Children.Add(subtitle); root.Children.Add(header);
        var tabs = new TabControl { Padding = new Thickness(0, 14, 0, 0) }; Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        var audio = new StackPanel { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
        var microphoneSetup = new StackPanel();
        mode.ItemsSource = new[]{new Mode(MicrophoneBackend.VirtualCable,"Soundshark Virtual Mic — VB-CABLE"),new Mode(MicrophoneBackend.NativeMicrophone,"Physical Mic — Experimental")};
        mode.SelectedItem = mode.Items.Cast<Mode>().First(m=>m.Backend==(original.Backend==MicrophoneBackend.NativeMicrophone?MicrophoneBackend.NativeMicrophone:MicrophoneBackend.VirtualCable));
        Field(microphoneSetup,"Microphone mode",mode);
        enableVirtual.IsChecked=original.VirtualRoutingEnabled;
        Field(virtualOptions,"Physical Voice Source",voice);
        Field(virtualOptions,"Cable Playback Endpoint",cable);
        Field(virtualOptions,"Cable Recording Endpoint",cableRecording);
        virtualOptions.Children.Add(Option(enableVirtual,"Starts continuous voice passthrough after you save. Soundshark stays available from the tray."));
        virtualOptions.Children.Add(Callout("Use this microphone in voice apps",destinationHint));
        virtualOptions.Children.Add(Callout("Connection status",virtualStatus,"SuccessSurface"));
        virtualOptions.Children.Add(Ui.Row(Action("Get VB-CABLE (Official)",()=>Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/"){UseShellExecute=true})),Action("Refresh Devices",()=>RefreshDevices(),true)));
        Field(physicalOptions,"Microphone",microphone); physicalOptions.Children.Add(setupStatus);
        var physicalWarning = Ui.Text("Choose this same physical microphone in Discord / your game. Current unsigned experimental setup disables protected AudioDG system-wide. Administrator access and a Windows restart are required.",12,true);
        physicalOptions.Children.Add(Callout("Experimental Windows audio route",physicalWarning));
        physicalOptions.Children.Add(Ui.Row(Action("Enable Microphone Playback",()=>Install(false)),Action("Remove Microphone Support",()=>Install(true),true)));
        microphoneSetup.Children.Add(virtualOptions); microphoneSetup.Children.Add(physicalOptions);
        var restore = Action("Restore Previous Windows Audio Settings",()=>{
            if(MessageBox.Show(this,"Remove experimental microphone support from all configured microphones and restore backed-up endpoint effects and audio policy? This requires administrator approval and may require a Windows restart. Missing backups or conflicting changes will stop cleanup for repair.","Restore Previous Audio Settings",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            try{MicrophoneSetup.RemoveAll();}catch(Exception ex){error.Text=ex.Message;}
        },true);
        var protectionBody = new StackPanel(); protectionBody.Children.Add(protectionStatus); protectionBody.Children.Add(Gap(10)); protectionBody.Children.Add(restore); microphoneSetup.Children.Add(Callout("Windows audio protection",protectionBody));
        audio.Children.Add(Card("Microphone setup","Virtual Cable is recommended for new setups. The Soundshark mode does not install or rename a Windows audio device.",microphoneSetup));
        mode.SelectionChanged+=(_,_)=>UpdateMode(); microphone.SelectionChanged+=(_,_)=>UpdateSetup(); cableRecording.SelectionChanged+=(_,_)=>UpdateMode();
        RefreshDevices(); UpdateMode(); UpdateSetup();
        statusTimer.Tick+=(_,_)=>{virtualStatus.Text=main.VirtualMicStatus;protectionStatus.Text=MicrophoneSetup.ProtectionStatus;};
        Loaded+=(_,_)=>statusTimer.Start(); Closed+=(_,_)=>statusTimer.Stop();
        var monitoring = new StackPanel(); Field(monitoring, "Preview output", speakers);
        monitor = Check("Also Hear Sounds I Send Through My Mic", original.HearTransmittedSounds); monitoring.Children.Add(Option(monitor,"Plays a local copy through the preview output while the sound is transmitted."));
        block = Check("Block My Voice While A Sound Is Playing", original.BlockVoiceDuringSounds); monitoring.Children.Add(Option(block,"Temporarily silences your live voice while a clip is playing."));
        audio.Children.Add(Card("Monitoring & voice","Control what you hear locally and whether your voice stays live during clips.",monitoring));
        soundVolume = Volume(original.TransmittedSoundVolume); var outputPanel = new StackPanel(); Field(outputPanel, "Sound output volume", soundVolume);
        outputPanel.Children.Add(Callout("Automatic protection",Ui.Text("Soundshark balances loudness and limits peaks automatically. 100% is the full controlled level; 0% mutes sound clips.",12,true),"SuccessSurface"));
        audio.Children.Add(Card("Sound output","Set one safe output level for every transmitted sound.",outputPanel));
        var behavior = new StackPanel(); repeat = Check("Repeat Sounds Until Stopped", original.RepeatSounds); autoStop = Check("Always Stop Hotkey Sounds At Their End", original.StopHotkeySoundsAtEnd);
        behavior.Children.Add(Option(repeat,"Loops regular playback until you use Stop or start another sound.")); behavior.Children.Add(Option(autoStop,"Prevents a hotkey-started sound from looping beyond its natural ending."));
        audio.Children.Add(Card("Playback behavior","Choose how Soundshark handles repeat playback and hotkey-started sounds.",behavior));
        tabs.Items.Add(Tab("General", audio));
        var shortcuts = new StackPanel { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
        shortcuts.Children.Add(Callout("Quick tip",Ui.Text("Click a shortcut box, then press a key combination. Keep a Stop shortcut so playback is always easy to end.",13,true)));
        var controls = new StackPanel();
        foreach (var command in Enum.GetValues<SoundboardCommand>().Where(c => c != SoundboardCommand.PlaySound && SoundboardConfiguration.PlaybackCommand(c))) AddBinding(controls, command, "");
        shortcuts.Children.Add(Card("Playback Controls","These shortcuts work while Soundshark is in the background. Set per-sound shortcuts from the Library.",controls));
        tabs.Items.Add(Tab("Hotkeys", shortcuts));
        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) }; footer.ColumnDefinitions.Add(new() { Width = new GridLength(1,GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.Children.Add(error);
        var save = Action("Save Settings", () => _ = Save()); save.IsDefault = true; var cancel = Action("Cancel", Close,true); cancel.IsCancel = true;
        var footerActions = Ui.Row(save,cancel); footerActions.Margin = new Thickness(0); footer.Children.Add(footerActions); Grid.SetColumn(footerActions,1); Grid.SetRow(footer, 2); root.Children.Add(footer);
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
            var candidate = original with {
                Backend = ((Mode)mode.SelectedItem).Backend, VirtualVoiceEndpointId=(voice.SelectedItem as Device)?.Id ?? "", TransmissionEndpointId=(cable.SelectedItem as Device)?.Id ?? "", CableRecordingEndpointId=(cableRecording.SelectedItem as Device)?.Id ?? "", VirtualRoutingEnabled=enableVirtual.IsChecked==true, MicrophoneEndpointId = (microphone.SelectedItem as Device)?.Id ?? "", MonitorEndpointId = (speakers.SelectedItem as Device)?.Id ?? "",
                TransmittedSoundVolume = soundVolume.Value / 100,
                HearTransmittedSounds = monitor.IsChecked == true, BlockVoiceDuringSounds = block.IsChecked == true,
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
