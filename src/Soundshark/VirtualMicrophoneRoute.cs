using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Soundshark.Core;

namespace Soundshark;

public interface IVirtualMicrophoneRoute
{
    VirtualClipMixer Mixer { get; }
    string Status { get; }
    Task StartAsync(SoundboardConfiguration settings, bool requireConnection);
    Task StopAsync();
}

public sealed class VirtualMicrophoneRoute : IVirtualMicrophoneRoute
{
    public VirtualClipMixer Mixer { get; } = new();
    string status = "Virtual microphone disabled.";
    public string Status => Volatile.Read(ref status);
    CancellationTokenSource? lifetime;
    Task runner = Task.CompletedTask;
    int reconnect;
    sealed class Connection
    {
        readonly MMDevice microphone, cable, recording;
        readonly WasapiCapture capture;
        readonly WasapiOut output;
        readonly VirtualVoiceBuffer voice;
        readonly VirtualClipMixer mixer;
        volatile bool accepting;
        long lastCapture;
        Exception? failure;
        Task? stopping;
        public bool Healthy => failure == null && microphone.State == DeviceState.Active && cable.State == DeviceState.Active && recording.State == DeviceState.Active &&
            Environment.TickCount64 - Interlocked.Read(ref lastCapture) < 2500 && Environment.TickCount64 - mixer.LastRead < 2500;
        public Connection(SoundboardConfiguration settings, VirtualClipMixer mixer)
        {
            this.mixer = mixer;
            using var devices = new MMDeviceEnumerator();
            microphone = devices.GetDevice(settings.VirtualVoiceEndpointId);
            try { cable = devices.GetDevice(settings.TransmissionEndpointId); }
            catch { microphone.Dispose(); throw; }
            try { recording = devices.GetDevice(settings.CableRecordingEndpointId); }
            catch { microphone.Dispose(); cable.Dispose(); throw; }
            try {
                if (microphone.State != DeviceState.Active || cable.State != DeviceState.Active || recording.State != DeviceState.Active ||
                    microphone.DataFlow != DataFlow.Capture || cable.DataFlow != DataFlow.Render || recording.DataFlow != DataFlow.Capture ||
                    microphone.ID == recording.ID || microphone.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                    !cable.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase) || !recording.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
                    throw new SoundboardException("Reconnect the saved physical voice source, CABLE Input and CABLE Output devices, or change their selections in Soundboard Settings.");
                capture = new WasapiCapture(microphone, true, 20) { ShareMode = AudioClientShareMode.Shared };
                var format = capture.WaveFormat.AsStandardWaveFormat();
                bool ieee = format.Encoding == WaveFormatEncoding.IeeeFloat;
                if ((!ieee && format.Encoding != WaveFormatEncoding.Pcm) || format.BitsPerSample is not (16 or 24 or 32)) throw new SoundboardException("This microphone format is unsupported. Choose PCM or IEEE float in Windows audio properties.");
                voice = new(format.SampleRate);
                output = new WasapiOut(cable, AudioClientShareMode.Shared, true, 20);
                capture.DataAvailable += (_, e) => {
                    if (!accepting) return;
                    try { voice.Append(e.Buffer.AsSpan(0, e.BytesRecorded), format.Channels, format.BitsPerSample, ieee); Interlocked.Exchange(ref lastCapture, Environment.TickCount64); }
                    catch (Exception ex) { failure = ex; }
                };
                capture.RecordingStopped += (_, e) => { if (accepting) failure = e.Exception ?? new SoundboardException("The voice source stopped."); };
                output.PlaybackStopped += (_, e) => { if (accepting) failure = e.Exception ?? new SoundboardException("The cable output stopped."); };
                mixer.Connect(voice, (float)settings.VoiceVolume);
                output.Init(new SampleToWaveProvider(mixer));
                accepting = true; Interlocked.Exchange(ref lastCapture, Environment.TickCount64);
                output.Play(); capture.StartRecording();
            } catch {
                accepting = false; mixer.Disconnect();
                capture?.Dispose(); output?.Dispose(); microphone.Dispose(); cable.Dispose(); recording.Dispose(); throw;
            }
        }
        public async Task StopAsync()
        {
            accepting = false; mixer.Disconnect();
            stopping ??= Task.Run(() => {
                try { capture.StopRecording(); output.Stop(); }
                finally { try { capture.Dispose(); output.Dispose(); } finally { microphone.Dispose(); cable.Dispose(); recording.Dispose(); } }
            });
            // Never open a replacement if the previous driver has not released its route.
            await stopping.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }
    }
    public async Task StartAsync(SoundboardConfiguration settings, bool requireConnection)
    {
        if (lifetime != null) throw new InvalidOperationException("The virtual route is already started.");
        settings.Validate();
        if (!settings.UsesContinuousVirtualMic) throw new InvalidOperationException("Virtual routing is not enabled.");
        lifetime = new(); var ct = lifetime.Token;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SystemEvents.PowerModeChanged += PowerChanged;
        runner = Task.Run(async () => {
            Connection? connection = null;
            try {
                while (!ct.IsCancellationRequested) {
                    try {
                        if (connection == null) {
                            Volatile.Write(ref status, "Connecting to saved voice and cable devices...");
                            connection = new Connection(settings, Mixer);
                            Volatile.Write(ref status, "Connected. Select CABLE Output in Discord / your game. Voice passthrough is running."); ready.TrySetResult();
                        }
                        await Task.Delay(300, ct).ConfigureAwait(false);
                        if (Interlocked.Exchange(ref reconnect, 0) != 0 || !connection.Healthy) throw new SoundboardException("The saved audio devices disconnected or Windows audio resumed.");
                    } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex) {
                        Mixer.Disconnect(ex);
                        if (connection != null) { await connection.StopAsync().ConfigureAwait(false); connection = null; }
                        Volatile.Write(ref status, "Disconnected. Retrying only the saved devices. " + ex.Message);
                        if (!ready.Task.IsCompleted && requireConnection) { ready.TrySetException(ex); return; }
                        ready.TrySetResult(); await Task.Delay(1500, ct).ConfigureAwait(false);
                    }
                }
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { ready.TrySetException(ex); Volatile.Write(ref status, "Audio route could not shut down safely. Restart Soundshark before reconnecting. " + ex.Message); throw; }
            finally { Mixer.Disconnect(); if (connection != null) await connection.StopAsync().ConfigureAwait(false); ready.TrySetCanceled(); }
        });
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); }
        catch { try { await StopAsync().ConfigureAwait(false); } catch (Exception ex) { AppLog.Warn("virtual.route.start.cleanup", ex); } throw; }
    }
    void PowerChanged(object sender, PowerModeChangedEventArgs e) { Interlocked.Exchange(ref reconnect, 1); Mixer.Disconnect(new SoundboardException("Windows audio power state changed; clip stopped.")); }
    public async Task StopAsync()
    {
        SystemEvents.PowerModeChanged -= PowerChanged;
        lifetime?.Cancel(); Mixer.Disconnect();
        await runner.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        lifetime?.Dispose(); lifetime = null;
    }
}

/// <summary>Route changes are serialized with clip cancellation and transactional persistence.</summary>
public sealed class MicrophoneRoutingController
{
    readonly Func<IVirtualMicrophoneRoute> factory;
    readonly SemaphoreSlim gate = new(1);
    IVirtualMicrophoneRoute? route;
    SoundboardConfiguration current;
    volatile bool safeForTransmission = true;
    public bool SafeForTransmission => safeForTransmission;
    public VirtualClipMixer? Mixer => Volatile.Read(ref route)?.Mixer;
    public string Status => !safeForTransmission ? "The previous audio route did not confirm shutdown. Transmission is blocked; restart Soundshark before reconnecting." : Volatile.Read(ref route)?.Status ?? "Virtual microphone disabled.";
    public MicrophoneRoutingController(SoundboardConfiguration settings, Func<IVirtualMicrophoneRoute>? factory = null)
    { current = settings; this.factory = factory ?? (() => new VirtualMicrophoneRoute()); }
    async Task StopRouteAsync()
    {
        if (route == null) return;
        safeForTransmission = false;
        await route.StopAsync();
        route = null;
        safeForTransmission = true;
    }
    public async Task InitializeAsync()
    {
        await gate.WaitAsync();
        try { if (current.UsesContinuousVirtualMic && route == null) { route = factory(); await route.StartAsync(current, false); } }
        finally { gate.Release(); }
    }
    public async Task ApplyAsync(SoundboardConfiguration next, Func<SoundboardConfiguration, Task> persist)
    {
        next.Validate(); await gate.WaitAsync(); var previous = current;
        try {
            await StopRouteAsync();
            try {
                if (next.UsesContinuousVirtualMic) { route = factory(); await route.StartAsync(next, true); }
                await persist(next); current = next;
            } catch (Exception original) {
                var errors = new List<Exception> { original };
                if (route != null) {
                    try { await StopRouteAsync(); }
                    catch (Exception ex) { errors.Add(ex); }
                }
                try { await persist(previous); } catch (Exception ex) { errors.Add(ex); }
                if (route == null && previous.UsesContinuousVirtualMic) {
                    try { route = factory(); await route.StartAsync(previous, false); } catch (Exception ex) { errors.Add(ex); }
                }
                if (errors.Count > 1) throw new AggregateException("Routing change failed. Previous settings were restored in memory; check the connection status before using the microphone.", errors);
                throw;
            }
        } finally { gate.Release(); }
    }
    public async Task StopAsync()
    { await gate.WaitAsync(); try { await StopRouteAsync(); } finally { gate.Release(); } }
}
