using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media;
using System.Globalization;
using Soundshark.Core;

namespace Soundshark;
public sealed partial class LibraryPane : Grid, IDisposable
{
    readonly MainWindow main;
    readonly ListBox rows = new() { Name = "LibraryRows", Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBox search = new() { Width = 260, ToolTip = "Search sounds by name", Name = "LibrarySearch" };
    readonly TextBlock count = Ui.Text("", 13, true);
    readonly TextBlock empty = Ui.Text("Your Library is ready for its first sound. Record or import audio, then save it here.", 16, true);
    readonly TextBlock soundboardStatus = Ui.Text("Preview is only for you. Play Through Mic sends the sound to your voice app.", 12, true);
    readonly Dictionary<string, (long Length, DateTime Stamp, MediaInfo Info)> metadata = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (long Length, DateTime Stamp, UserError Error)> failures = new(StringComparer.OrdinalIgnoreCase);
    readonly FileSystemWatcher watcher;
    readonly DispatcherTimer debounce;
    readonly SemaphoreSlim refreshLock = new(1), probes = new(2);
    readonly CancellationTokenSource lifetime = new();
    bool disposed;
    public sealed class SoundRow(string path, long length, DateTime stamp) : INotifyPropertyChanged
    {
        public string Path { get; } = path;
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
        public long Length { get; } = length;
        public DateTime Stamp { get; } = stamp;
        public bool Loading { get; set; }
        long playCount;
        public long PlayCount { get=>playCount; set { playCount=value; PropertyChanged?.Invoke(this,new(nameof(PlayCountText))); } }
        public string PlayCountText => $"Play Count: {PlayCount:N0}";
        string? colorHex;
        public string? ColorHex { get => colorHex; set {
            colorHex = value;
            ColorMark = Brushes.Transparent; RowTint = Brushes.Transparent; ButtonBackground = ButtonForeground = null;
            if (value != null) {
                var color = (Color)ColorConverter.ConvertFromString(value);
                ColorMark = ButtonBackground = SoundColors.Brush(color);
                ButtonForeground = SoundColors.Brush(SoundColors.TextColor(color));
                RowTint = SoundColors.Brush(Color.FromArgb(24, color.R, color.G, color.B));
            }
            PropertyChanged?.Invoke(this, new(""));
        } }
        public Brush ColorMark { get; private set; } = Brushes.Transparent;
        public Brush RowTint { get; private set; } = Brushes.Transparent;
        public Brush? ButtonBackground { get; private set; }
        public Brush? ButtonForeground { get; private set; }
        public void RefreshTheme() => PropertyChanged?.Invoke(this, new(""));
        string detail = System.IO.Path.GetExtension(path)[1..].ToUpperInvariant() + " · " + (length / 1000.0).ToString("F1") + " KB";
        string hint = path;
        public string Detail { get => detail; set { detail = value; PropertyChanged?.Invoke(this, new(nameof(Detail))); } }
        public string Hint { get => hint; set { hint = value; PropertyChanged?.Invoke(this, new(nameof(Hint))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public LibraryPane(MainWindow owner)
    {
        main = owner; main.Soundboard.Started += SoundStarted; RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var top = new StackPanel();
        var searchBox = new Grid { Width = 260 };
        var searchHint = Ui.Text("Search Sounds…", 13, true); searchHint.Margin = new Thickness(10, 0, 10, 0); searchHint.IsHitTestVisible = false;
        searchBox.Children.Add(search); searchBox.Children.Add(searchHint);
        search.TextChanged += (_, _) => searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var group = new CheckBox { Name = "GroupLibraryByColor", Content = "Group By Color", IsChecked = main.Settings.GroupLibraryByColor, ToolTip = "Keep sounds with the same button color together." };
        group.Click += async (_, _) => {
            CancelReorder(); bool manual = main.Settings.LibraryManualOrder; main.Settings.LibraryManualOrder = false;
            bool previous = main.Settings.GroupLibraryByColor; main.Settings.GroupLibraryByColor = group.IsChecked == true;
            try { await main.Settings.Save(); await Refresh(); }
            catch (Exception ex) { main.Settings.LibraryManualOrder=manual; group.IsChecked = main.Settings.GroupLibraryByColor = previous; main.Error(ex, "library.colors.group"); }
            finally { UpdateOrderHint(); }
        };
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var title = Ui.Text("Your Collection", 20); title.Margin = new Thickness(0, 0, 20, 0); toolbar.Children.Add(title);
        viewSwitch = Ui.Button("", () => _ = SwitchLibraryView()); viewSwitch.Name = "LibraryViewSwitch";
        foreach (var element in new UIElement[] { searchBox, viewSwitch, Ui.Button("Open Folder", () => Process.Start(new ProcessStartInfo(Paths.Library) { UseShellExecute = true })), Ui.Button("Stop All", () => _ = StopAll()), count }) toolbar.Children.Add(element);
        top.Children.Add(toolbar);
        var compact = new CheckBox { Name="CompactLibraryTiles", Content="Compact Library Tiles", IsChecked=main.Settings.Appearance.CompactLibrary };
        var newest = new CheckBox { Name="NewestSoundsFirst", Content="Newest Sounds First", IsChecked=main.Settings.Appearance.NewestFirst };
        var preferences = Ui.Row(group,compact,newest); preferences.Margin=new Thickness(0,4,0,8); top.Children.Add(preferences);
        async Task SaveLayout(bool sort = false) {
            CancelReorder(); bool manual=main.Settings.LibraryManualOrder; if(sort) main.Settings.LibraryManualOrder=false;
            var previous=main.Settings.Appearance; var next=previous with { CompactLibrary=compact.IsChecked==true, NewestFirst=newest.IsChecked==true, ShowFileDetails=true };
            try { main.Settings.Appearance=next; await main.Settings.Save(); Theme.Apply(next); await Refresh(); }
            catch(Exception ex) { main.Settings.LibraryManualOrder=manual; main.Settings.Appearance=previous; compact.IsChecked=previous.CompactLibrary; newest.IsChecked=previous.NewestFirst; main.Error(ex,"library.layout"); }
            finally { UpdateOrderHint(); }
        }
        compact.Click += async(_,_)=>await SaveLayout(); newest.Click += async(_,_)=>await SaveLayout(sort:true);
        orderHint = Ui.Text("", 11, true); top.Children.Add(orderHint);
        top.Children.Add(soundboardStatus); top.Children.Add(new Border { Height = 10 }); Children.Add(top);
        System.Windows.Automation.AutomationProperties.SetName(search, "Search Library");
        main.Soundboard.Status += status => _ = Dispatcher.BeginInvoke(() => soundboardStatus.Text = status);
        rows.SetValue(ScrollViewer.CanContentScrollProperty, true);
        rows.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(rows, true); VirtualizingPanel.SetVirtualizationMode(rows, VirtualizationMode.Recycling);
        ConfigureLibraryView();
        InitializeReorder();
        SetRow(rows, 1); Children.Add(rows); SetRow(empty, 1); empty.VerticalAlignment = VerticalAlignment.Top; Children.Add(empty);
        debounce = new() { Interval = TimeSpan.FromMilliseconds(250) }; debounce.Tick += async (_, _) => { debounce.Stop(); await Refresh(); };
        search.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
        watcher = new(Paths.Library, "*.*") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true };
        watcher.Created += Changed; watcher.Deleted += Changed; watcher.Changed += Changed; watcher.Renamed += Changed;
        Theme.Changed += RefreshRowTheme;
    }
    DataTemplate RowTemplate(bool list = false)
    {
        var border = new FrameworkElementFactory(typeof(Border)); border.SetResourceReference(Border.PaddingProperty, "LibraryPadding");
        border.SetValue(Border.BorderThicknessProperty, new Thickness(4, 0, 0, 0)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(SoundRow.ColorMark)));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(SoundRow.RowTint)));
        border.SetBinding(ToolTipProperty, new Binding(nameof(SoundRow.Hint)));
        border.AddHandler(LoadedEvent, new RoutedEventHandler((sender, args) => {
            var element = (FrameworkElement)sender;
            if (element.Tag == null) { element.Tag = true; element.DataContextChanged += (s, e) => { _ = LoadVisibleMetadata(element); }; }
            _ = LoadVisibleMetadata(element);
        }));
        var dock = new FrameworkElementFactory(typeof(DockPanel)); border.AppendChild(dock);
        if (list) {
            var handle = new FrameworkElementFactory(typeof(Border)); handle.SetValue(NameProperty,"SoundDragHandle");
            handle.SetValue(WidthProperty,30.0); handle.SetValue(DockPanel.DockProperty,Dock.Left);
            handle.SetValue(Border.BackgroundProperty,Brushes.Transparent);
            handle.SetValue(ToolTipProperty,"Drag to arrange · Escape to cancel");
            handle.SetValue(CursorProperty,System.Windows.Input.Cursors.SizeAll);
            handle.SetValue(IsEnabledProperty,!Theme.Current.ReduceMotion);
            handle.SetValue(OpacityProperty,Theme.Current.ReduceMotion ? .35 : 1.0);
            var grip = new FrameworkElementFactory(typeof(TextBlock)); grip.SetValue(TextBlock.TextProperty,"⠿");
            grip.SetResourceReference(TextBlock.FontSizeProperty,"Font20"); grip.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
            grip.SetValue(VerticalAlignmentProperty,VerticalAlignment.Center); grip.SetValue(IsHitTestVisibleProperty,false);
            handle.AppendChild(grip); dock.AppendChild(handle);
        }
        var actions = new FrameworkElementFactory(typeof(DockPanel)); actions.SetValue(DockPanel.DockProperty, list ? Dock.Right : Dock.Bottom);
        actions.SetValue(VerticalAlignmentProperty,VerticalAlignment.Center);
        actions.SetValue(MarginProperty, list ? new Thickness(12,0,0,0) : new Thickness(0, 7, 0, 0)); dock.AppendChild(actions);
        var template = ButtonTemplate();
        void Action(string label, string hint, Action<string, Button> action, bool primary = false) {
            var button = new FrameworkElementFactory(typeof(Button)); button.SetValue(ContentControl.ContentProperty, label);
            if (label == "•••") button.SetValue(DockPanel.DockProperty, Dock.Right);
            button.SetValue(ToolTipProperty, hint); button.SetValue(Control.PaddingProperty, new Thickness(8, 5, 8, 5)); button.SetValue(MarginProperty, new Thickness(0, 0, 5, 0));
            button.SetResourceReference(Control.FontSizeProperty, "Font12"); button.SetBinding(TagProperty, new Binding(nameof(SoundRow.Path)));
            button.SetValue(Control.TemplateProperty, template);
            button.SetBinding(Control.BackgroundProperty, new Binding(nameof(SoundRow.ButtonBackground)) { Converter = DefaultBrush.Instance, ConverterParameter = "AccentSurface" });
            button.SetBinding(Control.ForegroundProperty, new Binding(nameof(SoundRow.ButtonForeground)) { Converter = DefaultBrush.Instance, ConverterParameter = "Text" });
            if (primary) button.SetValue(Control.FontWeightProperty, FontWeights.SemiBold);
            button.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, args) => { args.Handled = true; var b = (Button)sender; if (b.Tag is string file) action(file, b); })); actions.AppendChild(button);
        }
        Action("•••", "More: Stop, Shortcut, Button Color, Open In Studio, Rename, Delete", More);
        Action("Preview", "Listen on your speakers or headphones", (file, _) => Preview(file));
        Action("Play Through Mic", "Send this sound through your microphone", (file, _) => Transmit(file), true);
        var text = new FrameworkElementFactory(typeof(StackPanel)); text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); dock.AppendChild(text);
        var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty, new Binding(nameof(SoundRow.Name))); name.SetResourceReference(TextBlock.FontSizeProperty, "Font16"); name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); text.AppendChild(name);
        var detail = new FrameworkElementFactory(typeof(TextBlock)); detail.SetBinding(TextBlock.TextProperty, new Binding(nameof(SoundRow.Detail))); detail.SetResourceReference(TextBlock.FontSizeProperty, "Font11"); detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); detail.SetResourceReference(VisibilityProperty, "LibraryDetailsVisibility"); text.AppendChild(detail);
        var plays = new FrameworkElementFactory(typeof(TextBlock)); plays.SetBinding(TextBlock.TextProperty,new Binding(nameof(SoundRow.PlayCountText))); plays.SetResourceReference(TextBlock.FontSizeProperty,"Font11"); plays.SetResourceReference(TextBlock.ForegroundProperty,"Muted"); text.AppendChild(plays);
        return new DataTemplate { VisualTree = border };
    }
    sealed class DefaultBrush : IValueConverter
    {
        public static readonly DefaultBrush Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value ?? Theme.Brush((string)parameter);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
    static ControlTemplate ButtonTemplate()
    {
        var plate = new FrameworkElementFactory(typeof(Border), "Plate");
        plate.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        plate.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        plate.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); plate.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        plate.SetResourceReference(Border.BorderBrushProperty, "AccentBorder");
        var caption = new FrameworkElementFactory(typeof(TextBlock)); caption.SetBinding(TextBlock.TextProperty, new Binding());
        caption.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Button.Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.ContentTemplateProperty, new DataTemplate { VisualTree = caption }); plate.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = plate };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(OpacityProperty, .85, "Plate")); template.Triggers.Add(hover);
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true }; focus.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("Accent"), "Plate")); template.Triggers.Add(focus);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(OpacityProperty, .4)); template.Triggers.Add(disabled);
        return template;
    }
    void More(string file, Button button)
    {
        var menu = new ContextMenu { PlacementTarget = button };
        foreach (var (label, action) in new (string, Action)[] { ("Stop", () => _ = StopSound(file)), ("Shortcut", () => _ = OpenShortcut(file)), ("Button Color", () => _ = ChooseColor(file)), ("Open In Studio", () => _ = main.OpenStudio(file)), ("Rename", () => Rename(file)), ("Delete", () => Delete(file)) }) {
            var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    void PreviewColor(string file, string? color)
    {
        foreach (var row in rows.Items.OfType<SoundRow>().Where(r => r.Path.Equals(file, StringComparison.OrdinalIgnoreCase))) row.ColorHex = color;
    }
    async Task ChooseColor(string file)
    {
        if (!main.Available) return;
        string name = Path.GetFileName(file); main.Settings.LibraryColors.TryGetValue(name, out var original);
        string? chosen = original;
        main.Hotkeys.Suspended = true;
        try {
            var picker = new ColorPickerWindow(main, original ?? Theme.Current.Accent, color => { chosen = color; PreviewColor(file, color); }, "Button Color", "Live preview · Use Color saves this sound's color.", allowDefault: true);
            picker.ShowDialog();
            if (picker.Accepted && File.Exists(file)) await SetButtonColor(file, picker.UseDefault ? null : chosen);
            else PreviewColor(file, original);
        } catch (Exception ex) { PreviewColor(file, original); main.Error(ex, "library.colors.save"); }
        finally { main.Hotkeys.Suspended = false; }
    }
    public async Task SetButtonColor(string file, string? color)
    {
        if (color != null && !SoundColors.Valid(color)) throw new ArgumentException("Choose a valid button color.");
        string name = Path.GetFileName(file); main.Settings.LibraryColors.TryGetValue(name, out var original);
        if (color == null) main.Settings.LibraryColors.Remove(name); else main.Settings.LibraryColors[name] = color.ToUpperInvariant();
        try { await main.Settings.Save(); }
        catch { if (original == null) main.Settings.LibraryColors.Remove(name); else main.Settings.LibraryColors[name] = original; throw; }
        PreviewColor(file, color);
        if (main.Settings.GroupLibraryByColor) await Refresh();
    }
    void ApplyMetadata(SoundRow row, MediaInfo? info, UserError? error = null)
    {
        row.Detail = error != null ? "Unreadable audio · open More to delete or replace" : info == null ? row.Detail : $"{Path.GetExtension(row.Path)[1..].ToUpperInvariant()} · {info.Duration:F2}s · {info.Size / 1000.0:F1} KB";
        row.Hint = error?.Display ?? row.Path;
    }
    async Task LoadVisibleMetadata(FrameworkElement element)
    {
        if (disposed || !element.IsLoaded || element.DataContext is not SoundRow row || row.Loading) return;
        if (metadata.TryGetValue(row.Path, out var known) && known.Length == row.Length && known.Stamp == row.Stamp) { ApplyMetadata(row, known.Info); return; }
        if (failures.TryGetValue(row.Path, out var failed) && failed.Length == row.Length && failed.Stamp == row.Stamp) { ApplyMetadata(row, null, failed.Error); return; }
        row.Loading = true; bool entered = false;
        try {
            await probes.WaitAsync(lifetime.Token); entered = true;
            if (!element.IsLoaded || element.DataContext != row || disposed) return;
            var info = await MediaTools.Probe(row.Path, lifetime.Token); metadata[row.Path] = (row.Length, row.Stamp, info); ApplyMetadata(row, info);
        } catch (OperationCanceledException) { }
        catch (Exception ex) {
            var error = UserErrors.Report(ex, "library.metadata", new { file = row.Path }); failures[row.Path] = (row.Length, row.Stamp, error); ApplyMetadata(row, null, error);
        } finally { if (entered) probes.Release(); row.Loading = false; }
    }
    void Changed(object sender, FileSystemEventArgs e) { if (!disposed) _ = Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); }); }
    public async Task Refresh()
    {
        if (disposed) return;
        if (reorderOriginal != null) { refreshAfterReorder = true; return; }
        await refreshLock.WaitAsync();
        try {
            string query = search.Text; bool newest = Theme.Current.NewestFirst;
            var files = await Task.Run(() => {
                var found = new List<SoundRow>();
                foreach (var file in Directory.EnumerateFiles(Paths.Library).Where(AudioConversion.IsLibraryAudio)) {
                    try { var stat = new FileInfo(file); found.Add(new(file, stat.Length, stat.LastWriteTimeUtc)); } catch (FileNotFoundException) { }
                }
                return found;
            });
            foreach (var row in files) { row.ColorHex = main.Settings.LibraryColors.GetValueOrDefault(Path.GetFileName(row.Path)); row.PlayCount = main.Settings.LibraryPlayCounts.GetValueOrDefault(Path.GetFileName(row.Path)); }
            var selected = files.Where(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
            IOrderedEnumerable<SoundRow> ordered = newest ? selected.OrderByDescending(x => x.Stamp).ThenBy(x => x.Name) : selected.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase);
            if (reorderOriginal != null) { refreshAfterReorder=true; return; }
            var sorted = (main.Settings.GroupLibraryByColor ? ordered.OrderBy(x => x.ColorHex == null).ThenBy(x => x.ColorHex, StringComparer.Ordinal) : ordered).ToArray();
            var ranks = main.Settings.LibraryOrder.Select((name,index)=>(name,index)).ToDictionary(p=>p.name,p=>p.index,StringComparer.OrdinalIgnoreCase);
            var visible = main.Settings.LibraryListView && main.Settings.LibraryManualOrder ? sorted.OrderBy(r=>ranks.GetValueOrDefault(Path.GetFileName(r.Path),int.MaxValue)).ToArray() : sorted;
            // Watcher bursts often report the same files twice. Keep containers and scroll position
            // when the collection has not changed (including after a theme-only refresh).
            var current = rows.Items.OfType<SoundRow>().ToArray();
            if (current.Length != visible.Length || !current.Zip(visible).All(pair =>
                pair.First.Path == pair.Second.Path && pair.First.Length == pair.Second.Length &&
                pair.First.Stamp == pair.Second.Stamp && pair.First.ColorHex == pair.Second.ColorHex)) rows.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<SoundRow>(visible);
            empty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            empty.Text = files.Count == 0 ? "Your Library is ready for its first sound. Record or import audio, then save it here." : "No sounds match this search.";
            count.Text = $"{visible.Length} sounds";
            var paths = files.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var old in metadata.Keys.Where(x => !paths.Contains(x)).ToArray()) metadata.Remove(old);
            foreach (var old in failures.Keys.Where(x => !paths.Contains(x)).ToArray()) failures.Remove(old);
        } catch (Exception ex) { count.Text = UserErrors.Report(ex, "library.refresh").Inline; }
        finally { refreshLock.Release(); }
    }
    string? playingFile;
    string? pendingPreviewFile;
    long previewRequest;
    async Task StopAll() { previewRequest++; pendingPreviewFile = playingFile = null; main.Player.Stop(); await main.Soundboard.StopAsync(); }
    async Task StopSound(string file)
    {
        if (string.Equals(pendingPreviewFile, file, StringComparison.OrdinalIgnoreCase)) { previewRequest++; pendingPreviewFile = null; }
        if (main.PlayingPane == null && string.Equals(playingFile, file, StringComparison.OrdinalIgnoreCase)) { main.Player.Stop(); playingFile = null; }
        if (string.Equals(main.Soundboard.CurrentFile, file, StringComparison.OrdinalIgnoreCase)) await main.Soundboard.StopAsync();
    }
    async Task OpenShortcut(string file)
    {
        if (!main.Available) return;
        main.Hotkeys.Suspended = true;
        try { await StopAll(); new SoundShortcutWindow(main, file).ShowDialog(); }
        catch (Exception ex) { main.Error(ex, "soundboard.shortcut.open"); }
        finally { main.Hotkeys.Suspended = false; }
    }
    void Transmit(string file, bool hotkey = false, bool? hearLocally = null, double startSeconds = 0)
    {
        if (!main.Available) return; main.Player.Stop(); main.Soundboard.Play(file, hotkey, hearLocally, startSeconds);
    }
    public void Execute(SoundboardBinding binding)
    {
        if (binding.Command == SoundboardCommand.Stop) { _ = StopAll(); return; }
        if (!main.Available) return;
        if (binding.Command is SoundboardCommand.SpeakerVolumeDown or SoundboardCommand.SpeakerVolumeUp or SoundboardCommand.ToggleSpeakers) {
            var current = main.Settings.Soundboard;
            double volume = binding.Command == SoundboardCommand.ToggleSpeakers ? current.SpeakerVolume == 0 ? 1 : 0 : Math.Clamp(current.SpeakerVolume + (binding.Command == SoundboardCommand.SpeakerVolumeUp ? .1 : -.1), 0, 2);
            main.Player.SetVolume((float)volume); main.Soundboard.SetSpeakerVolume((float)volume); main.Settings.Soundboard = current with { SpeakerVolume = volume }; _ = SaveCommandSettings(); return;
        }
        if (binding.Command == SoundboardCommand.ToggleMicrophoneRoute) { if (main.Soundboard.Active) _ = StopAll(); else if (main.Soundboard.CurrentFile is { } last) Transmit(last, true); return; }
        string? file = binding.Command == SoundboardCommand.PlaySound ? Path.Combine(Paths.Library, binding.SoundFilename) : main.Soundboard.CurrentFile;
        if (binding.Command == SoundboardCommand.PauseResume) { main.Soundboard.TogglePause(); return; }
        if (binding.Command is SoundboardCommand.Next or SoundboardCommand.Previous or SoundboardCommand.Random) {
            var files = Directory.GetFiles(Paths.Library).Where(AudioConversion.IsLibraryAudio).OrderBy(Path.GetFileName).ToArray(); if (files.Length == 0) return;
            int at = Array.FindIndex(files, f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
            file = files[binding.Command == SoundboardCommand.Random ? Random.Shared.Next(files.Length) : binding.Command == SoundboardCommand.Next ? (at + 1 + files.Length) % files.Length : (at - 1 + files.Length) % files.Length];
        }
        if (file == null || !File.Exists(file)) { soundboardStatus.Text = "That Library sound is missing. Choose another sound or update its shortcut in Settings."; return; }
        if (binding.Command is SoundboardCommand.SeekBackward or SoundboardCommand.SeekForward) { Transmit(file, true, startSeconds: main.Soundboard.PositionSeconds + (binding.Command == SoundboardCommand.SeekForward ? 5 : -5)); return; }
        if (binding.Route == SoundboardRoute.Preview) Preview(file);
        else Transmit(file, true, binding.Route == SoundboardRoute.Both ? true : null);
    }

    async Task SaveCommandSettings() { try { await main.Settings.Save(); } catch (Exception ex) { soundboardStatus.Text = UserErrors.Report(ex, "soundboard.settings.save").Inline; } }
    async void Preview(string file)
    {
        if (!main.Available) return;
        if (main.Player.Active && playingFile == file) { main.Player.Stop(); return; }
        Workspace? preview = null;
        long request = ++previewRequest; pendingPreviewFile = file;
        var config = main.Settings.Soundboard;
        await main.Run(async ct => { string pcm = await SoundboardAudio.Prepare(file, Workspace.Rate, config, config.NormalizeSpeakerPreview, ct); preview = new(); preview.Load(pcm, "Preview", false); }, operation: "playback.decode");
        if (request != previewRequest) return;
        pendingPreviewFile = null;
        if (preview == null || !main.Available) return;
        AutoKeyLease? keys=null;
        try {
            keys=await AutoKeyLease.Acquire(config.ForSoundPtt(file),false,CancellationToken.None);
            if(request!=previewRequest || disposed) { await keys.DisposeAsync(); return; }
            playingFile = file; main.PlayingPane = null; main.Player.Play(preview, 0, preview.Frames, false, endpointId: config.MonitorEndpointId, volume: (float)config.SpeakerVolume, keys:keys); keys=null; CountPlay(file); }
        catch (Exception ex) { if(keys!=null) await keys.DisposeAsync(); main.Error(ex, "playback.library"); }
    }
    void SoundStarted(string file) => Dispatcher.InvokeAsync(() => { if (!disposed) CountPlay(file); });
    async void CountPlay(string file)
    {
        string name=Path.GetFileName(file); long value=main.Settings.LibraryPlayCounts.GetValueOrDefault(name);
        value=value==long.MaxValue?value:Math.Max(0,value)+1; main.Settings.LibraryPlayCounts[name]=value;
        foreach(var row in rows.Items.OfType<SoundRow>().Where(r=>Path.GetFileName(r.Path).Equals(name,StringComparison.OrdinalIgnoreCase))) row.PlayCount=value;
        try { await main.Settings.Save(); } catch (Exception ex) { AppLog.Warn("library.play-count", ex); }
    }
    void Rename(string path)
    {
        if (!main.Available) return;
        string? name = Ui.Ask(main, "Rename sound", "Filename", Path.GetFileNameWithoutExtension(path)); if (name == null) return;
        _ = main.Run(async _ =>
        {
            string target = Path.Combine(Paths.Library, FileRules.Filename(name, Path.GetExtension(path)));
            if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase)) return;
            bool overwrite = File.Exists(target);
            if (overwrite && MessageBox.Show(main, $"Overwrite {Path.GetFileName(target)}?", "Rename", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await Task.Run(() => File.Move(path, target, overwrite));
            long plays=main.Settings.LibraryPlayCounts.GetValueOrDefault(Path.GetFileName(path)); main.Settings.LibraryPlayCounts.Remove(Path.GetFileName(path)); main.Settings.LibraryPlayCounts[Path.GetFileName(target)]=plays;
            if (main.Settings.Soundboard.SoundPtt.Remove(Path.GetFileName(path),out var ptt)) main.Settings.Soundboard.SoundPtt[Path.GetFileName(target)]=ptt; else main.Settings.Soundboard.SoundPtt.Remove(Path.GetFileName(target));
            SoundColors.Rename(main.Settings.LibraryColors, Path.GetFileName(path), Path.GetFileName(target));
            RenameOrderedSound(Path.GetFileName(path),Path.GetFileName(target));
            var current = main.Settings.Soundboard;
            await main.ApplySoundboardSettings(current with { Bindings = current.Bindings.Select(b => b.Command == SoundboardCommand.PlaySound && string.Equals(b.SoundFilename, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) ? b with { SoundFilename = Path.GetFileName(target) } : b).ToArray() });
            await Refresh();
        });
    }
    void Delete(string path)
    {
        if (!main.Available || MessageBox.Show(main, $"Permanently delete {Path.GetFileName(path)}?", "Delete Sound", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _ = main.Run(async _ => { await Task.Run(() => File.Delete(path));
            main.Settings.LibraryColors.Remove(Path.GetFileName(path)); main.Settings.LibraryPlayCounts.Remove(Path.GetFileName(path)); main.Settings.Soundboard.SoundPtt.Remove(Path.GetFileName(path));
            main.Settings.LibraryOrder.RemoveAll(n=>n.Equals(Path.GetFileName(path),StringComparison.OrdinalIgnoreCase));
            var current = main.Settings.Soundboard;
            await main.ApplySoundboardSettings(current with { Bindings = current.Bindings.Where(b => b.Command != SoundboardCommand.PlaySound || !string.Equals(b.SoundFilename, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)).ToArray() });
            await Refresh(); });
    }
    void RefreshRowTheme() {
        if(Arranging || dragSound != null) CancelReorder();
        if(performanceView != Theme.Current.ReduceMotion) { performanceView=Theme.Current.ReduceMotion; rows.ItemTemplate=RowTemplate(main.Settings.LibraryListView); }
        UpdateOrderHint(); foreach (var row in rows.Items.OfType<SoundRow>()) row.RefreshTheme();
    }
    public void Dispose() { disposed = true; CancelReorder(); main.Deactivated -= CancelOnDeactivate; main.Soundboard.Started -= SoundStarted; Theme.Changed -= RefreshRowTheme; watcher.Dispose(); debounce.Stop(); lifetime.Cancel(); }
}
