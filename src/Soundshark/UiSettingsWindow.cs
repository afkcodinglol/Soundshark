using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Soundshark;

public sealed class UiSettingsWindow : Window
{
    readonly MainWindow main;
    readonly Appearance original;
    readonly ComboBox font = new() { ItemsSource = Theme.Fonts, Width = 270, IsEditable = true, IsTextSearchEnabled = true, MaxDropDownHeight = 280 };
    readonly TextBox accent = new() { Name = "AccentHex", Width = 120, MaxLength = 7 };
    readonly Slider size = new() { Minimum = 12, Maximum = 19, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 230 };
    readonly CheckBox motion = new() { Content = "Performance Mode" };
    readonly TextBlock error = Ui.Text("", 12, true);
    bool ready, saved;
    public UiSettingsWindow(MainWindow owner)
    {
        main = owner; original = owner.Settings.Appearance; error.Foreground=System.Windows.Media.Brushes.IndianRed;
        Owner = owner; Title = "UI Settings"; Width = 700; Height = 720; MinWidth = 600; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, typeof(Window));
        var root = new Grid { Margin = new Thickness(24) }; root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); Content = root;
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(Ui.Text("Make Soundshark Yours", 24));
        body.Children.Add(Ui.Text("Changes preview immediately. Save to keep them next time you open Soundshark.", 13, true));
        void Field(string label, UIElement control) { var name = Ui.Text(label, 14); name.Width = 150; var row = Ui.Row(name, control); row.Margin = new Thickness(0, 14, 0, 0); body.Children.Add(row); }
        accent.Text = original.Accent; Field("Accent Color", Ui.Row(accent, Ui.Button("Color Wheel…", () => new ColorPickerWindow(this, Theme.Current.Accent, color => accent.Text = color).ShowDialog())));
        var swatches = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var color in new[] { "#A34F72", "#D54D75", "#E53935", "#F4511E", "#EF8F23", "#E4BB27", "#9DAE36", "#48A344", "#278266", "#159A86", "#23B6BE", "#3BA5D9", "#287BB8", "#3154B4", "#5544BA", "#7860CA", "#AB52C5", "#CF48AE", "#E174A6", "#C98F78", "#926A54", "#8996A8", "#D3DBE5", "#4E5666" }) {
            var button = Ui.Button("", () => accent.Text = color); button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); button.Width = 30; button.Height = 28; button.Padding = new Thickness(0); button.Margin = new Thickness(0, 0, 6, 6); button.ToolTip = color;
            System.Windows.Automation.AutomationProperties.SetName(button, "Accent " + color); swatches.Children.Add(button);
        }
        body.Children.Add(swatches);
        body.Children.Add(Ui.Text("Pick a swatch, open the color wheel, or enter a hex color.", 12, true));
        font.SelectedItem = original.Font; Field("Font", Ui.Row(font, Ui.Button("Custom Font", LoadFont)));
        body.Children.Add(Ui.Text("Bundled, installed and imported fonts. Load TrueType, OpenType or font collections.", 12, true));
        size.Value = original.FontSize; var amount = Ui.Text("", 13); amount.SetBinding(TextBlock.TextProperty, new Binding("Value") { Source = size, StringFormat = "{0:0}" });
        Field("Text Size", Ui.Row(size, amount));
        body.Children.Add(Ui.Text("The quick brown fox jumps over the lazy dog. 0123456789", 16));
        motion.IsChecked = original.ReduceMotion; motion.Margin = new Thickness(0,12,0,0); body.Children.Add(motion); motion.Click += (_,_)=>Preview();
        body.Children.Add(Ui.Text("Performance Mode reduces UI animation. Recording, Clipper and update indicators stay visible without pulsing.", 12, true));
        var footer = new StackPanel(); footer.Children.Add(error); footer.Children.Add(Ui.Row(Ui.Button("Save", () => _ = Save()), Ui.Button("Cancel", Close), Ui.Button("Reset To Defaults", Reset)));
        Grid.SetRow(footer, 1); root.Children.Add(footer);
        font.SelectionChanged += (_, _) => { if (font.SelectedItem is string) Preview(); }; size.ValueChanged += (_, _) => Preview(); accent.TextChanged += (_, _) => Preview();
        Closed += (_, _) => { if (!saved) { bool reorder = Theme.Current.NewestFirst != original.NewestFirst; Theme.Apply(original); if (reorder) _ = main.RefreshLibrary(); } };
        ready = true;
    }
    Appearance Read() => original with { Accent = accent.Text.Trim(), Font = font.SelectedItem as string ?? font.Text, FontSize = size.Value, ShowFileDetails = true, ReduceMotion = motion.IsChecked == true };
    void LoadFont()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Font files|*.ttf;*.otf;*.ttc;*.otc|All files|*.*", Title = "Load a custom font" };
        if (picker.ShowDialog(this) != true) return;
        try { var names = CustomFonts.Import(picker.FileName); font.ItemsSource = Theme.Fonts; font.SelectedItem = names[0]; Preview(); }
        catch (Exception ex) { error.Foreground = Brushes.IndianRed; error.Text = "Could not load that font. " + ex.Message; }
    }
    void Preview()
    {
        if (!ready) return;
        try { var value = Read(); bool reorder = Theme.Current.NewestFirst != value.NewestFirst; Theme.Apply(value); if (reorder) _ = main.RefreshLibrary(); error.Text = ""; }
        catch (ArgumentException ex) { error.Text = ex.Message; }
    }
    void Reset()
    {
        ready = false; var value = new Appearance(); font.SelectedItem = value.Font; accent.Text = value.Accent; size.Value = value.FontSize;
        motion.IsChecked = value.ReduceMotion;
        ready = true; Preview();
    }
    async Task Save()
    {
        try {
            var candidate = Read(); candidate.Validate(); main.Settings.Appearance = candidate;
            try { await main.Settings.Save(); } catch { main.Settings.Appearance = original; throw; }
            Theme.Apply(candidate); saved = true; await main.RefreshLibrary(); Close();
        } catch (ArgumentException ex) { error.Text = ex.Message; }
        catch (Exception ex) { error.Text = UserErrors.Report(ex, "appearance.save").Inline; }
    }
}
