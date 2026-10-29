using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Soundshark.Core;

namespace Soundshark;
public sealed record MediaInfo(double Duration, long Size);
public static class MediaTools
{
    public static async Task<string> Run(string exe, IEnumerable<string> args, CancellationToken ct, Action<string>? line = null, int stdoutLimit = 16000, Action<string>? diagnostic = null)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = new Process { StartInfo = info };
        string operation = "tool." + Path.GetFileNameWithoutExtension(exe);
        string invocation = Guid.NewGuid().ToString("N")[..8];
        AppLog.Info(operation + ".start", new { invocation, executable = exe, arguments = info.ArgumentList.ToArray() });
        ct.ThrowIfCancellationRequested();
        try { p.Start(); }
        catch (Exception ex) { AppLog.Error(operation + ".start", ex, new { invocation, executable = exe }); throw; }
        using var registration = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } });
        var errors = new StringBuilder(); var output = new StringBuilder();
        async Task Read(StreamReader reader, StringBuilder target, bool notify, int limit, string stream)
        {
            while (await reader.ReadLineAsync() is { } s)
            {
                if (target.Length > limit) target.Remove(0, target.Length / 2); target.AppendLine(s);
                if (stream == "stderr") diagnostic?.Invoke(s);
                // Successful video metadata can contain megabytes of unrelated account/site data.
                await AppLog.Log.Detail(operation + "." + stream, $"[{invocation}] " + (stdoutLimit > 16000 && stream == "stdout" ? $"Metadata received ({s.Length} characters)." : s));
                // Technical stderr remains in the logs, never in progress labels.
                if (notify && (s.StartsWith("out_time_us=") || s.StartsWith("[download]"))) line?.Invoke(s);
            }
        }
        await Task.WhenAll(Read(p.StandardError, errors, true, 16000, "stderr"), Read(p.StandardOutput, output, true, stdoutLimit, "stdout"), p.WaitForExitAsync());
        AppLog.Info(operation + ".exit", new { invocation, exitCode = p.ExitCode, cancelled = ct.IsCancellationRequested });
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0) throw new MediaToolException(Path.GetFileNameWithoutExtension(exe), p.ExitCode, errors.ToString().Trim());
        return output.ToString();
    }
    public static async Task<MediaInfo> Probe(string path, CancellationToken ct)
    {
        string s = await Run(Paths.Tool("ffprobe"), ["-v", "error", "-select_streams", "a:0", "-show_entries", "format=duration:stream=codec_type", "-of", "json", path], ct);
        using var j = JsonDocument.Parse(s);
        if (!j.RootElement.GetProperty("streams").EnumerateArray().Any()) throw new InvalidDataException("This file contains no audio stream.");
        return new(double.Parse(j.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture), new FileInfo(path).Length);
    }
    public static async Task<string> Decode(string input, CancellationToken ct, Action<string>? progress = null)
    {
        string output = Paths.NewPcm();
        try
        {
            await Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-progress", "pipe:1", "-i", input, "-map", "0:a:0", "-vn", "-f", "f32le", "-ac", "2", "-ar", "48000", output], ct, progress);
            if (new FileInfo(output).Length == 0) throw new InvalidDataException("No audio could be decoded.");
            return output;
        }
        catch { Cleanup(output); throw; }
    }
    public static async Task<MediaInfo> Export(Workspace w, string destination, int bitrate, bool discord, bool overwrite, CancellationToken ct, Action<string>? progress = null)
    {
        if (w.Frames == 0) throw new InvalidDataException("There is no audio to save.");
        if (discord && w.Seconds > 5) throw new InvalidDataException($"Audio is {w.Seconds:F3}s. Trim it to less than five seconds before exporting for Discord.");
        string raw = Paths.NewPcm(), temp = Path.Combine(Path.GetDirectoryName(destination)!, ".soundshark-" + Guid.NewGuid() + ".mp3");
        try
        {
            using (var stream = w.Open())
            await using (var file = new FileStream(raw, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true)) await stream.CopyToAsync(file, ct);
            var args = new List<string> { "-nostdin", "-v", "error", "-progress", "pipe:1", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", raw, "-map", "0:a:0", "-map_metadata", "-1", "-vn", "-c:a", "libmp3lame" };
            // Automatic mode uses LAME's best VBR setting: bitrate follows the audio,
            // preserving detail without asking users to estimate source fidelity.
            args.AddRange(bitrate > 0 ? new[] { "-b:a", $"{bitrate}k" } : new[] { "-q:a", "0" });
            args.AddRange(["-id3v2_version", "0", "-write_id3v1", "0", temp]);
            progress?.Invoke("Checking Sound Volume…");
            var level = await SavedAudioSafety.Measure(raw, ct, raw: true);
            double volume=w.Volume;
            double outputGain=level.Reduction+(volume>0?20*Math.Log10(volume):0);
            args.InsertRange(args.Count - 1, ["-af", volume==0?"volume=0":SavedAudioSafety.Filter(outputGain)]);
            await SavedAudioSafety.Encode(temp, outputGain, async gain => {
                args[args.IndexOf("-af") + 1] = volume==0?"volume=0":SavedAudioSafety.Filter(gain);
                await Run(Paths.Tool("ffmpeg"), args, ct, progress);
            }, ct, progress);
            var result = await Probe(temp, ct); FileRules.Validate(result.Duration, result.Size, discord); ct.ThrowIfCancellationRequested();
            // Both names share a volume; publication never exposes a partially encoded file.
            if (overwrite && File.Exists(destination)) File.Replace(temp, destination, null);
            else File.Move(temp, destination, false);
            return result;
        }
        finally { Cleanup(raw); Cleanup(temp); }
    }
    public static async Task<string> Download(string url, CancellationToken ct, Action<string> progress)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || !new[] { "youtube.com", "www.youtube.com", "m.youtube.com", "youtu.be", "tiktok.com", "www.tiktok.com", "vm.tiktok.com", "vt.tiktok.com" }.Contains(u.Host.ToLowerInvariant()) || u.Query.Contains("list=", StringComparison.OrdinalIgnoreCase) || u.AbsolutePath.Contains("/live/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Enter one public HTTPS YouTube video, Short, or TikTok video. Playlists, live streams and sign-in imports are excluded.");
        string host = u.Host.ToLowerInvariant(), path = u.AbsolutePath;
        bool individual = host == "youtu.be" ? System.Text.RegularExpressions.Regex.IsMatch(path, "^/[A-Za-z0-9_-]{11}/?$")
            : host.Contains("youtube.com") ? (path == "/watch" && System.Text.RegularExpressions.Regex.IsMatch(u.Query, "[?&]v=[A-Za-z0-9_-]{11}(&|$)")) || System.Text.RegularExpressions.Regex.IsMatch(path, "^/shorts/[A-Za-z0-9_-]{11}/?$")
            : host is "vm.tiktok.com" or "vt.tiktok.com" || System.Text.RegularExpressions.Regex.IsMatch(path, "^/@[^/]+/video/[0-9]+/?$") || path.StartsWith("/t/");
        if (!individual) throw new ArgumentException("This link does not identify an individual video. Channel, profile and search pages are excluded.");
        string dir = Path.Combine(Paths.Cache, Guid.NewGuid().ToString()); Directory.CreateDirectory(dir);
        var common = new[] { "--ignore-config", "--no-playlist", "--no-cookies", "--js-runtimes", "deno:" + Paths.Tool("deno"), "--ffmpeg-location", Path.GetDirectoryName(Paths.Tool("ffmpeg"))!, "--socket-timeout", "20" };
        try
        {
            string metadata = await Run(Paths.Tool("yt-dlp"), common.Concat(new[] { "--dump-single-json", "--skip-download", "--", url }), ct, null, stdoutLimit: 32 * 1024 * 1024);
            using (var j = JsonDocument.Parse(metadata))
            {
                var r = j.RootElement;
                if ((r.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True) || (r.TryGetProperty("live_status", out var ls) && ls.GetString() is "is_live" or "is_upcoming") || r.TryGetProperty("entries", out _)) throw new InvalidDataException("Live streams and playlists are not supported.");
            }
            await Run(Paths.Tool("yt-dlp"), common.Concat(new[] { "--newline", "--progress", "-f", "bestaudio/best", "-o", Path.Combine(dir, "audio.%(ext)s"), "--", url }), ct, s => { if (!s.StartsWith('{')) progress(s); });
            var file = Directory.GetFiles(dir).FirstOrDefault(x => !x.EndsWith(".part") && !x.EndsWith(".ytdl")) ?? throw new InvalidDataException("The website returned no audio.");
            return await Decode(file, ct, progress);
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception ex) { AppLog.Warn("download.cleanup", ex); } }
    }
    static void Cleanup(string path) { try { File.Delete(path); } catch (Exception ex) { AppLog.Warn("audio.cleanup", ex); } }
}
