using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;

namespace Soundshark;

public static class CustomFonts
{
    static readonly string root = Path.Combine(Paths.Root, "Fonts");
    public static IReadOnlyDictionary<string, FontFamily> Families => families;
    static readonly Dictionary<string, FontFamily> families = Load();
    static Dictionary<string, FontFamily> Load()
    {
        var result = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return result;
        foreach (string directory in Directory.EnumerateDirectories(root)) {
            if (!File.Exists(Path.Combine(directory,"accepted"))) continue;
            try { foreach (var family in Fonts.GetFontFamilies(new Uri(directory + Path.DirectorySeparatorChar))) {
                if (family.GetTypefaces().All(t=>t.TryGetGlyphTypeface(out _))) result[family.FamilyNames.Values.First()] = family;
            } }
            catch (Exception ex) { AppLog.Warn("font.load", ex); }
        }
        return result;
    }
    public static string[] Import(string path)
    {
        if (!new[] { ".ttf", ".otf", ".ttc", ".otc" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new ArgumentException("Choose a TTF, OTF, TTC or OTC file.");
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > 32 * 1024 * 1024) throw new ArgumentException("Choose a font file smaller than 32 MB.");
        byte[] bytes = File.ReadAllBytes(path);
        string folder = Path.Combine(root, Convert.ToHexString(SHA256.HashData(bytes)));
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, "font" + Path.GetExtension(path).ToLowerInvariant());
        if (!File.Exists(target)) File.WriteAllBytes(target, bytes);
        var loaded = Fonts.GetFontFamilies(new Uri(folder + Path.DirectorySeparatorChar)).ToArray();
        if (loaded.Length == 0) { File.Delete(target); throw new ArgumentException("Windows could not read any font families from this file."); }
        foreach (var family in loaded) {
            // Force font validation before it becomes the application font.
            foreach (var face in family.GetTypefaces()) if (!face.TryGetGlyphTypeface(out _)) throw new ArgumentException("This font is not supported by Windows text rendering.");
        }
        File.WriteAllText(Path.Combine(folder,"accepted"),"Soundshark font import");
        foreach (var family in loaded) {
            families[family.FamilyNames.Values.First()] = family;
        }
        return loaded.Select(f => f.FamilyNames.Values.First()).ToArray();
    }
}
