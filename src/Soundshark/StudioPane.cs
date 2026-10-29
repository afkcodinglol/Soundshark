using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Soundshark.Core;

namespace Soundshark;
public sealed class StudioPane : Grid
{
    readonly MainWindow main;
    readonly WaveNavigation navigation;
    bool volumeSync;
    readonly Expander sourcePanel = new() { Header = "Upload, import or record", IsExpanded = true, Margin = new Thickness(0,0,0,6) };
    bool microphone;
    readonly ComboBox source = new() { Name = "RecordingSource", ItemsSource = new[] { "PC Audio", "Microphone" }, SelectedIndex = 0, MinWidth = 205 };
    readonly TextBlock recordLabel = Ui.Text("Record PC Audio");
    readonly TextBlock sourceHint = Ui.Text("Records what you hear through your speakers or headphones.", 12, true);
    readonly TextBlock emptyHint = Ui.Text("Your audio will appear here\nUpload, import or record to get started.", 17, true);
    readonly Button refreshDevices;
    public Workspace Workspace { get; } = new();
    public Waveform Wave { get; } = new();
    readonly ComboBox devices = new() { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name" };
    readonly TextBlock summary, status, clock;
    readonly TextBox selectionA = new() { Name="SelectionStart",Width = 80 }, selectionB = new() { Name="SelectionEnd",Width = 80 };
    readonly ProgressBar meter = new() { Width = 100, Height = 8, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
    readonly Button record, stop, play, trim, keepSelected, deleteAll, save, undo, redo, import, url;
    readonly TextBox urlInput = new() { MinWidth = 160, ToolTip = "Paste a public YouTube, Shorts or TikTok URL", Name = "UrlInput" };
    readonly ProgressBar importProgress = new() { Name = "UrlImportProgress", Height = 9, Maximum = 100, Foreground = new SolidColorBrush(Color.FromRgb(66, 222, 144)), Background = new SolidColorBrush(Color.FromRgb(30, 54, 42)), IsIndeterminate = true };
    readonly TextBlock importStage = Ui.Text("", 12);
    readonly Border importPanel;
    bool importingUrl;
    readonly SemaphoreSlim journalLock = new(1);
    readonly RecordingLight recordingLight = new();
    readonly ClipperLiveStatus clipperLive = new();
    CaptureSession? capture;
    readonly CheckBox clipperToggle = new() { Name = "ClipperToggle", Content = "Clipper", Margin = new Thickness(0, 12, 0, 4) };
    readonly TextBox clipperSeconds = new() { Name = "ClipperSeconds", Width = 55, MaxLength = 2 };
    readonly StackPanel clipperPanel = new();
    readonly SemaphoreSlim clipperLock = new(1);
    ClipperSession? clipper;
    string clipperKey = "";
    bool clipperBusy, shuttingDown;
    bool finalizing, selectionSync;
    readonly string journal, recordingJournal;
    public bool Recording => capture != null;
    public bool LimitRecording => capture?.Limited ?? main.Settings.Discord;
    record DeviceChoice(string Id, string Name) { public override string ToString() => Name; }
    public StudioPane(MainWindow owner)
    {
        main = owner; Wave.Workspace = Workspace;
        // Keep the existing Editor journal so upgrades preserve recovery access.
        journal = Path.Combine(Paths.Cache, "editor.json"); recordingJournal = journal + ".capture";
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        var upper = new Grid(); upper.RowDefinitions.Add(new() { Height = GridLength.Auto }); upper.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        top.ColumnDefinitions.Add(new()); top.ColumnDefinitions.Add(new() { Width = new GridLength(14) }); top.ColumnDefinitions.Add(new());
        var addAudio = new StackPanel(); addAudio.Children.Add(Ui.Text("Upload Or Import", 18));
        addAudio.Children.Add(Ui.Text("Choose a file or paste a video link.", 12, true));
        import = Ui.Button("Upload Audio", PickFile); import.HorizontalAlignment = HorizontalAlignment.Left; import.Margin = new Thickness(0, 12, 0, 10);
        addAudio.Children.Add(import);
        var linkLabel = Ui.Text("YouTube / TikTok Link", 12, true); linkLabel.Margin = new Thickness(0, 0, 0, 6); addAudio.Children.Add(linkLabel);
        System.Windows.Automation.AutomationProperties.SetName(urlInput, "Video URL");
        urlInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ImportUrl(); e.Handled = true; } };
        url = Ui.Button("Import URL", ImportUrl); url.Margin = new Thickness(8, 0, 0, 0);
        var urlRow = new Grid(); urlRow.ColumnDefinitions.Add(new()); urlRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        urlRow.Children.Add(urlInput); SetColumn(url, 1); urlRow.Children.Add(url); addAudio.Children.Add(urlRow);
        var progressBody = new StackPanel(); progressBody.Children.Add(importStage); progressBody.Children.Add(new Border { Height = 9 }); progressBody.Children.Add(importProgress);
        importPanel = new Border { Child = progressBody, Background = new SolidColorBrush(Color.FromRgb(24, 39, 32)), CornerRadius = new CornerRadius(9), Padding = new Thickness(10), Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        importProgress.Effect = new DropShadowEffect { Color = Color.FromRgb(66, 222, 144), BlurRadius = 12, ShadowDepth = 0, Opacity = .4 };
        importPanel.SetResourceReference(Border.BackgroundProperty, "SuccessSurface"); importProgress.SetResourceReference(ProgressBar.BackgroundProperty, "SuccessSurface"); importProgress.SetResourceReference(ProgressBar.ForegroundProperty, "Live");
        addAudio.Children.Add(importPanel); var addCard=Ui.Card(addAudio);addCard.Padding=new Thickness(16);top.Children.Add(addCard);
        var recording = new StackPanel(); recording.Children.Add(Ui.Text("Record Audio", 18));
        recording.Children.Add(sourceHint);
        System.Windows.Automation.AutomationProperties.SetName(source, "Record From");
        source.Margin = new Thickness(0); source.HorizontalAlignment = HorizontalAlignment.Left;
        main.DiscordToggle.Margin=new Thickness(12,0,0,0);
        var recordingOptions=Ui.Row(source,main.DiscordToggle); recordingOptions.Margin=new Thickness(0,8,0,6);
        foreach(FrameworkElement option in recordingOptions.Children)option.Margin=new Thickness(0,0,12,0);
        recording.Children.Add(recordingOptions);
        record = Ui.Button("Record PC Audio", StartRecording);
        {
            recordingLight.Name = "StudioRecordingLight"; recordingLight.RecordingDescription = "Recording PC audio";
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(recordingLight);
            content.Children.Add(new TextBlock { Text = "\uE720", FontFamily = new FontFamily("Segoe MDL2 Assets"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(recordLabel); record.Content = content;
            System.Windows.Automation.AutomationProperties.SetName(record, "Record PC Audio");
        }
        stop = Ui.Button("■ Stop", () => _ = StopRecording());
        clock = Ui.Text("00:00.0", 16);
        var captureRow = Ui.Row(record, stop, clock, meter, clipperLive); captureRow.Margin = new Thickness(0); recording.Children.Add(captureRow);
        refreshDevices = Ui.Button("Refresh", RefreshDevices);
        var deviceLabel = Ui.Text("Audio Device", 12, true); deviceLabel.Margin = new Thickness(0, 4, 0, 6); recording.Children.Add(deviceLabel);
        devices.Name = "AudioDevice"; System.Windows.Automation.AutomationProperties.SetName(devices, "Audio Device");
        var deviceOptions = new Grid(); deviceOptions.ColumnDefinitions.Add(new()); deviceOptions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        deviceOptions.Children.Add(devices); SetColumn(refreshDevices, 1); refreshDevices.Margin = new Thickness(8, 0, 0, 0); deviceOptions.Children.Add(refreshDevices);
        recording.Children.Add(deviceOptions);
        clipperToggle.IsChecked = main.Settings.ClipperEnabled;
        clipperSeconds.Text = main.Settings.ClipperSeconds.ToString(CultureInfo.InvariantCulture);
        var length = Ui.Row(clipperToggle, Ui.Text("Last",12,true), clipperSeconds, Ui.Text("seconds (1–60)",12,true));
        length.Margin=new Thickness(0,10,0,4);
        foreach(FrameworkElement item in length.Children)item.Margin=new Thickness(0,0,8,0);
        clipperPanel.Children.Add(length);
        clipperPanel.Children.Add(Ui.Text("Keeps recent PC audio ready to clip. Older audio is discarded.",12,true));
        System.Windows.Automation.AutomationProperties.SetName(clipperSeconds, "Clipper seconds (1 to 60)");
        recording.Children.Add(clipperPanel);
        clipperToggle.Click += async (_, _) => await ConfigureClipper();
        clipperSeconds.LostKeyboardFocus += async (_, _) => await ConfigureClipper();
        clipperSeconds.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await ConfigureClipper(); } };
        devices.SelectionChanged += (_, _) => { if (IsLoaded && !Recording) _ = RefreshClipper(); };
        var recordCard = Ui.Card(recording); recordCard.Padding=new Thickness(16); SetColumn(recordCard, 2); top.Children.Add(recordCard);
        sourcePanel.Content = top; SetRow(sourcePanel, 0); upper.Children.Add(sourcePanel);
        var waveBody = new Grid(); waveBody.Children.Add(Wave); emptyHint.HorizontalAlignment = HorizontalAlignment.Center; emptyHint.TextAlignment = TextAlignment.Center; emptyHint.IsHitTestVisible = false; emptyHint.Background = new SolidColorBrush(Color.FromRgb(20, 19, 26)); emptyHint.SetResourceReference(TextBlock.BackgroundProperty, "WaveBackground"); emptyHint.Padding = new Thickness(16); waveBody.Children.Add(emptyHint);
        Wave.MinHeight=100; Wave.Height=160;
        var waveLayout = new DockPanel(); navigation = new WaveNavigation(Wave); DockPanel.SetDock(navigation, Dock.Bottom); waveLayout.Children.Add(navigation); waveLayout.Children.Add(waveBody);
        var waveCard = Ui.Card(waveLayout); waveCard.Padding = new Thickness(12); waveCard.MinHeight = 120; SetRow(waveCard, 1); upper.Children.Add(waveCard);
        var upperViewport=new ScrollViewer {Content=upper,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Children.Add(upperViewport);
        var bottom = new StackPanel { Name="StudioEditingControls", Margin = new Thickness(0,6,0,0) };
        summary=Ui.Text("No audio yet",12,true); summary.TextWrapping=TextWrapping.NoWrap;summary.TextTrimming=TextTrimming.CharacterEllipsis;
        var information=new Grid { Margin=new Thickness(0,0,0,6) }; information.ColumnDefinitions.Add(new());information.ColumnDefinitions.Add(new());information.Children.Add(summary);bottom.Children.Add(information);
        play = Ui.Button("▶ Play", TogglePlay); trim = Ui.Button("Trim Selected", Trim); keepSelected = Ui.Button("Keep Selected", KeepSelected); save = Ui.Button("Save To Library", () => _ = Save());
        trim.ToolTip = "Remove the highlighted audio and join what remains. Undo restores it.";
        keepSelected.ToolTip = "Keep only the highlighted audio and remove everything outside it. Undo restores it.";
        save.SetResourceReference(Button.BackgroundProperty, "SuccessButton");
        undo = Ui.Button("Undo", Undo); redo = Ui.Button("Redo", Redo);
        deleteAll = Ui.Button("Delete All", DeleteAll); deleteAll.ToolTip = "Remove all track audio. Undo restores it.";
        var actions = Ui.Row(play, trim, keepSelected, undo, redo, deleteAll, save);
        foreach (FrameworkElement child in actions.Children) { child.VerticalAlignment=VerticalAlignment.Center; child.Margin=new Thickness(0,0,6,4); if(child is Button button) {button.Padding=new Thickness(11,7,11,7);button.SetResourceReference(Control.FontSizeProperty,"Font13");} }
        actions.Margin=new Thickness(0,0,12,0);
        var editing=new WrapPanel { VerticalAlignment=VerticalAlignment.Center };editing.Children.Add(actions);
        var precise=new StackPanel { Name="PreciseSelection",VerticalAlignment=VerticalAlignment.Center };
        var selection=Ui.Row(Ui.Text("Selection",12,true),Ui.Text("Start (s)",12,true),selectionA,Ui.Text("End (s)",12,true),selectionB,Ui.Button("Apply",ApplySelection),Ui.Button("Select All",SelectAll));
        selection.Margin=new Thickness(0);
        foreach(FrameworkElement child in selection.Children) {child.Margin=new Thickness(0,0,6,4);if(child is Button button) {button.Padding=new Thickness(11,7,11,7);button.SetResourceReference(Control.FontSizeProperty,"Font13");}}
        precise.Children.Add(selection);editing.Children.Add(precise);bottom.Children.Add(editing);
        status=Ui.Text("",12,true);status.TextWrapping=TextWrapping.NoWrap;status.TextTrimming=TextTrimming.CharacterEllipsis;status.HorizontalAlignment=HorizontalAlignment.Right;
        status.SetBinding(ToolTipProperty,new System.Windows.Data.Binding("Text") {Source=status});
        SetColumn(status,1);information.Children.Add(status);SetRow(bottom,1);Children.Add(bottom);
        void FitViewport() {if(main.StudioHost.ActualHeight>0) {double maximum=Math.Max(100,main.StudioHost.ActualHeight-bottom.ActualHeight-bottom.Margin.Top);if(Math.Abs(upperViewport.MaxHeight-maximum)>1)upperViewport.MaxHeight=maximum;} }
        main.StudioHost.SizeChanged+=(_,_)=>FitViewport();SizeChanged+=(_,_)=>FitViewport();bottom.SizeChanged+=(_,_)=>FitViewport();Loaded+=(_,_)=>FitViewport();
        navigation.VolumeChanged+=value=>{
            if(volumeSync||Workspace.Frames==0)return;
            Workspace.Volume=value;Workspace.Dirty=true;
            if(main.PlayingPane==this&&main.Player.Active)main.Player.SetVolume((float)value);
            _=Journal();Update();
        };
        Wave.SelectionChanged += SyncSelection; Wave.Seeked += () => main.Player.Stop();
        source.SelectionChanged += (_, _) => ChangeSource();
        main.Settings.DiscordChanged += UpdateRecordingMode;
        UpdateRecordingMode(); RefreshDevices(); Update();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; timer.Tick += (_, _) => Tick(); timer.Start();
        Loaded += (_, _) => { CompositionTarget.Rendering += RenderPlayback; if (Environment.GetEnvironmentVariable("SOUNDSHARK_TEST_ROOT") == null) _ = RefreshClipper(); };
        Unloaded += (_, _) => CompositionTarget.Rendering -= RenderPlayback;
    }
    void RenderPlayback(object? sender, EventArgs e)
    {
        if (main.PlayingPane != this || !main.Player.Active) return;
        try { Wave.CursorFrame = (long)(main.Player.Position * Workspace.Rate); Wave.RevealCursor(); }
        catch (Exception ex) { main.Player.Stop(); main.Error(ex, "playback.position"); }
    }
    public void SetStatus(string value) => status.Text = value;
    void ChangeSource()
    {
        if (!CanEdit) { source.SelectedIndex = microphone ? 1 : 0; return; }
        microphone = source.SelectedIndex == 1;
        recordLabel.Text = microphone ? "Record Microphone" : "Record PC Audio";
        System.Windows.Automation.AutomationProperties.SetName(record, recordLabel.Text);
        recordingLight.RecordingDescription = microphone ? "Recording microphone" : "Recording PC audio";
        UpdateRecordingMode();
        status.Text = ""; RefreshDevices();
        if (IsLoaded) _ = RefreshClipper();
    }
    void UpdateRecordingMode()
    {
        clipperPanel.Visibility = microphone ? Visibility.Collapsed : Visibility.Visible;
        if (!microphone && main.Settings.ClipperEnabled) {
            sourceHint.Text = "Clipper keeps recent PC audio. Click Clip Last to load it into the track." + (main.Settings.Discord ? " Trim to 5 seconds or less before saving for Discord." : "");
            recordLabel.Text = $"Clip Last {main.Settings.ClipperSeconds} Seconds";
            recordingLight.RecordingDescription = "Clipper is listening to PC audio";
            System.Windows.Automation.AutomationProperties.SetName(record, recordLabel.Text); return;
        }
        recordLabel.Text = microphone ? "Record Microphone" : "Record PC Audio";
        recordingLight.RecordingDescription = microphone ? "Recording microphone" : "Recording PC audio";
        System.Windows.Automation.AutomationProperties.SetName(record, recordLabel.Text);
        sourceHint.Text = (microphone ? "Records your microphone. " : "Records your speakers or headphones. ") + (LimitRecording ? "Stops after 5 seconds for Discord." : "Record until you press Stop.");
        if (!Recording) clock.Text = LimitRecording ? "5.00s left" : "00:00.0";
    }
    void RefreshDevices()
    {
        if (Recording) return;
        try
        {
            using var e = new MMDeviceEnumerator(); var flow = microphone ? DataFlow.Capture : DataFlow.Render;
            string? defaultId = null; try { using var d = e.GetDefaultAudioEndpoint(flow, Role.Multimedia); defaultId = d.ID; } catch (Exception ex) { AppLog.Warn("devices.default", ex); }
            var choices = new List<DeviceChoice>();
            foreach (var d in e.EnumerateAudioEndPoints(flow, DeviceState.Active)) { choices.Add(new(d.ID, d.FriendlyName + (d.ID == defaultId ? " (Windows default)" : ""))); d.Dispose(); }
            devices.ItemsSource = choices; devices.SelectedItem = choices.FirstOrDefault(d => d.Id == defaultId) ?? choices.FirstOrDefault();
            if (choices.Count == 0) status.Text = microphone ? "No microphone found. Connect one and allow desktop microphone access in Windows Settings." : "No output device found. Connect speakers or headphones and refresh devices.";
        }
        catch (Exception ex) { status.Text = UserErrors.Report(ex, "recording.devices").Inline; }
    }
    public bool ConfirmReplace() => !Workspace.Dirty || MessageBox.Show(main, "Replace the audio on this track? Save it to Library first if you want to keep it.", "Unsaved Audio", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    async Task ConfigureClipper()
    {
        if (!int.TryParse(clipperSeconds.Text, out int seconds) || seconds is < 1 or > 60) {
            clipperSeconds.Text = main.Settings.ClipperSeconds.ToString(CultureInfo.InvariantCulture);
            status.Text = "Choose a Clipper length from 1 to 60 seconds.";
            seconds = main.Settings.ClipperSeconds;
        }
        if (main.Settings.ClipperEnabled == (clipperToggle.IsChecked == true) && main.Settings.ClipperSeconds == seconds) return;
        main.Settings.ClipperEnabled = clipperToggle.IsChecked == true;
        main.Settings.ClipperSeconds = seconds; UpdateRecordingMode();
        try { await main.Settings.Save(); await RefreshClipper(); }
        catch (Exception ex) { status.Text = UserErrors.Report(ex, "clipper.settings").Inline; }
    }
    async Task RefreshClipper()
    {
        await clipperLock.WaitAsync(); clipperBusy = true; UpdateButtons();
        try {
            string desired = !shuttingDown && !microphone && main.Settings.ClipperEnabled && devices.SelectedItem is DeviceChoice choice
                ? choice.Id + "|" + main.Settings.ClipperSeconds : "";
            if (clipper != null && clipperKey == desired && !clipper.Finished) return;
            await StopClipperCore();
            if (desired.Length == 0) { clock.Text = LimitRecording ? "5.00s left" : "00:00.0"; return; }
            using var enumerator = new MMDeviceEnumerator(); var device = enumerator.GetDevice(((DeviceChoice)devices.SelectedItem).Id);
            clipper = new ClipperSession(device, main.Settings.ClipperSeconds); clipper.Start(); clipperKey = desired;
            status.Text = $"Clipper is listening · last {main.Settings.ClipperSeconds} seconds of PC audio.";
        } catch (Exception ex) {
            await StopClipperCore(); status.Text = UserErrors.Report(ex, "clipper.start").Inline;
        } finally { clipperBusy = false; clipperLock.Release(); UpdateButtons(); }
    }
    async Task StopClipperCore()
    {
        var current = clipper; clipper = null; clipperKey = "";
        if (current == null) return;
        try { await current.Stop(); }
        catch (Exception ex) { AppLog.Warn("clipper.stop", ex); }
        finally { current.Dispose(); AppLog.Info("clipper.stopped"); }
    }
    async Task ClipperFailed(Exception? failure)
    {
        await clipperLock.WaitAsync(); clipperBusy = true;
        try {
            await StopClipperCore();
            status.Text = failure == null ? "Clipper stopped. Toggle it off and on to reconnect." : UserErrors.Report(failure, "clipper.device").Inline;
        } finally { clipperBusy = false; clipperLock.Release(); UpdateButtons(); }
    }
    public async Task ShutdownClipper()
    {
        shuttingDown = true; await clipperLock.WaitAsync();
        try { await StopClipperCore(); } finally { clipperLock.Release(); }
    }
    async Task ClipLast()
    {
        if (!CanEdit || clipper == null || !ConfirmReplace()) return;
        try {
            var snapshot = clipper.Snapshot();
            if (snapshot.Audio.Length == 0) { status.Text = "Clipper is warming up. Try again in a moment."; return; }
            string raw = Path.Combine(Paths.Cache, Guid.NewGuid() + ".wav");
            await main.Run(async ct => {
                try {
                    await Task.Run(() => { using var writer = new NAudio.Wave.WaveFileWriter(raw, snapshot.Format); writer.Write(snapshot.Audio, 0, snapshot.Audio.Length); }, ct);
                    string pcm = await MediaTools.Decode(raw, ct, Report);
                    await PcRecordingAudio.Prepare(pcm, ct, Report);
                    await LoadPcm(pcm, "PC audio clip",ct);
                    status.Text = $"Clipped {Workspace.Seconds:F1} seconds · ready to trim or save. Clipper is still listening.";
                } finally { File.Delete(raw); }
            }, this, "clipper.snapshot");
        } catch (Exception ex) { status.Text = UserErrors.Report(ex, "clipper.snapshot").Inline; }
    }
    void PickFile()
    {
        if (!main.Available || Recording) return;
        var dialog = new OpenFileDialog { Filter = "Audio|*.mp3;*.wav", Title = "Upload Audio" };
        if (dialog.ShowDialog(main) == true) _ = Import(dialog.FileName);
    }
    public async Task Import(string path)
    {
        if (!main.Available || !ConfirmReplace()) return;
        await main.Run(async ct => { string pcm = await MediaTools.Decode(path, ct, Report); await LoadPcm(pcm, Path.GetFileNameWithoutExtension(path),ct); }, this, "audio.import");
    }
    async void ImportUrl()
    {
        if (!main.Available) return;
        string input = urlInput.Text.Trim();
        if (input.Length == 0) { status.Text = "Paste a video URL in the box beside Import URL."; urlInput.Focus(); return; }
        if (!ConfirmReplace()) return;
        importingUrl = true; importPanel.Visibility = Visibility.Visible; importProgress.Value = 0; importProgress.IsIndeterminate = true;
        importStage.Text = "Connecting · checking the video…";
        bool completed = false;
        try
        {
            await main.Run(async ct =>
            {
                string pcm = await MediaTools.Download(input, ct, Report);
                importProgress.IsIndeterminate = true; importStage.Text = "Finishing · preparing your waveform…";
                await LoadPcm(pcm, "Video clip",ct); completed = true;
            }, this, "url.import");
        }
        finally
        {
            importingUrl = false; importProgress.IsIndeterminate = false; importProgress.Value = completed ? 100 : 0;
            importStage.Text = completed ? "✓ Import complete · your audio is ready" : "Import stopped · " + status.Text;
        }
    }
    public void Report(string s)
    {
        string raw = s;
        if (s.StartsWith("out_time_us=") && long.TryParse(s[12..], out long us)) s = $"Processing {us / 1000000.0:F1}s…";
        else if (s.StartsWith("[download]")) { var percentage = Regex.Match(s, @"\d+(?:\.\d+)?%"); s = percentage.Success ? "Downloading · " + percentage.Value : "Downloading audio…"; }
        else if (s.Contains('=') && !s.StartsWith("[download]")) return;
        if (s.Length > 180) s = s[..180];
        _ = Dispatcher.BeginInvoke(() =>
        {
            status.Text = s;
            if (!importingUrl) return;
            if (raw.StartsWith("[download]"))
            {
                var percent = Regex.Match(raw, @"(\d+(?:\.\d+)?)%");
                if (percent.Success && double.TryParse(percent.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                { importProgress.IsIndeterminate = false; importProgress.Value = Math.Clamp(value, 0, 100); importStage.Text = "Downloading · " + value.ToString("F1", CultureInfo.InvariantCulture) + "%"; }
                else { importProgress.IsIndeterminate = true; importStage.Text = "Downloading audio…"; }
            }
            else if (raw.StartsWith("out_time_us=")) { importProgress.IsIndeterminate = true; importStage.Text = "Preparing audio · " + s; }
        });
    }
    public async Task LoadPcm(string pcm, string name, CancellationToken ct=default)
    {
        if (!importingUrl) importPanel.Visibility = Visibility.Collapsed;
        main.Player.Stop(); pcm=await TrackAudio.Prepare(pcm,ct,Report); var peaks = await Peaks.Load(pcm,ct);
        Workspace.Load(pcm, name); volumeSync=true;try{navigation.Volume=Workspace.Volume;}finally{volumeSync=false;} sourcePanel.IsExpanded = false; Wave.Peaks = peaks; Wave.Live = []; Wave.A = Wave.B = Wave.CursorFrame = 0; Wave.Zoom = 1; Wave.Scroll = 0;
        await Journal(); status.Text = "Ready. Drag the waveform to select audio."; Update();
    }
    public async void StartRecording()
    {
        if (!microphone && main.Settings.ClipperEnabled) { await ClipLast(); return; }
        if (!main.Available || !ConfirmReplace()) return;
        sourcePanel.IsExpanded = true;
        if (devices.SelectedItem is not DeviceChoice choice) { main.Error("Select an available audio device first."); return; }
        main.Player.Stop();
        main.CaptureStarting = true; UpdateButtons();
        try
        {
            await main.Soundboard.StopAsync();
            using var e = new MMDeviceEnumerator(); var d = e.GetDevice(choice.Id);
            string path = Path.Combine(Paths.Cache, Guid.NewGuid() + ".wav");
            await File.WriteAllTextAsync(recordingJournal, path);
            capture = new(d, microphone, path, main.Settings.Discord); capture.Start();
            status.Text = microphone ? "Recording microphone only…" : "Recording computer output. Soundshark previews are disabled.";
        }
        catch (Exception ex)
        {
            capture?.Dispose(); capture = null; File.Delete(recordingJournal);
            main.Error(ex, "recording.start");
        }
        finally { main.CaptureStarting = false; Update(); }
    }
    public async Task StopRecording()
    {
        if (capture == null && clipper != null && !shuttingDown) {
            clipperToggle.IsChecked = false; await ConfigureClipper(); return;
        }
        if (capture == null || finalizing) return; finalizing = true;
        var session = capture;
        try
        {
            await session.Stop(); string? error = session.Error; string path = session.FilePath; session.Dispose(); capture = null;
            await main.Run(async ct =>
            {
                if (new FileInfo(path).Length > 44)
                {
                    string pcm = await MediaTools.Decode(path, ct, Report);
                    if (!microphone) await PcRecordingAudio.Prepare(pcm, ct, Report);
                    await LoadPcm(pcm, microphone ? "Microphone take" : "Computer capture",ct);
                    File.Delete(recordingJournal); File.Delete(path);
                }
                if (error != null) status.Text = "Capture ended: " + error + " Successfully captured audio was retained.";
            }, this, "recording.finalize");
        }
        catch (Exception ex) { main.Error(ex, "recording.stop"); }
        finally { capture = null; finalizing = false; Wave.Live = []; clock.Text = LimitRecording ? "5.00s left" : "00:00.0"; meter.Value = 0; Update(); }
    }
    void Tick()
    {
        if (clipper is { } history) {
            if (history.Finished && !clipperBusy) _ = ClipperFailed(history.Failure);
        }
        if (capture is { } c)
        {
            clock.Text = c.Limited ? $"{Math.Max(0, 5 - c.Seconds):F2}s remaining" : TimeSpan.FromSeconds(c.Seconds).ToString(@"hh\:mm\:ss\.f");
            meter.Value = c.Level; Wave.Live = c.Live; Wave.InvalidateVisual();
            if (c.Finished && !finalizing) _ = StopRecording();
        }
        UpdateButtons();
    }
    public void SelectAll() { if (!CanEdit) return; Wave.A = 0; Wave.B = Workspace.Frames; SyncSelection(); Wave.InvalidateVisual(); }
    bool CanEdit => main.Available && !Recording && !finalizing && !clipperBusy;
    public void Trim() { if (!CanEdit || Wave.B <= Wave.A) return; main.Player.Stop(); Workspace.Delete(Wave.A, Wave.B); Changed(); }
    public void KeepSelected() { if (!CanEdit || Wave.B <= Wave.A) return; main.Player.Stop(); Workspace.Keep(Wave.A, Wave.B); Changed(); }
    public void DeleteAll() { if (!CanEdit || Workspace.Frames == 0) return; main.Player.Stop(); Workspace.Delete(0, Workspace.Frames); Changed(); }
    public void Rewind() { Wave.CursorFrame = 0; Wave.Scroll = 0; Wave.InvalidateVisual(); }
    public void Undo() { if (!CanEdit) return; main.Player.Stop(); Workspace.Undo(); Changed(); }
    public void Redo() { if (!CanEdit) return; main.Player.Stop(); Workspace.Redo(); Changed(); }
    void Changed() { Wave.Zoom = 1; Wave.Scroll = 0; Wave.A = Wave.B = Wave.CursorFrame = 0; _ = Journal(); Update(); }
    void SyncSelection()
    {
        selectionSync = true; selectionA.Text = (Wave.A / (double)Workspace.Rate).ToString("F4", CultureInfo.InvariantCulture); selectionB.Text = (Wave.B / (double)Workspace.Rate).ToString("F4", CultureInfo.InvariantCulture); selectionSync = false; UpdateButtons();
    }
    void ApplySelection()
    {
        if (selectionSync || !CanEdit) return;
        if (!double.TryParse(selectionA.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) || !double.TryParse(selectionB.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double b) || !double.IsFinite(a) || !double.IsFinite(b) || a < 0 || b < a || b > Workspace.Seconds)
        { main.Error("Enter selection start and end in seconds, within the track duration."); return; }
        Wave.A = (long)Math.Round(a * Workspace.Rate); Wave.B = (long)Math.Round(b * Workspace.Rate); SyncSelection(); Wave.InvalidateVisual();
    }
    public void TogglePlay()
    {
        if (!CanEdit || Workspace.Frames == 0) return;
        if (main.Player.Active) { main.Player.Stop(); return; }
        if (main.PlayingPane == this && main.Player.Finished) Rewind();
        try { main.PlayingPane = this; main.Player.Play(Workspace, Wave.B > Wave.A ? Wave.A : Wave.CursorFrame, Wave.B > Wave.A ? Wave.B : Workspace.Frames, false,volume:(float)Workspace.Volume); }
        catch (Exception ex) { main.Error(ex, "playback.start"); }
    }
    public async Task Save()
    {
        if (!CanEdit || Workspace.Frames == 0) return;
        string? name = Ui.Ask(main, "Save To Library", "Sound Name", Workspace.Name); if (name == null) return;
        string destination;
        try { destination = Path.Combine(Paths.Library, FileRules.Filename(name)); } catch (Exception ex) { main.Error(ex, "export.filename"); return; }
        bool exists = File.Exists(destination);
        if (exists && MessageBox.Show(main, $"Overwrite {Path.GetFileName(destination)}?", "File Already Exists", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await main.Run(async ct =>
        {
            status.Text = "Checking Sound Volume…";
            var info = await MediaTools.Export(Workspace, destination, 0, main.Settings.Discord, exists, ct, Report);
            Workspace.Dirty = false; Update(); await Journal(); status.Text = $"Saved to Library · {Path.GetFileName(destination)} · {info.Duration:F2}s";
            await main.RefreshLibrary();
        }, this, "audio.export");
    }
    public async Task Journal()
    {
        if (Workspace.Source.Length == 0) return;
        var snapshot = Workspace.Snapshot(); await journalLock.WaitAsync();
        try { await Task.Run(() => FileRules.AtomicJsonAsync(journal, snapshot)); }
        catch (Exception ex) { status.Text = UserErrors.Report(ex, "workspace.snapshot").Inline; }
        finally { journalLock.Release(); }
    }
    void Update() { emptyHint.Visibility = Workspace.Frames == 0 && !Recording ? Visibility.Visible : Visibility.Collapsed; summary.Text = Workspace.Frames == 0 ? "No audio loaded" : $"{Workspace.Name}  ·  {Workspace.Seconds:F2} seconds  {(Workspace.Dirty ? "· not saved to Library" : "")}"; SyncSelection(); Wave.InvalidateVisual(); UpdateButtons(); }
    void UpdateButtons()
    {
        main.RefreshCaptureControls();
        recordingLight.SetRecording(Recording || clipper != null);
        bool liveClipper = clipper is { Finished: false };
        clipperLive.SetLive(liveClipper);
        clock.Visibility = meter.Visibility = liveClipper ? Visibility.Collapsed : Visibility.Visible;
        if (Recording) record.Opacity = 1; else record.ClearValue(OpacityProperty);
        bool available = CanEdit; record.IsEnabled = available && (microphone || !main.Settings.ClipperEnabled || clipper != null); stop.IsEnabled = Recording && !finalizing || clipper != null && available; devices.IsEnabled = source.IsEnabled = refreshDevices.IsEnabled = available;
        clipperToggle.IsEnabled = available; clipperSeconds.IsEnabled = available;
        play.Content = main.PlayingPane == this && main.Player.Active ? "■ Stop Playback" : "▶ Play";
        import.IsEnabled = url.IsEnabled = urlInput.IsEnabled = available; play.IsEnabled = save.IsEnabled = deleteAll.IsEnabled = available && Workspace.Frames > 0;
        trim.IsEnabled = keepSelected.IsEnabled = available && Wave.B > Wave.A; undo.IsEnabled = available && Workspace.CanUndo; redo.IsEnabled = available && Workspace.CanRedo;
        navigation.IsEnabled=available && Workspace.Frames>0;
        selectionA.IsEnabled = selectionB.IsEnabled = Wave.IsEnabled = available;
    }
}
