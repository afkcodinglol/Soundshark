namespace Soundshark.Core;

/// <summary>Fixed storage, frame-aligned writes; callers serialize capture and snapshots.</summary>
public sealed class RollingAudioBuffer
{
    readonly byte[] data;
    readonly int alignment;
    int next, count;
    public int Capacity => data.Length;
    public long FramesWritten { get; private set; }
    public int Frames => count / alignment;
    public RollingAudioBuffer(int sampleRate, int blockAlign, int seconds)
    {
        if (sampleRate <= 0 || blockAlign <= 0 || seconds is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(seconds));
        long bytes = (long)sampleRate * blockAlign * seconds;
        if (bytes > 128 * 1024 * 1024) throw new ArgumentException("This device's audio format needs too much buffer memory. Choose a shorter Clipper length or a stereo audio device.");
        data = new byte[(int)bytes]; alignment = blockAlign;
    }
    public void Write(ReadOnlySpan<byte> input)
    {
        if (input.Length % alignment != 0) throw new ArgumentException("Complete audio frames are required.");
        FramesWritten += input.Length / alignment;
        if (input.Length >= data.Length) { input[^data.Length..].CopyTo(data); next = 0; count = data.Length; return; }
        int first = Math.Min(input.Length, data.Length - next);
        input[..first].CopyTo(data.AsSpan(next)); input[first..].CopyTo(data);
        next = (next + input.Length) % data.Length; count = Math.Min(data.Length, count + input.Length);
    }
    public void Silence(long frames)
    {
        if (frames <= 0) return;
        FramesWritten = checked(FramesWritten + frames);
        if (frames >= data.Length / alignment) { Array.Clear(data); next = 0; count = data.Length; return; }
        int bytes = (int)frames * alignment, first = Math.Min(bytes, data.Length - next);
        data.AsSpan(next, first).Clear(); data.AsSpan(0, bytes - first).Clear();
        next = (next + bytes) % data.Length; count = Math.Min(data.Length, count + bytes);
    }
    public byte[] Snapshot()
    {
        byte[] result = new byte[count]; int start = (next - count + data.Length) % data.Length;
        int first = Math.Min(count, data.Length - start);
        data.AsSpan(start, first).CopyTo(result); data.AsSpan(0, count - first).CopyTo(result.AsSpan(first));
        return result;
    }
}
