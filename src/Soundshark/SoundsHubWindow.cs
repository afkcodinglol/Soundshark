using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Microsoft.Win32;
using Soundshark.Core;

namespace Soundshark;

public sealed class SoundsHubWindow : Window
{
    readonly MainWindow main;
    readonly SoundsHubClient? client;
    readonly bool ownsClient;
    readonly CancellationTokenSource lifetime = new();
    readonly Playback player = new();
    readonly TextBox search = new() { Name = "HubSearch", MinWidth = 220, MaxLength = 80 };
    readonly CheckBox mine = new() { Content = "My uploads" };
    readonly ListBox sounds = new() { Name = "HubSounds", HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock status = Ui.Text("", 13, true), account = Ui.Text("Browse community sounds", 13, true), pageLabel = Ui.Text("Page 1", 13, true);
    readonly Button preview, download, upload, delete, report, previous, next, signIn, flag, stop, cancel;
    readonly ToggleButton viewSwitch;
    readonly List<Button> actions = [];
    CancellationTokenSource? operation;
    bool busy, closed, hasMore, savingView;
    int page;
    public Task Pending { get; private set; } = Task.CompletedTask;
    public SoundsHubWindow(MainWindow owner, SoundsHubClient? service = null)
    {
        main = owner; ownsClient = service == null; Owner = owner; Title = "Sounds Hub"; Width = 1080; Height = 780; MinWidth = 800; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, typeof(Window));
        try { client = service ?? (SoundsHubSource.Load() is { } source ? new SoundsHubClient(source, sessionFile: Path.Combine(Paths.Root, "hub-session.dat")) : null); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { status.Text = ex.Message; }
        var root = new Grid { Margin = new Thickness(22) }; Content = root;
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) root.RowDefinitions.Add(new() { Height = height });
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        signIn = Ui.Button("Sign In / Create Account", () => _ = Account()); DockPanel.SetDock(signIn, Dock.Right); heading.Children.Add(signIn);
        flag = Ui.Button("Flag Selector", () => _ = CountryFlag()); flag.Margin = new Thickness(0,0,10,0); DockPanel.SetDock(flag, Dock.Right); heading.Children.Add(flag);
        var title = new StackPanel(); title.Children.Add(Ui.Text("Sounds Hub", 28)); title.Children.Add(account); heading.Children.Add(title); root.Children.Add(heading);
        Button Action(string label, Action click) { var button = Ui.Button(label, click); actions.Add(button); return button; }
        viewSwitch = new ToggleButton { Name = "HubViewSwitch", IsChecked = main.Settings.HubGridView, ToolTip = "Switch between full-width rows and compact sound cards" };
        viewSwitch.SetResourceReference(StyleProperty, "ViewModeSwitch");
        System.Windows.Automation.AutomationProperties.SetName(viewSwitch, "Sounds Hub List or Grid View");
        viewSwitch.Click += (_, _) => _ = SetHubGridView(viewSwitch.IsChecked == true);
        var toolbar = Ui.Row(search, Action("Search", () => { page = 0; _ = Refresh(); }), Action("Refresh Sounds", () => _ = Refresh()), mine, viewSwitch);
        search.ToolTip = "Search sound titles"; System.Windows.Automation.AutomationProperties.SetName(search, "Search Sounds Hub");
        search.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && !busy) { page = 0; _ = Refresh(); } };
        mine.Click += (_, _) => { page = 0; _ = Refresh(); }; Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
        sounds.SetResourceReference(BackgroundProperty, "Surface"); sounds.BorderThickness = new Thickness(0);
        sounds.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        sounds.SetValue(ScrollViewer.CanContentScrollProperty, true); VirtualizingPanel.SetIsVirtualizing(sounds, true); VirtualizingPanel.SetVirtualizationMode(sounds, VirtualizationMode.Recycling);
        ConfigureHubView(); sounds.SelectionChanged += (_, _) => UpdateActions(); Grid.SetRow(sounds, 2); root.Children.Add(sounds);
        preview = Action("Preview", () => _ = Preview()); download = Action("Download To Library", () => _ = Download());
        upload = Action("Upload Sound", () => _ = Upload()); delete = Action("Remove My Upload", () => _ = Delete()); report = Action("Report Sound", () => _ = Report());
        upload.ToolTip = "Share audio up to 60 seconds and 10 MB. A copy is prepared as MP3; your original stays unchanged.";
        stop = Ui.Button("Stop Preview", player.Stop);
        var controls = Ui.Row(preview, stop, download, upload, delete, report); controls.Margin = new Thickness(0, 14, 0, 0); Grid.SetRow(controls, 3); root.Children.Add(controls);
        previous = Action("Previous", () => { page--; _ = Refresh(); }); next = Action("Next", () => { page++; _ = Refresh(); });
        cancel = Ui.Button("Cancel", () => operation?.Cancel());
        var footer = new StackPanel(); footer.Children.Add(Ui.Row(previous, pageLabel, next, cancel)); footer.Children.Add(status); Grid.SetRow(footer, 4); root.Children.Add(footer);
        player.Failed += ex => Dispatcher.InvokeAsync(() => { if (!closed) ShowError("This sound could not be played. " + ex.Message); });
        Loaded += (_, _) => { if (client?.Session == null) { Close(); return; } _ = Refresh(); UpdateActions(); };
        Closing += (_, _) => { closed = true; lifetime.Cancel(); operation?.Cancel(); player.Dispose(); if (!busy && ownsClient) client?.Dispose(); };
    }
    DataTemplate HubSoundTemplate(bool grid)
    {
        var text = new FrameworkElementFactory(typeof(StackPanel)); text.SetValue(MarginProperty, grid ? new Thickness(14, 12, 14, 10) : new Thickness(12, 9, 12, 9));
        var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty, new Binding(nameof(HubSound.Title))); name.SetResourceReference(TextBlock.FontSizeProperty, "Font16"); name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); text.AppendChild(name);
        var detail = new FrameworkElementFactory(typeof(TextBlock)); detail.SetBinding(TextBlock.TextProperty, new Binding(nameof(HubSound.Detail))); detail.SetResourceReference(TextBlock.FontSizeProperty, "Font12"); detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); 
        var countryFlag = new FrameworkElementFactory(typeof(Image)); countryFlag.SetBinding(Image.SourceProperty,new Binding(nameof(HubSound.CountryFlag))); countryFlag.SetBinding(VisibilityProperty,new Binding(nameof(HubSound.CountryFlagVisibility))); countryFlag.SetValue(FrameworkElement.WidthProperty,24.0); countryFlag.SetValue(FrameworkElement.HeightProperty,16.0); countryFlag.SetValue(Image.StretchProperty,System.Windows.Media.Stretch.Uniform); countryFlag.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,7,0));
        var attribution = new FrameworkElementFactory(typeof(StackPanel)); attribution.SetValue(StackPanel.OrientationProperty,Orientation.Horizontal);
        attribution.AppendChild(countryFlag); attribution.AppendChild(detail); text.AppendChild(attribution);
        return new DataTemplate { VisualTree = text };
    }
    void ConfigureHubView()
    {
        bool grid = main.Settings.HubGridView;
        viewSwitch.IsChecked = grid;
        var panel = new FrameworkElementFactory(grid ? typeof(SoundTilePanel) : typeof(VirtualizingStackPanel));
        if (grid) panel.SetValue(SoundTilePanel.TileSizeProperty, new Size(300, 96));
        sounds.ItemsPanel = new ItemsPanelTemplate(panel);
        VirtualizingPanel.SetScrollUnit(sounds, ScrollUnit.Pixel);
        var cells = new Style(typeof(ListBoxItem), (Style)FindResource(typeof(ListBoxItem)));
        cells.Setters.Add(new Setter(MarginProperty, grid ? new Thickness(0, 0, 8, 8) : new Thickness(0, 0, 0, 4)));
        cells.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        sounds.ItemContainerStyle = cells;
        sounds.ItemTemplate = HubSoundTemplate(grid);
        viewSwitch.ToolTip = grid ? "Grid view is active · switch to full-width rows" : "List view is active · switch to compact cards";
    }
    internal async Task SetHubGridView(bool grid)
    {
        if (savingView || main.Settings.HubGridView == grid) { viewSwitch.IsChecked = main.Settings.HubGridView; return; }
        bool previous = main.Settings.HubGridView;
        savingView = true; main.Settings.HubGridView = grid; ConfigureHubView(); UpdateActions();
        try { await main.Settings.Save(); }
        catch (Exception ex) { main.Settings.HubGridView = previous; ConfigureHubView(); ShowError("Could not save the Hub view. " + ex.Message); }
        finally { savingView = false; UpdateActions(); }
    }
    static void Cleanup(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    void UpdateActions()
    {
        bool ready = client != null && !busy; var sound = sounds.SelectedItem as HubSound;
        foreach (var action in actions) action.IsEnabled = ready;
        preview.IsEnabled = download.IsEnabled = ready && sound != null;
        upload.IsEnabled = ready && client?.Session != null;
        delete.IsEnabled = ready && sound != null && (client?.IsOwner == true || client?.Session?.UserId == sound.OwnerId);
        delete.Content = client?.IsOwner == true ? "Remove Sound" : "Remove My Upload";
        report.IsEnabled = ready && sound != null && client?.Session != null;
        signIn.IsEnabled = ready; flag.IsEnabled = ready && client?.Session != null; flag.Visibility = client?.Session == null ? Visibility.Collapsed : Visibility.Visible; search.IsEnabled = ready; mine.IsEnabled = ready && client?.Session != null; viewSwitch.IsEnabled = ready && !savingView;
        previous.IsEnabled = ready && page > 0; next.IsEnabled = ready && hasMore; cancel.IsEnabled = busy;
        signIn.Content = client?.Session == null ? "Sign In / Create Account" : "Sign Out";
        account.Text = client?.Session is { } user ? "Signed in as " + user.DisplayName : "Sign in to access Sounds Hub.";
        pageLabel.Text = "Page " + (page + 1);
    }
    Task Run(string message, Func<CancellationToken, Task> action)
    {
        if (busy || closed || client == null) return Task.CompletedTask;
        return Pending = RunCore(message, action);
    }
    async Task RunCore(string message, Func<CancellationToken, Task> action)
    {
        busy = true; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); status.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); status.Text = message; UpdateActions();
        try { await client!.EnsureAccess(operation.Token); await action(operation.Token); }
        catch (OperationCanceledException) { if (!closed) status.Text = "Cancelled."; }
        catch (Exception ex) {
            if (!closed) ShowError(ex.Message);
            if (client!.Session == null && !closed) { sounds.ItemsSource = null; Close(); }
        }
        finally { operation.Dispose(); operation = null; busy = false; if (!closed) UpdateActions(); else if (ownsClient) client!.Dispose(); }
    }
    void ShowError(string text) { status.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 113, 125)); status.Text = text; }
    Task Refresh() => Run("Loading community sounds...", LoadSounds);
    async Task LoadSounds(CancellationToken ct)
    {
        var result = await client!.Browse(search.Text, page, mine.IsChecked == true, ct);
        if (result.Sounds.Length == 0 && page > 0) { page = 0; result = await client.Browse(search.Text, page, mine.IsChecked == true, ct); }
        if (closed) return; sounds.ItemsSource = result.Sounds; hasMore = result.HasMore;
        status.Text = result.Sounds.Length == 0 ? "No sounds found. Try another search or share the first sound." : "Select a sound to preview it or add it to your Library.";
    }
    async Task RefreshAfterMutation(string success, CancellationToken ct)
    {
        page = 0;
        try { await LoadSounds(ct); if (!closed) status.Text = success + " Sounds refreshed."; }
        catch (Exception ex) {
            if (closed) return;
            if (client!.Session == null) { sounds.ItemsSource = null; Close(); return; }
            ShowError(success + (ex is OperationCanceledException ? " Refresh cancelled." : " Could not refresh the list.") + " Use Refresh Sounds to try again.");
        }
    }
    async Task<string> FetchAudio(HubSound sound, CancellationToken ct)
    {
        var bytes = await client!.Download(sound, ct); string temp = Path.Combine(Paths.Cache, "hub-" + Guid.NewGuid().ToString("N") + ".mp3");
        try {
            await File.WriteAllBytesAsync(temp, bytes, ct); var info = await MediaTools.Probe(temp, ct);
            if (!double.IsFinite(info.Duration) || info.Duration is <= 0 or > 60.1 || Math.Abs(info.Duration - sound.Duration) > .25) throw new InvalidDataException("The downloaded audio does not match its listing.");
            return temp;
        } catch { Cleanup(temp); throw; }
    }
    Task Preview() => sounds.SelectedItem is not HubSound sound ? Task.CompletedTask : Run("Preparing preview...", async ct => {
        player.Stop(); string file = await FetchAudio(sound, ct); string? pcm = null;
        try {
            pcm = await MediaTools.Decode(file, ct); ct.ThrowIfCancellationRequested();
            var workspace = new Workspace(); workspace.Load(pcm, sound.Title); player.Play(workspace, 0, workspace.Frames, false, deleteSourceOnStop: true); pcm = null;
            status.Text = "Previewing " + sound.Title;
        } finally { Cleanup(file); if (pcm != null) Cleanup(pcm); }
    });
    Task Download() => sounds.SelectedItem is not HubSound sound ? Task.CompletedTask : Run("Downloading to Library...", async ct => {
        string file = await FetchAudio(sound, ct);
        try {
            ct.ThrowIfCancellationRequested();
            string destination = HubImports.Publish(file, sound.Title, Paths.Library); await main.RefreshLibrary();
            status.Text = "Added to Library: " + Path.GetFileName(destination);
        } finally { Cleanup(file); }
    });
    async Task Upload()
    {
        if (busy || client?.Session == null) return;
        var choose = new OpenFileDialog { Title = "Share a sound", Filter = "Audio|*.mp3;*.wav;*.flac;*.m4a;*.ogg;*.opus;*.aiff", InitialDirectory = Paths.Library };
        if (choose.ShowDialog(this) != true) return;
        string? title = Ui.Ask(this, "Upload Sound", "Sound title (up to 80 characters)", Path.GetFileNameWithoutExtension(choose.FileName));
        if (title == null) return;
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 80) { ShowError("Use a sound title between 1 and 80 characters."); return; }
        if (MessageBox.Show(this, "Share this sound publicly? Only upload audio you own or have permission to share.", "Upload Sound", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await UploadFile(choose.FileName, title);
    }
    internal Task UploadFile(string file, string title) => Run("Preparing your sound for sharing...", async ct => {
            var info = await MediaTools.Probe(file, ct);
            if (!double.IsFinite(info.Duration) || info.Duration is <= 0 or > 60) throw new InvalidDataException("Choose a sound up to 60 seconds long.");
            string? pcm = null; string encoded = Path.Combine(Paths.Cache, "hub-upload-" + Guid.NewGuid().ToString("N") + ".mp3");
            try {
                pcm = await MediaTools.Decode(file, ct); var workspace = new Workspace(); workspace.Load(pcm, title);
                await MediaTools.Export(workspace, encoded, 192, false, false, ct);
                var shared = await client!.Upload(title, await File.ReadAllBytesAsync(encoded, ct), ct);
                await RefreshAfterMutation("Shared " + shared.Title + ".", ct);
            } finally { if (pcm != null) Cleanup(pcm); Cleanup(encoded); }
        });
    Task Delete()
    {
        if (sounds.SelectedItem is not HubSound sound || MessageBox.Show(this, "Remove this sound from Sounds Hub? Copies already saved to other Libraries stay there.", "Remove My Upload", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return Task.CompletedTask;
        return RemoveSound(sound);
    }
    internal Task RemoveSound(HubSound sound) => Run("Removing upload...", async ct => {
        await client!.Delete(sound, ct);
        sounds.ItemsSource = sounds.Items.OfType<HubSound>().Where(x => x.Id != sound.Id).ToArray();
        await RefreshAfterMutation("Upload removed.", ct);
    });
    Task Report()
    {
        if (sounds.SelectedItem is not HubSound sound || MessageBox.Show(this, "Submit a private report for this sound? Three reports from different verified accounts hide it pending review. The Owner can review it in Admin Tools.", "Report Sound", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return Task.CompletedTask;
        return Run("Submitting report...", async ct => { await client!.Report(sound, ct); status.Text = "Report recorded. Three distinct verified reports hide a sound pending review. The Owner can review it in Admin Tools."; });
    }
    async Task Account()
    {
        if (client == null || busy) return;
        if (client.Session != null) {
            await Run("Signing out...", async ct => { await client.SignOut(ct); mine.IsChecked = false; Close(); }); return;
        }
        Close();
    }
    internal Task CountryFlag() => Run("Loading your flag preference...", async ct => {
        var preference = await client!.CountryPreference(ct);
        var dialog = new HubCountryWindow(this, preference) { ShowActivated = false };
        if (dialog.ShowDialog() != true) { status.Text = "Country flag preference unchanged."; return; }
        await client.SetCountryPreference(dialog.CountryCode, dialog.ShowCountry, ct);
        await RefreshAfterMutation(dialog.ShowCountry ? "Country flag displayed beside your uploads." : "Country flag hidden from your uploads.", ct);
    });
}

public static class HubImports
{
    public static string Publish(string staged, string title, string library)
    {
        string filename = FileRules.Filename(title, ".mp3");
        string stem = Path.GetFileNameWithoutExtension(filename);
        for (int suffix = 0; suffix < 10000; suffix++) {
            string target = Path.Combine(library, suffix == 0 ? filename : FileRules.Filename(stem + " (" + suffix + ")", ".mp3"));
            try { File.Move(staged, target, false); return target; }
            catch (IOException) when (File.Exists(target)) { }
        }
        throw new IOException("Too many sounds have that name. Rename a local copy and try again.");
    }
}

