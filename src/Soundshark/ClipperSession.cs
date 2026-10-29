using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Soundshark.Core;

namespace Soundshark;

/// <summary>Loopback-only bounded RAM history. No file writes/encoding or per-packet allocations.</summary>
public sealed class ClipperSession : IDisposable
{
    readonly object sync = new();
    readonly MMDevice device;
    readonly WasapiLoopbackCapture capture;
    readonly RollingAudioBuffer buffer;
    readonly Stopwatch clock = new();
    readonly System.Threading.Timer silence;
    readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool stopping, disposed;
    Exception? failure;
    public bool Finished => stopped.Task.IsCompleted;
    public Exception? Failure { get { lock (sync) return failure; } }
    public double Seconds { get { lock (sync) return buffer.Frames / (double)capture.WaveFormat.SampleRate; } }
    public int BufferBytes => buffer.Capacity;
    public ClipperSession(MMDevice device, int seconds)
    {
        this.device = device;
        capture = new WasapiLoopbackCapture(device);
        try { buffer = new(capture.WaveFormat.SampleRate, capture.WaveFormat.BlockAlign, seconds); }
        catch { capture.Dispose(); device.Dispose(); throw; }
        capture.DataAvailable += (_, e) => {
            lock (sync) {
                if (stopping || disposed) return;
                try {
                    FillSilence(Math.Max(0, clock.Elapsed.TotalSeconds - e.BytesRecorded / (double)capture.WaveFormat.AverageBytesPerSecond));
                    buffer.Write(e.Buffer.AsSpan(0, e.BytesRecorded));
                } catch (Exception ex) { Fail(ex); }
            }
        };
        capture.RecordingStopped += (_, e) => { lock (sync) { failure ??= e.Exception; stopping = true; } stopped.TrySetResult(); };
        silence = new(_ => {
            lock (sync) {
                if (stopping || disposed) return;
                try { FillSilence(Math.Max(0, clock.Elapsed.TotalSeconds - .25)); }
                catch (Exception ex) { Fail(ex); }
            }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }
    void FillSilence(double seconds) => buffer.Silence((long)(seconds * capture.WaveFormat.SampleRate) - buffer.FramesWritten);
    void Fail(Exception ex) { failure ??= ex; RequestStop(); }
    public void Start()
    {
        try { clock.Start(); capture.StartRecording(); silence.Change(100, 100); AppLog.Info("clipper.start", new { device = device.FriendlyName, bufferBytes = buffer.Capacity }); }
        catch (Exception ex) { lock (sync) { failure = ex; stopping = true; } stopped.TrySetResult(); throw; }
    }
    public (byte[] Audio, WaveFormat Format) Snapshot()
    {
        lock (sync) {
            if (stopping || disposed || failure != null) throw new SoundboardException("Clipper stopped. Turn it off and on to reconnect your PC audio device.", failure);
            FillSilence(clock.Elapsed.TotalSeconds);
            return (buffer.Snapshot(), capture.WaveFormat);
        }
    }
    void RequestStop()
    {
        if (stopping) return; stopping = true; silence.Change(Timeout.Infinite, Timeout.Infinite);
        ThreadPool.QueueUserWorkItem(_ => { try { capture.StopRecording(); } catch (Exception ex) { lock (sync) failure ??= ex; stopped.TrySetResult(); } });
    }
    public async Task Stop() { lock (sync) RequestStop(); await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; silence.Dispose(); }
        capture.Dispose(); device.Dispose();
    }
}
