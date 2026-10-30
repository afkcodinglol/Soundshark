using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Soundshark;

public sealed record HubCountry(string Code, string Name)
{
    public ImageSource Flag => HubCountries.Flag(Code);
    public string Display => $"{Name} ({Code})";
    public override string ToString() => Display;
}

public static class HubCountries
{
    static readonly Lazy<IReadOnlyList<HubCountry>> countries = new(Load);
    static readonly Dictionary<string, ImageSource> flags = new(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<HubCountry> All => countries.Value;
    public static bool Supports(string? code) => code != null && All.Any(country => country.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
    public static HubCountry Default
    {
        get
        {
            string code;
            try { code = RegionInfo.CurrentRegion.TwoLetterISORegionName; }
            catch (ArgumentException) { code = "US"; }
            return All.FirstOrDefault(country => country.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ?? All.First();
        }
    }
    static IReadOnlyList<HubCountry> Load()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Soundshark;component/Assets/Flags/countries.json"))
            ?? throw new InvalidDataException("Country flag catalogue is missing.");
        using var stream = resource.Stream;
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? throw new InvalidDataException("Country flag catalogue is invalid.");
        var result = values.Select(item => new HubCountry(item.Key.ToUpperInvariant(), item.Value)).OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (result.Length != 255 || result.Any(item => item.Code.Length is not (2 or 6)))
            throw new InvalidDataException("Country flag catalogue is incomplete.");
        return result;
    }
    public static ImageSource Flag(string code)
    {
        if (!Supports(code)) throw new ArgumentException("Choose a supported country or region.");
        lock (flags)
        {
            if (flags.TryGetValue(code, out var cached)) return cached;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri($"pack://application:,,,/Soundshark;component/Assets/Flags/{code.ToLowerInvariant()}.png");
            image.EndInit(); image.Freeze(); flags[code] = image; return image;
        }
    }
}

public sealed class HubCountryWindow : Window
{
    readonly ComboBox country = new() { Name = "HubCountry", ItemsSource = HubCountries.All, IsTextSearchEnabled = true, MinWidth = 360 };
    readonly CheckBox show = new() { Name = "HubShowCountry", Content = "Display this flag beside my uploader name" };
    readonly Image preview = new() { Width = 96, Height = 64, Stretch = Stretch.Uniform };
    public string CountryCode => ((HubCountry)country.SelectedItem).Code;
    public bool ShowCountry => show.IsChecked == true;

    public HubCountryWindow(Window owner, HubCountryPreference preference)
    {
        Owner = owner; Title = "My Country Flag"; Width = 540; Height = 390; MinWidth = 460; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, typeof(Window));
        var selected = HubCountries.All.FirstOrDefault(item => item.Code.Equals(preference.CountryCode, StringComparison.OrdinalIgnoreCase)) ?? HubCountries.Default;
        country.SelectedItem = selected; show.IsChecked = preference.ShowCountry; TextSearch.SetTextPath(country, nameof(HubCountry.Name));
        var item = new FrameworkElementFactory(typeof(StackPanel)); item.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(Image)); icon.SetBinding(Image.SourceProperty, new Binding(nameof(HubCountry.Flag))); icon.SetValue(Image.WidthProperty, 30.0); icon.SetValue(Image.HeightProperty, 20.0); icon.SetValue(Image.StretchProperty, Stretch.Uniform); icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0,0,10,0)); item.AppendChild(icon);
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding(nameof(HubCountry.Display))); item.AppendChild(label);
        country.ItemTemplate = new DataTemplate { VisualTree = item };
        void UpdatePreview() { if (country.SelectedItem is HubCountry value) preview.Source = value.Flag; }
        country.SelectionChanged += (_, _) => UpdatePreview(); UpdatePreview();

        var body = new StackPanel { Margin = new Thickness(24) };
        var title = Ui.Text("Choose your country or region", 22); title.FontWeight = FontWeights.SemiBold; body.Children.Add(title);
        var hint = Ui.Text("Your choice is private until you enable display. Saving updates the flag beside your name on existing and future uploads.", 13, true); hint.Margin = new Thickness(0,6,0,18); body.Children.Add(hint);
        var previewPlate = new Border { Child = preview, Height = 82, CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Margin = new Thickness(0,0,0,14) }; previewPlate.SetResourceReference(Border.BackgroundProperty,"Raised"); previewPlate.SetResourceReference(Border.BorderBrushProperty,"Border"); previewPlate.BorderThickness=new Thickness(1); body.Children.Add(previewPlate);
        body.Children.Add(country); show.Margin = new Thickness(0,16,0,18); body.Children.Add(show);
        var save = Ui.Button("Save Flag Preference", () => DialogResult = true); save.IsDefault = true;
        var cancel = Ui.Button("Cancel", () => DialogResult = false); cancel.IsCancel = true; cancel.SetResourceReference(Control.BackgroundProperty,"Raised"); body.Children.Add(Ui.Row(save,cancel));
        Content = body;
    }
}
