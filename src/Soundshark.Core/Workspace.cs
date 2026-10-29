using System.Text.Json;

namespace Soundshark.Core;

public readonly record struct SampleRange(long Start, long Length);
public sealed record WorkspaceState(string Source, string Name, List<SampleRange> Ranges, bool Dirty,
    List<List<SampleRange>> Undo, List<List<SampleRange>> Redo, double Volume = 1);

/// <summary>48 kHz stereo float PCM. All offsets are frames, never bytes or rounded seconds.</summary>
public sealed class Workspace
{
    public const int Rate = 48000, FrameBytes = 8;
    public string Source { get; private set; } = "";
    public string Name { get; set; } = "Untitled";
    public bool Dirty { get; set; }
    double volume=1;
    public double Volume { get=>volume;set {if(!double.IsFinite(value)||value<0||value>1)throw new ArgumentOutOfRangeException(nameof(value),"Track volume must be between 0 and 100 percent.");volume=value;} }
    public List<SampleRange> Ranges { get; private set; } = [];
    readonly Stack<List<SampleRange>> undo = new(), redo = new();
    public long Frames => Ranges.Sum(r => r.Length);
    public double Seconds => (double)Frames / Rate;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public void Load(string path, string name, bool dirty = true)
    {
        if (new FileInfo(path).Length % FrameBytes != 0) throw new InvalidDataException("Incomplete PCM frame.");
        Source = path; Name = name; Dirty = dirty; Volume=1;
        Ranges = [new(0, new FileInfo(path).Length / FrameBytes)]; undo.Clear(); redo.Clear();
    }
    public void Delete(long start, long end)
    {
        start = Math.Clamp(start, 0, Frames); end = Math.Clamp(end, start, Frames);
        if (end == start) return;
        undo.Push([.. Ranges]); redo.Clear();
        var kept = new List<SampleRange>(); long cursor = 0;
        foreach (var r in Ranges)
        {
            long left = Math.Clamp(start - cursor, 0, r.Length);
            long right = Math.Clamp(end - cursor, 0, r.Length);
            if (left > 0) kept.Add(new(r.Start, left));
            if (right < r.Length) kept.Add(new(r.Start + right, r.Length - right));
            cursor += r.Length;
        }
        Ranges = kept; Dirty = true;
    }
    public void Keep(long start, long end)
    {
        long frames = Frames;
        start = Math.Clamp(start, 0, frames); end = Math.Clamp(end, start, frames);
        if (end == start || start == 0 && end == frames) return;
        var kept = new List<SampleRange>(); long cursor = 0;
        foreach (var r in Ranges)
        {
            long left = Math.Clamp(start - cursor, 0, r.Length);
            long right = Math.Clamp(end - cursor, 0, r.Length);
            if (right > left) kept.Add(new(r.Start + left, right - left));
            cursor += r.Length;
        }
        undo.Push([.. Ranges]); redo.Clear();
        Ranges = kept; Dirty = true;
    }
    public void Undo() { if (undo.TryPop(out var r)) { redo.Push([.. Ranges]); Ranges = r; Dirty = true; } }
    public void Redo() { if (redo.TryPop(out var r)) { undo.Push([.. Ranges]); Ranges = r; Dirty = true; } }
    public WorkspaceState Snapshot() => new(Source, Name, [.. Ranges], Dirty, undo.Select(x => x.ToList()).ToList(), redo.Select(x => x.ToList()).ToList(), Volume);
    public void Restore(WorkspaceState s)
    {
        long max = new FileInfo(s.Source).Length / FrameBytes;
        foreach (var r in s.Ranges.Concat(s.Undo.SelectMany(x => x)).Concat(s.Redo.SelectMany(x => x)))
            if (r.Start < 0 || r.Length < 0 || r.Start > max - r.Length) throw new InvalidDataException("Recovery range outside source.");
        Volume=s.Volume; Source = s.Source; Name = s.Name; Ranges = [.. s.Ranges]; Dirty = s.Dirty;
        undo.Clear(); redo.Clear(); foreach (var r in s.Undo.AsEnumerable().Reverse()) undo.Push(r); foreach (var r in s.Redo.AsEnumerable().Reverse()) redo.Push(r);
    }
    public TimelineStream Open() => new(Source, Ranges);
}

/// <summary>Seekable projection of retained source ranges; memory stays bounded for multi-hour files.</summary>
public sealed class TimelineStream : Stream
{
    readonly FileStream file;
    readonly SampleRange[] ranges;
    readonly long[] offsets;
    long position;
    public TimelineStream(string source, IEnumerable<SampleRange> retained)
    {
        file = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        ranges = retained.ToArray(); offsets = new long[ranges.Length + 1];
        for (int i = 0; i < ranges.Length; i++) offsets[i + 1] = checked(offsets[i] + ranges[i].Length * Workspace.FrameBytes);
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (count > 0 && position < Length)
        {
            int i = Array.BinarySearch(offsets, position);
            if (i < 0) i = ~i - 1;
            if (i >= ranges.Length) break;
            file.Position = ranges[i].Start * Workspace.FrameBytes + position - offsets[i];
            int n = file.Read(buffer, offset, (int)Math.Min(count, offsets[i + 1] - position));
            if (n == 0) throw new EndOfStreamException("Audio source was truncated.");
            position += n; total += n; offset += n; count -= n;
        }
        return total;
    }
    public override long Seek(long offset, SeekOrigin origin) => Position = (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? position : Length) + offset;
    public override long Length => offsets[^1];
    public override long Position { get => position; set => position = Math.Clamp(value, 0, Length); }
    public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
}

public static class FileRules
{
    public static readonly string[] AudioExtensions = [".mp3", ".wav", ".flac", ".m4a", ".ogg", ".opus", ".aiff", ".aif"];
    public static string Filename(string name, string extension = ".mp3")
    {
        extension = extension.ToLowerInvariant();
        if (!AudioExtensions.Contains(extension)) throw new ArgumentException("Unsupported audio format.");
        var invalid = "<>:\"/\\|?*";
        name = new string(name.Trim().Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        string suffix = Path.GetExtension(name);
        if (AudioExtensions.Contains(suffix.ToLowerInvariant())) name = name[..^suffix.Length].TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a filename.");
        var stem = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) name = "_" + name;
        return name[..Math.Min(name.Length, 120)] + extension;
    }
    public static void Validate(double encodedSeconds, long bytes, bool discord)
    {
        if (!double.IsFinite(encodedSeconds) || encodedSeconds <= 0 || bytes <= 0) throw new InvalidDataException("The encoder produced no valid audio.");
        if (!discord) return;
        if (encodedSeconds > 5)
            throw new InvalidDataException($"Encoded MP3 is {encodedSeconds:F3}s. Trim at least {encodedSeconds - 5 + .027:F3}s more and save again. MP3 frame padding counts toward the five-second limit; your speech has been kept.");
        if (bytes > 512000) throw new InvalidDataException($"MP3 is {bytes:N0} bytes; limit is 512,000. Choose a lower bitrate or trim the clip.");
    }
    public static async Task AtomicJsonAsync<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".new";
        try { await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(value), ct); File.Move(tmp, path, true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}

/// <summary>Sample-boundary capture gate, independent of UI timers and scheduling.</summary>
public sealed class CaptureGate(int sampleRate, int blockAlign, bool limitToFiveSeconds)
{
    public long Frames { get; private set; }
    public long Limit { get; } = limitToFiveSeconds ? checked(sampleRate * 5L) : long.MaxValue;
    public int Accept(int bytes)
    {
        int accepted = (int)Math.Min(bytes / blockAlign, Limit - Frames) * blockAlign;
        Frames += accepted / blockAlign; return accepted;
    }
    public bool Full => Frames == Limit;
}
