using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Soundshark;
public sealed record AppBackup(string Target, string Version, UpdateFile[] Files);
public sealed record RollbackChoice(string Folder, AppBackup Backup);

public static class AppRollback
{
    public const string HoldFile = "app-update-hold.json";
    public static void ResumeIfReturned(string target, Version current)
    {
        string hold = Path.Combine(target, HoldFile);
        try {
            if (File.Exists(hold) && Version.TryParse(File.ReadAllText(hold).Trim(), out var skipped) && current >= skipped) {
                File.Delete(hold);
                string stamp = Path.Combine(Paths.Root, "app-update-check.txt");
                if (File.Exists(stamp)) File.Delete(stamp);
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("app.rollback.resume", ex); }
    }
    public static bool IsHeld(string target, Version release)
    {
        string path = Path.Combine(target, HoldFile);
        if (!File.Exists(path)) return false;
        try { return Version.TryParse(File.ReadAllText(path).Trim(), out var held) && release <= held; }
        catch (IOException ex) { AppLog.Warn("app.rollback.hold", ex); return false; }
    }
    public static RollbackChoice? Find(string target, string? updatesRoot = null, Version? current = null)
    {
        string root = updatesRoot ?? Path.Combine(Paths.Root, "Updates");
        if (!Directory.Exists(root)) return null;
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories().OrderByDescending(d => d.LastWriteTimeUtc)) {
            try {
                string manifest = Path.Combine(directory.FullName, "backup.json");
                if (!File.Exists(manifest) || new FileInfo(manifest).Length > 2_000_000 || !File.Exists(Path.Combine(directory.FullName, "apply.log")) || !File.ReadAllText(Path.Combine(directory.FullName, "apply.log")).StartsWith("Update applied successfully.")) continue;
                var backup = JsonSerializer.Deserialize<AppBackup>(File.ReadAllText(manifest));
                if (backup != null && Path.GetFullPath(backup.Target).TrimEnd('\\').Equals(Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && Version.TryParse(backup.Version, out var version) && version < (current ?? AppUpdates.CurrentVersion) && Directory.Exists(Path.Combine(directory.FullName, "Backup")))
                    return new(directory.FullName, backup);
            } catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { AppLog.Warn("app.rollback.find", ex); }
        }
        return null;
    }
    public static async Task Verify(RollbackChoice choice, CancellationToken ct)
    {
        string root = Path.GetFullPath(Path.Combine(choice.Folder, "Backup")) + Path.DirectorySeparatorChar;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in choice.Backup.Files) {
            ct.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(Path.Combine(root, file.Relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !names.Add(file.Relative)) throw new InvalidDataException("The previous version backup is invalid.");
            for (string? part = path; part != null && part.Length >= root.Length - 1; part = Path.GetDirectoryName(part))
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The backup contains a redirected path.");
            await using var stream = File.OpenRead(path);
            if (stream.Length != file.Length || !Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The previous version backup is incomplete or changed.");
        }
        if (!new[] { "Soundshark.exe", "Soundshark.dll", "Soundshark.Core.dll" }.All(names.Contains) || AssemblyName.GetAssemblyName(Path.Combine(root, "Soundshark.dll")).Version != Version.Parse(choice.Backup.Version)) throw new InvalidDataException("The previous version backup does not match its version.");
    }
    public static void ApplyOnExit(RollbackChoice choice)
    {
        string stage = Path.Combine(Paths.Root, "Updates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        AppUpdates.LaunchPlan(stage, AppContext.BaseDirectory, Path.Combine(choice.Folder, "Backup"), choice.Backup.Files, true, AppUpdates.CurrentVersion.ToString(3));
    }
}
