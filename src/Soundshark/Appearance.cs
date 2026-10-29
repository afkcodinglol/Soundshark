using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace Soundshark;

public sealed record Appearance
{
    public bool LightMode { get; init; }
    public string Accent { get; init; } = "#A34F72";
    public string Font { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 14;
    public bool CompactLibrary { get; init; } = true;
    public bool ShowFileDetails { get; init; } = true;
    public bool ReduceMotion { get; init; }
    public bool NewestFirst { get; init; }
    public void Validate()
    {
        if (!Regex.IsMatch(Accent ?? "", "^#[0-9A-Fa-f]{6}$") || !Theme.Fonts.Contains(Font) || !double.IsFinite(FontSize) || FontSize is < 12 or > 19)
            throw new ArgumentException("Choose a listed font, a text size from 12 to 19, and an accent color such as #A34F72.");
    }
}

public static class Theme
{
    public static readonly string[] BundledFonts = ["Lato", "Carlito", "Abel", "Anonymous Pro", "Cardo", "Cousine", "Didact Gothic", "Fira Sans", "IBM Plex Mono", "Inria Serif", "Poppins", "Questrial"];
    static readonly string[] StandardFonts = new[] { "Segoe UI" }.Concat(BundledFonts)
        .Concat(System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source)
            .Where(n => !new[] { "MDL2", "Symbol", "Wingdings", "Webdings", "Marlett", "Fluent Icons" }.Any(s => n.Contains(s, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static string[] Fonts => StandardFonts.Concat(CustomFonts.Families.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static Appearance Current { get; private set; } = new();
    public static event Action? Changed;
    public static FontFamily Family(string name) => CustomFonts.Families.TryGetValue(name, out var custom) ? custom : BundledFonts.Contains(name)
        ? new FontFamily(new Uri("pack://application:,,,/Soundshark;component/Assets/Fonts/"), "./#" + name) : new FontFamily(name);
    public static Brush Brush(string name) => (Brush)Application.Current.FindResource(name);
    public static void Apply(Appearance value)
    {
        value.Validate(); Current = value; var resources = Application.Current.Resources;
        bool light = value.LightMode;
        Color accent = (Color)ColorConverter.ConvertFromString(value.Accent);
        Color background = (Color)ColorConverter.ConvertFromString(light ? "#F3F4F7" : "#151419");
        static Color Blend(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        void Put(string key, Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); resources[key] = brush; }
        void Hex(string key, string dark, string bright) => Put(key, (Color)ColorConverter.ConvertFromString(light ? bright : dark));
        Put("AppBackground", background);
        Hex("Text", "#F3EDF2", "#20232B"); Hex("Muted", "#B8AFBC", "#555D6B");
        Hex("Surface", "#201E26", "#FFFFFF"); Hex("Raised", "#26222B", "#FFFFFF");
        Hex("Border", "#493C4B", "#CAD0DC"); Hex("WaveBackground", "#14131A", "#FFFFFF");
        Hex("WaveGrid", "#2F2A35", "#E1E4EB"); Hex("Live", "#3DE68F", "#087443");
        Hex("SuccessSurface", "#182720", "#E3F2E9"); Hex("SuccessButton", "#265B40", "#D5EDDE");
        Put("Accent", accent); Put("AccentSurface", Blend(background, accent, light ? .16 : .28));
        Put("AccentHover", Blend(background, accent, light ? .25 : .46));
        Put("AccentBorder", Blend(background, accent, .65));
        // Selected controls keep readable foregrounds even for extremely light/dark custom accents.
        Put("Selected", Blend(background, accent, light ? .24 : .4));
        Hex("SelectedText", "#FFFFFF", "#111820");
        var selection = new SolidColorBrush(accent) { Opacity = .24 }; selection.Freeze(); resources["WaveSelection"] = selection;
        resources["UiFont"] = Family(value.Font);
        for (int size = 10; size <= 32; ++size) resources["Font" + size.ToString(CultureInfo.InvariantCulture)] = size * value.FontSize / 14;
        resources["LibraryPadding"] = new Thickness(value.CompactLibrary ? 10 : 14, value.CompactLibrary ? 6 : 12, value.CompactLibrary ? 10 : 14, value.CompactLibrary ? 6 : 12);
        resources["LibraryTileSize"] = new Size(270 * value.FontSize / 14, (value.CompactLibrary ? 128 : 154) * value.FontSize / 14);
        resources["LibraryDetailsVisibility"] = Visibility.Visible;
        Changed?.Invoke();
    }
}
