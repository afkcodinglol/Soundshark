using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Soundshark.Core;

namespace Soundshark;

/// <summary>Serialized, latest-request-wins playback. Native audio callbacks never run this code.</summary>
public sealed class SoundboardCoordinator
{
    readonly Func<SoundboardConfiguration> configuration;
    CancellationTokenSource? cancellation;
    Task runner = Task.CompletedTask;
    readonly object stateLock = new();
    volatile bool paused;
    volatile float speakerGain = -1;
    public void SetSpeakerVolume(float volume) => speakerGain = Math.Clamp(volume, 0, 2);
    public string? CurrentFile { get; private set; }
    public bool Active => !runner.IsCompleted;
    public double PositionSeconds { get; private set; }
    public event Action<string>? Status;
    public event Action<string>? Started;
    public SoundboardCoordinator(Func<SoundboardConfiguration> configuration) => this.configuration = configuration;
    public void Play(string file, bool hotkey = false, bool? hearLocally = null, double startSeconds = 0)
    {
        lock (stateLock) {
        if (cancellation != null) _ = cancellation.CancelAsync();
        var previous = runner; var next = new CancellationTokenSource(); cancellation = next;
        var settings = configuration().ForSoundOutput(); if (hearLocally.HasValue) settings = settings with { HearTransmittedSounds = hearLocally.Value };
        CurrentFile = file; paused = false; speakerGain = (float)settings.SpeakerVolume;
        runner = Task.Run(async () => {
            try {
                await previous.ConfigureAwait(false); next.Token.ThrowIfCancellationRequested();
                do { await Run(file, settings, hotkey, startSeconds, next.Token).ConfigureAwait(false); startSeconds = 0; }
                while (settings.RepeatSounds && !(hotkey && settings.StopHotkeySoundsAtEnd) && !next.IsCancellationRequested);
                Status?.Invoke("Finished · " + Path.GetFileNameWithoutExtension(file));
            } catch (OperationCanceledException) { Status?.Invoke("Playback stopped."); }
            catch (Exception ex) { Status?.Invoke(UserErrors.Report(ex, "soundboard.play", new { file }).Inline); }
            finally { lock (stateLock) { if (ReferenceEquals(cancellation, next)) cancellation = null; next.Dispose(); } }
        });
        }
    }
    public void TogglePause() { paused = !paused; Status?.Invoke(paused ? "Paused · your voice is available" : "Playing through microphone…"); }
    public async Task StopAsync() { Task pending; lock (stateLock) { if (cancellation != null) _ = cancellation.CancelAsync(); pending = runner; } await pending.ConfigureAwait(false); }
    async Task Run(string file, SoundboardConfiguration settings, bool hotkey, double startSeconds, CancellationToken ct)
    {
        settings.Validate();
        if (settings.Backend != MicrophoneBackend.NativeMicrophone || string.IsNullOrWhiteSpace(settings.MicrophoneEndpointId)) throw new SoundboardException("Choose your microphone in Soundboard Settings and install microphone support first.");
        if (MicrophoneSetup.DevelopmentBuild && !MicrophoneSetup.ExperimentalEnabled) throw new SoundboardException("Enable experimental microphone playback in Soundboard Settings, then restart Windows. Preview is available now.");
        using var devices = new MMDeviceEnumerator();
        using var microphone = devices.GetDevice(settings.MicrophoneEndpointId);
        if (microphone.DataFlow != DataFlow.Capture || microphone.State != DeviceState.Active) throw new SoundboardException("The selected microphone is unavailable. Reconnect it or choose another microphone in Settings.");
        using var keepAlive = new WasapiCapture(microphone);
        var ended = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        keepAlive.RecordingStopped += (_, e) => ended.TrySetResult(e.Exception);
        NativeAudioBridge? bridge = null; string? sequence = null;
        using var speaker = new Playback();
        float MonitorVolume() => Math.Min(1, speakerGain) * (float)settings.TransmittedSoundVolume;
        AutoKeyLease? keys = null;
        try {
            Status?.Invoke("Connecting to your microphone…"); keepAlive.StartRecording();
            var deadline = Environment.TickCount64 + 2500;
            Exception? connectionError = null;
            while (Environment.TickCount64 < deadline) {
                ct.ThrowIfCancellationRequested();
                try { bridge = NativeAudioBridge.Open(settings.MicrophoneEndpointId); if (bridge.Alive && bridge.Rate > 0) break; bridge.Dispose(); bridge = null; }
                catch (SoundboardException ex) { connectionError = ex; }
                await Task.Delay(30, ct).ConfigureAwait(false);
            }
            if (bridge == null) throw new SoundboardException("Windows hasn't activated microphone support. Open Soundboard Settings to check setup, then reopen your voice app. No audio was sent.", connectionError);
            int rate = bridge.Rate;
            Status?.Invoke("Preparing sound and matching volume…");
            string clip = await SoundboardAudio.PrepareMicrophone(file, rate, settings, ct).ConfigureAwait(false);
            string? prelude = settings.UseActivationSound ? await SoundboardAudio.PrepareMicrophone(settings.ActivationSoundPath, rate, settings, ct).ConfigureAwait(false) : null;
            string? monitor = settings.HearTransmittedSounds ? await SoundboardAudio.PrepareMicrophone(file, Workspace.Rate, settings, ct).ConfigureAwait(false) : null;
            double preludeSeconds = prelude == null ? 0 : new FileInfo(prelude).Length / 8.0 / rate;
            sequence = Paths.NewPcm();
            await using (var output = File.Create(sequence)) {
                if (prelude != null) { await using var input = File.OpenRead(prelude); await input.CopyToAsync(output, ct).ConfigureAwait(false); }
                if (settings.RelativeMicrophoneDelayMs > 0) await output.WriteAsync(new byte[(long)rate * settings.RelativeMicrophoneDelayMs / 1000 * 8], ct).ConfigureAwait(false);
                await using var clipInput = File.OpenRead(clip);
                startSeconds = Math.Clamp(startSeconds, 0, Math.Max(0, clipInput.Length / 8.0 / rate - 1.0 / rate));
                clipInput.Position = (long)(startSeconds * rate) * 8; await clipInput.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            await Task.Delay(settings.StartDelayMs, ct).ConfigureAwait(false);
            keys = await AutoKeyLease.Acquire(settings.ForSoundPtt(file), hotkey, ct).ConfigureAwait(false);
            await Task.Delay(settings.AutoKeyLeadInMs, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            long total = new FileInfo(sequence).Length / 8;
            bridge.Begin(total, (float)settings.VoiceVolume, (float)settings.TransmittedSoundVolume, settings.BlockVoiceDuringSounds);
            await using var stream = new FileStream(sequence, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            var bytes = new byte[8192]; var samples = new float[bytes.Length / 4];
            var timeline = new System.Diagnostics.Stopwatch();
            bool activated = false, monitorStarted = false;
            Exception? speakerError = null; speaker.Failed += ex => speakerError = ex;
            while (bridge.Consumed < total) {
                ct.ThrowIfCancellationRequested();
                if (ended.Task.IsCompleted) throw await ended.Task.ConfigureAwait(false) ?? new SoundboardException("The microphone disconnected. Reconnect it and try again.");
                if (!bridge.Alive || bridge.Rate != rate) throw new SoundboardException("The microphone audio connection changed. Playback stopped; choose the device again in Settings.");
                if (speakerError != null) throw speakerError;
                bridge.Heartbeat();
                PositionSeconds = startSeconds + Math.Max(0, bridge.Consumed / (double)rate - preludeSeconds - Math.Max(0, settings.RelativeMicrophoneDelayMs) / 1000.0);
                if(paused && keys!=null) {await keys.DisposeAsync().ConfigureAwait(false);keys=null;}
                else if(!paused && keys==null) {keys=await AutoKeyLease.Acquire(settings.ForSoundPtt(file),hotkey,ct).ConfigureAwait(false);await Task.Delay(settings.AutoKeyLeadInMs,ct).ConfigureAwait(false);}
                bridge.Pause(paused); speaker.SetPaused(paused); speaker.SetVolume(MonitorVolume()); if (paused) timeline.Stop(); else if (activated) timeline.Start();
                while (!paused && bridge.Written - bridge.Consumed < Math.Min(rate / 5, NativeAudioBridge.Capacity / 2) && stream.Position < stream.Length) {
                    int read = await stream.ReadAsync(bytes, ct).ConfigureAwait(false); if (read == 0 || read % 8 != 0) throw new IOException("Incomplete soundboard PCM buffer.");
                    Buffer.BlockCopy(bytes, 0, samples, 0, read); bridge.Write(samples, read / 4);
                }
                if (!activated && (bridge.Written >= Math.Min(total, Math.Min(rate / 10, NativeAudioBridge.Capacity / 4)))) { bridge.Activate(); Started?.Invoke(file); activated = true; timeline.Start(); Status?.Invoke("Playing Through Mic · " + Path.GetFileNameWithoutExtension(file)); }
                if (activated && !paused && monitor != null && !monitorStarted && timeline.Elapsed.TotalMilliseconds >= preludeSeconds * 1000 + Math.Max(0, -settings.RelativeMicrophoneDelayMs)) {
                    var workspace = new Workspace(); workspace.Load(monitor, "Monitor", false);
                    speaker.Play(workspace, Math.Min(workspace.Frames - 1, (long)(startSeconds * Workspace.Rate)), workspace.Frames, false, endpointId: settings.MonitorEndpointId, volume: MonitorVolume()); monitorStarted = true;
                }
                // Pause affects only the sound. The native gate restores voice while paused.
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
            bridge.Stop();
            if (monitor != null && !monitorStarted) {
                await Task.Delay((int)Math.Max(0, preludeSeconds * 1000 + Math.Max(0, -settings.RelativeMicrophoneDelayMs) - timeline.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
                var workspace = new Workspace(); workspace.Load(monitor, "Monitor", false);
                speaker.Play(workspace, Math.Min(workspace.Frames - 1, (long)(startSeconds * Workspace.Rate)), workspace.Frames, false, endpointId: settings.MonitorEndpointId, volume: MonitorVolume());
            }
            while (speaker.Active) { ct.ThrowIfCancellationRequested(); await Task.Delay(10, ct).ConfigureAwait(false); }
            await Task.Delay(settings.AutoKeyReleaseDelayMs, ct).ConfigureAwait(false);
        } finally {
            bridge?.Stop(); speaker.Stop();
            if (keys != null) await keys.DisposeAsync().ConfigureAwait(false);
            if (bridge != null) bridge.Dispose();
            try { keepAlive.StopRecording(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception ex) { AppLog.Warn("soundboard.microphone.cleanup", ex); }
            if (sequence != null) try { File.Delete(sequence); } catch (Exception ex) { AppLog.Warn("soundboard.sequence.cleanup", ex); }
        }
    }
}
