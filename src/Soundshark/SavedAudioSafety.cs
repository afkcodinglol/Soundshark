using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Soundshark;

/// <summary>Attenuation only: preserve quiet recordings and dynamics; check the actual encoded file.</summary>
public static class SavedAudioSafety
{
    public sealed record Level(double Lufs, double TruePeak)
    {
        public double Reduction => Math.Min(0, Math.Min(double.IsFinite(Lufs) ? -18 - Lufs : 0,
            double.IsFinite(TruePeak) ? -2 - TruePeak : 0));
        public bool Safe => (double.IsNegativeInfinity(Lufs) || double.IsFinite(Lufs) && Lufs <= -17.9)
            && (double.IsNegativeInfinity(TruePeak) || double.IsFinite(TruePeak) && TruePeak <= -1.5);
    }
    public static async Task<Level> Measure(string file, CancellationToken ct, bool raw = false)
    {
        var args = new List<string> { "-nostdin", "-v", "info" };
        if (raw) args.AddRange(["-f", "f32le", "-ar", "48000", "-ac", "2"]);
        args.AddRange(["-i", file, "-map", "0:a:0", "-af", "loudnorm=I=-18:TP=-2:LRA=11:print_format=json", "-f", "null", "-"]);
        var diagnostic = new StringBuilder();
        await MediaTools.Run(Paths.Tool("ffmpeg"), args, ct, diagnostic: s => {
            diagnostic.AppendLine(s); if (diagnostic.Length > 32000) diagnostic.Remove(0, diagnostic.Length - 16000);
        });
        string text = diagnostic.ToString(); int start = text.LastIndexOf('{'), end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new SoundboardException("Couldn't check this sound's volume. Try saving again.");
        using var json = JsonDocument.Parse(text[start..(end + 1)]);
        static double Number(JsonElement root, string name) {
            string text = root.GetProperty(name).GetString()!;
            if (text == "-inf") return double.NegativeInfinity;
            return double.Parse(text, CultureInfo.InvariantCulture);
        }
        var level = new Level(Number(json.RootElement, "input_i"), Number(json.RootElement, "input_tp"));
        if (double.IsNaN(level.Lufs) || double.IsNaN(level.TruePeak) || double.IsPositiveInfinity(level.Lufs) || double.IsPositiveInfinity(level.TruePeak))
            throw new SoundboardException("Couldn't check this sound's volume. Try saving again.");
        return level;
    }
    public static string Filter(double gain) => "volume=" + gain.ToString("R", CultureInfo.InvariantCulture) + "dB";

    public static async Task Encode(string temp, double gain, Func<double, Task> encode, CancellationToken ct, Action<string>? progress = null)
    {
        for (int attempt = 0; attempt < 3; ++attempt) {
            await encode(gain);
            progress?.Invoke("Checking Saved Sound Volume…");
            var actual = await Measure(temp, ct);
            if (actual.Safe) return;
            gain += Math.Min(-.5, actual.Reduction - .25);
            System.IO.File.Delete(temp);
        }
        throw new SoundboardException("This sound is still too loud after processing. Turn it down and try saving again.");
    }
}
