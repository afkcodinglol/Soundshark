using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Soundshark.Core;

namespace Soundshark;
public partial class MainWindow : Window
{
    public Settings Settings { get; } = Settings.Load();
    public CheckBox DiscordToggle { get; } = new() { Name="DiscordToggle",Content="Discord Soundboard", Margin=new Thickness(0,10,0,8),ToolTip="Limit recordings and saved sounds for Discord. Previews play in full." };
    public Playback Player { get; } = new();
    public StudioPane? PlayingPane { get; set; }
    public StudioPane Studio { get; }
    public SoundboardCoordinator Soundboard { get; }
    public SoundboardHotkeys Hotkeys { get; }
    public AppUpdates Updates { get; } = new();
    SoundsHubClient? hub;
    public SoundsHubClient Hub {
        get {
            if (hub != null) return hub;
            hub = new(SoundsHubSource.Load() ?? throw new InvalidDataException("Sounds Hub needs its service connection configured."), sessionFile: Path.Combine(Paths.Root,"hub-session.dat"));
            hub.AccountChanged += UpdateAdminVisibility; return hub;
        }
    }
    void UpdateAdminVisibility() => Dispatcher.InvokeAsync(() => {
        if (Tabs.Template.FindName("AdminToolsButton",Tabs) is Button button) button.Visibility = hub?.IsOwner == true ? Visibility.Visible : Visibility.Collapsed;
    });
    void AdminButtonLoaded(object sender, RoutedEventArgs e) => UpdateAdminVisibility();
    public async Task RefreshHubRole()
    {
        try { await Hub.RefreshRole(startupChecks.Token); }
        catch (Exception ex) { AppLog.Warn("hub.role",ex); UpdateAdminVisibility(); }
    }
    async Task OpenIssues()
    {
        if (!Available) return;
        Hotkeys.Suspended = true;
        try {
            if (!await Hub.Resume(startupChecks.Token) && !await HubLoginFlow.Authenticate(this,Hub)) return;
            await RefreshHubRole(); var dialog = new IssueReportWindow(this,Hub); dialog.ShowDialog(); await dialog.Pending;
        } catch (Exception ex) { Error(ex,"issues.open"); }
        finally { Hotkeys.Suspended = false; }
    }
    async void OpenAdminTools(object sender, RoutedEventArgs e)
    {
        if (!Available) return;
        Hotkeys.Suspended = true;
        try {
            await RefreshHubRole();
            if (hub?.IsOwner != true) return;
            var dialog = new AdminToolsWindow(this,hub); dialog.ShowDialog(); await dialog.Pending;
        } catch (Exception ex) { Error(ex,"admin.open"); }
        finally { Hotkeys.Suspended = false; }
    }
    public RollbackChoice? PendingRollback { get; set; }
    readonly LibraryPane library;
    readonly ConverterPane converter;
    CancellationTokenSource? job;
    bool closing, explicitExit, microphoneInitialized;
    VirtualMicTray? tray;
    readonly MicrophoneRoutingController microphoneRoutes;
    public string VirtualMicStatus => microphoneRoutes.Status;
    readonly CancellationTokenSource startupChecks = new();
    bool importToolUpdateAvailable;
    public bool CaptureStarting { get; set; }
    public bool Available => job == null && !CaptureStarting && Studio?.Recording != true;
    public MainWindow()
    {
        Theme.Apply(Settings.Appearance);
        InitializeComponent();
        WindowBounds.Attach(this);
        StateChanged += (_, _) =>
        {
            bool maximized = WindowState == WindowState.Maximized;
            MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
            MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
            System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
        };
        UpdateThemeSwitch();
        ThemeSwitch.Click += async (_, _) => {
            var previous = Settings.Appearance; Settings.Appearance = previous with { LightMode = !previous.LightMode };
            try { await Settings.Save(); Theme.Apply(Settings.Appearance); UpdateThemeSwitch(); RefreshUpdateHints(); }
            catch (Exception ex) { Settings.Appearance = previous; Error(ex, "appearance.mode"); }
        };
        IssuesButton.Click += async (_, _) => await OpenIssues();
        Soundboard = new(() => Settings.Soundboard, () => microphoneRoutes?.Mixer, transmissionAllowed: () => microphoneRoutes?.SafeForTransmission == true);
        microphoneRoutes = new(Settings.Soundboard);
        Hotkeys = new(binding => library?.Execute(binding));
        UiSettingsButton.Click += (_, _) => {
            if (!Available) return;
            Hotkeys.Suspended = true;
            try { new UiSettingsWindow(this).ShowDialog(); }
            catch (Exception ex) { Error(ex, "appearance.open"); }
            finally { Hotkeys.Suspended = false; UpdateThemeSwitch(); RefreshUpdateHints(); }
        };

        Studio = new(this); library = new(this); converter = new(this);
        Player.Completed += session => _ = Dispatcher.BeginInvoke(() =>
        {
            if (Player.SessionId == session && !Player.Active) PlayingPane?.Rewind();
        });
        Player.Failed += ex => _ = Dispatcher.BeginInvoke(() => Error(ex, "playback"));
        StudioHost.Content = Studio;
        LibraryHost.Content = library; ConverterHost.Content = converter;
        DiscordToggle.IsChecked = Settings.Discord; UpdateMode();
        Settings.DiscordChanged += () => { DiscordToggle.IsChecked = Settings.Discord; UpdateMode(); };
        DiscordToggle.Click += async (_, _) => { Settings.Discord = DiscordToggle.IsChecked == true; await Settings.Save(); };
        Tabs.SelectionChanged += (_, e) => { if (e.Source == Tabs) { Player.Stop(); UpdateMode(); } };
        CancelJob.Click += (_, _) => job?.Cancel();
        OpenLogs.Click += async (_, _) => { try { await AppLog.OpenFolder(); } catch (Exception ex) { Error(ex, "logs.open"); } };
        UpdateLogButton.Click += (_, _) => { try { new UpdateLogWindow(this).ShowDialog(); } catch (Exception ex) { Error(ex, "updates.history"); } };
        UpdateAppButton.Click += (_, _) => { if (Available) new AppUpdateWindow(this).ShowDialog(); };
        RollbackAppButton.Click += (_, _) => { if (Available) new AppRollbackWindow(this).ShowDialog(); };
        Updates.Changed += () => Dispatcher.InvokeAsync(() => { UpdateAppButton.ToolTip = Updates.Ready ? "Update ready — install and restart Soundshark." : "Check for Soundshark updates."; RefreshUpdateHints(); });
        UpdateTools.Click += (_, _) => { if (Available) new ToolUpdateWindow(this).ShowDialog(); };
        RollbackTools.Click += (_, _) => _ = Run(async _ => { await ToolUpdater.Rollback(); await CheckImportToolUpdates(); MessageBox.Show(this, "Previous downloader toolset restored.", "Soundshark"); }, operation: "tools.rollback");
        PreviewKeyDown += Shortcuts; DragOver += (_, e) => { e.Effects = Available && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) =>
        {
            if (!Available || e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            if (Tabs.SelectedIndex == 2) converter.Add(files);
            else if (files.Length == 1 && new[] { ".mp3", ".wav" }.Contains(Path.GetExtension(files[0]).ToLowerInvariant())) { Tabs.SelectedIndex = 0; _ = Studio.Import(files[0]); }
            else Error("Drop one MP3 or WAV in The Studio, or use Audio Converter for multiple files and other formats.");
        };
        Loaded += async (_, _) => { await library.Refresh(); try { if (Environment.GetEnvironmentVariable("SOUNDSHARK_TEST_ROOT") == null) { Hotkeys.Update(Settings.Soundboard); _ = RestoreHub(); _ = Updates.Check(); _ = CheckImportToolUpdates(); } } catch (Exception ex) { Error(ex, "soundboard.hotkeys"); } };
        Loaded += async (_, _) => {
            if (microphoneInitialized || Environment.GetEnvironmentVariable("SOUNDSHARK_TEST_ROOT") != null) return;
            microphoneInitialized = true;
            try { await Soundboard.ChangeRouteAsync(() => microphoneRoutes.InitializeAsync()); }
            catch (Exception ex) { Error(ex, "virtual.startup"); }
        };
        Closing += OnClosing;
    }
    async Task RestoreHub()
    {
        try { if (await Hub.Resume(startupChecks.Token)) await RefreshHubRole(); }
        catch (Exception ex) { AppLog.Warn("hub.resume",ex); }
    }
    public void RefreshCaptureControls() => DiscordToggle.IsEnabled = Available;
    public void SetImportToolUpdateAvailable(bool value) { importToolUpdateAvailable = value; RefreshUpdateHints(); }
    void RefreshUpdateHints()
    {
        UpdateHint.Set(UpdateAppButton, Updates.Release != null, Settings.Appearance.ReduceMotion);
        UpdateHint.Set(UpdateTools, importToolUpdateAvailable, Settings.Appearance.ReduceMotion);
    }
    async Task CheckImportToolUpdates()
    {
        try {
            bool available = await Task.Run(() => ToolUpdater.CheckAvailable(startupChecks.Token));
            if (!startupChecks.IsCancellationRequested) { SetImportToolUpdateAvailable(available); UpdateTools.ToolTip = available ? "An import tool update is available." : "Import tools are up to date."; }
        } catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("tools.update.check", ex); UpdateTools.ToolTip = "Couldn't check import tools. Click to try again."; }
    }
    void UpdateMode() => ModeLabel.Text = Settings.Discord ? "Record up to 5 seconds · save as MP3" : "Full-length audio · Discord limits off";
    void UpdateThemeSwitch()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        TextBlock Icon(string glyph) => new() { Text=glyph, FontFamily=new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize=15, VerticalAlignment=VerticalAlignment.Center };
        row.Children.Add(Icon("\uE706"));
        var rail = new Border { Width=36,Height=20,CornerRadius=new CornerRadius(10),Margin=new Thickness(8,0,8,0),Padding=new Thickness(3),Background=Theme.Brush("AccentHover") };
        rail.Child = new System.Windows.Shapes.Ellipse { Width=14,Height=14,Fill=Theme.Brush("Text"),HorizontalAlignment=Settings.Appearance.LightMode?HorizontalAlignment.Left:HorizontalAlignment.Right };
        row.Children.Add(rail); row.Children.Add(Icon("\uE708")); ThemeSwitch.Content=row;
        ThemeSwitch.ToolTip=Settings.Appearance.LightMode?"Light mode · switch to dark":"Dark mode · switch to light";
    }
    void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    void ToggleMaximizeWindow(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    void CloseWindow(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    public void Error(string text) => MessageBox.Show(this, text, "Soundshark", MessageBoxButton.OK, MessageBoxImage.Warning);
    public UserError Error(Exception ex, string operation)
    {
        var error = UserErrors.Report(ex, operation); Error(error.Display); return error;
    }
    public async Task Run(Func<CancellationToken, Task> action, StudioPane? pane = null, string operation = "operation")
    {
        if (!Available) return;
        job = new(); Player.Stop(); CancelJob.IsEnabled = true; DiscordToggle.IsEnabled = UpdateTools.IsEnabled = RollbackTools.IsEnabled = false;
        AppLog.Info(operation + ".start");
        try { await Soundboard.StopAsync(); await action(job.Token); }
        catch (OperationCanceledException) { AppLog.Info(operation + ".cancelled"); pane?.SetStatus("Task cancelled. Existing audio and Library files were retained."); }
        catch (Exception ex) { var error = Error(ex, operation); pane?.SetStatus(error.Inline); }
        finally { AppLog.Info(operation + ".finished"); job.Dispose(); job = null; CancelJob.IsEnabled = false; DiscordToggle.IsEnabled = UpdateTools.IsEnabled = RollbackTools.IsEnabled = true; }
    }
    public Task RefreshLibrary() => library.Refresh();
    async void OpenSoundboardSettings(object sender, RoutedEventArgs e)
    {
        if (!Available) return;
        Hotkeys.Suspended = true;
        try { Player.Stop(); await Soundboard.StopAsync(); new SoundboardSettingsWindow(this).ShowDialog(); }
        catch (Exception ex) { Error(ex, "soundboard.settings.open"); }
        finally { Hotkeys.Suspended = false; }
    }
    async void OpenSoundsHub(object sender, RoutedEventArgs e)
    {
        if (!Available) return;
        Hotkeys.Suspended = true;
        try { Player.Stop(); await Soundboard.StopAsync(); await HubLoginFlow.Open(this); }
        catch (Exception ex) { Error(ex, "hub.open"); }
        finally { Hotkeys.Suspended = false; }
    }
    public async Task ApplySoundboardSettings(SoundboardConfiguration candidate)
    {
        candidate = candidate.ForSoundOutput(); candidate.Validate(); Player.Stop();
        bool previouslySuspended = Hotkeys.Suspended; Hotkeys.Suspended = true;
        try {
            await Soundboard.ChangeRouteAsync(() => microphoneRoutes.ApplyAsync(candidate, async value => {
                Settings.Soundboard = value;
                if (Environment.GetEnvironmentVariable("SOUNDSHARK_TEST_ROOT") == null) Hotkeys.Update(value);
                await Settings.Save();
            }));
            if (!candidate.UsesContinuousVirtualMic && IsVisible) { tray?.Dispose(); tray = null; }
        } finally { Hotkeys.Suspended = previouslySuspended; }

    }
    public async Task OpenStudio(string path) { if (!Available) return; Tabs.SelectedIndex = 0; await Studio.Import(path); }
    void Shortcuts(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox or Button || Tabs.SelectedIndex != 0) return;
        var pane = Studio;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Space) pane.TogglePlay(); else if (ctrl && e.Key == Key.A) pane.SelectAll(); else if (e.Key == Key.Delete) pane.Trim(); else if (ctrl && e.Key == Key.Z) pane.Undo(); else if (ctrl && e.Key == Key.Y) pane.Redo(); else if (ctrl && e.Key == Key.S) _ = pane.Save(); else return;
        e.Handled = true;
    }
    public void OpenFromTray() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    public async Task ToggleVirtualMic()
    {
        if (!Available) { OpenFromTray(); Error("Finish the current recording or task before changing microphone routing."); return; }
        try { await ApplySoundboardSettings(Settings.Soundboard with { Backend = MicrophoneBackend.VirtualCable, VirtualRoutingEnabled = !Settings.Soundboard.UsesContinuousVirtualMic }); }
        catch (Exception ex) { OpenFromTray(); Error(ex, "virtual.toggle"); }
    }
    public void ExitFromTray()
    {
        if (Settings.Soundboard.UsesContinuousVirtualMic && MessageBox.Show(this, "Exit Soundshark? CABLE Output will become silent because Soundshark supplies your voice and clips. Your voice app will need another microphone until Soundshark is running again.", "Exit Soundshark Virtual Mic", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        explicitExit = true; OpenFromTray(); Close();
    }
    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return; e.Cancel = true;
        if (MicrophoneLifetime.HideOnClose(Settings.Soundboard, explicitExit)) {
            try { tray ??= new VirtualMicTray(this); Hide(); }
            catch (Exception ex) { Error(ex, "virtual.tray"); }
            return;
        }
        explicitExit = false;
        if (job != null || CaptureStarting) { PendingRollback = null; Updates.RestartAfterInstall = false; Error("Finish or cancel the active task before closing."); return; }
        if ((Studio.Workspace.Dirty || Studio.Recording) && MessageBox.Show(this, "Close without saving to Library? Save your audio before closing if you want to keep it.", "Unsaved Audio", MessageBoxButton.YesNo) != MessageBoxResult.Yes) { PendingRollback = null; Updates.RestartAfterInstall = false; return; }
        startupChecks.Cancel(); await Updates.Stop();
        UpdateHint.Set(UpdateTools, false, true); UpdateHint.Set(UpdateAppButton, false, true);
        try { await Soundboard.ChangeRouteAsync(() => microphoneRoutes.StopAsync()); }
        catch (Exception ex) { Error(ex, "virtual.shutdown"); return; }
        tray?.Dispose(); tray = null;
        Hotkeys.Dispose(); await Studio.ShutdownClipper();
        try { await Studio.StopRecording(); await Studio.Journal(); await Settings.Save(); }
        catch (Exception ex) { AppLog.Error("application.close.save", ex); }
        Player.Dispose(); library.Dispose(); hub?.Dispose();
        try { if (PendingRollback is { } rollback) AppRollback.ApplyOnExit(rollback); else Updates.ApplyOnExit(); } catch (Exception ex) { AppLog.Error("app.update.launch", ex); }
        closing = true; Close();
    }
}
