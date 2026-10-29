using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Soundshark;

internal static partial class Program
{
    sealed class UpdateHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    static byte[] UpdateZip(params (string Name, byte[] Bytes)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) foreach (var entry in entries) { using var stream = zip.CreateEntry(entry.Name).Open(); stream.Write(entry.Bytes); }
        return memory.ToArray();
    }
    static string ReleaseJson(byte[] zip, string? digest = null, string? url = null, bool draft = false, bool prerelease = false, string? version = null) => JsonSerializer.Serialize(new {
        tag_name = "v" + (version ?? AppUpdates.CurrentVersion.ToString(3)), draft, prerelease, body = "Fixture release notes", assets = new[] {
            new { name = AppUpdates.AssetName, size = zip.Length, digest = "sha256:" + (digest ?? Convert.ToHexString(SHA256.HashData(zip))), browser_download_url = url ?? "https://github.com/fixture/Soundshark/releases/download/v1.8.0/" + AppUpdates.AssetName }
        }
    });
    sealed class PacedUpdateStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(15, ct); return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 4096)], ct); }
    }
    static HttpClient FixtureHttp(string json, byte[] zip, bool slow = false) => new(new UpdateHttp((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
        Content = request.RequestUri!.Host == "api.github.com" ? new StringContent(json) : slow ? new StreamContent(new PacedUpdateStream(zip)) : new ByteArrayContent(zip)
    })));
    static async Task AppUpdateChecks()
    {
        byte[] package = UpdateZip(("Soundshark.exe", [1, 2, 3]), ("Soundshark.dll", await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Soundshark.dll"))), ("Soundshark.Core.dll", await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Soundshark.Core.dll"))));
        string release = ReleaseJson(package); var old = new Version(1, 7, 6);
        using (var json = JsonDocument.Parse(release)) {
            Check(AppUpdates.ParseRelease(json.RootElement, "fixture/Soundshark", old)?.Version.ToString() == AppUpdates.CurrentVersion.ToString(3), "updater accepts a newer stable release from its configured repository");
            Check(AppUpdates.ParseRelease(json.RootElement, "fixture/Soundshark", AppUpdates.CurrentVersion) == null, "three-part and four-part equal versions do not cause an update loop");
            Check(AppUpdates.ParseRelease(json.RootElement, "fixture/Soundshark", new Version(99, 0)) == null, "updater never downgrades a newer installation");
        }
        foreach (string jsonText in new[] { ReleaseJson(package, draft: true), ReleaseJson(package, prerelease: true) }) {
            using var json = JsonDocument.Parse(jsonText); Check(AppUpdates.ParseRelease(json.RootElement, "fixture/Soundshark", old) == null, "unpublished and prerelease builds are excluded");
        }
        foreach (string url in new[] { "http://github.com/fixture/Soundshark/releases/download/v1.8.0/" + AppUpdates.AssetName, "https://github.com/attacker/Soundshark/releases/download/v1.8.0/" + AppUpdates.AssetName, "https://example.com/" + AppUpdates.AssetName }) {
            using var json = JsonDocument.Parse(ReleaseJson(package, url: url));
            try { AppUpdates.ParseRelease(json.RootElement, "fixture/Soundshark", old); throw new Exception("unsafe source accepted"); } catch (InvalidDataException) { Check(true, "unexpected repository host and insecure app download URLs are rejected"); }
        }
        using (var json = JsonDocument.Parse(ReleaseJson(package, digest: "bad"))) {
            try { AppUpdates.ParseRelease(json.RootElement, "fixture/Soundshark", old); throw new Exception("missing hash accepted"); } catch (InvalidDataException) { Check(true, "an app release needs a complete SHA-256 digest"); }
        }
        using (var http = FixtureHttp(release, package)) {
            await File.WriteAllTextAsync(Path.Combine(Paths.Root, "app-update-check.txt"), "recent startup check");
            var updates = new AppUpdates("fixture/Soundshark", http, old); await updates.Check();
            Check(updates.Ready && updates.Status.Contains("installs when Soundshark closes"), "verified app package downloads and stages without touching the running app");
            Check(updates.ProgressPercent == 100, "download progress reaches 100 percent before staging completes"); await updates.Stop();
        }
        using (var http = FixtureHttp(ReleaseJson(package, digest: new string('0', 64)), package)) {
            var updates = new AppUpdates("fixture/Soundshark", http, old); await updates.Check(manual: true);
            Check(!updates.Ready && updates.Status.Contains("current version is unchanged"), "checksum failure cannot arm an app update"); await updates.Stop();
        }
        using (var http = FixtureHttp(release, package[..^2])) {
            var updates = new AppUpdates("fixture/Soundshark", http, old); await updates.Check(manual: true);
            Check(!updates.Ready, "truncated downloads leave the current application untouched"); await updates.Stop();
        }
        using (var http = new HttpClient(new UpdateHttp((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))))) {
            var updates = new AppUpdates("fixture/Soundshark", http, old); await updates.Check(manual: true);
            Check(!updates.Ready && updates.Status.Contains("No app update"), "an empty release repository produces a clean no-release status"); await updates.Stop();
        }
        using (var http = new HttpClient(new UpdateHttp(async (_, ct) => { await Task.Delay(30000, ct); return new HttpResponseMessage(HttpStatusCode.OK); }))) {
            var updates = new AppUpdates("fixture/Soundshark", http, old); var pending = updates.Check(manual: true); await Task.Delay(50); await updates.Stop(); await pending;
            Check(!updates.Ready && !updates.Busy, "closing cancels an unfinished update check without staging or hanging");
        }
        foreach (string unsafePath in new[] { "../escaped.txt", "Library/user.mp3", "settings.json", "C:/outside.txt", "folder/../outside.txt", "folder./file.dll", "folder//file.dll" }) {
            string zip = Path.Combine(Paths.Cache, Guid.NewGuid() + ".zip"); await File.WriteAllBytesAsync(zip, UpdateZip((unsafePath, [1])));
            try { await AppUpdates.Extract(zip, Path.Combine(Paths.Cache, Guid.NewGuid().ToString()), AppUpdates.CurrentVersion, CancellationToken.None); throw new Exception("unsafe archive accepted"); }
            catch (InvalidDataException) { Check(true, "update archive rejects unsafe or user-data path: " + unsafePath); }
        }
        string wrong = Path.Combine(Paths.Cache, "wrong-version.zip"); await File.WriteAllBytesAsync(wrong, package);
        try { await AppUpdates.Extract(wrong, Path.Combine(Paths.Cache, "wrong-version"), new Version(99, 0, 0), CancellationToken.None); throw new Exception("wrong binary version accepted"); }
        catch (InvalidDataException) { Check(true, "the staged executable assembly version must match the release tag"); }
        await UpdateHelperCheck(0); await UpdateHelperCheck(1); await UpdateHelperCheck(2);
        Check(UpdateLogWindow.History.Last().Version == "1.0.0" && UpdateLogWindow.History.First().Version == AppUpdates.CurrentVersion.ToString(3), "offline update history spans the initial release through the current build");
        Check(UpdateLogWindow.History.Select(e => Version.Parse(e.Version)).SequenceEqual(UpdateLogWindow.History.Select(e => Version.Parse(e.Version)).OrderDescending()), "update history is ordered newest first with no invented release dates");
        Console.WriteLine($"Update checks complete; cumulative checks={passed}");
    }
    static async Task UpdateHelperCheck(int failureMode)
    {
        string root = Path.Combine(Paths.Cache, "update-helper-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "App"), stage = Path.Combine(root, "Files"); Directory.CreateDirectory(target); Directory.CreateDirectory(stage);
        Directory.CreateDirectory(Path.Combine(target, "Library")); await File.WriteAllTextAsync(Path.Combine(target, "Library", "keep.mp3"), "user sound");
        await File.WriteAllTextAsync(Path.Combine(target, "settings.json"), "user settings");
        await File.WriteAllTextAsync(Path.Combine(target, "obsolete.dll"), "old dependency");
        await File.WriteAllTextAsync(Path.Combine(target, "unins000.dat"), "installer records");
        if (failureMode == 2) Directory.CreateDirectory(Path.Combine(target, "new.dll"));
        var files = new List<UpdateFile>();
        foreach (string name in new[] { "Soundshark.exe", "Soundshark.dll", "new.dll", "Soundshark.Core.dll" }) {
            if (name != "new.dll") await File.WriteAllTextAsync(Path.Combine(target, name), "old " + name);
            string path = Path.Combine(stage, name); await File.WriteAllTextAsync(path, "new " + name);
            files.Add(new(name, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))), new FileInfo(path).Length));
        }
        string plan = Path.Combine(root, "plan.json");
        await File.WriteAllTextAsync(plan, JsonSerializer.Serialize(new UpdatePlan(target, stage, files.ToArray(), int.MaxValue, DateTime.UtcNow.ToString("O"), false)));
        using var hold = failureMode == 1 ? new FileStream(Path.Combine(target, "Soundshark.Core.dll"), FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "Updater", "Apply-AppUpdate.ps1"), "-PlanPath", plan }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Check(process.ExitCode == (failureMode != 0 ? 1 : 0), failureMode != 0 ? "blocked replacement fails the update helper cleanly" : "detached updater applies a staged app after the original process exits");
        string expected = failureMode != 0 ? "old Soundshark.exe" : "new Soundshark.exe";
        Check(await File.ReadAllTextAsync(Path.Combine(target, "Soundshark.exe")) == expected && File.Exists(Path.Combine(target, "new.dll")) == (failureMode == 0), failureMode != 0 ? "failed replacement restores old files and removes newly added files" : "program files are updated while a backup of the prior version remains");
        Check(File.Exists(Path.Combine(target, "obsolete.dll")) == (failureMode != 0) && await File.ReadAllTextAsync(Path.Combine(target, "unins000.dat")) == "installer records", "clean replacement removes obsolete DLLs, rollback restores them, and uninstaller records survive");
        Check(await File.ReadAllTextAsync(Path.Combine(target, "Library", "keep.mp3")) == "user sound" && await File.ReadAllTextAsync(Path.Combine(target, "settings.json")) == "user settings", "the updater preserves Library audio and user preferences");
    }
    static async Task UpdateUiChecks(MainWindow window)
    {
        var log = (Button)window.FindName("UpdateLogButton"); var app = (Button)window.FindName("UpdateAppButton");
        Check(log.IsVisible && app.IsVisible && log.TransformToAncestor(window).Transform(new Point()).X > app.TransformToAncestor(window).Transform(new Point()).X, "Update Log and Update App are visible at the bottom right");
        var opened = new TaskCompletionSource<bool>();
        _ = window.Dispatcher.BeginInvoke(async () => { await Task.Delay(100); var actual = Application.Current.Windows.OfType<UpdateLogWindow>().FirstOrDefault(); opened.TrySetResult(actual != null); if (actual != null) { Snapshot(actual, "update-log-dark.png"); actual.Close(); } });
        log.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(await opened.Task, "Update Log button opens the offline version history dialog");
        var original = Theme.Current; Theme.Apply(original with { LightMode = true });
        var history = new UpdateLogWindow(window) { ShowActivated = false }; history.Show(); await Task.Delay(80); Snapshot(history, "update-log-light.png");
        Descendants<ScrollViewer>(history).Single().ScrollToEnd(); await Task.Delay(80); Snapshot(history, "update-log-initial-release.png");
        Check(Descendants<TextBlock>(history).Any(t => t.Text.Contains("Initial Soundshark")), "the initial release remains reachable in the scrollable history"); history.Close(); Theme.Apply(original);
        using var http = FixtureHttp(ReleaseJson([1], version: AppUpdates.CurrentVersion.ToString(3)), [1]);
        var updates = new AppUpdates("fixture/Soundshark", http);
        var dialog = new AppUpdateWindow(window, updates) { ShowActivated = false }; dialog.Show(); await updates.Check(manual: true); await Task.Delay(80); Snapshot(dialog, "update-app-current.png");
        Check(Descendants<TextBlock>(dialog).Any(t => t.Text.Contains("You're up to date")), "Update App presents a clean current-version status"); dialog.Close(); await updates.Stop();
        byte[] package = UpdateZip(("Soundshark.exe", [1]), ("Soundshark.dll", await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Soundshark.dll"))), ("Soundshark.Core.dll", await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Soundshark.Core.dll"))));
        using var slow = FixtureHttp(ReleaseJson(package), package, slow: true);
        var downloading = new AppUpdates("fixture/Soundshark", slow, new Version(1, 7, 6));
        var downloadWindow = new AppUpdateWindow(window, downloading) { ShowActivated = false };
        var captured = new TaskCompletionSource<bool>();
        downloading.Changed += () => { if (downloading.ProgressPercent is > 0 and < 100 && !captured.Task.IsCompleted) _ = downloadWindow.Dispatcher.InvokeAsync(() => { Snapshot(downloadWindow, "update-app-downloading.png"); captured.TrySetResult(true); }); };
        downloadWindow.Show(); await downloading.Check(manual: true); await Task.Delay(80);
        Check(await captured.Task.WaitAsync(TimeSpan.FromSeconds(5)), "update download displays the live percentage progress panel");
        var bar = Descendants<ProgressBar>(downloadWindow).Single();
        var urlBar = Descendants<ProgressBar>(window.Studio).Single(b => b.Name == "UrlImportProgress");
        Check(bar.Name == "AppDownloadProgress" && bar.Height == urlBar.Height && bar.Foreground == urlBar.Foreground && bar.Background == urlBar.Background && bar.Effect is System.Windows.Media.Effects.DropShadowEffect, "app download uses the URL import green progress colors, height and glow");
        Snapshot(downloadWindow, "update-app-ready.png"); Check(downloading.Ready && bar.Value == 100 && !bar.IsIndeterminate, "download completion presents a full green bar and a ready update"); downloadWindow.Close(); await downloading.Stop();
    }
}
