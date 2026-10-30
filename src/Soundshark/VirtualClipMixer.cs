using NAudio.Wave;
using Soundshark.Core;

namespace Soundshark;

/// <summary>Continuous voice output. Clip controls never close the voice route.</summary>
public sealed class VirtualClipMixer : ISampleProvider
{
    public const int Rate = 48000;
    readonly object sync = new();
    readonly Func<long> clock;
    readonly float[] queue = new float[Rate / 2]; // 250 ms stereo, bounded.
    VirtualVoiceBuffer? voice;
    int read, queued;
    long generation, written, consumed, total, silenceFrom, silenceTo;
    bool active, paused, block, connected;
    float voiceGain = 1, clipGain = 1;
    Exception? failure;
    long lastRead, lastProgress;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
    public VirtualClipMixer(Func<long>? clock = null) => this.clock = clock ?? (() => Environment.TickCount64);
    public long Consumed { get { lock (sync) return consumed; } }
    public long Written { get { lock (sync) return written; } }
    public bool Connected { get { lock (sync) return connected; } }
    public Exception? Failure { get { lock (sync) return failure; } }
    public long LastRead => Interlocked.Read(ref lastRead);
    public void Connect(VirtualVoiceBuffer buffer, float gain)
    { lock (sync) { voice = buffer; voiceGain = gain; connected = true; lastRead = clock(); } }
    public void Disconnect(Exception? error = null)
    { lock (sync) { connected = false; voice?.Clear(); voice = null; active = false; queued = 0; generation++; failure = error ?? new SoundboardException("Virtual microphone disconnected. The saved devices will be retried."); } }
    public long Begin(long frames, float voiceVolume, float clipVolume, bool blockVoice, long silenceStart = 0, long silenceEnd = 0)
    {
        lock (sync) {
            if (!connected) throw new SoundboardException("Enable the virtual microphone route and connect its saved devices first.");
            if (frames <= 0) throw new ArgumentOutOfRangeException(nameof(frames));
            generation++; read = queued = 0; consumed = written = 0; total = frames;
            voiceGain = voiceVolume; clipGain = clipVolume; block = blockVoice; silenceFrom = silenceStart; silenceTo = silenceEnd;
            active = paused = false; failure = null; lastProgress = clock(); return generation;
        }
    }
    public int Write(long lease, ReadOnlySpan<float> stereo)
    {
        if (stereo.Length % 2 != 0) throw new ArgumentException("Expected stereo frames.");
        lock (sync) {
            if (lease != generation || !connected) throw new OperationCanceledException("The virtual clip route changed.");
            int frames = (int)Math.Min(Math.Min(stereo.Length / 2, queue.Length / 2 - queued), total - written);
            for (int i = 0; i < frames; i++) { int index = (read + queued + i) % (queue.Length / 2); queue[index * 2] = stereo[i * 2]; queue[index * 2 + 1] = stereo[i * 2 + 1]; }
            queued += frames; written += frames; return frames;
        }
    }
    public void Activate(long lease) { lock (sync) { if (lease != generation) throw new OperationCanceledException(); active = true; lastProgress = clock(); } }
    public void Pause(long lease, bool value) { lock (sync) { if (lease == generation && paused != value) { paused = value; lastProgress = clock(); } } }
    public void StopClip(long lease) { lock (sync) { if (lease == generation) { active = false; queued = 0; generation++; } } }
    public void FailClip(Exception error) { lock (sync) { active = false; queued = 0; failure = error; } }
    public int Read(float[] buffer, int offset, int count)
    {
        if (count % 2 != 0) throw new ArgumentException("Expected stereo samples.");
        lock (sync) {
            long now = clock();
            if (lastRead != 0 && now - lastRead > 500) {
                voice?.Clear(); if (active) { active = false; queued = 0; failure = new SoundboardException("Audio output was interrupted. Clip stopped to avoid delayed playback."); }
            }
            Interlocked.Exchange(ref lastRead, now);
            var output = buffer.AsSpan(offset, count); output.Clear();
            if (!connected) return count;
            voice?.Read(output, Rate);
            for (int i = 0; i < count; i += 2) {
                bool playing = active && !paused && consumed < total && queued > 0;
                bool muteVoice = playing && block && !(consumed >= silenceFrom && consumed < silenceTo);
                float left = muteVoice ? 0 : output[i] * voiceGain, right = muteVoice ? 0 : output[i + 1] * voiceGain;
                if (playing) {
                    left += queue[read * 2] * clipGain; right += queue[read * 2 + 1] * clipGain;
                    read = (read + 1) % (queue.Length / 2); queued--; consumed++; lastProgress = now;
                }
                float peak = Math.Max(Math.Abs(left), Math.Abs(right)); float gain = peak > .98f ? .98f / peak : 1;
                output[i] = float.IsFinite(left) ? left * gain : 0; output[i + 1] = float.IsFinite(right) ? right * gain : 0;
            }
            if (active && !paused && consumed < total && now - lastProgress > 500) { active = false; queued = 0; failure = new SoundboardException("Clip audio stalled. Voice passthrough continues."); }
            return count; // The output clock never stops at clip EOF.
        }
    }
}
