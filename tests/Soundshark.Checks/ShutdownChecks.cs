using System.Diagnostics;
using System.IO;
using Soundshark;

internal static partial class Program
{
    static async Task ShutdownChecks()
    {
        string root = Path.Combine(Paths.Cache, "shutdown-logs-" + Guid.NewGuid().ToString("N"));
        await using var log = new DiagnosticLog(root);
        for (int i = 0; i < 100; i++) log.Write("ERROR", "shutdown.fixture", i + ":" + new string('x', 3000));
        var watch = Stopwatch.StartNew();
        bool complete = log.Flush().Wait(TimeSpan.FromSeconds(1));
        Check(complete && watch.Elapsed < TimeSpan.FromSeconds(1), "shutdown log flush completes while the UI dispatcher is synchronously waiting");
        string text = string.Join("", await Task.WhenAll(Directory.GetFiles(root, "*.log").Select(path => File.ReadAllTextAsync(path))));
        Check(text.Split("shutdown.fixture").Length - 1 == 100, "quick shutdown flush retains every queued diagnostic entry");
        await log.DisposeAsync();
        Check(log.Flush().Wait(TimeSpan.FromSeconds(1)), "flushing an already-completed log writer also needs no UI continuation");
        Console.WriteLine($"UI-thread shutdown flush: {watch.Elapsed.TotalMilliseconds:F0} ms including verification");
    }
}
