using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Soundshark;

public sealed class ColorPickerWindow : Window
{
    readonly ColorWheel wheel = new();
    readonly Slider brightness = new() { Minimum = 0, Maximum = 1, Width = 240 };
    readonly TextBox hex = new() { Name = "ColorHex", Width = 115, MaxLength = 7 };
    readonly Border swatch = new() { Width = 44, Height = 35, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 10, 0) };
    readonly TextBlock error = Ui.Text("", 12, true);
    readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly Action<string> preview;
    readonly string original;
    bool sync, accepted, valid = true;
    public bool Accepted => accepted;
    public bool UseDefault { get; private set; }
    public ColorPickerWindow(Window owner, string color, Action<string> preview, string title = "Choose Accent Color", string instruction = "Live preview · use this color, then Save in UI Settings.", bool allowDefault = false)
    {
        Owner = owner; this.preview = preview; original = color;
        Title = title; Width = 440; Height = 570; MinWidth = 400; MinHeight = 530; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; SetResourceReference(StyleProperty, typeof(Window));
        var root = new Grid { Margin = new Thickness(22) }; root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); Content = root;
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(Ui.Text("Choose Any Color", 22)); body.Children.Add(Ui.Text("Drag on the wheel, then adjust brightness.", 13, true));
        wheel.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(wheel);
        body.Children.Add(Ui.Text("Brightness", 13, true)); body.Children.Add(brightness);
        var selection = Ui.Row(swatch, hex); selection.Margin = new Thickness(0, 14, 0, 0); body.Children.Add(selection);
        body.Children.Add(Ui.Text(instruction, 12, true));
        var footer = new StackPanel(); footer.Children.Add(error);
        var buttons = new WrapPanel(); buttons.Children.Add(Ui.Button("Use Color", Accept)); buttons.Children.Add(Ui.Button("Cancel", Close));
        if (allowDefault) buttons.Children.Add(Ui.Button("Use Default", () => { previewTimer.Stop(); UseDefault = accepted = true; Close(); }));
        footer.Children.Add(buttons); Grid.SetRow(footer, 1); root.Children.Add(footer);
        wheel.SetColor((Color)ColorConverter.ConvertFromString(color)); Update();
        wheel.ColorChanged += _ => Update();
        brightness.ValueChanged += (_, _) => { if (!sync) wheel.SetBrightness(brightness.Value); };
        hex.TextChanged += (_, _) => {
            if (sync) return;
            if (!System.Text.RegularExpressions.Regex.IsMatch(hex.Text, "^#[0-9a-fA-F]{6}$")) { valid = false; error.Text = "Enter a color such as #A34F72."; return; }
            wheel.SetColor((Color)ColorConverter.ConvertFromString(hex.Text)); Update();
        };
        previewTimer.Tick += (_, _) => { previewTimer.Stop(); if (valid) preview(Hex(wheel.SelectedColor)); };
        Closed += (_, _) => { previewTimer.Stop(); if (!accepted) preview(original); };
    }
    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    void Update()
    {
        sync = true; valid = true; hex.Text = Hex(wheel.SelectedColor); brightness.Value = wheel.Brightness; swatch.Background = new SolidColorBrush(wheel.SelectedColor); error.Text = ""; sync = false;
        if (!previewTimer.IsEnabled) previewTimer.Start();
    }
    void Accept()
    {
        if (!valid) return;
        previewTimer.Stop(); preview(Hex(wheel.SelectedColor)); accepted = true; Close();
    }
}
