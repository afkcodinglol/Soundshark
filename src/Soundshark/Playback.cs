using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;
using Soundshark.Core;

namespace Soundshark;
public sealed class Playback : IDisposable
{
    IWavePlayer? output;
    AutoKeyLease? heldKeys;
    void ReleaseKeys() { var lease=Interlocked.Exchange(ref heldKeys,null); if(lease!=null) _=lease.DisposeAsync(); }
    MMDevice? endpoint;
    RawSourceWaveStream? source;
    TimelineStream? timeline;
    string? temporarySource;
    VolumeSampleProvider? volumeProvider;
    internal float OutputGain => volumeProvider?.Volume ?? 0;
    long start;
    double duration;
    long lastDeviceBytes, wrappedDeviceBytes;
    public long SessionId { get; private set; }
    public event Action<long>? Completed;
    public event Action<Exception>? Failed;
    public bool Active => output?.PlaybackState == PlaybackState.Playing;
    public bool Finished => output?.PlaybackState == PlaybackState.Stopped && source != null && source.Position >= source.Length;
    public double Position
    {
        get
        {
            if (output is not { } device) return start / (double)Workspace.Rate;
            long bytes = device is WaveOutEvent wave ? wave.GetPosition() : ((WasapiOut)device).GetPosition();
            // WinMM's byte counter wraps after 4 GB, including during multi-hour previews.
            if (bytes < lastDeviceBytes && lastDeviceBytes - bytes > uint.MaxValue / 2L) wrappedDeviceBytes += 1L << 32;
            lastDeviceBytes = bytes;
            var format = device is WaveOutEvent waveFormat ? waveFormat.OutputWaveFormat : ((WasapiOut)device).OutputWaveFormat;
            return start / (double)Workspace.Rate + Math.Clamp((wrappedDeviceBytes + bytes) / (double)format.AverageBytesPerSecond, 0, duration);
        }
    }
    public void Play(Workspace w, long from, long to, bool limit, bool deleteSourceOnStop = false, string? endpointId = null, float volume = 1, AutoKeyLease? keys = null)
    {
        Stop(); heldKeys=keys; from = Math.Clamp(from, 0, w.Frames); to = Math.Clamp(to, from, w.Frames);
        if (limit) to = Math.Min(to, from + Workspace.Rate * 5L);
        if (to == from) { ReleaseKeys(); return; }
        var ranges = new List<SampleRange>(); long cursor = 0;
        foreach (var r in w.Ranges)
        {
            long a = Math.Clamp(from - cursor, 0, r.Length), b = Math.Clamp(to - cursor, 0, r.Length);
            if (b > a) ranges.Add(new(r.Start + a, b - a)); cursor += r.Length;
        }
        try
        {
            start = from; duration = (to - from) / (double)Workspace.Rate; lastDeviceBytes = wrappedDeviceBytes = 0;
            temporarySource = deleteSourceOnStop ? w.Source : null; timeline = new(w.Source, ranges);
            source = new(timeline, WaveFormat.CreateIeeeFloatWaveFormat(Workspace.Rate, 2));
            if (string.IsNullOrEmpty(endpointId)) output = new WaveOutEvent { DesiredLatency = 100 };
            else { using var devices = new MMDeviceEnumerator(); endpoint = devices.GetDevice(endpointId); output = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 100); }
            var device = output; long session = SessionId;
            device.PlaybackStopped += (_, e) =>
            {
                if (!ReferenceEquals(output, device) || SessionId != session) return;
                ReleaseKeys();
                if (e.Exception != null) Failed?.Invoke(e.Exception); else Completed?.Invoke(session);
            };
            volumeProvider = new VolumeSampleProvider(source.ToSampleProvider()) { Volume = Math.Clamp(volume, 0, 2) };
            output.Init(volumeProvider.ToWaveProvider()); output.Play();
            AppLog.Info("playback.start", new { source = w.Source, from, to, limit });
        }
        catch { Stop(); throw; }
    }
    public void Stop()
    {
        SessionId++; ReleaseKeys();
        try { output?.Stop(); }
        finally
        {
            output?.Dispose(); output = null; endpoint?.Dispose(); endpoint = null; source?.Dispose(); source = null; timeline?.Dispose(); timeline = null;
            if (temporarySource is { } path) _ = Task.Run(() => { try { System.IO.File.Delete(path); } catch (Exception ex) { AppLog.Warn("playback.cleanup", ex); } });
            temporarySource = null;
        }
    }
    public void SetPaused(bool paused)
    {
        if (paused && output?.PlaybackState == PlaybackState.Playing) output.Pause();
        else if (!paused && output?.PlaybackState == PlaybackState.Paused) output.Play();
    }
    public void SetVolume(float volume) { if (volumeProvider != null) volumeProvider.Volume = Math.Clamp(volume, 0, 2); }
    public void Dispose() => Stop();
}
