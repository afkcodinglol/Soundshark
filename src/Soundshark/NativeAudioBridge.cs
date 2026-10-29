using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text.RegularExpressions;

namespace Soundshark;

public sealed class SoundboardException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One producer, independent APO readers. No microphone samples enter this mapping.</summary>
public sealed class NativeAudioBridge : IDisposable
{
    public const int Capacity = 8192, HeaderBytes = 4096, Bytes = HeaderBytes + Capacity * 8;
    readonly MemoryMappedFile mapping;
    readonly MemoryMappedViewAccessor view;
    readonly bool stopOnDispose;
    unsafe byte* pointer;
    public static string EndpointGuid(string id)
    {
        var match = Regex.Matches(id, @"\{[0-9A-Fa-f-]{36}\}").LastOrDefault();
        return match != null && Guid.TryParse(match.Value, out var guid) ? guid.ToString("B").ToUpperInvariant() : throw new SoundboardException("Choose an available microphone in Soundboard Settings.");
    }
    public static NativeAudioBridge Open(string endpointId, bool stopOnDispose = true)
    {
        try { return new(MemoryMappedFile.OpenExisting("Global\\Soundshark-Audio-v1-" + EndpointGuid(endpointId), MemoryMappedFileRights.ReadWrite), stopOnDispose); }
        catch (FileNotFoundException ex) { throw new SoundboardException("Microphone support isn't active for this device. Open Soundboard Settings and install microphone support, then reopen your voice app.", ex); }
        catch (UnauthorizedAccessException ex) { throw new SoundboardException("Microphone support is configured for another Windows user. Run microphone setup for your account.", ex); }
    }
    public unsafe NativeAudioBridge(MemoryMappedFile file, bool stopOnDispose = true)
    {
        mapping = file; this.stopOnDispose = stopOnDispose;
        try {
            view = mapping.CreateViewAccessor(0, Bytes, MemoryMappedFileAccess.ReadWrite);
            byte* value = null; view.SafeMemoryMappedViewHandle.AcquirePointer(ref value); pointer = value + view.PointerOffset;
            if (Read32(0) != 0x53484B31 || Read32(4) != 3 || Read32(8) != Capacity) throw new SoundboardException("The microphone component needs an update. Open Soundboard Settings and choose Enable Microphone Playback, then restart Windows.");
        } catch { if (pointer != null) view!.SafeMemoryMappedViewHandle.ReleasePointer(); view?.Dispose(); mapping.Dispose(); throw; }
    }
    public int Rate => Read32(12);
    public bool Alive => Environment.TickCount64 - Read64(56) is >= -100 and < 1500;
    public long Generation { get; private set; }
    public long Written => Read64(32);
    public long Total => Read64(40);
    public void Begin(long frames, float voice, float sound, bool block)
    {
        if (Rate is < 8000 or > 384000 || frames <= 0) throw new SoundboardException("The microphone audio format isn't ready. Reopen your voice app and try again.");
        Write32(20, 0); Generation = Read64(24) + 1;
        Write64(32, 0); Write64(40, frames); Write64(64, 0);
        view.Write(72, voice); view.Write(76, sound);
        Write64(24, Generation); Write64(48, Environment.TickCount64);
        Write32(20, block ? 2 : 0);
    }
    public long Consumed
    {
        get {
            long result = Written; bool found = false; long now = Environment.TickCount64;
            for (int i = 0; i < 32; ++i) {
                int at = 128 + i * 32;
                if (Read64(at) == 0 || Read64(at + 8) != Generation || now - Read64(at + 24) is < -100 or >= 1000) continue;
                result = Math.Min(result, Read64(at + 16)); found = true;
            }
            return found ? result : 0;
        }
    }
    public void Activate() => Write32(20, Read32(20) | 1);
    public void Pause(bool paused) => Write32(20, paused ? Read32(20) | 4 : Read32(20) & ~4);
    public void Heartbeat() => Write64(48, Environment.TickCount64);
    public void Stop() { Write32(20, 0); view.Write(72, 1f); Heartbeat(); }
    public void SetEqualizer(bool enabled, double[] coefficients)
    {
        if (coefficients.Length != 25) throw new ArgumentException("Five EQ filters are required.");
        int sequence = Read32(1160); Write32(1160, sequence | 1);
        for (int i = 0; i < 25; ++i) view.Write(1168 + i * 8, coefficients[i]);
        Write32(1164, enabled ? 1 : 0); Write32(1160, (sequence + 2) & ~1); RefreshEqualizer();
    }
    public void RefreshEqualizer() => Write64(1152, Environment.TickCount64);
    public void DisableEqualizer() { Write32(1164, 0); Write64(1152, 0); }
    public unsafe void Write(float[] samples, int count)
    {
        if (count % 2 != 0 || count < 0 || count > samples.Length) throw new ArgumentException("Complete stereo frames are required.");
        long written = Written;
        if (count / 2 > Capacity - (written - Consumed)) throw new IOException("Microphone audio buffer is full.");
        int start = (int)(written % Capacity), first = Math.Min(count / 2, Capacity - start) * 2;
        fixed (float* input = samples) {
            Buffer.MemoryCopy(input, pointer + HeaderBytes + start * 8, first * 4L, first * 4L);
            if (first < count) Buffer.MemoryCopy(input + first, pointer + HeaderBytes, (count - first) * 4L, (count - first) * 4L);
        }
        Write64(32, written + count / 2);
    }
    public unsafe int Read32(int offset) => Volatile.Read(ref *(int*)(pointer + offset));
    public unsafe long Read64(int offset) => Volatile.Read(ref *(long*)(pointer + offset));
    public unsafe void Write32(int offset, int value) => Volatile.Write(ref *(int*)(pointer + offset), value);
    public unsafe void Write64(int offset, long value) => Volatile.Write(ref *(long*)(pointer + offset), value);
    public unsafe void Dispose() { if (stopOnDispose) Stop(); view.SafeMemoryMappedViewHandle.ReleasePointer(); pointer = null; view.Dispose(); mapping.Dispose(); }
}
