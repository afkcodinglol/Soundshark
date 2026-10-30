namespace Soundshark.Core;

/// <summary>Bounded stereo capture queue, resampled against the output clock.</summary>
public sealed class VirtualVoiceBuffer
{
    readonly object sync = new();
    readonly float[] samples;
    readonly int rate, target, maximum;
    int read, count;
    double phase, correction = 1;
    bool primed;
    public long DroppedFrames { get; private set; }
    public int BufferedFrames { get { lock (sync) return count; } }
    public double Correction { get { lock (sync) return correction; } }
    public VirtualVoiceBuffer(int inputRate)
    {
        if (inputRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(inputRate));
        rate = inputRate; target = rate * 60 / 1000; maximum = rate * 150 / 1000;
        samples = new float[(rate / 4 + 2) * 2];
    }
    public void Clear() { lock (sync) { read = count = 0; phase = 0; correction = 1; primed = false; } }
    public void Append(ReadOnlySpan<byte> bytes, int channels, int bits, bool ieeeFloat)
    {
        if (channels < 1 || channels > 32 || bits is not (16 or 24 or 32) || (ieeeFloat && bits != 32)) throw new ArgumentException("Unsupported microphone sample format.");
        int width = bits / 8, stride = channels * width;
        if (bytes.Length % stride != 0) throw new ArgumentException("Incomplete microphone frame.");
        lock (sync) {
            for (int i = 0; i < bytes.Length; i += stride) {
                if (count >= samples.Length / 2) { read = (read + 1) % (samples.Length / 2); count--; DroppedFrames++; phase = 0; }
                int write = (read + count) % (samples.Length / 2);
                float left = Decode(bytes.Slice(i, width), bits, ieeeFloat);
                float right = channels == 1 ? left : Decode(bytes.Slice(i + width, width), bits, ieeeFloat);
                samples[write * 2] = left; samples[write * 2 + 1] = right; count++;
            }
            if (count > maximum) {
                int discard = count - target; read = (read + discard) % (samples.Length / 2); count -= discard; DroppedFrames += discard; phase = 0;
            }
        }
    }
    static float Decode(ReadOnlySpan<byte> value, int bits, bool ieeeFloat)
    {
        float sample = ieeeFloat ? BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(value)) : bits switch {
            16 => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(value) / 32768f,
            24 => ((value[0] | value[1] << 8 | value[2] << 16) << 8 >> 8) / 8388608f,
            _ => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(value) / 2147483648f
        };
        return float.IsFinite(sample) ? Math.Clamp(sample, -1, 1) : 0;
    }
    public void Read(Span<float> stereo, int outputRate)
    {
        if (stereo.Length % 2 != 0 || outputRate <= 0) throw new ArgumentException("Expected stereo frames.");
        stereo.Clear();
        lock (sync) {
            if (!primed) { if (count < target) return; primed = true; }
            // Low-pass occupancy feedback compensates independent hardware clocks.
            double desired = Math.Clamp(1 + .002 * (count - target) / target, .995, 1.005);
            correction += (desired - correction) * .02;
            double step = rate / (double)outputRate * correction;
            for (int i = 0; i < stereo.Length; i += 2) {
                if (count < 2) { read = count = 0; phase = 0; primed = false; break; }
                int next = (read + 1) % (samples.Length / 2);
                stereo[i] = (float)(samples[read * 2] + (samples[next * 2] - samples[read * 2]) * phase);
                stereo[i + 1] = (float)(samples[read * 2 + 1] + (samples[next * 2 + 1] - samples[read * 2 + 1]) * phase);
                phase += step;
                int advance = (int)phase;
                if (advance > 0) { advance = Math.Min(advance, count); read = (read + advance) % (samples.Length / 2); count -= advance; phase -= advance; }
            }
        }
    }
}
