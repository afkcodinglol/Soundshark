using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Soundshark;
public sealed record AppRelease(Version Version, Uri Download, string Sha256, long Size, string Notes);
public sealed record UpdateFile(string Relative, string Sha256, long Length);
public sealed record UpdatePlan(string Target, string Stage, UpdateFile[] Files, int ParentPid, string ParentStartedUtc, bool Restart, string? SuppressVersion = null, string? CheckStamp = null);

/// <summary>Only public, stable releases from the packaged repository are trusted.</summary>
public sealed class AppUpdates
{
    public static Version CurrentVersion => typeof(App).Assembly.GetName().Version!;
    public const string AssetName = "Soundshark-portable-x64.zip";
    const long MaxDownload = 1024L * 1024 * 1024, MaxExpanded = 2L * 1024 * 1024 * 1024;
    readonly HttpClient client;
    readonly Version installedVersion;
    readonly SemaphoreSlim gate = new(1);
    readonly CancellationTokenSource lifetime = new();
    readonly object sync = new();
    Task active = Task.CompletedTask;
    string? ready;
    UpdateFile[] files = [];
    public string Repository { get; }
    public string Status { get; private set; } = "Updates check automatically when Soundshark opens.";
    public AppRelease? Release { get; private set; }
    public bool Ready => ready != null;
    public bool Busy => gate.CurrentCount == 0;
    public int? ProgressPercent { get; private set; }
    public bool RestartAfterInstall { get; set; }
    public event Action? Changed;
    public AppUpdates(string? repository = null, HttpClient? http = null, Version? installedVersion = null)
    {
        if (repository == null) {
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "update-source.json")));
            repository = config.RootElement.GetProperty("Repository").GetString();
        }
        Repository = ValidateRepository(repository ?? "");
        this.installedVersion = installedVersion ?? CurrentVersion;
        if (installedVersion == null) AppRollback.ResumeIfReturned(AppContext.BaseDirectory, this.installedVersion);
        client = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Soundshark/" + CurrentVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }
    public static string ValidateRepository(string value) => Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$") && !value.Contains("..") ? value : throw new InvalidDataException("The update source is not configured correctly.");
    void Report(string message) { Status = message; Changed?.Invoke(); }
    public Task Check(bool manual = false) { lock (sync) { if (!active.IsCompleted) return active; return active = Task.Run(() => CheckCore(manual)); } }
    async Task CheckCore(bool manual)
    {
        if (!await gate.WaitAsync(0)) return;
        string? stage = null;
        try {
            if (Ready) { Report("Update ready · " + Release!.Version + " · installs when Soundshark closes."); return; }
            string stamp = Path.Combine(Paths.Root, "app-update-check.txt");
            if (!manual && File.Exists(stamp) && File.GetLastWriteTimeUtc(stamp) > DateTime.UtcNow) { Report("Automatic updates paused after rollback · use Update Soundshark to update again."); return; }
            ProgressPercent = null; Report("Checking for app updates…");
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); checkTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await client.GetAsync("https://api.github.com/repos/" + Repository + "/releases/latest", checkTimeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) { Report("No app update has been published yet."); return; }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 2_000_000) throw new InvalidDataException("Update information is too large.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(checkTimeout.Token));
            Release = ParseRelease(json.RootElement, Repository, installedVersion);
            if (!manual && Release != null && AppRollback.IsHeld(AppContext.BaseDirectory, Release.Version)) { Report("Rolled-back version kept · use Update Soundshark to update again."); Release = null; return; }
            if (Release == null) { Report("You're up to date · " + CurrentVersion.ToString(3)); await File.WriteAllTextAsync(stamp, "Current", lifetime.Token); return; }
            Report("Downloading " + Release.Version + "… 0%");
            stage = Path.Combine(Paths.Root, "Updates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            string archive = Path.Combine(stage, "update.zip");
            await Download(Release, archive, lifetime.Token);
            Report("Checking update files…");
            string payload = Path.Combine(stage, "Files");
            files = await Extract(archive, payload, Release.Version, lifetime.Token);
            File.Delete(archive); lifetime.Token.ThrowIfCancellationRequested();
            ready = payload; stage = null;
            Report("Update ready · " + Release.Version + " · installs when Soundshark closes.");
        } catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) Report("The update check timed out. Try again later."); }
        catch (Exception ex) { AppLog.Error("app.update", ex); Report("Couldn't get the app update. Your current version is unchanged. Try again later."); }
        finally { if (stage != null) try { Directory.Delete(stage, true); } catch (Exception ex) { AppLog.Warn("app.update.cleanup", ex); } gate.Release(); Changed?.Invoke(); }
    }
    public static AppRelease? ParseRelease(JsonElement json, string repository, Version current)
    {
        ValidateRepository(repository);
        if (json.GetProperty("draft").GetBoolean() || json.GetProperty("prerelease").GetBoolean()) return null;
        string tag = json.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var version)) throw new InvalidDataException("The release version is invalid.");
        static Version Comparable(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        if (Comparable(version) <= Comparable(current)) return null;
        var assets = json.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == AssetName).ToArray();
        if (assets.Length != 1) throw new InvalidDataException("The release is missing its app update package.");
        var asset = assets[0]; string digest = asset.GetProperty("digest").GetString() ?? "";
        if (!Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$")) throw new InvalidDataException("The update package has no valid checksum.");
        var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
        string prefix = "/" + repository + "/releases/download/";
        if (url.Scheme != "https" || url.Host != "github.com" || !url.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 || !url.AbsolutePath.EndsWith("/" + AssetName, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected update download source.");
        long size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaxDownload) throw new InvalidDataException("The update package size is invalid.");
        return new(version, url, digest[7..], size, json.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "");
    }
    async Task Download(AppRelease release, string destination, CancellationToken ct)
    {
        using var response = await client.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new InvalidDataException("The update size changed.");
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(destination)) {
            var buffer = new byte[131072]; long total = 0; int shown = -1;
            for (int read; (read = await input.ReadAsync(buffer, ct)) != 0;) {
                total += read; if (total > release.Size) throw new InvalidDataException("The update exceeds its expected size.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                int percent = (int)(total * 100 / release.Size); if (percent != shown) { shown = percent; ProgressPercent = percent; Report("Downloading " + release.Version + "… " + percent + "%"); }
            }
            if (total != release.Size) throw new InvalidDataException("The update download is incomplete.");
        }
        await using var verify = File.OpenRead(destination);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(verify, ct)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update checksum does not match.");
    }
    public static async Task<UpdateFile[]> Extract(string archive, string target, Version version, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive); if (zip.Entries.Count > 5000) throw new InvalidDataException("Too many update files.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var result = new List<UpdateFile>(); long total = 0;
        Directory.CreateDirectory(target); string prefix = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries) {
            ct.ThrowIfCancellationRequested(); string relative = entry.FullName.Replace('\\', '/');
            if (relative.StartsWith('/') || relative.TrimEnd('/').Split('/').Any(p => p.Length == 0 || p is ".." or "." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith('.') || p.EndsWith(' ')) || (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000) throw new InvalidDataException("Unsafe update archive path.");
            string first = relative.Split('/')[0];
            if (new[] { "Library", "Workspaces", "Logs", "ToolSets", "Updates", "settings.json", "app-update-hold.json", ".git" }.Contains(first, StringComparer.OrdinalIgnoreCase) || Regex.IsMatch(first, @"^unins[0-9]+\.(exe|dat|msg)$", RegexOptions.IgnoreCase)) throw new InvalidDataException("The update contains user-data paths or installer records.");
            if (relative.EndsWith('/')) continue;
            if (!paths.Add(relative)) throw new InvalidDataException("Duplicate update path.");
            total += entry.Length; if (total > MaxExpanded || entry.Length > MaxDownload) throw new InvalidDataException("Expanded update is too large.");
            string path = Path.GetFullPath(Path.Combine(target, relative)); if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update path escapes staging.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var input = entry.Open()) await using (var output = File.Create(path)) {
                var buffer = new byte[131072]; long written = 0;
                for (int read; (read = await input.ReadAsync(buffer, ct)) != 0;) { written += read; if (written > entry.Length) throw new InvalidDataException("Expanded file exceeds its declared size."); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
                if (written != entry.Length) throw new InvalidDataException("Incomplete update file.");
            }
            await using var check = File.OpenRead(path); result.Add(new(relative, Convert.ToHexString(await SHA256.HashDataAsync(check, ct)), entry.Length));
        }
        if (!paths.Contains("Soundshark.exe") || !paths.Contains("Soundshark.dll") || !paths.Contains("Soundshark.Core.dll")) throw new InvalidDataException("The update app files are missing.");
        var actual = System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(target, "Soundshark.dll")).Version!;
        if (actual.Major != version.Major || actual.Minor != version.Minor || actual.Build != version.Build) throw new InvalidDataException("The package version does not match the release.");
        return result.ToArray();
    }
    public async Task Stop() { lifetime.Cancel(); try { await active; } catch (Exception ex) { AppLog.Warn("app.update.stop", ex); } }
    public void ApplyOnExit()
    {
        if (!Ready) return;
        string stage = Path.GetDirectoryName(ready!)!;
        LaunchPlan(stage, AppContext.BaseDirectory, ready!, files, RestartAfterInstall);
    }
    internal static void LaunchPlan(string stage, string target, string payload, UpdateFile[] files, bool restart, string? suppressVersion = null)
    {
        string helper = Path.Combine(stage, "Apply-AppUpdate.ps1");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Updater", "Apply-AppUpdate.ps1"), helper, true);
        using var process = Process.GetCurrentProcess();
        string plan = Path.Combine(stage, "plan.json");
        File.WriteAllText(plan, JsonSerializer.Serialize(new UpdatePlan(target, payload, files, Environment.ProcessId, process.StartTime.ToUniversalTime().ToString("O"), restart, suppressVersion, Path.Combine(Paths.Root, "app-update-check.txt"))));
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper, "-PlanPath", plan }) start.ArgumentList.Add(arg);
        Process.Start(start)?.Dispose();
    }
}
