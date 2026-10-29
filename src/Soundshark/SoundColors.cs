using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Soundshark;
public static class SoundColors
{
    public static bool Valid(string? color) => color != null && Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$");
    public static Dictionary<string, string> Normalize(Dictionary<string, string>? colors)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (colors != null) foreach (var pair in colors)
            if (Path.GetFileName(pair.Key) == pair.Key && Valid(pair.Value)) result[pair.Key] = pair.Value.ToUpperInvariant();
        return result;
    }
    public static void Rename(Dictionary<string, string> colors, string oldName, string newName)
    {
        colors.Remove(oldName, out var color); colors.Remove(newName);
        if (color != null) colors[newName] = color;
    }
    public static Brush Brush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    public static Color TextColor(Color background)
    {
        static double Linear(byte value) { double v = value / 255.0; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        double luminance = .2126 * Linear(background.R) + .7152 * Linear(background.G) + .0722 * Linear(background.B);
        return luminance > .179 ? Colors.Black : Colors.White;
    }
}
