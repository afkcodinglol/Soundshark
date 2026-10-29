using System.Globalization;
using System.IO;
using System.Text.Json;
using Soundshark.Core;

namespace Soundshark;

public sealed record AudioOutputFormat(string Label, string Extension, string Codec)
{
    public override string ToString() => Label;
    public static readonly AudioOutputFormat Mp3 = new("MP3", ".mp3", "mp3");
    public static readonly AudioOutputFormat[] All = [Mp3, new("WAV", ".wav", "pcm"), new("FLAC", ".flac", "flac"), new("M4A", ".m4a", "aac"), new("OGG", ".ogg", "vorbis"), new("Opus", ".opus", "opus"), new("AIFF", ".aiff", "pcm")];
}

/// <summary>Direct source-to-format conversion. Compatible audio is remuxed without re-encoding.</summary>
public static class AudioConversion
{
    public static bool IsLibraryAudio(string path) => FileRules.AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) && !Path.GetFileName(path).StartsWith(".soundshark-", StringComparison.OrdinalIgnoreCase);

    public static string NewDestination(string input, AudioOutputFormat format)
    {
        string name = Path.GetFileNameWithoutExtension(input);
        name = name[..Math.Min(name.Length, 100)];
        string path = Path.Combine(Paths.Library, FileRules.Filename(name, format.Extension));
        int suffix = 2;
        while (File.Exists(path) || string.Equals(Path.GetFullPath(input), path, StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Paths.Library, FileRules.Filename($"{name} ({suffix++})", format.Extension));
        return path;
    }

    public static async Task<MediaInfo> Convert(string input, string destination, AudioOutputFormat format, bool discord, CancellationToken ct, Action<string>? progress = null)
    {
        if (!AudioOutputFormat.All.Contains(format)) throw new ArgumentException("Choose a supported output format.");
        if (discord && format != AudioOutputFormat.Mp3) throw new InvalidDataException("Discord Soundboard requires MP3. MP3 is selected automatically when Discord mode is on.");
        if (!Path.GetExtension(destination).Equals(format.Extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output filename does not match the selected format.");
        if (Path.GetFullPath(input).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a new output filename so the original is preserved.");
        var sourceInfo = await MediaTools.Probe(input, ct);
        if (discord && sourceInfo.Duration > 5) throw new InvalidDataException($"This audio is {sourceInfo.Duration:F2} seconds. Open it in The Studio and trim it to five seconds or less for Discord.");
        string json = await MediaTools.Run(Paths.Tool("ffprobe"), ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,channels,bits_per_raw_sample,bits_per_sample", "-of", "json", input], ct);
        using var document = JsonDocument.Parse(json);
        var stream = document.RootElement.GetProperty("streams")[0];
        string codec = stream.GetProperty("codec_name").GetString() ?? "";
        int channels = stream.TryGetProperty("channels", out var c) ? c.GetInt32() : 2;
        int bits = 0;
        foreach (string property in new[] { "bits_per_raw_sample", "bits_per_sample" })
            if (stream.TryGetProperty(property, out var b) && int.TryParse(b.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) bits = Math.Max(bits, value);
        progress?.Invoke("Checking Sound Volume…");
        var level = await SavedAudioSafety.Measure(input, ct);
        bool copy = level.Reduction == 0 && (format.Codec == codec
            || format.Extension == ".wav" && (codec is "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_f32le" or "pcm_f64le")
            || format.Extension == ".aiff" && (codec is "pcm_s16be" or "pcm_s24be" or "pcm_s32be"));
        var encoding = copy ? new[] { "-c:a", "copy" } : format.Extension switch
        {
            ".mp3" => ["-c:a", "libmp3lame", "-q:a", "2"],
            ".wav" => ["-c:a", bits > 24 ? "pcm_s32le" : bits > 16 ? "pcm_s24le" : "pcm_s16le"],
            ".flac" => ["-c:a", "flac", "-compression_level", "5"],
            ".m4a" => ["-c:a", "aac", "-b:a", "192k"],
            ".ogg" => ["-c:a", "libvorbis", "-q:a", "5"],
            ".opus" => ["-c:a", "libopus", "-b:a", "128k", "-vbr", "on"],
            ".aiff" => ["-c:a", bits > 24 ? "pcm_s32be" : bits > 16 ? "pcm_s24be" : "pcm_s16be"],
            _ => throw new ArgumentException("Unsupported output format.")
        };
        string temp = Path.Combine(Path.GetDirectoryName(destination)!, ".soundshark-" + Guid.NewGuid() + format.Extension);
        try
        {
            var args = new List<string> { "-nostdin", "-v", "error", "-progress", "pipe:1", "-i", input, "-map", "0:a:0", "-map_metadata", "-1", "-vn" };
            args.AddRange(encoding);
            if (!copy && format == AudioOutputFormat.Mp3 && channels > 2) args.AddRange(["-ac", "2"]);
            if (format == AudioOutputFormat.Mp3) args.AddRange(["-id3v2_version", "0", "-write_id3v1", "0"]);
            args.Add(temp);
            if (copy) {
                await MediaTools.Run(Paths.Tool("ffmpeg"), args, ct, progress);
                if (!(await SavedAudioSafety.Measure(temp, ct)).Safe) throw new SoundboardException("Couldn't verify this sound's volume. Try another output format.");
            } else {
                args.InsertRange(args.Count - 1, ["-af", SavedAudioSafety.Filter(level.Reduction)]);
                await SavedAudioSafety.Encode(temp, level.Reduction, async gain => {
                    args[args.IndexOf("-af") + 1] = SavedAudioSafety.Filter(gain);
                    await MediaTools.Run(Paths.Tool("ffmpeg"), args, ct, progress);
                }, ct, progress);
            }
            var result = await MediaTools.Probe(temp, ct); FileRules.Validate(result.Duration, result.Size, discord); ct.ThrowIfCancellationRequested();
            // Never overwrite a source or another conversion; a concurrent filename collision is an error.
            File.Move(temp, destination, false);
            return result;
        }
        finally { try { File.Delete(temp); } catch (Exception ex) { AppLog.Warn("conversion.cleanup", ex); } }
    }
}
