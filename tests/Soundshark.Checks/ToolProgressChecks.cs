using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Soundshark;

internal static partial class Program
{
    sealed class ToolReports(Action<ToolUpdateProgress>? report = null) : IProgress<ToolUpdateProgress>
    {
        public List<ToolUpdateProgress> Values { get; } = [];
        public void Report(ToolUpdateProgress value) { Values.Add(value); report?.Invoke(value); }
    }
    static async Task ToolProgressChecks()
    {
        byte[] video = RandomNumberGenerator.GetBytes(400000), runtime = UpdateZip(("deno.exe", RandomNumberGenerator.GetBytes(16000)));
        string Json(string name, byte[] bytes, bool bad = false) => JsonSerializer.Serialize(new { tag_name = "v99.0.0", assets = new[] { new { name, size = bytes.Length, digest = "sha256:" + (bad ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes))), browser_download_url = "https://github.com/fixture/releases/download/test/" + name } } });
        var downloads = new List<string>();
        HttpClient Http(bool bad = false) => new(new UpdateHttp((request, _) => {
            string url = request.RequestUri!.AbsoluteUri;
            if (request.RequestUri.Host != "api.github.com") downloads.Add(url);
            HttpContent content = request.RequestUri.Host == "api.github.com" ? new StringContent(url.Contains("yt-dlp") ? Json("yt-dlp.exe", video, bad) : Json("deno-x86_64-pc-windows-msvc.zip", runtime)) : new ByteArrayContent(url.EndsWith("yt-dlp.exe") ? video : runtime);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        string previous = Paths.Tools; var reports = new ToolReports(); var checkedTools = new List<string>();
        using (var http = Http()) await ToolUpdater.Update(CancellationToken.None, reports, http, (path, _) => { Check(Paths.Tools == previous, "import tools stay unchanged until both downloaded tools pass their tests"); checkedTools.Add(Path.GetFileName(path)); return Task.CompletedTask; }, readVersion: (_, _) => Task.FromResult("1.0.0"));
        Check(checkedTools.SequenceEqual(new[] { "yt-dlp.exe", "deno.exe" }) && Paths.Tools != previous, "verified video importer and runtime are activated together");
        var percentages = reports.Values.Where(p => p.Percent.HasValue).Select(p => p.Percent!.Value).ToArray();
        Check(percentages.Length > 5 && percentages.SequenceEqual(percentages.Order()) && percentages.Last() == 100, "combined tool download and validation progress is monotonic and finishes at 100 percent");
        Check(reports.Values[0].Percent == null && reports.Values.Any(p => p.Status.Contains("video importer")) && reports.Values.Any(p => p.Status.Contains("video runtime")) && reports.Values.Any(p => p.Status.Contains("Testing")), "tool progress identifies checking downloads and smoke-test stages");
        string activated = Paths.Tools, rollbackPointer = await File.ReadAllTextAsync(Path.Combine(Paths.Root, "previous-toolset.txt"));
        int count = Directory.GetDirectories(Paths.ToolSets).Length;
        foreach (string version in new[] { "99.0.0", "100.0.0" }) {
            downloads.Clear(); var current = new ToolReports();
            using (var http = Http()) await ToolUpdater.Update(CancellationToken.None, current, http, (_, _) => Task.CompletedTask, (_, _) => Task.FromResult("deno " + version + " (stable)\nother output"));
            Check(downloads.Count == 0 && Paths.Tools == activated && Directory.GetDirectories(Paths.ToolSets).Length == count && await File.ReadAllTextAsync(Path.Combine(Paths.Root, "previous-toolset.txt")) == rollbackPointer, "current or newer tool versions skip all downloads staging and rollback-pointer changes");
            Check(current.Values.Last().Status.Contains("You're up to date") && current.Values.Last().Percent == 100, "unchanged import tools finish with an up-to-date result");
        }
        downloads.Clear();
        using (var http = Http()) Check(!await ToolUpdater.CheckAvailable(CancellationToken.None, http, (_, _) => Task.FromResult("99.0.0")) && downloads.Count == 0, "startup import tool check reads release metadata and versions without downloading assets");
        using (var http = Http()) Check(await ToolUpdater.CheckAvailable(CancellationToken.None, http, (_, _) => Task.FromResult("98.0.0")) && downloads.Count == 0, "startup metadata check identifies an available tool update without installing it");
        foreach (string outdated in new[] { "yt-dlp.exe", "deno.exe" }) {
            downloads.Clear(); string retained = outdated == "yt-dlp.exe" ? "deno.exe" : "yt-dlp.exe";
            byte[] retainedBytes = await File.ReadAllBytesAsync(Path.Combine(Paths.Tools, retained));
            using (var http = Http()) await ToolUpdater.Update(CancellationToken.None, null, http, (_, _) => Task.CompletedTask, (path, _) => Task.FromResult(Path.GetFileName(path) == outdated ? "98.0.0" : "99.0.0"));
            Check(downloads.Count == 1 && downloads[0].EndsWith(outdated == "deno.exe" ? ".zip" : "yt-dlp.exe") && (await File.ReadAllBytesAsync(Path.Combine(Paths.Tools, retained))).SequenceEqual(retainedBytes), "only the outdated " + outdated + " downloads; the current companion is reused byte-for-byte");
            await ToolUpdater.Rollback();
        }
        await File.WriteAllTextAsync(Path.Combine(Paths.Root, "previous-toolset.txt"), previous);
        await ToolUpdater.Rollback(); Check(Paths.Tools == previous, "rollback remains available after a tool update with progress");
        using (var cancel = new CancellationTokenSource()) using (var http = Http()) {
            var cancelled = new ToolReports(p => { if (p.Percent > 0) cancel.Cancel(); });
            try { await ToolUpdater.Update(cancel.Token, cancelled, http, (_, _) => Task.CompletedTask, readVersion: (_, _) => Task.FromResult("1.0.0")); throw new Exception("cancelled update completed"); }
            catch (OperationCanceledException) { Check(Paths.Tools == previous && !cancelled.Values.Any(p => p.Percent == 100), "cancelled download never activates partial tools or reports success"); }
        }
        using (var http = Http(bad: true)) {
            var failed = new ToolReports();
            try { await ToolUpdater.Update(CancellationToken.None, failed, http, (_, _) => Task.CompletedTask, readVersion: (_, _) => Task.FromResult("1.0.0")); throw new Exception("corrupt update accepted"); }
            catch (InvalidDataException) { Check(Paths.Tools == previous && !failed.Values.Any(p => p.Percent == 100), "checksum failure retains previous tools and never reports a completed update"); }
        }
    }
    static async Task ToolProgressUiChecks(MainWindow window)
    {
        var complete = new TaskCompletionSource<bool>();
        var dialog = new ToolUpdateWindow(window, async (ct, p) => { p.Report(new("Downloading video runtime… 35%", 35)); await complete.Task.WaitAsync(ct); p.Report(new("Import tools updated · ready to use.", 100)); }) { ShowActivated = false };
        dialog.Show(); var deadline = DateTime.UtcNow.AddSeconds(5);
        var bar = Descendants<ProgressBar>(dialog).Single();
        while (bar.Value != 35 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Check(bar.Value == 35 && !bar.IsIndeterminate && !window.Available, "import tool window displays live progress while locking conflicting operations");
        var url = Descendants<ProgressBar>(window.Studio).Single(b => b.Name == "UrlImportProgress");
        Check(bar.Name == "ImportToolsDownloadProgress" && bar.Height == url.Height && bar.Foreground == url.Foreground && bar.Background == url.Background && bar.Effect is System.Windows.Media.Effects.DropShadowEffect, "import tools use the same green colors height and glow as URL downloads");
        Snapshot(dialog, "import-tools-downloading.png"); complete.TrySetResult(true);
        while (!window.Available && DateTime.UtcNow < deadline) await Task.Delay(20);
        Check(window.Available && bar.Value == 100 && !bar.IsIndeterminate, "completed import update shows a full bar and restores app controls"); Snapshot(dialog, "import-tools-complete.png"); dialog.Close();
        var cancelled = new ToolUpdateWindow(window, async (ct, p) => { p.Report(new("Downloading video importer… 20%", 20)); await Task.Delay(30000, ct); }) { ShowActivated = false };
        cancelled.Show(); await Task.Delay(60);
        Descendants<Button>(cancelled).Single(b => b.Content is string s && s == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        deadline = DateTime.UtcNow.AddSeconds(5); while (!window.Available && DateTime.UtcNow < deadline) await Task.Delay(20);
        Check(window.Available && Descendants<TextBlock>(cancelled).Any(t => t.Text.Contains("Update cancelled")) && Descendants<ProgressBar>(cancelled).Single().Value < 100, "Cancel stops a tool update and leaves a clear non-success status");
        cancelled.Close();
        var current = new ToolUpdateWindow(window, (_, p) => { p.Report(new("You're up to date · import tools need no update.", 100)); return Task.CompletedTask; }) { ShowActivated = false };
        current.Show(); deadline = DateTime.UtcNow.AddSeconds(5);
        while ((!window.Available || !Descendants<TextBlock>(current).Any(t => t.Text.Contains("You're up to date"))) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Check(Descendants<TextBlock>(current).Any(t => t.Text.Contains("You're up to date")), "up-to-date import tool dialog retains its honest no-install completion message"); current.Close();
        var appButton = (Button)window.FindName("UpdateAppButton"); var toolsButton = (Button)window.FindName("UpdateTools");
        foreach (var button in new[] { appButton, toolsButton }) {
            UpdateHint.Set(button, true, false);
            Check(button.Effect is System.Windows.Media.Effects.DropShadowEffect glow && glow.HasAnimatedProperties && button.Background == window.FindResource("SuccessSurface"), "available update button uses a green breathing glow");
        }
        window.UpdateLayout(); Snapshot(window, "available-update-glows.png");
        foreach (var button in new[] { appButton, toolsButton }) {
            UpdateHint.Set(button, true, true);
            Check(button.Effect is System.Windows.Media.Effects.DropShadowEffect glow && !glow.HasAnimatedProperties, "Performance Mode keeps the available update glow steady");
            UpdateHint.Set(button, false, false);
            Check(button.Effect == null && button.Background == window.FindResource("AccentSurface"), "current update button clears its glow and restores normal styling");
        }
    }
}
