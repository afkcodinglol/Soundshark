using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Soundshark;

internal static partial class Program
{
    static async Task RollbackChecks()
    {
        string root = Path.Combine(Paths.Cache, "rollback-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "App"), stage = Path.Combine(root, "Files"), history = Path.Combine(root, "Updates"), first = Path.Combine(history, "first"), second = Path.Combine(history, "second");
        Directory.CreateDirectory(target); Directory.CreateDirectory(stage); Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        Directory.CreateDirectory(Path.Combine(target, "Library"));
        await File.WriteAllTextAsync(Path.Combine(target, "Library", "keep.mp3"), "sound");
        await File.WriteAllTextAsync(Path.Combine(target, "settings.json"), "preferences");
        await File.WriteAllTextAsync(Path.Combine(target, "obsolete.dll"), "old dependency");
        var files = new List<UpdateFile>();
        foreach (string name in new[] { "Soundshark.exe", "Soundshark.dll", "Soundshark.Core.dll" }) {
            byte[] bytes = name.EndsWith(".dll") ? await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, name)) : [1, 2, 3];
            await File.WriteAllBytesAsync(Path.Combine(target, name), bytes);
            await File.WriteAllBytesAsync(Path.Combine(stage, name), bytes);
            files.Add(new(name, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length));
        }
        async Task Apply(string directory, string payload, UpdateFile[] entries, string? suppress = null) {
            string plan = Path.Combine(directory, "plan.json");
            await File.WriteAllTextAsync(plan, JsonSerializer.Serialize(new UpdatePlan(target, payload, entries, int.MaxValue, DateTime.UtcNow.ToString("O"), false, suppress, Path.Combine(root, "check.txt"))));
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "Updater", "Apply-AppUpdate.ps1"), "-PlanPath", plan }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Check(process.ExitCode == 0, "rollback fixture helper completes a real program replacement");
        }
        await Apply(first, stage, files.ToArray());
        var choice = AppRollback.Find(target, history, new Version(99, 0))!;
        Check(choice != null && choice.Backup.Version == AppUpdates.CurrentVersion.ToString(), "successful update records the prior app version and complete hash manifest");
        Check(AppRollback.Find(Path.Combine(root, "OtherApp"), history, new Version(99, 0)) == null, "rollback backups cannot cross installed and portable application paths");
        Check(AppRollback.Find(target, history, AppUpdates.CurrentVersion) == null, "rollback chooser excludes equal and newer versions");
        await AppRollback.Verify(choice!, CancellationToken.None);
        Check(true, "complete local backup passes hashes and actual assembly version verification");
        string obsolete = Path.Combine(first, "Backup", "obsolete.dll"); await File.WriteAllTextAsync(obsolete, "tampered");
        try { await AppRollback.Verify(choice!, CancellationToken.None); throw new Exception("changed backup accepted"); }
        catch (InvalidDataException) { Check(true, "changed rollback backup is rejected before app shutdown"); }
        await File.WriteAllTextAsync(obsolete, "old dependency");
        await Apply(second, Path.Combine(first, "Backup"), choice!.Backup.Files, "99.0.0");
        Check(File.Exists(Path.Combine(target, "obsolete.dll")) && await File.ReadAllTextAsync(Path.Combine(target, "Library", "keep.mp3")) == "sound" && await File.ReadAllTextAsync(Path.Combine(target, "settings.json")) == "preferences", "explicit rollback restores old dependencies while preserving Library sounds and settings");
        Check(AppRollback.IsHeld(target, new Version(99, 0, 0)) && !AppRollback.IsHeld(target, new Version(100, 0, 0)), "rollback records the skipped version for automatic update checks");
        Check(File.GetLastWriteTimeUtc(Path.Combine(root, "check.txt")) > DateTime.UtcNow.AddYears(90), "rollback also pauses legacy automatic checks without blocking manual checks");
        AppRollback.ResumeIfReturned(target, new Version(99, 0, 0));
        Check(!File.Exists(Path.Combine(target, AppRollback.HoldFile)), "returning to the skipped version clears the rollback hold");
    }
    static async Task RollbackUiChecks(MainWindow window)
    {
        var update = (Button)window.FindName("UpdateAppButton");
        var log = (Button)window.FindName("UpdateLogButton"); var rollback = (Button)window.FindName("RollbackAppButton");
        Check(Equals(update.Content,"Update Soundshark") && update.ActualWidth==log.ActualWidth && update.ActualHeight==log.ActualHeight && update.ActualWidth==rollback.ActualWidth, "Update Soundshark retains its label and matches the other footer action sizes");
        var dialog = new AppRollbackWindow(window) { ShowActivated = false }; dialog.Show(); await Task.Delay(80);
        Check(Descendants<TextBlock>(dialog).Any(t => t.Text.Contains("No previous version")) && !Descendants<Button>(dialog).Single(b => b.Content is string s && s == "Restore And Restart").IsEnabled, "rollback dialog explains an unavailable backup and disables restoration");
        Snapshot(dialog, "rollback-no-backup.png"); dialog.Close();
    }
}
