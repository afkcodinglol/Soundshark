using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Soundshark.Core;

namespace Soundshark;
/// <summary>Owns WASAPI and writes native-format samples immediately. UI only polls snapshots.</summary>
public sealed class CaptureSession : IDisposable
{
    readonly object sync = new();
    readonly WasapiCapture capture;
    readonly MMDevice device;
    readonly WaveFileWriter writer;
    readonly CaptureGate gate;
    readonly Stopwatch clock = new();
    readonly bool microphone;
    readonly System.Threading.Timer? silenceTimer;
    readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool stopping, disposed;
    float level;
    readonly Queue<float> live = new();
    public bool Limited { get; }
    public string FilePath { get; }
    public string? Error { get; private set; }
    public bool Finished => stopped.Task.IsCompleted;
    public double Seconds { get { lock (sync) return (double)gate.Frames / capture.WaveFormat.SampleRate; } }
    public float Level { get { lock (sync) return level; } }
    public float[] Live { get { lock (sync) return live.ToArray(); } }
    public CaptureSession(MMDevice device, bool useMicrophone, string path, bool limitToFiveSeconds = false)
    {
        microphone = useMicrophone; Limited = limitToFiveSeconds; FilePath = path; this.device = device;
        capture = microphone ? new WasapiCapture(device) : new WasapiLoopbackCapture(device);
        writer = new(path, capture.WaveFormat);
        gate = new(capture.WaveFormat.SampleRate, capture.WaveFormat.BlockAlign, Limited);
        AppLog.Info("recording.prepare", new { microphone, Limited, FilePath, device = device.FriendlyName, format = capture.WaveFormat.ToString() });
        capture.DataAvailable += Data;
        capture.RecordingStopped += (_, e) =>
        {
            lock (sync)
            {
                if (disposed) return;
                if (e.Exception != null) RecordFailure(e.Exception, "device");
                try { if (!microphone) FillSilence(clock.Elapsed.TotalSeconds); writer.Dispose(); }
                catch (Exception ex) { RecordFailure(ex, "finalize"); }
                AppLog.Info("recording.stopped", new { microphone, Limited, FilePath, frames = gate.Frames, hadError = Error != null });
                stopped.TrySetResult();
            }
        };
        if (!microphone) silenceTimer = new(_ =>
        {
            lock (sync)
            {
                if (stopping || disposed) return;
                try { FillSilence(Math.Max(0, clock.Elapsed.TotalSeconds - .25)); if (gate.Full) RequestStop(); }
                catch (Exception ex) { RecordFailure(ex, "silence"); RequestStop(); }
            }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }
    public void Start() { clock.Start(); capture.StartRecording(); silenceTimer?.Change(100, 100); AppLog.Info("recording.start", new { microphone, Limited, FilePath }); }
    void FillSilence(double until)
    {
        long missing = (long)(until * capture.WaveFormat.SampleRate) - gate.Frames;
        byte[] zeros = new byte[32768 - 32768 % capture.WaveFormat.BlockAlign];
        while (missing > 0)
        {
            int n = gate.Accept((int)Math.Min(missing * capture.WaveFormat.BlockAlign, zeros.Length));
            if (n == 0) break;
            writer.Write(zeros, 0, n); missing -= n / capture.WaveFormat.BlockAlign;
        }
    }
    void Data(object? sender, WaveInEventArgs e)
    {
        lock (sync)
        {
            if (stopping || disposed) return;
            try
            {
                var f = capture.WaveFormat;
                if (!microphone) FillSilence(Math.Max(0, clock.Elapsed.TotalSeconds - (double)e.BytesRecorded / f.AverageBytesPerSecond));
                int n = gate.Accept(e.BytesRecorded);
                writer.Write(e.Buffer, 0, n); writer.Flush();
                float peak = 0;
                bool floating = f.Encoding == WaveFormatEncoding.IeeeFloat || f is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
                int bytes = f.BitsPerSample / 8;
                for (int i = 0; i + bytes <= n; i += bytes)
                {
                    float sample = floating ? BitConverter.ToSingle(e.Buffer, i) : bytes == 2 ? BitConverter.ToInt16(e.Buffer, i) / 32768f : bytes == 4 ? BitConverter.ToInt32(e.Buffer, i) / 2147483648f : bytes == 3 ? ((e.Buffer[i] | e.Buffer[i + 1] << 8 | e.Buffer[i + 2] << 16) << 8) / 2147483648f : (e.Buffer[i] - 128) / 128f;
                    if (float.IsFinite(sample)) peak = Math.Max(peak, Math.Abs(sample));
                }
                level = Math.Clamp(peak, 0, 1); live.Enqueue(level); while (live.Count > 300) live.Dequeue();
                if (gate.Full) RequestStop();
            }
            catch (Exception ex) { RecordFailure(ex, "write"); RequestStop(); }
        }
    }
    void RequestStop()
    {
        if (stopping) return; stopping = true;
        silenceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        // Avoid stopping from inside WASAPI's callback thread.
        ThreadPool.QueueUserWorkItem(_ => { try { capture.StopRecording(); } catch (Exception ex) { lock (sync) RecordFailure(ex, "stop"); stopped.TrySetResult(); } });
    }
    void RecordFailure(Exception ex, string stage)
    {
        var error = UserErrors.Report(ex, "recording." + stage, new { microphone, Limited, FilePath, frames = gate.Frames });
        Error ??= error.Inline;
    }
    public async Task Stop() { lock (sync) RequestStop(); await stopped.Task; }
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; silenceTimer?.Dispose(); writer.Dispose(); }
        capture.Dispose(); device.Dispose();
    }
}
