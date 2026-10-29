using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Soundshark;
public sealed record ToolUpdateProgress(string Status, double? Percent);
/// <summary>Stage a complete toolset; checksum and smoke-test before switching a single pointer.</summary>
public static class ToolUpdater
{
    static readonly HttpClient client = CreateClient();
    static HttpClient CreateClient() { var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) }; c.DefaultRequestHeaders.UserAgent.ParseAdd("Soundshark/1.0"); return c; }
    public static async Task Update(CancellationToken ct, IProgress<ToolUpdateProgress>? progress = null, HttpClient? http = null, Func<string, CancellationToken, Task>? verifyVersion = null, Func<string, CancellationToken, Task<string>>? readVersion = null)
    {
        http ??= client;
        void Report(string status, double? percent) => progress?.Report(new(status, percent));
        string? dir = null;
        bool published = false;
        try
        {
            Report("Checking for import tool updates…", null);
            var check = await Inspect(ct, http, readVersion);
            var (yt, deno, previous, ytTag, denoTag, latestYt, latestDeno, currentYt, currentDeno) = check;
            bool updateYt = currentYt == null || currentYt < latestYt, updateDeno = currentDeno == null || currentDeno < latestDeno;
            if (!updateYt && !updateDeno) { Report("You're up to date · import tools need no update.", 100); return; }
            dir = Path.Combine(Paths.ToolSets, Guid.NewGuid().ToString()); Directory.CreateDirectory(dir);
            var ya = Asset(yt, "yt-dlp.exe"); var da = Asset(deno, "deno-x86_64-pc-windows-msvc.zip");
            string? yhash = updateYt ? Digest(ya) : null, dhash = updateDeno ? Digest(da) : null;
            long Size(JsonElement asset) => asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long size) && size > 0 ? size : 0;
            long ys = updateYt ? Size(ya) : 0, ds = updateDeno ? Size(da) : 0, total = (!updateYt || ys > 0) && (!updateDeno || ds > 0) ? ys + ds : 0;
            void Downloading(string name, long previous, long received) {
                double? percent = total > 0 ? Math.Clamp((previous + received) * 90.0 / total, 0, 90) : null;
                Report("Downloading " + name + (percent.HasValue ? "… " + percent.Value.ToString("0") + "%" : "…"), percent);
            }
            if (updateYt) await Download(ya.GetProperty("browser_download_url").GetString()!, Path.Combine(dir, "yt-dlp.exe"), yhash!, ys, http, received => Downloading("video importer", 0, received), ct);
            else File.Copy(Path.Combine(previous, "yt-dlp.exe"), Path.Combine(dir, "yt-dlp.exe"));
            if (updateDeno) {
                string zip = Path.Combine(dir, "deno.zip"); await Download(da.GetProperty("browser_download_url").GetString()!, zip, dhash!, ds, http, received => Downloading("video runtime", ys, received), ct);
                Report("Unpacking and checking import tools…", 92);
                using (var archive = ZipFile.OpenRead(zip))
                { var entry = archive.GetEntry("deno.exe") ?? throw new InvalidDataException("Deno executable missing."); entry.ExtractToFile(Path.Combine(dir, "deno.exe")); }
                File.Delete(zip);
            } else File.Copy(Path.Combine(previous, "deno.exe"), Path.Combine(dir, "deno.exe"));
            Report("Testing updated import tools…", 96);
            foreach (string name in new[] { "yt-dlp.exe", "deno.exe" }) {
                if (verifyVersion != null) await verifyVersion(Path.Combine(dir, name), ct);
                else await MediaTools.Run(Path.Combine(dir, name), ["--version"], ct);
            }
            await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new { yt = updateYt ? ytTag : currentYt!.ToString(), deno = updateDeno ? denoTag : currentDeno!.ToString(), ytSha256 = yhash, denoZipSha256 = dhash }), ct);
            ct.ThrowIfCancellationRequested(); Report("Finishing import tool update…", 99);
            await File.WriteAllTextAsync(Path.Combine(Paths.Root, "previous-toolset.txt"), previous, ct);
            string pointer = Paths.ToolPointer + ".new"; await File.WriteAllTextAsync(pointer, dir, ct); File.Move(pointer, Paths.ToolPointer, true); published = true;
            Report("Import tools updated · ready to use.", 100);
        }
        finally { if (!published && dir != null) Directory.Delete(dir, true); }
    }
    sealed record ToolCheck(JsonElement Yt, JsonElement Deno, string Previous, string YtTag, string DenoTag, Version LatestYt, Version LatestDeno, Version? CurrentYt, Version? CurrentDeno);
    static async Task<ToolCheck> Inspect(CancellationToken ct, HttpClient http, Func<string, CancellationToken, Task<string>>? readVersion)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var yt = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest", timeout.Token));
        using var deno = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/denoland/deno/releases/latest", timeout.Token));
        string previous = Paths.Tools;
        string ytTag = yt.RootElement.GetProperty("tag_name").GetString()!, denoTag = deno.RootElement.GetProperty("tag_name").GetString()!;
        Version? latestYt = ParseVersion(ytTag), latestDeno = ParseVersion(denoTag);
        if (latestYt == null || latestDeno == null) throw new InvalidDataException("The import tool release versions could not be checked.");
        var currentYt = await InstalledVersion(Path.Combine(previous, "yt-dlp.exe"), readVersion, ct);
        var currentDeno = await InstalledVersion(Path.Combine(previous, "deno.exe"), readVersion, ct);
        return new(yt.RootElement.Clone(), deno.RootElement.Clone(), previous, ytTag, denoTag, latestYt, latestDeno, currentYt, currentDeno);
    }
    public static async Task<bool> CheckAvailable(CancellationToken ct, HttpClient? http = null, Func<string, CancellationToken, Task<string>>? readVersion = null)
    {
        var check = await Inspect(ct, http ?? client, readVersion);
        return check.CurrentYt == null || check.CurrentDeno == null || check.CurrentYt < check.LatestYt || check.CurrentDeno < check.LatestDeno;
    }
    static Version? ParseVersion(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(?:deno\s+|v)?(\d+\.\d+(?:\.\d+){0,2})(?=\s|$)");
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision)) : null;
    }
    static async Task<Version?> InstalledVersion(string path, Func<string, CancellationToken, Task<string>>? read, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { return ParseVersion(read != null ? await read(path, timeout.Token) : await MediaTools.Run(path, ["--version"], timeout.Token)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { AppLog.Warn("tools.version", ex); return null; }
    }
    static JsonElement Asset(JsonElement release, string name) => release.GetProperty("assets").EnumerateArray().First(a => a.GetProperty("name").GetString() == name);
    static string Digest(JsonElement asset)
    {
        string? digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
        if (digest == null || !digest.StartsWith("sha256:")) throw new InvalidDataException("Release has no SHA-256 digest. Update cancelled; existing tools retained.");
        return digest[7..];
    }
    static async Task Download(string url, string path, string expected, long expectedSize, HttpClient http, Action<long> progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com") throw new InvalidDataException("Unexpected download origin.");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        long? length = expectedSize > 0 ? expectedSize : response.Content.Headers.ContentLength;
        if (expectedSize > 0 && response.Content.Headers.ContentLength is long size && size != expectedSize) throw new InvalidDataException("Import tool download size changed.");
        await using (var stream = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(path)) {
            var buffer = new byte[131072]; long received = 0; long reported = -1;
            progress(0);
            for (int read; (read = await stream.ReadAsync(buffer, ct)) != 0;) {
                received += read; if (length.HasValue && received > length.Value) throw new InvalidDataException("Import tool download exceeds its expected size.");
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                long percent = length > 0 ? received * 100 / length.Value : received / 262144;
                if (percent != reported) { reported = percent; progress(received); }
            }
            if (length.HasValue && received != length.Value) throw new InvalidDataException("Import tool download is incomplete.");
            progress(received);
        }
        await using var check = File.OpenRead(path); string hash = Convert.ToHexString(await SHA256.HashDataAsync(check, ct));
        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Downloaded tool checksum mismatch. Existing tools retained.");
    }
    public static async Task Rollback()
    {
        string prev = Path.Combine(Paths.Root, "previous-toolset.txt");
        if (!File.Exists(prev)) throw new InvalidOperationException("No previous downloader toolset is available.");
        string target = (await File.ReadAllTextAsync(prev)).Trim(), current = Paths.Tools;
        if (!File.Exists(Path.Combine(target, "yt-dlp.exe")) || !File.Exists(Path.Combine(target, "deno.exe"))) throw new InvalidDataException("Previous toolset is missing.");
        string pointer = Paths.ToolPointer + ".new"; await File.WriteAllTextAsync(pointer, target); File.Move(pointer, Paths.ToolPointer, true); await File.WriteAllTextAsync(prev, current);
    }
}
