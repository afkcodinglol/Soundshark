using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Soundshark.Core;

namespace Soundshark;
public static class SoundboardAudio
{
    public static Task<string> PrepareMicrophone(string file, int rate, SoundboardConfiguration settings, CancellationToken ct)
        => Prepare(file, rate, settings.ForSoundOutput(), true, ct);
    static async Task<double> Gain(string file, SoundboardConfiguration settings, int channel, CancellationToken ct)
    {
        var text = new StringBuilder();
        string channelFilter = channel < 0 ? "" : "aformat=channel_layouts=stereo,pan=mono|c0=c" + channel + ",";
        await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "info", "-i", file, "-map", "0:a:0", "-af", channelFilter + "loudnorm=I=-23:TP=-1.5:LRA=11:print_format=json", "-f", "null", "-"], ct,
            diagnostic: line => { if (text.Length < 64000) text.AppendLine(line); });
        string result = text.ToString(); int start = result.LastIndexOf('{'), end = result.LastIndexOf('}');
        if (start < 0 || end <= start) throw new SoundboardException("Couldn't measure this sound's volume. Try another file or turn automatic volume off in Settings.");
        using var json = JsonDocument.Parse(result[start..(end + 1)]);
        static double Value(JsonElement root, string name) => double.TryParse(root.GetProperty(name).GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
        double loudness = Value(json.RootElement, "input_i"), peak = Value(json.RootElement, "input_tp");
        if (!double.IsFinite(peak)) return 0; // Digital silence.
        // Clips shorter than the loudness analysis window still need protection.
        // Use a conservative peak target when integrated loudness is unavailable.
        double gain = double.IsFinite(loudness) && loudness >= -70
            ? Math.Clamp(settings.TargetLufs - loudness, -60, settings.MaximumBoostDb)
            : !double.IsFinite(loudness) && peak >= -70 ? Math.Clamp(-12 - peak, -60, settings.MaximumBoostDb) : 0;
        return Math.Min(gain, -1.5 - peak);
    }
    public static async Task<string> Prepare(string file, int rate, SoundboardConfiguration settings, bool normalize, CancellationToken ct)
    {
        settings.Validate();
        await using var source = File.OpenRead(file);
        string identity = Convert.ToHexString(await SHA256.HashDataAsync(source, ct));
        string options = $"v2|{identity}|{rate}|{normalize}|{settings.VolumeMode}|{settings.TargetLufs:R}|{settings.FixedAdjustmentDb:R}|{settings.MaximumBoostDb:R}|{settings.LinkStereoChannels}";
        string path = Path.Combine(Paths.Cache, "soundboard-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options))) + ".pcm");
        if (File.Exists(path) && new FileInfo(path).Length > 0 && new FileInfo(path).Length % 8 == 0) return path;
        string temp = Paths.NewPcm();
        try {
            var args = new List<string> { "-nostdin", "-v", "error", "-i", file, "-map", "0:a:0", "-vn", "-ac", "2" };
            if (normalize && settings.VolumeMode == SoundboardVolumeMode.Automatic) {
                // Measure integrated loudness and true peak off-thread, then apply bounded static gain.
                static string Filter(double gain) => "volume=" + gain.ToString(CultureInfo.InvariantCulture) + "dB,alimiter=limit=0.98:level=false:latency=true";
                if (settings.LinkStereoChannels) args.AddRange(["-af", Filter(await Gain(file, settings, -1, ct))]);
                else {
                    double left = await Gain(file, settings, 0, ct), right = await Gain(file, settings, 1, ct);
                    args.RemoveRange(args.IndexOf("-map"), 2);
                    args.AddRange(["-filter_complex", "[0:a:0]aformat=channel_layouts=stereo,channelsplit=channel_layout=stereo[l][r];[l]" + Filter(left) + "[ln];[r]" + Filter(right) + "[rn];[ln][rn]join=inputs=2:channel_layout=stereo[out]", "-map", "[out]"]);
                }
            } else if (normalize && settings.VolumeMode == SoundboardVolumeMode.FixedAdjustment)
                args.AddRange(["-af", "volume=" + settings.FixedAdjustmentDb.ToString(CultureInfo.InvariantCulture) + "dB,alimiter=limit=0.98:level=false:latency=true"]);
            args.AddRange(["-f", "f32le", "-ar", rate.ToString(CultureInfo.InvariantCulture), temp]);
            await MediaTools.Run(Paths.Tool("ffmpeg"), args, ct);
            if (new FileInfo(temp).Length == 0 || new FileInfo(temp).Length % 8 != 0) throw new SoundboardException("This file didn't provide playable sound.");
            ct.ThrowIfCancellationRequested(); File.Move(temp, path, true); return path;
        } finally { try { File.Delete(temp); } catch (Exception ex) { AppLog.Warn("soundboard.cache.cleanup", ex); } }
    }
}
