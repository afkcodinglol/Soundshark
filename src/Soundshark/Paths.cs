using System.IO;
using System.Text.Json;

namespace Soundshark;
public static class Paths
{
    public static readonly string Root = Environment.GetEnvironmentVariable("SOUNDSHARK_TEST_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundshark");
    public static readonly string Cache = Path.Combine(Root, "Workspaces");
    public static readonly string Library = Environment.GetEnvironmentVariable("SOUNDSHARK_TEST_ROOT") is { } test ? Path.Combine(test, "Library") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Soundshark", "Library");
    public static readonly string ToolSets = Path.Combine(Root, "ToolSets");
    public static readonly string ToolPointer = Path.Combine(Root, "toolset.txt");
    public static string Tools => File.Exists(ToolPointer) && Directory.Exists(File.ReadAllText(ToolPointer).Trim()) ? File.ReadAllText(ToolPointer).Trim() : Path.Combine(AppContext.BaseDirectory, "Tools");
    public static string Tool(string name) => Path.Combine(name is "ffmpeg" or "ffprobe" ? Path.Combine(AppContext.BaseDirectory, "Tools") : Tools, name + ".exe");
    public static string NewPcm() => Path.Combine(Cache, Guid.NewGuid() + ".pcm");
    public static void Init() { Directory.CreateDirectory(Cache); Directory.CreateDirectory(Library); Directory.CreateDirectory(ToolSets); }
}
public sealed class Settings
{
    public Appearance Appearance { get; set; } = new();
    public Dictionary<string, string> LibraryColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string,long> LibraryPlayCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool GroupLibraryByColor { get; set; }
    public bool LibraryListView { get; set; }
    public bool LibraryManualOrder { get; set; }
    public List<string> LibraryOrder { get; set; } = [];
    public bool ClipperEnabled { get; set; }
    public int ClipperSeconds { get; set; } = 15;
    public Soundshark.Core.SoundboardConfiguration Soundboard { get; set; } = new();
    readonly SemaphoreSlim saveLock = new(1);
    bool discord;
    public event Action? DiscordChanged;
    public bool Discord { get => discord; set { if (discord == value) return; discord = value; DiscordChanged?.Invoke(); } }
    public static Settings Load()
    {
        try { string path = Path.Combine(Paths.Root, "settings.json"); var settings = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new() : new();
            settings.ClipperSeconds = Math.Clamp(settings.ClipperSeconds, 1, 60);
            settings.LibraryOrder = (settings.LibraryOrder ?? []).Where(n => !string.IsNullOrWhiteSpace(n) && Path.GetFileName(n) == n).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            settings.LibraryPlayCounts = (settings.LibraryPlayCounts ?? new()).Where(p=>p.Value>=0).GroupBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.Max(p=>p.Value),StringComparer.OrdinalIgnoreCase);
            settings.LibraryColors = SoundColors.Normalize(settings.LibraryColors);
            try { settings.Appearance ??= new(); if (!Theme.Fonts.Contains(settings.Appearance.Font)) settings.Appearance = settings.Appearance with { Font = "Segoe UI" }; settings.Appearance = settings.Appearance with { ShowFileDetails=true }; settings.Appearance.Validate(); } catch (Exception ex) { AppLog.Warn("appearance.load", ex); settings.Appearance = new(); }
            try { settings.Soundboard ??= new(); settings.Soundboard.Validate(); } catch (Exception ex) { AppLog.Warn("soundboard.settings.load", ex); settings.Soundboard = new(); }
            settings.Soundboard = settings.Soundboard.ForSoundOutput();
            return settings; }
        catch (Exception ex) { AppLog.Warn("settings.load", ex); return new(); }
    }
    public async Task Save()
    {
        await saveLock.WaitAsync();
        try { await Soundshark.Core.FileRules.AtomicJsonAsync(Path.Combine(Paths.Root, "settings.json"), this); }
        finally { saveLock.Release(); }
    }
}
