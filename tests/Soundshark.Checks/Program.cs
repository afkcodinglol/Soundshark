using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Soundshark;
using Soundshark.Core;

internal static partial class Program
{
    static int passed;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle file, uint control, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
    static string output = "";
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS " + name); }
    [STAThread]
    static int Main(string[] args)
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        output = Path.GetFullPath(args.FirstOrDefault() ?? "outputs/qa"); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SOUNDSHARK_TEST_ROOT", Path.Combine(output, "sandbox-" + Guid.NewGuid().ToString("N")));
        var app = new App(); app.InitializeComponent();
        int code = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                Paths.Init();
                if (args.Contains("--url-ui")) await UrlUi();
                else if (args.Contains("--update-tools"))
                {
                    string previous = Paths.Tools; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    var toolProgress = new ToolReports(p => Console.WriteLine(p.Status));
                    await ToolUpdater.Update(timeout.Token, toolProgress);
                    string active = Paths.Tools;
                    var repeated = new ToolReports(p => Console.WriteLine(p.Status));
                    await ToolUpdater.Update(timeout.Token, repeated);
                    Check(Paths.Tools == active && repeated.Values.Last().Status.Contains("You're up to date") && !repeated.Values.Any(p => p.Status.StartsWith("Downloading")), "a second live tool update checks versions and skips all downloads and activation");
                    if (active != previous) {
                        Check(toolProgress.Values.Last().Percent == 100 && toolProgress.Values.Any(p => p.Percent is > 0 and < 90), "real GitHub tool downloads report byte progress and verified completion");
                        await ToolUpdater.Rollback(); Check(Paths.Tools == previous, "rollback restores previous complete toolset");
                    } else Check(toolProgress.Values.Last().Status.Contains("You're up to date"), "current bundled tools need no live download");
                }
                else if (args.Contains("--virtual-mic")) { app.ShutdownMode=ShutdownMode.OnExplicitShutdown; await VirtualMicChecks(); }
                else if (args.Contains("--devices")) await Devices();
                else if (args.Contains("--pc-limit")) await PcLimit();
                else if (args.Contains("--pc-volume")) await PcVolumeChecks();
                else if (args.Contains("--live-soundboard")) await LiveSoundboardChecks();
                else if (args.Contains("--live-soundboard-matched")) await LiveSoundboardChecks(matched: true);
                else if (args.Contains("--live-output-levels")) { app.ShutdownMode = ShutdownMode.OnExplicitShutdown; await LiveSoundboardChecks(matched: true, level: .5); await LiveSoundboardChecks(matched: true, level: 0); }
                else if (args.Contains("--live-library-file")) await LiveLibraryFile(args[Array.IndexOf(args, "--live-library-file") + 1]);
                else if (args.Contains("--app-updates")) await AppUpdateChecks();
                else if (args.Contains("--rollback")) await RollbackChecks();
                else if (args.Contains("--shutdown")) await ShutdownChecks();
                else if (args.Contains("--mouse-shortcuts")) await MouseShortcutChecks();
                else if (args.Contains("--hub")) { app.ShutdownMode = ShutdownMode.OnExplicitShutdown; await HubChecks(); }
                else if (args.Contains("--library-first-open")) await LibraryFirstOpenChecks();
                else if (args.Contains("--full-preview")) {
                    using (var writer = new NAudio.Wave.WaveFileWriter(Path.Combine(Paths.Cache, "Six seconds.wav"), new NAudio.Wave.WaveFormat(48000, 16, 2))) writer.Write(new byte[48000 * 4 * 6], 0, 48000 * 4 * 6);
                    var window = new MainWindow { ShowActivated = false }; window.Show(); await Task.Delay(100); await FullPreviewChecks(window); window.Studio.Workspace.Dirty = false; window.Close();
                }
                else if (args.Contains("--library-grid")) { var window = new MainWindow { ShowActivated = false }; window.Show(); await LibraryGridChecks(window); window.Close(); }
                else if (args.Contains("--library-views")) { app.ShutdownMode=ShutdownMode.OnExplicitShutdown; await LibraryViewChecks(); }
                else if (args.Contains("--library-colors")) { var window = new MainWindow { ShowActivated = false }; window.Show(); await LibraryColorChecks(window); window.Close(); }
                else if (args.Contains("--tool-update-progress")) { await ToolProgressChecks(); var window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000 }; window.Show(); await ToolProgressUiChecks(window); window.Close(); Console.WriteLine($"{passed} import tool progress checks passed"); }
                else if (args.Contains("--footer-ui")) await FooterDiagnostic();
                else if (args.Contains("--live-clipper")) await LiveClipperChecks();
                else if (args.Contains("--track-volume")) { app.ShutdownMode=ShutdownMode.OnExplicitShutdown; await TrackVolumeChecks(); }
                else if (args.Contains("--studio-layout")) { app.ShutdownMode=ShutdownMode.OnExplicitShutdown; await StudioLayoutChecks(); }
                else if (args.Contains("--reorg")) { app.ShutdownMode=ShutdownMode.OnExplicitShutdown; await ReorgChecks(); }
                else if (args.Contains("--local-ui")) { app.ShutdownMode = ShutdownMode.OnExplicitShutdown; await LocalUiChecks(); }
                else if (args.Contains("--appearance-ui")) {
                    var window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000 }; window.Show(); await Task.Delay(100);
                    await AppearanceChecks(window); window.Close(); Console.WriteLine($"{passed} appearance checks passed");
                }
                else if (args.Contains("--shortcut-ui")) {
                    var window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000 }; window.Show(); await Task.Delay(100);
                    await ShortcutUiChecks(window); window.Close(); Console.WriteLine($"{passed} shortcut UI checks passed");
                }
                else if (args.Contains("--url"))
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    string pcm = await MediaTools.Download(args[Array.IndexOf(args, "--url") + 1], timeout.Token, Console.WriteLine);
                    Console.WriteLine($"URL IMPORT OK: {new FileInfo(pcm).Length / 8.0 / 48000:F3}s decoded stereo audio");
                }
                else if (args.Contains("--soundboard-config")) SoundboardConfigChecks();
                else if (args.Contains("--conversion")) await ConversionChecks();
                else if (args.Contains("--conversion-safety")) await SafetyChecks();
                else { ClipperChecks(); SoundboardConfigChecks(); await LoggingChecks(); await ShutdownChecks(); await MouseShortcutChecks(); await Checks(); await ConversionChecks(); await SafetyChecks(); await PcVolumeChecks(); await SoundboardChecks(); await AutoKeyChecks(); await AppUpdateChecks(); await RollbackChecks(); await ToolProgressChecks(); await Render(); Console.WriteLine($"{passed} checks passed"); }
                code = 0;
            }
            catch (Exception ex) { Console.WriteLine(ex); }
            finally { foreach (var window in app.Windows.OfType<MainWindow>()) window.Studio.Workspace.Dirty = false; app.Shutdown(); }
        };
        app.Run(); return code;
    }
    static void ClipperChecks()
    {
        var buffer = new RollingAudioBuffer(4, 4, 2);
        byte[] Frames(params int[] values) { byte[] bytes = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length); return bytes; }
        int[] Values(byte[] bytes) { int[] values = new int[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length); return values; }
        buffer.Write(Frames(1, 2, 3));
        Check(Values(buffer.Snapshot()).SequenceEqual(new[] { 1, 2, 3 }), "Clipper warmup includes only available audio");
        buffer.Write(Frames(4, 5, 6, 7, 8, 9, 10));
        Check(Values(buffer.Snapshot()).SequenceEqual(Enumerable.Range(3, 8)), "Clipper wraps and retains newest frames in chronological order");
        var snapshot = buffer.Snapshot(); buffer.Write(Frames(11, 12));
        Check(Values(snapshot).SequenceEqual(Enumerable.Range(3, 8)) && Values(buffer.Snapshot()).SequenceEqual(Enumerable.Range(5, 8)), "taking a clip leaves history running and snapshots independent");
        buffer.Write(Frames(Enumerable.Range(20, 20).ToArray()));
        Check(Values(buffer.Snapshot()).SequenceEqual(Enumerable.Range(32, 8)), "oversized callback discards older frames without growing storage");
        buffer.Silence(3);
        Check(Values(buffer.Snapshot()).SequenceEqual(new[] { 35, 36, 37, 38, 39, 0, 0, 0 }), "Clipper silence advances history instead of preserving stale sound");
        buffer.Silence(1000000000);
        Check(buffer.Snapshot().All(b => b == 0) && buffer.Capacity == 32, "long silence clears bounded history in one operation");
        var maximum = new RollingAudioBuffer(48000, 8, 60);
        byte[] packet = new byte[4096];
        // Measure steady capture writes after runtime/span helpers have warmed up.
        for (int i = 0; i < 1000; ++i) maximum.Write(packet);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; ++i) maximum.Write(packet);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Clipper allocation fixture: bytes={allocated}, capacity={maximum.Capacity}, frames={maximum.Frames}");
        Check(allocated == 0 && maximum.Capacity == 23040000 && maximum.Frames == 48000 * 60, "60-second stereo buffer has fixed 23 MB storage and allocation-free capture writes");
        try { _ = new RollingAudioBuffer(48000, 8, 61); throw new Exception("oversized history accepted"); }
        catch (ArgumentOutOfRangeException) { Check(true, "Clipper rejects lengths above sixty seconds"); }
        try { buffer.Write(new byte[3]); throw new Exception("partial frame accepted"); }
        catch (ArgumentException) { Check(true, "Clipper rejects incomplete audio frames"); }
    }
    static async Task LiveClipperChecks()
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        using var outputDevice = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        using var captureDevice = enumerator.GetDevice(outputDevice.ID);
        using var clipper = new ClipperSession(captureDevice, 2);
        clipper.Start(); await Task.Delay(450);
        var warm = clipper.Snapshot();
        Check(warm.Audio.Length > 0 && warm.Audio.Length < warm.Format.AverageBytesPerSecond, "live Clipper warms up with elapsed silence and no invented full buffer");
        string reference = Path.Combine(output, "clipper-reference.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(reference, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))) {
            float[] samples = new float[48000 * 4 * 2];
            for (int f = 0; f < 48000 * 4; ++f) { float value = (float)(.03 * Math.Sin(2 * Math.PI * (f < 48000 ? 440 : 880) * f / 48000)); samples[f * 2] = samples[f * 2 + 1] = value; }
            writer.WriteSamples(samples, 0, samples.Length);
        }
        using var reader = new NAudio.Wave.AudioFileReader(reference);
        using var player = new NAudio.Wave.WasapiOut(outputDevice, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 50);
        player.Init(reader); player.Play(); await Task.Delay(3300);
        var snapshot = clipper.Snapshot(); player.Stop();
        Check(snapshot.Audio.Length == snapshot.Format.AverageBytesPerSecond * 2 && clipper.BufferBytes == snapshot.Audio.Length, "live PC audio history remains exactly bounded after wrapping");
        string wav = Path.Combine(output, "clipper-received.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(wav, snapshot.Format)) writer.Write(snapshot.Audio, 0, snapshot.Audio.Length);
        string pcm = await MediaTools.Decode(wav, CancellationToken.None); byte[] bytes = await File.ReadAllBytesAsync(pcm);
        double Amplitude(int frequency) {
            int frames = bytes.Length / 8; double real = 0, imaginary = 0;
            for (int f = 0; f < frames; ++f) { double value = BitConverter.ToSingle(bytes, f * 8), phase = 2 * Math.PI * frequency * f / 48000; real += value * Math.Cos(phase); imaginary += value * Math.Sin(phase); }
            return 2 * Math.Sqrt(real * real + imaginary * imaginary) / frames;
        }
        Console.WriteLine($"LIVE CLIPPER {outputDevice.FriendlyName} latest={Amplitude(880):F5} old={Amplitude(440):F5} buffer={clipper.BufferBytes}");
        Check(Amplitude(880) > .005 && Amplitude(440) < Amplitude(880) * .1, "live clip retains the recent PC test tone and discards the old tone");
        await Task.Delay(2300); var quiet = clipper.Snapshot();
        Check(quiet.Audio.Length == snapshot.Audio.Length && !clipper.Finished, "Clipper stays armed after a clip and handles trailing silence without growing");
        await clipper.Stop(); Check(clipper.Finished && clipper.Failure == null, "live Clipper releases the capture device cleanly when stopped");
        var window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000 }; window.Show(); await Task.Delay(100);
        var toggle = Descendants<System.Windows.Controls.CheckBox>(window.Studio).Single(x => x.Name == "ClipperToggle");
        var length = Descendants<System.Windows.Controls.TextBox>(window.Studio).Single(x => x.Name == "ClipperSeconds");
        length.Text = "2"; toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Task.Delay(1100);
        Check(Settings.Load().ClipperEnabled && Settings.Load().ClipperSeconds == 2, "live Clipper toggle and duration persist through UI settings");
        Snapshot(window, "studio-clipper.png");
        var clipButton = Descendants<System.Windows.Controls.Button>(window.Studio).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Clip Last 2 Seconds");
        clipButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var deadline = DateTime.UtcNow.AddSeconds(8); while (!window.Available && DateTime.UtcNow < deadline) await Task.Delay(30);
        Check(window.Studio.Workspace.Seconds > .5 && window.Studio.Workspace.Seconds <= 2.01, "actual Clip Last button loads recent audio into the editable Studio track");
        var sources = Descendants<System.Windows.Controls.ComboBox>(window.Studio).Single(x => x.Name == "RecordingSource");
        sources.SelectedIndex = 1; await Task.Delay(300);
        var light = Descendants<RecordingLight>(window.Studio).Single();
        Check(!toggle.IsVisible && !light.IsRecording && window.Studio.Workspace.Frames > 0, "switching to microphone stops Clipper and preserves the edited track");
        sources.SelectedIndex = 0; await Task.Delay(300);
        Check(light.IsRecording, "switching back to PC Audio resumes the enabled Clipper");
        length.Text = "0"; toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(300);
        Check(!light.IsRecording && !Settings.Load().ClipperEnabled && length.Text == "2", "turning Clipper off always stops capture even when the seconds field was invalid");
        window.Studio.Workspace.Dirty = false; window.Close();
        Console.WriteLine($"{passed} live Clipper checks passed");
    }
    static async Task LiveSoundboardChecks(bool matched = false, double level = 1)
    {
        var saved = System.Text.Json.JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundshark", "settings.json")))!;
        var config = (saved.Soundboard with { BlockVoiceDuringSounds = true, HearTransmittedSounds = matched && saved.Soundboard.HearTransmittedSounds, AutoKeysEnabled = false, RepeatSounds = false, VoiceVolume = 1, TransmittedSoundVolume = level }).ForSoundOutput();
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator(); using var device = enumerator.GetDevice(config.MicrophoneEndpointId);
        Console.WriteLine("LIVE ENDPOINT " + device.FriendlyName + " " + device.ID);
        string source = Path.Combine(output, "transmitted-test-tone.wav"), recording = Path.Combine(output, "microphone-received-test.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(source, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))) {
            float[] samples = new float[48000 * 2 * 2]; for (int frame = 0; frame < 96000; ++frame) { double t = frame / 48000.0; float value = (float)((Math.Sin(2 * Math.PI * 997 * t) + .6 * Math.Sin(2 * Math.PI * 1423 * t)) * .06); samples[frame * 2] = samples[frame * 2 + 1] = value; }
            writer.WriteSamples(samples, 0, samples.Length);
        }
        double expectedAmplitude = .06;
        {
            string prepared = await SoundboardAudio.PrepareMicrophone(source, 48000, config, CancellationToken.None);
            byte[] reference = await File.ReadAllBytesAsync(prepared);
            double real = 0, imaginary = 0;
            for (int i = 0; i < 12000; i++) { double sample = BitConverter.ToSingle(reference, i * 8); double phase = 2 * Math.PI * 997 * i / 48000; real += sample * Math.Cos(phase); imaginary += sample * Math.Sin(phase); }
            expectedAmplitude = 2 * Math.Sqrt(real * real + imaginary * imaginary) / 12000 * level;
            Console.WriteLine($"MATCHED target={config.TargetLufs} boostLimit={config.MaximumBoostDb} volume={level:P0} expectedAmplitude={expectedAmplitude:F6}");
        }
        using var capture = new NAudio.CoreAudioApi.WasapiCapture(device);
        using var received = new NAudio.Wave.WaveFileWriter(recording, capture.WaveFormat);
        var ended = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.DataAvailable += (_, e) => received.Write(e.Buffer, 0, e.BytesRecorded);
        capture.RecordingStopped += (_, e) => ended.TrySetResult(e.Exception);
        var window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000 };
        window.Settings.Soundboard = config; window.Show();
        string librarySound = Path.Combine(Paths.Library, "Live microphone test.wav"); File.Copy(source, librarySound, true); await window.RefreshLibrary();
        ((System.Windows.Controls.TabControl)window.FindName("Tabs")).SelectedIndex = 1; await Task.Delay(100);
        var board = window.Soundboard; var statuses = new System.Collections.Concurrent.ConcurrentQueue<string>(); board.Status += s => { statuses.Enqueue(s); Console.WriteLine("STATUS " + s); };

        try {
            capture.StartRecording(); await Task.Delay(400);
            try { using var bridge = NativeAudioBridge.Open(device.ID); Console.WriteLine($"NATIVE rate={bridge.Rate} alive={bridge.Alive}"); Check(bridge.Alive && bridge.Rate > 0, "real Windows microphone APO publishes a live native bridge after restart"); }
            catch { Console.WriteLine("Native initialization failed; see the Windows trace."); throw; }
            var micButton = Descendants<System.Windows.Controls.Button>(window).Single(b => b.Content is string text && text == "Play Through Mic");
            micButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await Task.Delay(100);
            var deadline = DateTime.UtcNow.AddSeconds(12); while (board.Active && DateTime.UtcNow < deadline) await Task.Delay(40);
            Check(!board.Active && statuses.Any(s => s.StartsWith("Finished")), "Library Play Through Mic button completes a real two-second transmission");
            await Task.Delay(400);
            using var mapping = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting("Global\\Soundshark-Audio-v1-" + NativeAudioBridge.EndpointGuid(device.ID), System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
            using var state = mapping.CreateViewAccessor(0, NativeAudioBridge.Bytes, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            Check(Environment.TickCount64 - state.ReadInt64(56) < 1500 && Environment.TickCount64 - state.ReadInt64(48) < 1500, "real microphone producer and native graph heartbeats remain live");
            Check(state.ReadInt32(20) == 0 && state.ReadSingle(72) == 1, "real EOF clears transmission/voice gating and restores unity voice gain");
            board.Play(librarySound); await Task.Delay(600); board.TogglePause(); await Task.Delay(150);
            long pausedFrame = state.ReadInt64(64); await Task.Delay(180);
            Check((state.ReadInt32(20) & 4) != 0 && state.ReadInt64(64) == pausedFrame, "real microphone pause holds the clip position and publishes voice-restoring pause");
            board.TogglePause(); await Task.Delay(180);
            Check(state.ReadInt64(64) > pausedFrame, "real microphone playback resumes from its paused frame");
            await board.StopAsync(); await Task.Delay(100);
            Check(state.ReadInt32(20) == 0 && !board.Active, "real microphone Stop clears transmission and completes cleanup");

        } finally { await board.StopAsync(); capture.StopRecording(); var error = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5)); if (error != null) throw error; received.Flush(); }
        received.Dispose();
        Check(new FileInfo(recording).Length > 44, "independent WASAPI capture receives samples from the selected physical microphone");
        string decoded = await MediaTools.Decode(recording, CancellationToken.None); byte[] pcm = await File.ReadAllBytesAsync(decoded); int frames = pcm.Length / 8;
        double best = 0; int analysisWindow = 12000;
        for (int start = 0; start + analysisWindow <= frames; start += 3000) {
            double real = 0, imaginary = 0; for (int i = 0; i < analysisWindow; i++) { double sample = BitConverter.ToSingle(pcm, (start + i) * 8); double phase = 2 * Math.PI * 997 * i / 48000; real += sample * Math.Cos(phase); imaginary += sample * Math.Sin(phase); }
            best = Math.Max(best, 2 * Math.Sqrt(real * real + imaginary * imaginary) / analysisWindow);
        }
        Console.WriteLine($"RECEIVED tone amplitude={best:F6} frames={frames}"); Check(level == 0 ? best < .003 : best > .015, level == 0 ? "0% mutes the transmitted test tone" : "known 997 Hz test signal is present in independently captured microphone audio");
        Check(Math.Abs(best - expectedAmplitude) < .012, "received clip level matches the protected baseline times the output slider");
        Console.WriteLine($"{passed} live soundboard checks passed; no Discord/game test performed");
        window.Studio.Workspace.Dirty = false; window.Close();
    }
    static async Task LiveLibraryFile(string file)
    {
        var saved = System.Text.Json.JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundshark", "settings.json")))!;
        var config = saved.Soundboard with { BlockVoiceDuringSounds = true, AutoKeysEnabled = false, RepeatSounds = false };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)); var ct = timeout.Token;
        string prepared = await SoundboardAudio.PrepareMicrophone(file, 48000, config, ct);
        string stem = Path.GetFileNameWithoutExtension(file);
        string recording = Path.Combine(output, stem + "-physical-mic.wav");
        using var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        using var device = devices.GetDevice(config.MicrophoneEndpointId);
        using var capture = new NAudio.CoreAudioApi.WasapiCapture(device);
        using var writer = new NAudio.Wave.WaveFileWriter(recording, capture.WaveFormat);
        var ended = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);
        capture.RecordingStopped += (_, e) => ended.TrySetResult(e.Exception);
        var board = new SoundboardCoordinator(() => config);
        var statuses = new List<string>(); board.Status += s => { lock (statuses) statuses.Add(s); Console.WriteLine(s); };
        try {
            capture.StartRecording(); await Task.Delay(200, ct); board.Play(file);
            while (board.Active) await Task.Delay(30, ct);
            Check(statuses.Any(s => s.StartsWith("Finished")), stem + " transmission completed");
            await Task.Delay(300, ct);
        } finally { await board.StopAsync(); capture.StopRecording(); var error = await ended.Task.WaitAsync(TimeSpan.FromSeconds(3)); if (error != null) throw error; }
        writer.Dispose();
        async Task<float[]> Analyze(string input, bool raw) {
            string dest = Path.Combine(output, stem + (raw ? "-reference" : "-received") + ".f32");
            var args = new List<string> { "-nostdin", "-v", "error", "-y" };
            if (raw) args.AddRange(["-f", "f32le", "-ar", "48000", "-ac", "2"]);
            args.AddRange(["-i", input, "-af", "lowpass=f=1000", "-ar", "3000", "-ac", "1", "-f", "f32le", dest]);
            await MediaTools.Run(Paths.Tool("ffmpeg"), args, ct);
            byte[] bytes = await File.ReadAllBytesAsync(dest, ct); var samples = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length); return samples;
        }
        float[] reference = await Analyze(prepared, true), received = await Analyze(recording, false);
        double best = -1; int offset = 0;
        for (int start = 0; start + reference.Length <= received.Length; start++) {
            double dot = 0, a = 0, b = 0;
            for (int i = 0; i < reference.Length; i++) { double x = reference[i], y = received[start + i]; dot += x * y; a += x * x; b += y * y; }
            double correlation = a * b > 1e-20 ? dot / Math.Sqrt(a * b) : 0;
            if (correlation > best) { best = correlation; offset = start; }
        }
        Console.WriteLine($"{stem}: full-clip correlation={best:F5}, alignment={offset / 3000.0:F3}s, duration={reference.Length / 3000.0:F3}s");
        Check(best > .9, stem + " full waveform reaches independent physical-microphone capture");
        for (int part = 0; part < 3; part++) {
            double dot = 0, a = 0, b = 0;
            for (int i = reference.Length * part / 3; i < reference.Length * (part + 1) / 3; i++) { double x = reference[i], y = received[offset + i]; dot += x * y; a += x * x; b += y * y; }
            double correlation = dot / Math.Sqrt(a * b);
            Console.WriteLine($"{stem}: third {part + 1} correlation={correlation:F5}");
            Check(correlation > .85, stem + " third " + (part + 1) + " reaches microphone without being cut off");
        }
        Console.WriteLine($"{passed} exact Library clip checks passed; Discord not captured");
    }
    static async Task Devices()
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        foreach (bool booth in new[] { true, false })
        {
            var device = enumerator.GetDefaultAudioEndpoint(booth ? NAudio.CoreAudioApi.DataFlow.Capture : NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            string path = Path.Combine(Paths.Cache, booth ? "microphone-native.wav" : "loopback-native.wav");
            using (var session = new CaptureSession(device, booth, path, booth))
            {
                session.Start();
                if (booth) Thread.Sleep(5700); else await Task.Delay(1100);
                await session.Stop();
                if (session.Error != null) throw new Exception(session.Error);
            }
            using (var reader = new NAudio.Wave.WaveFileReader(path))
            {
                long frames = reader.Length / reader.WaveFormat.BlockAlign;
                Console.WriteLine($"{(booth ? "MICROPHONE" : "LOOPBACK")}: {frames} frames, {reader.WaveFormat.SampleRate} Hz, {reader.TotalTime.TotalSeconds:F6}s");
                Check(booth ? frames == reader.WaveFormat.SampleRate * 5L : reader.TotalTime.TotalSeconds >= 1, booth ? "real microphone cap at exactly five seconds with UI stalled 5.7 seconds" : "real loopback retained elapsed time including silence");
            }
            string pcm = await MediaTools.Decode(path, CancellationToken.None);
            Check(new FileInfo(pcm).Length > 0, "captured WAV successfully decoded to disk PCM");
        }
        var mic = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Multimedia);
        string earlyPath = Path.Combine(Paths.Cache, "microphone-early.wav");
        using (var early = new CaptureSession(mic, true, earlyPath)) { early.Start(); await Task.Delay(650); await early.Stop(); }
        using var earlyReader = new NAudio.Wave.WaveFileReader(earlyPath);
        Check(earlyReader.TotalTime.TotalSeconds > 0 && earlyReader.TotalTime.TotalSeconds < 5, "real microphone early stop");
        Console.WriteLine($"DEVICE CHECKS PASSED: {passed}");
    }
    static void SoundboardConfigChecks()
    {
        var config = new SoundboardConfiguration(); config.Validate();
        Check(config.Backend == MicrophoneBackend.NotConfigured && !config.AutoKeysEnabled && config.Bindings.Length == 0, "soundboard defaults do not open devices, inject keys or register shortcuts");
        var protectedOutput = (config with { VolumeMode = SoundboardVolumeMode.FixedAdjustment, FixedAdjustmentDb = 30, TargetLufs = -3, MaximumBoostDb = 30, TransmittedSoundVolume = 2, LinkStereoChannels = false }).ForSoundOutput();
        Check(protectedOutput.VolumeMode == SoundboardVolumeMode.Automatic && protectedOutput.TargetLufs == -18 && protectedOutput.MaximumBoostDb == 24 && protectedOutput.LinkStereoChannels && protectedOutput.TransmittedSoundVolume == 1, "legacy gain settings cannot bypass the protected sound output baseline or exceed 100%");
        Check((config with { TransmittedSoundVolume = .37, VoiceVolume = .8, SpeakerVolume = .6 }).ForSoundOutput() is { TransmittedSoundVolume: .37, VoiceVolume: .8, SpeakerVolume: .6 }, "sound output retains slider attenuation and independent voice/preview preferences");
        var sound = new SoundboardBinding { Chord = new(3, 0x4b), SoundFilename = "Hello.mp3" };
        var stop = new SoundboardBinding { Chord = new(3, 0x4b), Command = SoundboardCommand.Stop };
        try { (config with { Bindings = [sound, stop] }).Validate(); throw new Exception("duplicate accepted"); }
        catch (ArgumentException) { Check(true, "special and per-sound shortcuts cannot share the same gesture"); }
        try { (config with { TargetLufs = double.NaN }).Validate(); throw new Exception("invalid gain accepted"); }
        catch (ArgumentException) { Check(true, "nonfinite loudness settings are rejected"); }
        try { (config with { Bindings = [sound], AutoKeys = [new() { Enabled = true, Chord = sound.Chord }] }).Validate(); throw new Exception("self trigger accepted"); }
        catch (ArgumentException) { Check(true, "automatic keys cannot trigger their own soundboard binding"); }
        string json = System.Text.Json.JsonSerializer.Serialize(config with { Bindings = [sound], TargetLufs = -23 });
        var restored = System.Text.Json.JsonSerializer.Deserialize<SoundboardConfiguration>(json)!; restored.Validate();
        Check(restored.Bindings.Single().SoundFilename == "Hello.mp3" && restored.TargetLufs == -23, "typed soundboard configuration round-trips without losing bindings or legacy gain data");
    }
    static async Task SoundboardChecks()
    {
        using (var mapping = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew("Local\\Soundshark-Test-" + Guid.NewGuid(), NativeAudioBridge.Bytes))
        using (var view = mapping.CreateViewAccessor())
        {
            view.Write(0, 0x53484B31); view.Write(4, 3); view.Write(8, NativeAudioBridge.Capacity); view.Write(12, 48000); view.Write(16, 2); view.Write(56, Environment.TickCount64);
            using var bridge = new NativeAudioBridge(mapping);
            bridge.Begin(100, 1, 1, true); bridge.Write(Enumerable.Repeat(.25f, 20).ToArray(), 20); bridge.Activate();
            Check(view.ReadInt64(32) == 10 && view.ReadInt32(20) == 3, "managed bridge publishes complete stereo frames and voice-gate flags");
            view.Write(128, 1L); view.Write(136, bridge.Generation); view.Write(144, 7L); view.Write(152, Environment.TickCount64);
            view.Write(160, 2L); view.Write(168, bridge.Generation); view.Write(176, 5L); view.Write(184, Environment.TickCount64);
            Check(bridge.Consumed == 5, "producer tracks the slowest active native reader");
            view.Write(184, Environment.TickCount64 - 2000);
            Check(bridge.Consumed == 7, "stale native readers cannot hold the producer indefinitely");
            bridge.Pause(true); Check((view.ReadInt32(20) & 4) != 0, "pause is published without consuming the native timeline");
            bridge.Stop(); Check(view.ReadInt32(20) == 0, "stop clears transmission and voice-block flags");
        }
        string input = Path.Combine(Paths.Cache, "Original 音楽.wav");
        byte[] before = await File.ReadAllBytesAsync(input);
        var config = new SoundboardConfiguration();
        string linked = await SoundboardAudio.Prepare(input, 48000, config, true, CancellationToken.None);
        Check(new FileInfo(linked).Length > 0 && new FileInfo(linked).Length % 8 == 0, "loudness analysis and linked normalization produce complete floating-point stereo");
        string separate = await SoundboardAudio.Prepare(input, 44100, config with { LinkStereoChannels = false }, true, CancellationToken.None);
        Check(new FileInfo(separate).Length > 0 && new FileInfo(separate).Length % 8 == 0, "independent channel normalization resamples for the native endpoint");
        Check((await File.ReadAllBytesAsync(input)).SequenceEqual(before), "soundboard preparation leaves the imported file unchanged");
        Check(await SoundboardAudio.Prepare(input, 48000, config, true, CancellationToken.None) == linked, "content-addressed cache reuses matching audio preparation");
        Check(config.TargetLufs == -18 && config.MaximumBoostDb == 24, "sound output has an automatic controlled loudness baseline");
        // Higher advanced settings remain supported, but are not the default.
        config = config with { TargetLufs = -18, MaximumBoostDb = 18 };
        var ct = CancellationToken.None;
        string quiet = Path.Combine(Paths.Cache, "quiet-mic-playback.wav");
        await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=997:duration=2:sample_rate=48000", "-af", "volume=0.2", "-c:a", "pcm_f32le", quiet], ct);
        var quietBefore = await SavedAudioSafety.Measure(quiet, ct);
        var boosted = await SavedAudioSafety.Measure(await SoundboardAudio.Prepare(quiet, 48000, config, true, ct), ct, raw: true);
        var previous = await SavedAudioSafety.Measure(await SoundboardAudio.Prepare(quiet, 48000, config with { TargetLufs = -23, MaximumBoostDb = 12 }, true, ct), ct, raw: true);
        Check(Math.Abs(boosted.Lufs - config.TargetLufs) < .3 && boosted.Lufs > previous.Lufs + 4.5, "quiet microphone playback reaches the louder target and exceeds the previous default by at least 4.5 dB");
        Check(boosted.TruePeak <= -1.4 && boosted.Lufs - quietBefore.Lufs <= config.MaximumBoostDb + .2, "quiet playback respects the boost limit and true-peak headroom");
        var unchanged = await SavedAudioSafety.Measure(await SoundboardAudio.Prepare(quiet, 48000, config, false, ct), ct, raw: true);
        Check(Math.Abs(unchanged.Lufs - quietBefore.Lufs) < .2, "preview without volume matching retains its original level");
        string veryQuiet = Path.Combine(Paths.Cache, "very-quiet-mic-playback.wav");
        await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-i", quiet, "-af", "volume=0.1", veryQuiet], ct);
        var veryQuietBefore = await SavedAudioSafety.Measure(veryQuiet, ct);
        var bounded = await SavedAudioSafety.Measure(await SoundboardAudio.Prepare(veryQuiet, 48000, config, true, ct), ct, raw: true);
        Check(Math.Abs(bounded.Lufs - veryQuietBefore.Lufs - 18) < .3, "very quiet clips get at most the configured 18 dB boost");
        string silent = Path.Combine(Paths.Cache, "silent-mic-playback.wav");
        await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "1", silent], ct);
        var silence = await SavedAudioSafety.Measure(await SoundboardAudio.Prepare(silent, 48000, config, true, ct), ct, raw: true);
        Check(double.IsNegativeInfinity(silence.Lufs) && double.IsNegativeInfinity(silence.TruePeak), "louder playback defaults leave digital silence silent");
        string loudOutput = Path.Combine(Paths.Cache, "loud-microphone-source.wav");
        await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=997:duration=2:sample_rate=48000", "-af", "volume=7", "-c:a", "pcm_f32le", loudOutput], ct);
        var guarded = await SavedAudioSafety.Measure(await SoundboardAudio.PrepareMicrophone(loudOutput, 48000, config with { VolumeMode = SoundboardVolumeMode.Unchanged, TargetLufs = -3, FixedAdjustmentDb = 30, TransmittedSoundVolume = 2 }, ct), ct, raw: true);
        Check(guarded.Lufs <= -17.8 && guarded.TruePeak <= -1.4, "100% microphone output automatically reduces loud material even with legacy bypass/gain settings");
        string shortLoud = Path.Combine(Paths.Cache, "short-loud-microphone-source.wav");
        await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-i", loudOutput, "-t", "0.1", shortLoud], ct);
        var shortGuarded = await SavedAudioSafety.Measure(await SoundboardAudio.PrepareMicrophone(shortLoud, 48000, config, ct), ct, raw: true);
        Check(double.IsFinite(shortGuarded.TruePeak) && shortGuarded.TruePeak <= -11.8, "sub-400ms clips use conservative peak matching when integrated loudness is unavailable");
        Check(UserErrors.Message(new SoundboardException("Choose your microphone first."), "soundboard") == "Choose your microphone first.", "soundboard setup errors remain actionable without native diagnostic leakage");
    }
    sealed class FakeKeys : ISoundboardKeyTransport
    {
        public readonly HashSet<ushort> Held = [];
        public readonly List<(ushort Key, bool Up)> Events = [];
        public ushort FailOn;
        public bool Applicable = true;
        public bool Down(ushort key) => Held.Contains(key);
        public bool Applies(SoundboardAutoKey binding) => Applicable;
        public void Send(ushort key, bool up) { if (!up && key == FailOn) throw new IOException("fixture failure"); Events.Add((key, up)); if (up) Held.Remove(key); else Held.Add(key); }
    }
    static async Task AutoKeyChecks()
    {
        var binding = new SoundboardAutoKey { Enabled = true, Chord = new(2, 0x4b) };
        var config = new SoundboardConfiguration { AutoKeysEnabled = true, AutoKeys = [binding] };
        var keys = new FakeKeys(); keys.Held.Add(0xa2);
        var lease = await AutoKeyLease.Acquire(config, false, CancellationToken.None, keys);
        Check(keys.Events.SequenceEqual(new[] { ((ushort)0x4b, false) }), "automatic keys leave an already held user modifier alone");
        await lease.DisposeAsync(); await lease.DisposeAsync();
        Check(keys.Held.SetEquals(new ushort[] { 0xa2 }) && keys.Events.Count == 2, "automatic key cleanup releases only owned keys and is idempotent");
        keys = new FakeKeys { FailOn = 0x4b };
        try { await AutoKeyLease.Acquire(config, false, CancellationToken.None, keys); throw new Exception("Expected fake key failure"); } catch (IOException) { }
        Check(keys.Held.Count == 0 && keys.Events.SequenceEqual(new[] { ((ushort)0xa2, false), ((ushort)0xa2, true) }), "partial automatic key failure releases modifiers already acquired");
        keys = new FakeKeys();
        lease = await AutoKeyLease.Acquire(config with { AutoKeys = [binding with { HotkeyPlaybackOnly = true }] }, false, CancellationToken.None, keys); await lease.DisposeAsync();
        Check(keys.Events.Count == 0, "hotkey-only automatic keys ignore button playback");
        keys.Applicable = false; lease = await AutoKeyLease.Acquire(config, true, CancellationToken.None, keys); await lease.DisposeAsync();
        Check(keys.Events.Count == 0, "automatic key application filters skip nonmatching applications");
        keys = new FakeKeys(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await AutoKeyLease.Acquire(config, true, cancelled.Token, keys); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        Check(keys.Events.Count == 0, "cancellation before automatic key acquisition sends no keys");
        keys = new FakeKeys(); lease = await AutoKeyLease.Acquire(config with { AutoKeys = [binding with { Mode = AutoKeyMode.TapBeforeAndAfter }] }, true, CancellationToken.None, keys);
        Check(keys.Held.Count == 0 && keys.Events.Count == 4, "before-and-after mode finishes its initial tap before playback");
        await lease.DisposeAsync(); Check(keys.Held.Count == 0 && keys.Events.Count == 8, "before-and-after mode completes a balanced final tap");
    }
    static async Task PcLimit()
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        string path = Path.Combine(Paths.Cache, "limited-pc.wav");
        using (var capture = new CaptureSession(device, false, path, true))
        {
            capture.Start(); await Task.Delay(5700);
            Check(capture.Finished && capture.Error == null, "PC Audio automatically stops at Discord limit without hanging during silence filling");
            await capture.Stop().WaitAsync(TimeSpan.FromSeconds(5));
        }
        using var reader = new NAudio.Wave.WaveFileReader(path);
        Check(reader.Length / reader.WaveFormat.BlockAlign == reader.WaveFormat.SampleRate * 5L, "limited PC Audio retains exactly five seconds of native frames");
    }
    static async Task LoggingChecks()
    {
        Exception failure;
        try { throw new InvalidOperationException("VERBOSE-DIAGNOSTIC-MARKER: native stack/HRESULT details"); } catch (Exception ex) { failure = ex; }
        var report = UserErrors.Report(failure, "test.failure", new { file = "example.wav" });
        Check(!report.Display.Contains("VERBOSE-DIAGNOSTIC-MARKER") && report.Display.Contains(report.Reference), "unexpected error UI hides technical detail and shows a reference");
        await AppLog.Log.Flush();
        string logs = string.Join("\n", await Task.WhenAll(Directory.GetFiles(AppLog.Log.DirectoryPath, "*.log").Select(file => File.ReadAllTextAsync(file))));
        Check(logs.Contains("VERBOSE-DIAGNOSTIC-MARKER") && logs.Contains(report.Reference) && logs.Contains("LoggingChecks"), "error reference links to full exception and stack trace in logs");
        var tool = UserErrors.Report(new MediaToolException("ffprobe", 1, "SECRET-RAW-TOOL-OUTPUT"), "test.probe");
        Check(tool.Message.Contains("Couldn't read audio") && !tool.Display.Contains("SECRET-RAW"), "tool failures produce actionable plain-language errors");
        var blocked = UserErrors.Message(new MediaToolException("yt-dlp", 1, "Your IP address is blocked"), "url.import");
        Check(blocked.Contains("website blocked") && !blocked.Contains("IP address"), "website blocking error is friendly");
        Check(UserErrors.Message(new IOException("The disk is full"), "export").Contains("free disk space"), "disk-full errors explain the correction");
        try { FileRules.Validate(5.03, 120000, true); } catch (InvalidDataException ex) { Check(UserErrors.Message(ex, "export").Contains("Trim at least"), "friendly error handling preserves precise Discord trim guidance"); }
        string redacted = DiagnosticLog.Redact("https://example.com/video?token=SECRET-QUERY\nAuthorization: Bearer SECRET-AUTH\npassword=SECRET-PASSWORD");
        Check(!redacted.Contains("SECRET-") && redacted.Contains("example.com/video"), "diagnostic logs redact URL queries and common credentials");
        string concurrent = Path.Combine(output, "log-concurrency-" + Guid.NewGuid().ToString("N"));
        await using (var log = new DiagnosticLog(concurrent))
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(n => Task.Run(() => { for (int i = 0; i < 30; i++) log.Write("ERROR", "parallel", $"worker-{n}-entry-{i}"); })));
            await log.Flush();
            string[] lines = Directory.GetFiles(concurrent, "*.log").SelectMany(File.ReadAllLines).ToArray();
            Check(lines.Length == 240 && lines.All(line => { using var json = System.Text.Json.JsonDocument.Parse(line); return json.RootElement.GetProperty("operation").GetString() == "parallel"; }), "concurrent background writes retain complete valid log records");
        }
        string rotated = Path.Combine(output, "log-rotation-" + Guid.NewGuid().ToString("N"));
        await using (var log = new DiagnosticLog(rotated, maxBytes: 1024, maxFiles: 3))
        {
            for (int i = 0; i < 50; i++) await log.Detail("rotation", $"entry-{i}-" + new string('x', 160));
            log.Write("ERROR", "rotation.final", "LAST-ERROR"); await log.Flush();
            var files = Directory.GetFiles(rotated, "*.log");
            Check(files.Length <= 3 && files.All(file => new FileInfo(file).Length <= 1024) && files.Any(file => File.ReadAllText(file).Contains("LAST-ERROR")), "log rotation limits size and count while retaining latest errors");
        }
        string blockedPath = Path.Combine(output, "not-a-log-directory"); await File.WriteAllTextAsync(blockedPath, "blocked");
        await using (var log = new DiagnosticLog(blockedPath))
        { log.Write("ERROR", "test", "unwritable"); await log.Flush(); Check(log.StorageUnavailable, "unwritable log storage fails safely without crashing or hanging"); }
        string fallback = Path.Combine(output, "fallback-logs-" + Guid.NewGuid().ToString("N"));
        await using (var log = new DiagnosticLog(blockedPath, fallback: fallback))
        { log.Write("ERROR", "test", "FALLBACK-ERROR"); await log.Flush(); Check(log.DirectoryPath == fallback && !log.StorageUnavailable && Directory.GetFiles(fallback).Any(), "logger falls back when primary log storage is unavailable"); }
    }
    static async Task Checks()
    {
        string raw = Paths.NewPcm();
        await using (var f = File.Create(raw)) { var bytes = new byte[48000 * 8 * 6]; for (int i = 0; i < bytes.Length; i += 4) BitConverter.GetBytes((float)Math.Sin(i / 8.0 * .057) * .4f).CopyTo(bytes, i); await f.WriteAsync(bytes); }
        var w = new Workspace(); w.Load(raw, "Test"); w.Delete(48000, 96000);
        Check(w.Frames == 240000 && w.Ranges.Count == 2, "trim deletes highlighted second and stitches remaining ranges");
        using (var s = w.Open()) { s.Position = 48000 * 8 - 8; byte[] joined = new byte[16]; s.ReadExactly(joined); using var src = File.OpenRead(raw); byte[] expected = new byte[16]; src.Position = 48000 * 8 - 8; src.ReadExactly(expected.AsSpan(0, 8)); src.Position = 96000 * 8; src.ReadExactly(expected.AsSpan(8)); Check(joined.SequenceEqual(expected), "exact sample stitching at cut boundary"); }
        var keeper = new Workspace(); keeper.Load(raw, "Keep selection"); keeper.Delete(48000, 96000);
        var beforeKeep = keeper.Ranges.ToArray();
        keeper.Keep(24000, 72000);
        Check(keeper.Ranges.SequenceEqual(new[] { new SampleRange(24000, 24000), new SampleRange(96000, 24000) }) && keeper.Frames == 48000, "Keep selected retains only highlighted frames across a previous cut");
        using (var s = keeper.Open())
        {
            byte[] actual = new byte[48000 * 8], expected = new byte[48000 * 8]; s.ReadExactly(actual);
            using var src = File.OpenRead(raw); src.Position = 24000 * 8; src.ReadExactly(expected.AsSpan(0, 24000 * 8)); src.Position = 96000 * 8; src.ReadExactly(expected.AsSpan(24000 * 8));
            Check(actual.SequenceEqual(expected), "Keep selected preserves exact source samples and order");
        }
        keeper.Undo(); Check(keeper.Ranges.SequenceEqual(beforeKeep), "one Undo restores both sides removed by Keep selected");
        keeper.Redo(); Check(keeper.Frames == 48000, "Redo reapplies Keep selected as one edit");
        var noOp = new Workspace(); noOp.Load(raw, "No selection"); noOp.Keep(100, 100); noOp.Keep(0, noOp.Frames);
        Check(noOp.Frames == 288000 && !noOp.CanUndo, "empty or full Keep selected leaves audio and Undo history unchanged");
        w.Delete(0, w.Frames); Check(w.Frames == 0, "full-track deletion"); w.Undo(); Check(w.Frames == 240000, "Undo full deletion");
        for (int i = 0; i < 40; i++) { w.Redo(); Check(w.Frames == 0, "Redo empty " + i); w.Undo(); }
        Check(w.Frames == 240000, "repeated Undo returns retained audio");
        var state = w.Snapshot(); await FileRules.AtomicJsonAsync(Path.Combine(output, "snapshot.json"), state); var restored = new Workspace(); restored.Restore(state); restored.Undo(); Check(restored.Frames == 288000, "recovery restores Undo history");
        Check(FileRules.Filename("CON") == "_CON.mp3" && FileRules.Filename("  shark:clip?.mp3 ") == "shark_clip_.mp3", "Windows filename sanitization and reserved names");
        Check(FileRules.Filename("音楽 🦈") == "音楽 🦈.mp3", "Unicode filename preserved");
        var gate = new CaptureGate(44100, 8, true); while (!gate.Full) gate.Accept(7001); Check(gate.Frames == 220500 && gate.Accept(8000) == 0, "hard five-second sample gate despite oversized late callbacks");
        var early = new CaptureGate(48000, 4, true); early.Accept(123456); Check(early.Frames == 30864 && !early.Full, "early stop preserves only received samples");
        var computer = new CaptureGate(48000, 8, false); computer.Accept(48000 * 8 * 8); Check(computer.Frames == 384000, "computer recording has no Booth cap");
        FileRules.Validate(5, 512000, true); Check(true, "exact duration and byte limits accepted");
        try { FileRules.Validate(5.001, 120000, true); throw new Exception("validation missing"); } catch (InvalidDataException) { Check(true, "MP3 padding over five seconds rejected"); }
        try { FileRules.Validate(4, 512001, true); throw new Exception("validation missing"); } catch (InvalidDataException) { Check(true, "actual file size over 512000 rejected"); }
        var autoWork = new Workspace(); autoWork.Load(raw, "Automatic quality"); autoWork.Keep(0, Workspace.Rate * 3);
        var autoResult = await MediaTools.Export(autoWork, Path.Combine(Paths.Library, "automatic-quality.mp3"), 0, true, false, CancellationToken.None);
        Check(autoResult.Duration <= 5 && autoResult.Size < 512000 && autoWork.Frames == Workspace.Rate * 3, "automatic best-quality MP3 encoding passes Discord validation without trimming source audio");
        string mp3 = Path.Combine(Paths.Library, "音楽 🦈.mp3"); w.Load(raw, "Six second"); w.Delete(0, 57600);
        var info = await MediaTools.Export(w, mp3, 192, true, false, CancellationToken.None); Check(info.Duration <= 5 && info.Size < 512000, "Unicode path encoded MP3 duration and size validation");
        try { await MediaTools.Export(w, mp3, 192, false, false, CancellationToken.None); throw new Exception("overwrite guard missing"); } catch (IOException) { Check(true, "duplicate name does not overwrite without explicit authorization"); }
        byte[] before = await File.ReadAllBytesAsync(mp3);
        var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await MediaTools.Export(w, mp3, 192, false, true, cancel.Token); throw new Exception("cancel missing"); } catch (OperationCanceledException) { Check((await File.ReadAllBytesAsync(mp3)).SequenceEqual(before), "cancel retains published file"); }
        w.Load(raw, "Boundary"); w.Delete(240000, w.Frames);
        string boundary = Path.Combine(Paths.Library, "boundary.mp3"); File.Delete(boundary);
        try { var encoded = await MediaTools.Export(w, boundary, 192, true, false, CancellationToken.None); Check(encoded.Duration <= 5 && w.Frames == 240000, "five-second boundary accepted only when probed encoded duration passes"); } catch (InvalidDataException ex) { Check(ex.Message.Contains("padding") && w.Frames == 240000, "padded boundary rejected without trimming speech"); }
        string bad = Path.Combine(output, "corrupt.wav"); await File.WriteAllTextAsync(bad, "corrupt");
        try { await MediaTools.Decode(bad, CancellationToken.None); throw new Exception("corrupt accepted"); } catch (MediaToolException) { Check(true, "corrupt input reports FFmpeg error"); }
        // Sparse disk file: two hours represented without holding the PCM in memory.
        string longFile = Paths.NewPcm(); using (var f = File.Create(longFile)) {
            if (!DeviceIoControl(f.SafeFileHandle, 0x900c4, 0, 0, 0, 0, out _, 0)) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "Could not mark the long-audio test file sparse.");
            f.SetLength(2L * 3600 * 48000 * 8);
        }
        var longWork = new Workspace(); longWork.Load(longFile, "Two hours"); longWork.Delete(48000, 96000); using (var stream = longWork.Open()) { stream.Position = stream.Length - 16; byte[] end = new byte[16]; stream.ReadExactly(end); Check(longWork.Frames == 2L * 3600 * 48000 - 48000, "two-hour disk-backed seek and cut"); }
        File.Delete(longFile);
        Check(!Directory.GetFiles(Paths.Library, ".soundshark-*").Any(), "failed exports clean temporary publications");
        using (var inFlight = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
        {
            try { await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-re", "-f", "lavfi", "-i", "sine=frequency=440:duration=3600", "-f", "null", "-"], inFlight.Token); throw new Exception("running process ignored cancellation"); }
            catch (OperationCanceledException) { Check(true, "in-flight FFmpeg cancellation terminates process tree"); }
        }
        w.Load(raw, "Quality"); w.Delete(48000 * 4, w.Frames);
        foreach (int rate in new[] { 128, 192, 256, 320 })
        {
            string path = Path.Combine(Paths.Library, $"quality-{rate}.mp3"); var encoded = await MediaTools.Export(w, path, rate, true, false, CancellationToken.None);
            string probe = await MediaTools.Run(Paths.Tool("ffprobe"), ["-v", "error", "-show_entries", "stream=codec_name,codec_type,bit_rate", "-of", "json", path], CancellationToken.None);
            using var json = System.Text.Json.JsonDocument.Parse(probe); var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            Check(streams.Length == 1 && streams[0].GetProperty("codec_name").GetString() == "mp3" && streams[0].GetProperty("bit_rate").GetString() == (rate * 1000).ToString() && encoded.Duration <= 5, $"{rate} kbps CBR MP3, no artwork, Discord validation");
        }
    }
    static async Task PcVolumeChecks()
    {
        var ct = CancellationToken.None;
        Check(PcRecordingAudio.Boost(new(-40, -20)) == 18, "PC boost has an 18 dB ceiling");
        Check(PcRecordingAudio.Boost(new(-30, -3)) == 1, "PC boost respects true-peak headroom");
        Check(PcRecordingAudio.Boost(new(-12, -1)) == 0, "PC boost leaves loud captures alone for save protection");
        foreach (var level in new[] { new SavedAudioSafety.Level(-70, -60), new(double.NegativeInfinity, double.NegativeInfinity), new(double.NaN, -20) })
            Check(PcRecordingAudio.Boost(level) == 0, "PC boost skips silence, near-silence or invalid levels");
        string pcm = Paths.NewPcm();
        await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=997:duration=2:sample_rate=48000",
            "-af", "volume=-12dB", "-ac", "2", "-f", "f32le", pcm], ct);
        long length = new FileInfo(pcm).Length;
        var before = await SavedAudioSafety.Measure(pcm, ct, raw: true);
        await PcRecordingAudio.Prepare(pcm, ct);
        var after = await SavedAudioSafety.Measure(pcm, ct, raw: true);
        Check(after.Lufs > before.Lufs + 8 && Math.Abs(after.Lufs + 18) < .15 && after.Safe, "quiet PC capture reaches target loudness with safe peaks");
        Check(new FileInfo(pcm).Length == length, "PC adjustment preserves duration and stereo PCM format");
        byte[] once = await File.ReadAllBytesAsync(pcm);
        await PcRecordingAudio.Prepare(pcm, ct);
        var twice = await File.ReadAllBytesAsync(pcm);
        Check(once.SequenceEqual(twice), "PC adjustment does not compound on a prepared recording");
        var workspace = new Workspace(); workspace.Load(pcm, "PC volume check", false);
        string saved = Path.Combine(Paths.Library, "pc-volume-check.mp3");
        await MediaTools.Export(workspace, saved, 0, true, false, ct);
        var encoded = await SavedAudioSafety.Measure(saved, ct);
        Check(encoded.Safe && encoded.Lufs > before.Lufs + 7, "Library MP3 retains PC volume improvement and passes encoded safety checks");
        string silence = Paths.NewPcm();
        await File.WriteAllBytesAsync(silence, new byte[48000 * 8]);
        await PcRecordingAudio.Prepare(silence, ct);
        Check((await File.ReadAllBytesAsync(silence)).All(b => b == 0), "silent PC recording remains silent");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await PcRecordingAudio.Prepare(pcm, cancelled.Token); throw new Exception("cancelled preparation accepted"); }
        catch (OperationCanceledException) {
            var retained = await File.ReadAllBytesAsync(pcm);
            Check(once.SequenceEqual(retained), "cancelled PC adjustment preserves source audio");
        }
    }
    static async Task SafetyChecks()
    {
        var ct = CancellationToken.None;
        string loud = Path.Combine(Paths.Cache, "loud-source.wav");
        await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=997:duration=2:sample_rate=48000", "-af", "volume=7", "-c:a", "pcm_f32le", loud], ct);
        var before = await SavedAudioSafety.Measure(loud, ct);
        Check(before.Lufs > -10 && before.Reduction < -8, "loudness measurement detects excessively loud source");
        foreach (var format in AudioOutputFormat.All) {
            string saved = AudioConversion.NewDestination(loud, format);
            await AudioConversion.Convert(loud, saved, format, false, ct);
            var level = await SavedAudioSafety.Measure(saved, ct);
            Check(level.Safe && level.Lufs < before.Lufs - 8, $"{format.Label} save automatically reduces excessive loudness and verifies encoded peaks");
        }
        var workspace = new Workspace(); workspace.Load(await MediaTools.Decode(loud, ct), "Loud export", false);
        string exported = Path.Combine(Paths.Library, "loud-studio.mp3");
        await MediaTools.Export(workspace, exported, 0, true, false, ct);
        Check((await SavedAudioSafety.Measure(exported, ct)).Safe, "Studio export checks actual MP3 loudness and true peak before publishing");
        string quiet = Path.Combine(Paths.Cache, "quiet.wav"), quietSaved = Path.Combine(Paths.Library, "quiet-copy.wav");
        await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-i", loud, "-af", "volume=-35dB", quiet], ct);
        await AudioConversion.Convert(quiet, quietSaved, AudioOutputFormat.All.Single(f => f.Extension == ".wav"), false, ct);
        var quietBefore = await SavedAudioSafety.Measure(quiet, ct); var quietAfter = await SavedAudioSafety.Measure(quietSaved, ct);
        Check(quietBefore.Reduction == 0 && Math.Abs(quietBefore.Lufs - quietAfter.Lufs) < .02, "quiet audio is never boosted by save protection");
        string silent = Path.Combine(Paths.Cache, "silence.wav");
        await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "1", silent], ct);
        var silence = await SavedAudioSafety.Measure(silent, ct);
        Check(silence.Safe && silence.Reduction == 0, "silence is valid and does not receive automatic gain");
        var legacy = System.Text.Json.JsonSerializer.Deserialize<SoundboardConfiguration>("{\"MicrophoneEqEnabled\":true,\"MicrophoneEqDb\":[0,0,6,0,0]}")!;
        legacy.Validate(); Check(!System.Text.Json.JsonSerializer.Serialize(legacy).Contains("MicrophoneEq"),"removed microphone EQ settings are ignored on upgrade");
    }
    static async Task ConversionChecks()
    {
        string source = Path.Combine(Paths.Cache, "Original 音楽.wav");
        await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=997:duration=4:sample_rate=48000", "-ac", "2", "-c:a", "pcm_s24le", source], CancellationToken.None);
        byte[] original = await File.ReadAllBytesAsync(source);
        foreach (var format in AudioOutputFormat.All)
        {
            string path = AudioConversion.NewDestination(source, format);
            var result = await AudioConversion.Convert(source, path, format, false, CancellationToken.None);
            string probe = await MediaTools.Run(Paths.Tool("ffprobe"), ["-v", "error", "-show_entries", "stream=codec_name,codec_type", "-of", "json", path], CancellationToken.None);
            using var json = System.Text.Json.JsonDocument.Parse(probe); var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            string expected = format.Extension == ".wav" ? "pcm_s24le" : format.Extension == ".aiff" ? "pcm_s24be" : format.Codec;
            Check(streams.Length == 1 && streams[0].GetProperty("codec_name").GetString() == expected && Math.Abs(result.Duration - 4) < .1, $"automatic {format.Label} conversion produces correct audio-only format and duration");
            Check(AudioConversion.IsLibraryAudio(path), $"Library recognizes {format.Label} output");
        }
        Check((await File.ReadAllBytesAsync(source)).SequenceEqual(original), "conversion leaves source bytes unchanged");
        string mp3 = Path.Combine(Paths.Library, "quality-128.mp3");
        if (!File.Exists(mp3)) await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-i", source, "-c:a", "libmp3lame", "-b:a", "128k", mp3], CancellationToken.None);
        string remuxed = AudioConversion.NewDestination(mp3, AudioOutputFormat.Mp3);
        await AudioConversion.Convert(mp3, remuxed, AudioOutputFormat.Mp3, true, CancellationToken.None);
        string details = await MediaTools.Run(Paths.Tool("ffprobe"), ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=bit_rate", "-of", "default=nw=1:nk=1", remuxed], CancellationToken.None);
        Check(details.Trim() == "128000", "existing low-bitrate MP3 stays at source bitrate without re-encoding");
        async Task<string> PacketHashes(string path) => await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-i", path, "-map", "0:a:0", "-c:a", "copy", "-f", "hash", "-hash", "sha256", "-"], CancellationToken.None);
        Check(await PacketHashes(mp3) == await PacketHashes(remuxed), "MP3 conversion preserves encoded audio packets exactly");
        Check(AudioConversion.NewDestination(source, AudioOutputFormat.All.Single(x => x.Extension == ".wav")).EndsWith("(2).wav"), "converter chooses numbered filenames instead of overwriting");
        Check(FileRules.Filename("CON.wav", ".flac") == "_CON.flac", "format-aware filename sanitization");
        Check(!AudioConversion.IsLibraryAudio(Path.Combine(Paths.Library, ".soundshark-test.mp3")), "Library excludes unfinished conversion files");
        try { await AudioConversion.Convert(source, Path.Combine(Paths.Library, "discord.wav"), AudioOutputFormat.All.Single(x => x.Extension == ".wav"), true, CancellationToken.None); throw new Exception("non-MP3 Discord conversion accepted"); }
        catch (InvalidDataException) { Check(true, "converter service independently enforces MP3 in Discord mode"); }
        string longSource = Paths.NewPcm(); await using (var f = File.Create(longSource)) f.SetLength(48000 * 8 * 6);
        string longWav = Path.Combine(Paths.Cache, "Six seconds.wav");
        await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", longSource, longWav], CancellationToken.None);
        try { await AudioConversion.Convert(longWav, Path.Combine(Paths.Library, "too-long.mp3"), AudioOutputFormat.Mp3, true, CancellationToken.None); throw new Exception("long Discord conversion accepted"); }
        catch (InvalidDataException) { Check(!File.Exists(Path.Combine(Paths.Library, "too-long.mp3")), "Discord conversion rejects long tracks without publishing or truncating"); }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await AudioConversion.Convert(source, Path.Combine(Paths.Library, "cancelled.flac"), AudioOutputFormat.All.Single(x => x.Extension == ".flac"), false, cancel.Token); throw new Exception("cancel ignored"); }
        catch (OperationCanceledException) { Check(!File.Exists(Path.Combine(Paths.Library, "cancelled.flac")), "cancelled conversion publishes no output"); }
        string corrupt = Path.Combine(Paths.Cache, "broken.wav"); await File.WriteAllTextAsync(corrupt, "broken");
        try { await AudioConversion.Convert(corrupt, Path.Combine(Paths.Library, "broken.mp3"), AudioOutputFormat.Mp3, false, CancellationToken.None); throw new Exception("corrupt file accepted"); }
        catch (MediaToolException ex)
        {
            Check(!File.Exists(Path.Combine(Paths.Library, "broken.mp3")), "corrupt conversion fails without publishing");
            var error = UserErrors.Report(ex, "conversion", new { input = corrupt }); await AppLog.Log.Flush();
            var records = Directory.GetFiles(AppLog.Log.DirectoryPath, "*.log").SelectMany(File.ReadAllLines).Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
            try
            {
                Check(records.Any(record => record.RootElement.GetProperty("operation").GetString() == "tool.ffprobe.stderr" && record.RootElement.GetProperty("detail").GetString()!.Contains("broken.wav")), "real tool stderr is retained in diagnostic logs");
                Check(records.Any(record => record.RootElement.TryGetProperty("reference", out var reference) && reference.GetString() == error.Reference), "real corrupt-file error is correlated with the UI reference");
            }
            finally { foreach (var record in records) record.Dispose(); }
        }
    }
    static async Task Render()
    {
        // A known six-second fixture keeps selection checks independent of filesystem order.
        string previous = Paths.NewPcm();
        var samples = new float[48000 * 2 * 6];
        for (int i = 0; i < samples.Length; i++) samples[i] = (float)(.1 * Math.Sin(2 * Math.PI * 440 * (i / 2) / 48000));
        var sampleBytes = new byte[samples.Length * sizeof(float)]; Buffer.BlockCopy(samples, 0, sampleBytes, 0, sampleBytes.Length);
        await File.WriteAllBytesAsync(previous, sampleBytes);
        var oldWorkspace = new Workspace(); oldWorkspace.Load(previous, "Previous unsaved workspace");
        await FileRules.AtomicJsonAsync(Path.Combine(Paths.Cache, "editor.json"), oldWorkspace.Snapshot());
        await FileRules.AtomicJsonAsync(Path.Combine(Paths.Cache, "booth.json"), oldWorkspace.Snapshot());
        var window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000 }; window.Show();
        await Task.Delay(300);
        Check(Descendants<System.Windows.Controls.ComboBox>(window.Studio).Single(x => x.Name == "AudioDevice").IsVisible, "audio device selector is visible without expanding any controls");
        var preciseControls = Descendants<System.Windows.Controls.StackPanel>(window.Studio).Single(x => x.Name == "PreciseSelection");
        Check(preciseControls.IsVisible && !Descendants<System.Windows.Controls.Expander>(window.Studio).Any(expander=>Descendants<System.Windows.Controls.StackPanel>(expander).Contains(preciseControls)), "precise selection is always visible without an expander");
        Check(window.Studio.Workspace.Frames == 0, "Studio startup stays empty despite both legacy recovery snapshots");
        var initialTabs = (System.Windows.Controls.TabControl)window.FindName("Tabs");
        Check(initialTabs.Items.Cast<System.Windows.Controls.TabItem>().Select(t => t.Header.ToString()).SequenceEqual(new[] { "The Studio", "Library", "Audio Converter" }), "exactly three tabs with the requested names");
        Check(Descendants<StudioPane>(window).Count() == 1, "one shared Studio track replaces separate Editor and Booth panes");
        Snapshot(window, "startup-studio-empty.png");
        Check(window.Studio.Wave.Cursor == System.Windows.Input.Cursors.Arrow, "waveform uses the normal pointer rather than a text cursor");
        var sourceChoice = Descendants<System.Windows.Controls.ComboBox>(window.Studio).Single(x => x.Name == "RecordingSource");
        Check(sourceChoice.SelectedIndex == 0, "PC audio is the default recording source");
        var clipperToggle = Descendants<System.Windows.Controls.CheckBox>(window.Studio).Single(x => x.Name == "ClipperToggle");
        var clipperLength = Descendants<System.Windows.Controls.TextBox>(window.Studio).Single(x => x.Name == "ClipperSeconds");
        Check(clipperToggle.IsVisible && clipperToggle.IsChecked != true && clipperLength.Text == "15", "PC Audio shows opt-in Clipper and a fifteen-second default");
        await window.Studio.LoadPcm(previous, "Shared Studio track");
        long recoveredFrames = window.Studio.Workspace.Frames;
        sourceChoice.SelectedIndex = 1;
        Check(!clipperToggle.IsVisible && !clipperLength.IsVisible, "Microphone hides PC-only Clipper controls");
        Check(window.Studio.Workspace.Frames == recoveredFrames && Descendants<System.Windows.Controls.TextBlock>(window.Studio).Any(t => t.Text == "Record Microphone"), "microphone source changes the record action and keeps the shared track");
        Check(!window.Studio.LimitRecording && Descendants<System.Windows.Controls.TextBlock>(window.Studio).Any(t => t.Text.Contains("Record until you press Stop")), "microphone is uncapped when Discord mode is off");
        window.Settings.Discord = true;
        Check(window.Studio.LimitRecording && Descendants<System.Windows.Controls.TextBlock>(window.Studio).Any(t => t.Text.Contains("Stops after 5 seconds")), "Discord toggle caps microphone recording and updates its hint");
        sourceChoice.SelectedIndex = 0;
        Check(window.Studio.LimitRecording, "Discord toggle also caps PC Audio recording");
        window.Settings.Discord = false;
        Check(!window.Studio.LimitRecording, "PC Audio recording is uncapped with Discord off");
        sourceChoice.SelectedIndex = 1;
        Snapshot(window, "studio-microphone.png");
        sourceChoice.SelectedIndex = 0;
        Check(window.Studio.Workspace.Frames == recoveredFrames && Descendants<System.Windows.Controls.TextBlock>(window.Studio).Any(t => t.Text == "Record PC Audio"), "switching back to PC audio retains the same track");
        Check(!Descendants<System.Windows.Controls.Button>(window.Studio).Any(x => x.Content is string t && t.Contains("Recover", StringComparison.OrdinalIgnoreCase)), "Studio has no recovery buttons");
        Check(Descendants<System.Windows.Controls.Slider>(window.Studio).Select(s=>s.Name).Order().SequenceEqual(new[]{"TrackVolume","WaveScroll","WaveZoom"}) && !Descendants<System.Windows.Controls.ComboBox>(window.Studio).Any(x => x.Name == "QualitySelector"), "Studio provides volume zoom and scroll controls without a quality selector");
        Check(sourceChoice.Items.Cast<string>().SequenceEqual(new[] { "PC Audio", "Microphone" }), "source menu uses simple PC Audio and Microphone labels");
        window.UpdateLayout(); Snapshot(window, "studio-precise-selection.png");
        var light = Descendants<RecordingLight>(window.Studio).Single();
        Check(!light.IsRecording && light.Effect == null && ((SolidColorBrush)light.Background).Color == Color.FromRgb(229, 79, 90), "Studio recording light defaults to solid red");
        light.SetRecording(true);
        Check(light.IsRecording && light.HasAnimatedProperties && light.Effect is System.Windows.Media.Effects.DropShadowEffect glow && glow.HasAnimatedProperties && ((SolidColorBrush)light.Background).Color == Color.FromRgb(61, 230, 143), "recording state starts green pulse and glow animations");
        Snapshot(window, "studio-recording-light.png");
        light.SetRecording(false); Check(!light.HasAnimatedProperties && light.Effect == null && light.Opacity == 1, "idle state removes pulse and glow");
        string pcm = previous; await window.Studio.LoadPcm(pcm, "Night drive — stereo capture"); window.Studio.Wave.A = 48000; window.Studio.Wave.B = 160000;
        window.UpdateLayout();
        double handleX = window.Studio.Wave.ActualWidth * 48000 / window.Studio.Wave.ViewFrames;
        var handleCursor = window.Studio.Wave.PointerAt(new Point(handleX, 10));
        Check(handleCursor != System.Windows.Input.Cursors.Arrow && handleCursor != System.Windows.Input.Cursors.Hand, "selection handle loads its custom open grab-hand cursor");
        Check(window.Studio.Wave.PointerAt(new Point(handleX, 60)) == System.Windows.Input.Cursors.Arrow, "ordinary waveform hover retains the arrow outside the selection tabs");
        var deleteAll = Descendants<System.Windows.Controls.Button>(window.Studio).Single(x => x.Content is string t && t == "Delete All");
        deleteAll.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(window.Studio.Workspace.Frames == 0, "visible Delete all clears the Studio track");
        sourceChoice.SelectedIndex = 1; window.Studio.Undo();
        Check(window.Studio.Workspace.Frames == new FileInfo(pcm).Length / 8, "Undo history survives switching recording sources");
        sourceChoice.SelectedIndex = 0;
        string longPcm = Paths.NewPcm(); using (var file = File.Create(longPcm)) file.SetLength(60L * Workspace.Rate * Workspace.FrameBytes);
        await window.Studio.LoadPcm(longPcm, "Long scrolling track");
        Check(window.Studio.Wave.ViewFrames == 60L * Workspace.Rate, "long tracks initially fit the entire audio");
        window.Studio.Wave.Zoom = 4;
        var wheel = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent };
        window.Studio.Wave.RaiseEvent(wheel);
        Check(wheel.Handled && window.Studio.Wave.ViewStart > 0, "mouse wheel down scrolls waveform later without scrolling the page");
        window.Studio.Wave.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent });
        Check(window.Studio.Wave.ViewStart == 0, "mouse wheel up returns toward the beginning");
        window.Studio.Wave.CursorFrame = 40L * Workspace.Rate; window.Studio.Wave.RevealCursor();
        Check(window.Studio.Wave.ViewStart <= window.Studio.Wave.CursorFrame && window.Studio.Wave.ViewStart + window.Studio.Wave.ViewFrames > window.Studio.Wave.CursorFrame, "playback follows the playhead through long audio");
        string silence = Paths.NewPcm(); await File.WriteAllBytesAsync(silence, new byte[48000 * 8 / 3]);
        await window.Studio.LoadPcm(silence, "Silent playback check"); window.Studio.Wave.CursorFrame = 4800;
        window.Studio.TogglePlay();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (window.Player.Active && DateTime.UtcNow < deadline) await Task.Delay(25);
        await Task.Delay(100);
        Check(!window.Player.Active && window.Studio.Wave.CursorFrame == 0 && window.Studio.Wave.Scroll == 0, "natural playback completion rewinds Editor playhead and view");
        window.Studio.TogglePlay(); Check(window.Player.Active, "Play restarts completed clip immediately"); window.Player.Stop();
        await window.Studio.LoadPcm(pcm, "Night drive — stereo capture");
        Check(!window.Settings.Discord, "global Discord mode defaults off");
        var tabControl = (System.Windows.Controls.TabControl)window.FindName("Tabs");
        Check(Descendants<System.Windows.Controls.CheckBox>(window.Studio).Contains(window.DiscordToggle), "Discord toggle belongs to Studio recording controls");
        var keepSelected = Descendants<System.Windows.Controls.Button>(window.Studio).Single(x => x.Content is string t && t == "Keep Selected");
        Check(!keepSelected.IsEnabled, "Keep selected is disabled until audio is highlighted");
        window.UpdateLayout();
        var times = Descendants<System.Windows.Controls.TextBox>(window.Studio).Where(t => t.Name is "SelectionStart" or "SelectionEnd").ToArray();
        times[0].Text = "0.5"; times[1].Text = "1.5";
        Descendants<System.Windows.Controls.Button>(window.Studio).Single(x => x.Content is string t && t == "Apply").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(keepSelected.IsEnabled, "Keep selected becomes available for a highlighted range");
        keepSelected.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(window.Studio.Workspace.Frames == 48000, "visible Keep selected button removes audio before and after the highlight");
        window.Studio.Undo(); Check(window.Studio.Workspace.Frames == new FileInfo(pcm).Length / 8, "one Undo restores the Studio track after Keep selected");
        window.Studio.SelectAll();
        var deleteSelected = Descendants<System.Windows.Controls.Button>(window.Studio).Single(x => x.Content is string t && t == "Trim Selected");
        deleteSelected.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(window.Studio.Workspace.Frames == 0, "Trim removes the highlighted audio");
        window.Studio.Undo(); Check(window.Studio.Workspace.Frames == new FileInfo(pcm).Length / 8, "Undo restores deleted selection");
        await FullPreviewChecks(window);
        await window.Studio.LoadPcm(pcm, "Night drive — stereo capture");
        window.Studio.SelectAll();
        tabControl.SelectedIndex = 2; await Task.Delay(100);
        var formats = Descendants<System.Windows.Controls.ComboBox>(window).Single(x => x.Name == "OutputFormat");
        Check(new[]{"AAC","M4A (Apple Lossless)","WMA","AC-3","WebM (Opus)","WavPack"}.All(label=>AudioOutputFormat.All.Any(format=>format.Label==label))&&formats.Items.Count==13,"Converter exposes six additional verified output formats");
        Check(!Descendants<System.Windows.Controls.ComboBox>(window).Any(x => x.Name == "QualitySelector"), "Converter has format selection and no quality controls");
        var wavFormat = AudioOutputFormat.All.Single(x => x.Extension == ".wav"); formats.SelectedItem = wavFormat;
        var discordToggle = window.DiscordToggle;
        discordToggle.IsChecked = true; discordToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(Equals(formats.SelectedItem, AudioOutputFormat.Mp3) && !formats.IsEnabled, "Discord toggle immediately selects and locks MP3");
        Snapshot(window, "converter-discord.png");
        discordToggle.IsChecked = false; discordToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(Equals(formats.SelectedItem, wavFormat) && formats.IsEnabled, "switching Discord off restores previous format choice");
        var converter = Descendants<ConverterPane>(window).Single();
        converter.Add([Path.Combine(Paths.Cache, "broken.wav"), Path.Combine(Paths.Cache, "Original 音楽.wav")]);
        var convertButton = Descendants<System.Windows.Controls.Button>(converter).Single(x => x.Content is string s && s == "Convert");
        convertButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var conversionDeadline = DateTime.UtcNow.AddSeconds(15);
        while (!window.Available && DateTime.UtcNow < conversionDeadline) await Task.Delay(50);
        Check(window.Available && Descendants<System.Windows.Controls.TextBlock>(converter).Any(x => x.Text.StartsWith("Saved ") && x.Text.Contains(".wav")), "Add files, choose WAV and Convert succeeds through the UI");
        Check(Descendants<System.Windows.Controls.TextBlock>(converter).Any(x => x.Text.StartsWith("Needs attention")), "batch conversion reports a corrupt file and continues with valid audio");
        var displayedError = Descendants<System.Windows.Controls.TextBlock>(converter).Single(x => x.Text.StartsWith("Needs attention"));
        Check(displayedError.Text.Contains("Ref:") && !displayedError.Text.Contains("ffprobe") && displayedError.ToolTip is string tip && !tip.Contains("MediaToolException"), "Converter error text and hover details stay friendly and include a reference");
        await Task.Delay(150); Snapshot(window, "converter-completed.png");
        await window.RefreshLibrary();
        foreach (int tab in Enumerable.Range(0, 3))
        {
            var tabs = (System.Windows.Controls.TabControl)window.FindName("Tabs"); tabs.SelectedIndex = tab;
            await Task.Delay(100); window.UpdateLayout();
            foreach (double scale in new[] { 1.0, 1.5, 2.0 })
            {
                var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(window);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, $"tab-{tab}-{scale * 100:0}.png")); png.Save(file);
            }
        }
        Check(true, "three WPF tabs rendered at 100, 150 and 200 percent output DPI");
        var savedSoundboard = window.Settings.Soundboard;
        await window.ApplySoundboardSettings(savedSoundboard with { SpeakerVolume = .65 });
        Check(Settings.Load().Soundboard.SpeakerVolume == .65, "Library soundboard settings persist through the application save path");
        await window.ApplySoundboardSettings(savedSoundboard);
        window.Settings.Soundboard = savedSoundboard with { AutoKeys = [new SoundboardAutoKey { Enabled = true, Chord = new(2, 0x4b) }] };
        var settingsWindow = new SoundboardSettingsWindow(window) { ShowActivated = false };
        settingsWindow.Show(); await Task.Delay(100);
        var outputSlider = Descendants<System.Windows.Controls.Slider>(settingsWindow).Single();
        Check(outputSlider.Name == "SoundOutputVolume" && outputSlider.Minimum == 0 && outputSlider.Maximum == 100, "Audio settings exposes one sound output slider bounded to 0–100 percent");
        Check(settingsWindow.Background is SolidColorBrush background && background.Color == (Color)ColorConverter.ConvertFromString("#151419"), "Library Settings inherits the dark application theme");
        var settingsTabs = Descendants<System.Windows.Controls.TabControl>(settingsWindow).Single();
        Check(settingsTabs.Items.Cast<System.Windows.Controls.TabItem>().Select(t=>t.Header.ToString()).SequenceEqual(new[]{"General","Hotkeys"}), "Soundboard Settings contains General and Hotkeys only");
        foreach (int index in Enumerable.Range(0, 2)) { settingsTabs.SelectedIndex = index; await Task.Delay(80); Snapshot(settingsWindow, $"library-settings-{index}.png"); }
        settingsTabs.SelectedIndex = 0; settingsWindow.Width = 780; settingsWindow.Height = 600; await Task.Delay(80); Snapshot(settingsWindow, "library-settings-small.png");
        var saveSettings = Descendants<System.Windows.Controls.Button>(settingsWindow).Single(b => b.Content is string s && s == "Save Settings");
        Check(saveSettings.IsVisible && saveSettings.TransformToAncestor(settingsWindow).Transform(new Point()).Y < settingsWindow.ActualHeight - 40, "Library Settings save remains visible at minimum size");
        outputSlider.Value = 0; settingsWindow.Close();
        Check(window.Settings.Soundboard.TransmittedSoundVolume == savedSoundboard.TransmittedSoundVolume, "Cancel does not apply an edited output volume");
        window.Settings.Soundboard = savedSoundboard;
        tabControl.SelectedIndex = 0; window.Width = 850; window.Height = 700; await Task.Delay(100);
        var preciseBottom = preciseControls.TransformToAncestor(window).Transform(new Point(0, preciseControls.ActualHeight));
        Check(preciseControls.IsVisible && preciseBottom.Y < window.ActualHeight - 30, "precise selection remains on screen at the minimum window size");
        Snapshot(window, "studio-small-window.png");
        await AppearanceChecks(window);
        await LibraryColorChecks(window);
        await ShortcutUiChecks(window);
        await UpdateUiChecks(window);
        await ToolProgressUiChecks(window);
        await RollbackUiChecks(window);
        await FooterChecks(window);
        await LibraryFirstOpenChecks();
        window.Studio.Workspace.Dirty = false;
    }
    static async Task AppearanceChecks(MainWindow window)
    {
        var original = window.Settings.Appearance;
        Check(window.FindName("UiSettingsButton") is System.Windows.Controls.Button, "UI Settings is available in the main header");
        foreach (string font in Theme.BundledFonts) {
            Check(Theme.Family(font).GetTypefaces().Any(t => t.TryGetGlyphTypeface(out var face) && face.FamilyNames.Values.Any(n => n == font)), $"bundled {font} resolves to its actual offline font face");
        }
        var dialog = new UiSettingsWindow(window) { ShowActivated = false }; dialog.Show(); await Task.Delay(80);
        Check(!Descendants<System.Windows.Controls.ComboBox>(dialog).Any(c=>c.Items.Contains("Light")), "appearance selector is replaced by the main theme switch");
        var slider = Descendants<System.Windows.Controls.Slider>(dialog).Single(); slider.Value = 17;
        Check(Theme.Current.LightMode == original.LightMode && Theme.Current.FontSize == 17 && window.Background is SolidColorBrush b && b.Color == (Color)ColorConverter.ConvertFromString(original.LightMode ? "#F3F4F7" : "#151419"), "appearance previews update the existing main window immediately");
        Snapshot(dialog, "ui-settings-light.png"); dialog.Close();
        Check(Theme.Current == original && window.Settings.Appearance == original, "Cancel restores original appearance without changing persisted preferences");
        Check(Theme.Fonts.Length > Theme.BundledFonts.Length && Theme.BundledFonts.Length == 12, "font chooser includes twelve offline families and installed Windows text fonts");
        var picker = new ColorPickerWindow(window, original.Accent, color => Theme.Apply(original with { Accent = color })) { ShowActivated = false };
        picker.Show(); await Task.Delay(50);
        var wheel = Descendants<ColorWheel>(picker).Single();
        foreach (var color in new[] { Colors.Red, Colors.Lime, Colors.Blue, Colors.White, Colors.Black, Color.FromRgb(25, 167, 196) }) {
            wheel.SetColor(color); Check(wheel.SelectedColor == color, "color wheel retains exact RGB choice " + ColorPickerWindow.Hex(color));
        }
        Descendants<System.Windows.Controls.TextBox>(picker).Single().Text = "#19A7C4";
        // Dispatcher rendering can delay the coalesced preview on a busy desktop.
        for (int attempt = 0; attempt < 40 && Theme.Current.Accent != "#19A7C4"; attempt++) await Task.Delay(50);
        Check(Theme.Current.Accent == "#19A7C4", "color picker live preview applies the selected custom accent");
        Snapshot(picker, "color-wheel.png");
        Descendants<System.Windows.Controls.Button>(picker).Single(b => b.Content is string s && s == "Use Color").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(!picker.IsVisible && Theme.Current.Accent == "#19A7C4", "Use Color retains the selected color for the parent settings save");
        picker = new ColorPickerWindow(window, "#19A7C4", color => Theme.Apply(original with { Accent = color })) { ShowActivated = false };
        picker.Show(); Descendants<System.Windows.Controls.TextBox>(picker).Single().Text = "#C41987"; await Task.Delay(100); picker.Close();
        Check(Theme.Current.Accent == "#19A7C4", "color picker Cancel restores its entry color without leaking a delayed preview"); Theme.Apply(original);
        var saved = original with { LightMode = true, Accent = "#287BB8", Font = "Lato", FontSize = 16, ReduceMotion = true, NewestFirst = original.NewestFirst };
        window.Settings.Appearance = original with { LightMode = true }; Theme.Apply(window.Settings.Appearance);
        dialog = new UiSettingsWindow(window) { ShowActivated = false }; dialog.Show(); await Task.Delay(50);
        var fontChooser = Descendants<System.Windows.Controls.ComboBox>(dialog).Single(c => c.Items.Contains("Lato"));
        fontChooser.Text = "Poppins"; await Task.Delay(50);
        Check(fontChooser.SelectedItem is string typed && typed == "Poppins" && Theme.Current.Font == "Poppins", "typing a font name selects and previews it in the editable font chooser");
        fontChooser.SelectedItem = "Lato";
        Descendants<System.Windows.Controls.TextBox>(dialog).Single(t => t.Name == "AccentHex").Text = "#287BB8";
        Descendants<System.Windows.Controls.Slider>(dialog).Single().Value = 16;
        foreach (string label in new[] { "Performance Mode" }) {
            var option = Descendants<System.Windows.Controls.CheckBox>(dialog).Single(c => c.Content is string name && name == label);
            option.IsChecked = true; option.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        Descendants<System.Windows.Controls.Button>(dialog).Single(b => b.Content is string label && label == "Save").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var deadline = DateTime.UtcNow.AddSeconds(5); while (dialog.IsVisible && DateTime.UtcNow < deadline) await Task.Delay(20);
        Check(Settings.Load().Appearance == saved, "appearance colors fonts text size density and motion preferences persist across settings reload");
        Check((double)Application.Current.FindResource("Font14") == 16, "font-size slider scales explicit app text resources");
        var tabs = (System.Windows.Controls.TabControl)window.FindName("Tabs");
        window.Width = 1240; window.Height = 940;
        for (int tab = 0; tab < 3; ++tab) { tabs.SelectedIndex = tab; await Task.Delay(100); Snapshot(window, $"theme-light-tab-{tab}.png"); }
        var maximumText = saved with { FontSize = 19 }; Theme.Apply(maximumText); window.Width = 850; window.Height = 700;
        await Task.Delay(100); Snapshot(window, "theme-large-text-small-window.png"); Theme.Apply(saved); window.Width = 1240; window.Height = 940;
        string fixture = Path.Combine(Paths.Cache, "collection-fixture.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(fixture, new NAudio.Wave.WaveFormat(48000, 16, 1))) writer.Write(new byte[960], 0, 960);
        for (int i = 0; i < 800; ++i) File.Copy(fixture, Path.Combine(Paths.Library, $"Collection sound {i:0000}.wav"));
        await window.RefreshLibrary(); tabs.SelectedIndex = 1; await Task.Delay(200);
        var list = Descendants<System.Windows.Controls.ListBox>(window).Single(x => x.Name == "LibraryRows");
        Check(list.Items.Count >= 800 && Descendants<System.Windows.Controls.ListBoxItem>(list).Count() < 80, "large Library virtualizes rows instead of constructing hundreds of cards");
        Snapshot(window, "library-compact-light.png");
        Theme.Apply(original); window.Settings.Appearance = original; await window.Settings.Save();
        await window.RefreshLibrary(); await Task.Delay(100); Snapshot(window, "library-compact-dark.png");
        list.ScrollIntoView(list.Items[list.Items.Count - 1]); list.UpdateLayout(); await Task.Delay(100);
        Check(list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) != null, "virtualized Library can scroll to and realize the last sound");
        var more = Descendants<System.Windows.Controls.Button>(list).First(b => b.Content is string text && text == "•••");
        foreach (bool light in new[] { false, true }) {
            Theme.Apply(original with { LightMode = light });
            more.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await Task.Delay(100);
            var menu = PresentationSource.CurrentSources.Cast<PresentationSource>()
                .Where(p => p.RootVisual is Visual).SelectMany(p => Descendants<System.Windows.Controls.ContextMenu>((Visual)p.RootVisual))
                .Single(m => m.IsOpen && m.PlacementTarget == more);
            Snapshot(menu, light ? "library-menu-light.png" : "library-menu-dark.png"); menu.IsOpen = false;
        }
        Theme.Apply(original);
        window.Studio.Workspace.Dirty = false;
    }
    static async Task ShortcutUiChecks(MainWindow window)
    {
        string first = Path.Combine(Paths.Library, "Shortcut first.wav"), second = Path.Combine(Paths.Library, "Shortcut second.wav");
        foreach (string file in new[] { first, second }) {
            using var writer = new NAudio.Wave.WaveFileWriter(file, new NAudio.Wave.WaveFormat(48000, 16, 1)); writer.Write(new byte[960], 0, 960);
        }
        var original = window.Settings.Soundboard;
        var one = new SoundboardBinding { Chord = new(2, 0x4b), SoundFilename = Path.GetFileName(first), Route = SoundboardRoute.Microphone };
        var two = new SoundboardBinding { Chord = new(2, 0x4a), SoundFilename = Path.GetFileName(second), Route = SoundboardRoute.Preview };
        var stop = new SoundboardBinding { Chord = new(2, 0x79), Command = SoundboardCommand.Stop };
        window.Settings.Soundboard = original with { Bindings = [one, two, stop], AutoKeys = [], HotkeysEnabled = false };
        await window.Settings.Save(); await window.RefreshLibrary();
        var tabs = (System.Windows.Controls.TabControl)window.FindName("Tabs"); tabs.SelectedIndex = 1; await Task.Delay(100);
        var global = Descendants<System.Windows.Controls.Button>(window).Single(b => b.Content is string label && label == "Soundboard Settings");
        var converterTab = tabs.Items.Cast<System.Windows.Controls.TabItem>().Single(t => t.Header is string label && label == "Audio Converter");
        Check(tabs.Items.Count == 3 && global.TransformToAncestor(window).Transform(new Point()).X >= converterTab.TransformToAncestor(window).Transform(new Point(converterTab.ActualWidth, 0)).X, "Soundboard Settings is a button directly beside Audio Converter while keeping three tabs");
        var opened = new TaskCompletionSource<bool>();
        _ = window.Dispatcher.BeginInvoke(async () => {
            await Task.Delay(100); var actual = Application.Current.Windows.OfType<SoundboardSettingsWindow>().FirstOrDefault(w => w.Owner == window);
            opened.TrySetResult(actual?.Title == "Soundboard Settings"); actual?.Close();
        });
        global.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(await opened.Task.WaitAsync(TimeSpan.FromSeconds(5)), "global Soundboard Settings button opens its dialog from the tab strip");
        var library = Descendants<LibraryPane>(window).Single();
        Check(!Descendants<System.Windows.Controls.Button>(library).Any(b => b.Content is string s && s == "Soundboard Settings"), "Library no longer contains the global Soundboard Settings button");
        var rows = Descendants<System.Windows.Controls.ListBox>(library).Single(); var sound = rows.Items.Cast<LibraryPane.SoundRow>().Single(r => r.Path == first);
        rows.ScrollIntoView(sound); rows.UpdateLayout(); await Task.Delay(100);
        var row = (System.Windows.Controls.ListBoxItem)rows.ItemContainerGenerator.ContainerFromItem(sound);
        var soundMenu = OpenSoundMenu(row);
        Check(soundMenu.Items.OfType<System.Windows.Controls.MenuItem>().Any(item => item.Header is string label && label == "Shortcut"), "each saved sound has a Shortcut action in its tile menu");
        soundMenu.IsOpen = false;
        var dialog = new SoundShortcutWindow(window, first) { ShowActivated = false }; dialog.Show(); await Task.Delay(80);
        var route = Descendants<System.Windows.Controls.ComboBox>(dialog).Single();
        Check(route.Items.Cast<SoundShortcutWindow.RouteOption>().Select(r => r.Label).SequenceEqual(new[] { "Mic", "Preview", "Both" }), "per-sound shortcut dropdown exposes Mic Preview and Both");
        Snapshot(dialog, "sound-shortcut-settings.png");
        route.SelectedItem = route.Items.Cast<SoundShortcutWindow.RouteOption>().Single(r => r.Route == SoundboardRoute.Both);
        Descendants<System.Windows.Controls.Button>(dialog).Single(b => b.Content is string s && s == "Save").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var deadline = DateTime.UtcNow.AddSeconds(5); while (dialog.IsVisible && DateTime.UtcNow < deadline) await Task.Delay(20);
        var saved = Settings.Load().Soundboard;
        Check(saved.Bindings.Single(b => b.SoundFilename == one.SoundFilename).Route == SoundboardRoute.Both && saved.Bindings.Contains(two) && saved.Bindings.Contains(stop) && saved.HotkeysEnabled && saved.PassHotkeysToOtherApps && saved.AllowLeftRightModifiers, "saving a sound route preserves other sounds and playback controls with automatic shortcut defaults");
        var settings = new SoundboardSettingsWindow(window) { ShowActivated = false }; settings.Show(); await Task.Delay(50);
        var volume = Descendants<System.Windows.Controls.Slider>(settings).Single(); volume.Value = 37;
        Check(volume.Name == "SoundOutputVolume" && volume.Maximum == 100, "global sound settings use the single protected output slider");
        var settingsTabs = Descendants<System.Windows.Controls.TabControl>(settings).Single(); settingsTabs.SelectedIndex = 1; await Task.Delay(50);
        Check(new[] { "Enable Global Shortcuts", "Pass Shortcuts Through To Other Apps", "Distinguish Left And Right Ctrl / Alt / Shift" }.All(label => !Descendants<System.Windows.Controls.CheckBox>(settings).Any(c => c.Content is string s && s == label)) && !Descendants<System.Windows.Controls.TextBlock>(settings).Any(t => t.Text == "Sound Shortcuts"), "Hotkeys contains playback controls without global preference toggles");
        Snapshot(settings, "global-hotkey-settings.png");
        Descendants<System.Windows.Controls.Button>(settings).Single(b => b.Content is string s && s == "Save Settings").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        deadline = DateTime.UtcNow.AddSeconds(5); while (settings.IsVisible && DateTime.UtcNow < deadline) await Task.Delay(20);
        var outputSaved = Settings.Load().Soundboard;
        Check(outputSaved.TransmittedSoundVolume == .37 && outputSaved.VolumeMode == SoundboardVolumeMode.Automatic && outputSaved.TargetLufs == -18 && outputSaved.MaximumBoostDb == 24, "Save retains the output percentage across launches with automatic protection enabled");
        Check(Settings.Load().Soundboard.Bindings.Where(b => b.Command == SoundboardCommand.PlaySound).SequenceEqual(saved.Bindings.Where(b => b.Command == SoundboardCommand.PlaySound)), "saving global settings retains the Library sound shortcut assignments");
        dialog = new SoundShortcutWindow(window, first) { ShowActivated = false }; dialog.Show();
        var key = Descendants<HotkeyBox>(dialog).Single(k=>k.Name!="SoundPttKey");
        key.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, System.Windows.Input.Key.J) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        dialog.Close();
        Check(Settings.Load().Soundboard.Bindings.Single(b => b.SoundFilename == one.SoundFilename).Chord == one.Chord, "Cancel leaves the sound's saved keyboard shortcut unchanged");
        // Seed an edited chord that collides with another sound to verify save-time conflict handling.
        window.Settings.Soundboard = saved with { Bindings = saved.Bindings.Select(b => b.SoundFilename == one.SoundFilename ? b with { Chord = two.Chord } : b).ToArray() };
        dialog = new SoundShortcutWindow(window, first) { ShowActivated = false }; dialog.Show();
        Descendants<System.Windows.Controls.Button>(dialog).Single(b => b.Content is string s && s == "Save").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await Task.Delay(80);
        Check(dialog.IsVisible && Descendants<System.Windows.Controls.TextBlock>(dialog).Any(t => t.Text.Contains("same shortcut")), "per-sound shortcut conflicts are rejected with a readable error"); dialog.Close();
        window.Settings.Soundboard = saved;
        dialog = new SoundShortcutWindow(window, first) { ShowActivated = false }; dialog.Show();
        Descendants<System.Windows.Controls.Button>(dialog).Single(b => b.Content is string s && s == "Remove").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Descendants<System.Windows.Controls.Button>(dialog).Single(b => b.Content is string s && s == "Save").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        deadline = DateTime.UtcNow.AddSeconds(5); while (dialog.IsVisible && DateTime.UtcNow < deadline) await Task.Delay(20);
        Check(!Settings.Load().Soundboard.Bindings.Any(b => b.SoundFilename == one.SoundFilename) && Settings.Load().Soundboard.Bindings.Contains(two), "removing a sound shortcut leaves other sound assignments intact");
        window.Settings.Soundboard = original; await window.Settings.Save(); window.Studio.Workspace.Dirty = false;
    }
    static async Task FullPreviewChecks(MainWindow window)
    {
        var tabs = (System.Windows.Controls.TabControl)window.FindName("Tabs"); tabs.SelectedIndex = 0;
        var toggle = window.DiscordToggle;
        toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await window.Settings.Save(); Check(Settings.Load().Discord, "global Discord toggle persists across launches");
        var recordingSource = Descendants<System.Windows.Controls.ComboBox>(window.Studio).Single(x => x.Name == "RecordingSource");
        recordingSource.SelectedIndex = 1;
        string source = Path.Combine(Paths.Cache, "Six seconds.wav");
        string pcm = await MediaTools.Decode(source, CancellationToken.None); await window.Studio.LoadPcm(pcm, "Full preview check");
        // WPF throttles offscreen rendering; measure display cadence on a visible window.
        double previousLeft = window.Left, previousTop = window.Top;
        window.Left = 20; window.Top = 20; window.Topmost = true; window.Activate(); await Task.Delay(250);
        window.Studio.TogglePlay();
        double initialPosition = window.Player.Position;
        Check(initialPosition < .05, "playback position begins at the device clock rather than buffered-ahead audio");
        var playheadFrames = new HashSet<long>(); var watch = System.Diagnostics.Stopwatch.StartNew(); double lastPosition = 0; bool accurateClock = true;
        while (watch.Elapsed.TotalSeconds < 1)
        {
            await Task.Delay(8); double position = window.Player.Position;
            accurateClock &= position >= lastPosition && position <= watch.Elapsed.TotalSeconds + .04;
            lastPosition = position; playheadFrames.Add(window.Studio.Wave.CursorFrame);
        }
        Check(accurateClock, "device playback position stays monotonic and does not lead elapsed audio");
        Console.WriteLine($"Playback cadence: {playheadFrames.Count} cursor frames; visible={window.IsVisible}, loaded={window.Studio.IsLoaded}, active={window.IsActive}");
        Check(playheadFrames.Count > 15, "playhead advances at display-frame cadence above the old ten updates per second");
        window.Topmost = false; window.Left = previousLeft; window.Top = previousTop;
        // The cadence probe activates a real window. Once it is moved offscreen,
        // keep incidental desktop keyboard input from changing its test settings.
        bool wasEnabled = window.IsEnabled; window.IsEnabled = false;
        try {
            await Task.Delay(4200);
            Check(window.Player.Active && window.Player.Position >= 5, "Studio previews beyond five seconds with microphone selected and Discord enabled"); window.Player.Stop();
            Console.WriteLine($"Discord export fixture: enabled={window.Settings.Discord}, seconds={window.Studio.Workspace.Seconds:F3}");
            try { await MediaTools.Export(window.Studio.Workspace, Path.Combine(Paths.Library, "long-editor-export.mp3"), 192, window.Settings.Discord, false, CancellationToken.None); throw new Exception("long Editor Discord export accepted"); }
            catch (InvalidDataException) { Check(!File.Exists(Path.Combine(Paths.Library, "long-editor-export.mp3")), "same global setting blocks long Editor saves without changing preview audio"); }
        } finally { window.IsEnabled = wasEnabled; }
        string libraryFile = Path.Combine(Paths.Library, "Full preview check.wav");
        await AudioConversion.Convert(source, libraryFile, AudioOutputFormat.All.Single(x => x.Extension == ".wav"), false, CancellationToken.None);
        await window.RefreshLibrary(); tabs.SelectedIndex = 1; await Task.Delay(100);
        Check(toggle.IsChecked == true && window.Settings.Discord, "Library shares the enabled global Discord toggle");
        var library = Descendants<LibraryPane>(window).Single();
        var list = Descendants<System.Windows.Controls.ListBox>(library).Single();
        System.Windows.Controls.ListBoxItem? card = null;
        var rowDeadline = DateTime.UtcNow.AddSeconds(5);
        // A delayed file-watcher refresh can replace row objects while the list virtualizes.
        while (card == null && DateTime.UtcNow < rowDeadline) {
            var item = list.Items.Cast<LibraryPane.SoundRow>().SingleOrDefault(x => x.Name == "Full preview check");
            if (item != null) { list.ScrollIntoView(item); list.UpdateLayout(); card = list.ItemContainerGenerator.ContainerFromItem(item) as System.Windows.Controls.ListBoxItem; }
            if (card == null) await Task.Delay(50);
        }
        if (card == null) throw new Exception("The Library preview row did not become visible within five seconds.");
        Check(Descendants<System.Windows.Controls.Button>(card).Any(x => x.Content is string s && s == "Play Through Mic"), "Library has a separate microphone transmission button beside Preview");
        var preview = Descendants<System.Windows.Controls.Button>(card).Single(x => x.Content is string s && s == "Preview");
        preview.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!window.Player.Active && DateTime.UtcNow < deadline) await Task.Delay(50);
        await Task.Delay(5200);
        Check(window.Player.Active && window.Player.Position >= 5, "Library previews beyond five seconds with the same Discord setting");
        var menu = OpenSoundMenu(card);
        menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(item => item.Header is string label && label == "Stop").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        menu.IsOpen = false;
        Check(!window.Player.Active, "the sound's Library Stop menu action immediately stops its preview");
        toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        recordingSource.SelectedIndex = 0;
    }
    static System.Windows.Controls.ContextMenu OpenSoundMenu(DependencyObject row)
    {
        var more = Descendants<System.Windows.Controls.Button>(row).Single(b => b.Content is string label && label == "•••");
        more.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        return PresentationSource.CurrentSources.Cast<PresentationSource>()
            .Where(source => source.RootVisual is Visual)
            .SelectMany(source => Descendants<System.Windows.Controls.ContextMenu>((Visual)source.RootVisual))
            .Single(menu => menu.IsOpen && menu.PlacementTarget == more);
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    static void Snapshot(FrameworkElement window, string name)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, name)); png.Save(file);
    }
    static async Task UrlUi()
    {
        var window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000 }; window.Show(); await Task.Delay(200);
        var input = Descendants<System.Windows.Controls.TextBox>(window).Single(x => x.Name == "UrlInput");
        input.Text = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
        var button = Descendants<System.Windows.Controls.Button>(window).Single(x => x.Content is string s && s == "Import URL");
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var bar = Descendants<System.Windows.Controls.ProgressBar>(window).Single(x => x.Name == "UrlImportProgress");
        Check(!window.Available && bar.IsVisible, "inline URL import immediately shows progress");
        await Task.Delay(300); Snapshot(window, "url-import-progress.png");
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!window.Available && DateTime.UtcNow < deadline) await Task.Delay(100);
        Check(window.Available && window.Studio.Workspace.Frames > 0 && bar.Value == 100 && !bar.IsIndeterminate, "inline URL import completes with green 100 percent progress");
        await Task.Delay(150); Snapshot(window, "url-import-complete.png"); window.Studio.Workspace.Dirty = false;
    }
}
