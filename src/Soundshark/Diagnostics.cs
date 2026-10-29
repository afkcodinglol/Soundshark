using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Soundshark;

/// <summary>A bounded background writer. Audio callbacks and the UI never wait for disk I/O.</summary>
public sealed class DiagnosticLog : IAsyncDisposable
{
    sealed record Entry(string? Json, TaskCompletionSource? Barrier = null);
    readonly Channel<Entry> queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    readonly Task writer;
    readonly long maxBytes;
    readonly int maxFiles;
    readonly string? fallback;
    string? file;
    long dropped;
    public string DirectoryPath { get; private set; }
    public bool StorageUnavailable { get; private set; }
    public DiagnosticLog(string directory, long maxBytes = 5 * 1024 * 1024, int maxFiles = 10, string? fallback = null)
    {
        if (maxBytes < 1024 || maxFiles < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        DirectoryPath = directory; this.maxBytes = maxBytes; this.maxFiles = maxFiles; this.fallback = fallback;
        writer = Task.Run(WriteLoop);
    }
    public static string Redact(string value)
    {
        value = Regex.Replace(value, @"https?://[^\s\""<>]+", m =>
        {
            if (!Uri.TryCreate(m.Value, UriKind.Absolute, out var u)) return "[URL]";
            var safe = new UriBuilder(u) { UserName = "", Password = "", Query = "", Fragment = "" };
            return safe.Uri.GetLeftPart(UriPartial.Path) + (u.Query.Length > 0 ? "?[query omitted]" : "");
        });
        return Regex.Replace(value, @"(?i)\b(authorization|cookie|password|access_token|api[_-]?key)\u0022?\s*[:=]\s*[^\r\n]+", "$1=[redacted]");
    }
    Entry MakeEntry(string level, string operation, string detail, string? reference = null) => new(JsonSerializer.Serialize(new
    {
        timestamp = DateTimeOffset.Now, level, operation, reference, session = AppLog.Session,
        thread = Environment.CurrentManagedThreadId, detail = Redact(detail)
    }));
    public void Write(string level, string operation, string detail, string? reference = null)
    {
        try
        {
            detail = Redact(detail);
            int chunk = (int)Math.Min(16384, Math.Max(64, (maxBytes - 512) / 8));
            for (int start = 0; start < Math.Max(1, detail.Length); start += chunk)
            {
                var entry = MakeEntry(level, operation, detail.Substring(start, Math.Min(chunk, detail.Length - start)), reference);
                if (queue.Writer.TryWrite(entry)) continue;
                if (level == "ERROR") _ = EnqueueCritical(entry);
                else Interlocked.Increment(ref dropped);
            }
        }
        catch { StorageUnavailable = true; }
    }
    async Task EnqueueCritical(Entry entry)
    {
        try { await queue.Writer.WriteAsync(entry); } catch (ChannelClosedException) { StorageUnavailable = true; }
    }
    public async ValueTask Detail(string operation, string detail)
    {
        // Split very long tool lines so each rotated file remains bounded.
        detail = Redact(detail);
        int chunk = (int)Math.Min(16384, Math.Max(64, (maxBytes - 512) / 8));
        for (int start = 0; start < detail.Length; start += chunk)
        {
            try { await queue.Writer.WriteAsync(MakeEntry("DEBUG", operation, detail.Substring(start, Math.Min(chunk, detail.Length - start)))); }
            catch (ChannelClosedException) { return; }
        }
    }
    public async Task Flush()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Exit may synchronously wait on this task from the WPF dispatcher.
        // Completing a disk flush must never require that dispatcher to resume.
        try { await queue.Writer.WriteAsync(new(null, barrier)).ConfigureAwait(false); await barrier.Task.ConfigureAwait(false); }
        catch (ChannelClosedException) { await writer.ConfigureAwait(false); }
    }
    async Task WriteLoop()
    {
        await foreach (var entry in queue.Reader.ReadAllAsync())
        {
            if (entry.Barrier != null) { entry.Barrier.TrySetResult(); continue; }
            try
            {
                long missed = Interlocked.Exchange(ref dropped, 0);
                if (missed > 0) await Append(MakeEntry("WARN", "logging.backpressure", $"Skipped {missed} routine entries while the log writer was busy.").Json!);
                await Append(entry.Json!); StorageUnavailable = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                StorageUnavailable = true;
                if (fallback == null || DirectoryPath == fallback) continue;
                DirectoryPath = fallback; file = null;
                try { await Append(entry.Json!); StorageUnavailable = false; } catch { StorageUnavailable = true; }
            }
        }
    }
    async Task Append(string json)
    {
        Directory.CreateDirectory(DirectoryPath);
        long bytes = Encoding.UTF8.GetByteCount(json) + 1;
        if (file == null || !File.Exists(file) || new FileInfo(file).Length + bytes > maxBytes)
        {
            file = Path.Combine(DirectoryPath, $"soundshark-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.log");
            var old = Directory.GetFiles(DirectoryPath, "soundshark-*.log").OrderByDescending(File.GetLastWriteTimeUtc).Skip(maxFiles - 1);
            foreach (string path in old) File.Delete(path);
        }
        await File.AppendAllTextAsync(file, json + "\n", new UTF8Encoding(false));
    }
    public async ValueTask DisposeAsync() { queue.Writer.TryComplete(); await writer; }
}

public static class AppLog
{
    public static readonly string Session = Guid.NewGuid().ToString("N")[..12];
    static readonly Lazy<DiagnosticLog> instance = new(() => new(Path.Combine(Paths.Root, "Logs"), fallback: Path.Combine(Path.GetTempPath(), "Soundshark", "Logs")));
    public static DiagnosticLog Log => instance.Value;
    static string Describe(object? detail) { try { return JsonSerializer.Serialize(detail); } catch { return "[Context could not be serialized]"; } }
    public static void Info(string operation, object? detail = null) => Log.Write("INFO", operation, Describe(detail));
    public static void Warn(string operation, Exception ex) => Log.Write("WARN", operation, ex.ToString());
    public static string Error(string operation, Exception ex, object? context = null)
    {
        string reference = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Log.Write("ERROR", operation, $"Context: {Describe(context)}\n{ex}", reference); return reference;
    }
    public static async Task OpenFolder()
    {
        await Log.Flush(); Directory.CreateDirectory(Log.DirectoryPath);
        Process.Start(new ProcessStartInfo(Log.DirectoryPath) { UseShellExecute = true });
    }
    public static void FlushOnExit()
    {
        if (!instance.IsValueCreated) return;
        try { Log.Flush().Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}
