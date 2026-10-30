using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NAudio.CoreAudioApi;
using Soundshark;
using Soundshark.Core;

internal static partial class Program
{
    static byte[] VoiceBytes(int frames,float value=.25f,int channels=1)
    {var floats=Enumerable.Repeat(value,frames*channels).ToArray();var bytes=new byte[floats.Length*4];Buffer.BlockCopy(floats,0,bytes,0,bytes.Length);return bytes;}
    static VirtualVoiceBuffer PrimedVoice(float value=.25f){var voice=new VirtualVoiceBuffer(48000);voice.Append(VoiceBytes(4800,value),1,32,true);return voice;}
    sealed class RouteFixture(List<string> events,string name,bool fail=false) : IVirtualMicrophoneRoute
    {
        public VirtualClipMixer Mixer{get;}=new();public string Status=>name;public bool FailStop;
        public Task StartAsync(SoundboardConfiguration settings,bool requireConnection){events.Add("start:"+name);if(fail)throw new IOException("fixture start failure");Mixer.Connect(PrimedVoice(),1);return Task.CompletedTask;}
        public Task StopAsync(){events.Add("stop:"+name);if(FailStop)throw new IOException("driver is still stopping");Mixer.Disconnect();return Task.CompletedTask;}
    }
    sealed class VirtualKeys : ISoundboardKeyTransport
    {
        readonly HashSet<ushort> held=[];readonly object sync=new();public int Downs,Ups;
        public int Count{get{lock(sync)return held.Count;}}
        public bool Down(ushort key){lock(sync)return held.Contains(key);}
        public bool Applies(SoundboardAutoKey key)=>true;
        public void Send(ushort key,bool up){lock(sync){if(up){held.Remove(key);Ups++;}else{held.Add(key);Downs++;}}}
    }
    static async Task Until(Func<bool> predicate,string failure,int milliseconds=5000)
    {var deadline=DateTime.UtcNow.AddMilliseconds(milliseconds);while(!predicate()){if(DateTime.UtcNow>=deadline)throw new Exception(failure);await Task.Delay(10);}}
    static async Task VirtualMicChecks()
    {
        var physical=new SoundboardConfiguration{Backend=MicrophoneBackend.NativeMicrophone,MicrophoneEndpointId="physical-saved"};
        var virtualConfig=physical with{Backend=MicrophoneBackend.VirtualCable,VirtualRoutingEnabled=true,VirtualVoiceEndpointId="voice",TransmissionEndpointId="cable-in",CableRecordingEndpointId="cable-out"};virtualConfig.Validate();
        var restored=JsonSerializer.Deserialize<SoundboardConfiguration>(JsonSerializer.Serialize(virtualConfig))!;
        Check(restored.MicrophoneEndpointId=="physical-saved"&&restored.VirtualVoiceEndpointId=="voice"&&restored.TransmissionEndpointId=="cable-in"&&restored.CableRecordingEndpointId=="cable-out","both backend selections survive serialization independently");
        Check(!new SoundboardConfiguration().UsesContinuousVirtualMic&&new SoundboardConfiguration().Backend==MicrophoneBackend.NotConfigured,"new settings never enable capture automatically");
        foreach(int bits in new[]{16,24,32}) {
            int width=bits/8;var pcm=new byte[4800*width*2];
            for(int i=0;i<4800;i++){pcm[i*width*2+width-1]=0x20;pcm[i*width*2+width*2-1]=0x40;}
            var converted=new VirtualVoiceBuffer(48000);converted.Append(pcm,2,bits,false);var decoded=new float[960];converted.Read(decoded,48000);
            Check(Math.Abs(decoded[0]-.25)<.001&&Math.Abs(decoded[1]-.5)<.001,$"PCM {bits}-bit stereo capture preserves channel levels during float conversion");
        }
        Check(JsonSerializer.Deserialize<SoundboardConfiguration>("{\"Backend\":2,\"MicrophoneEndpointId\":\"legacy\"}") is{Backend:MicrophoneBackend.NativeMicrophone,MicrophoneEndpointId:"legacy",VirtualRoutingEnabled:false},"legacy physical settings keep their backend and opt-in state");
        try{(virtualConfig with{VirtualVoiceEndpointId="cable-out"}).Validate();throw new Exception("feedback accepted");}catch(ArgumentException){Check(true,"virtual voice feedback is rejected");}
        var mixer=new VirtualClipMixer();mixer.Connect(PrimedVoice(.4f),1);var output=new float[480];mixer.Read(output,0,output.Length);
        Check(output.All(x=>Math.Abs(x-.4)<.001),"continuous voice is present without any clip");
        long lease=mixer.Begin(120,1,1,true);mixer.Write(lease,Enumerable.Repeat(.2f,240).ToArray());mixer.Read(output,0,120);
        Check(output.Take(120).All(x=>Math.Abs(x-.4)<.001),"prepared clips do not block voice until activated");
        mixer.Activate(lease);mixer.Pause(lease,true);mixer.Read(output,0,120);
        Check(output.Take(120).All(x=>Math.Abs(x-.4)<.001)&&mixer.Consumed==0,"pause restores voice and stops only clip progress");
        mixer.Pause(lease,false);mixer.Read(output,0,240);
        Check(output.Take(240).All(x=>Math.Abs(x-.2)<.001),"Block My Voice applies while a clip is actively consumed");
        mixer.Read(output,0,240);Check(output.Take(240).All(x=>Math.Abs(x-.4)<.001),"voice immediately returns at clip EOF");
        mixer.StopClip(lease);lease=mixer.Begin(120,1,1,false);mixer.Write(lease,Enumerable.Repeat(.9f,240).ToArray());mixer.Activate(lease);mixer.Read(output,0,240);
        Check(output.Take(240).All(x=>float.IsFinite(x)&&Math.Abs(x)<=.98001f)&&output[0]>.97,"voice plus loud clips are peak-protected as a linked stereo mix");
        mixer.StopClip(lease);mixer.Read(output,0,240);Check(output.Take(240).All(x=>Math.Abs(x-.4)<.001),"Stop All clip completion leaves continuous voice connected");
        lease=mixer.Begin(120,1,0,true);mixer.Write(lease,new float[240]);mixer.Activate(lease);mixer.FailClip(new IOException("decode failure"));mixer.Read(output,0,240);
        Check(mixer.Connected&&output.Take(240).All(x=>Math.Abs(x-.4)<.001),"clip failure releases blocking without closing the voice route");
        mixer.StopClip(lease);
        try{mixer.Write(lease,new float[2]);throw new Exception("retired clip accepted");}catch(OperationCanceledException){Check(true,"retired clip generations cannot feed a later mixer session");}
        foreach(int rate in new[]{44100,48000,96000})foreach(int ppm in new[]{-1000,1000}){
            var voice=new VirtualVoiceBuffer(rate);voice.Append(VoiceBytes(rate*80/1000),1,32,true);
            var packet=VoiceBytes(rate/50+2);var block=new float[960];double fractional=0;int empty=0;
            for(int i=0;i<60000;i++){
                fractional+=rate*.01*(1+ppm/1e6);int frames=(int)fractional;fractional-=frames;voice.Append(packet.AsSpan(0,frames*4),1,32,true);voice.Read(block,48000);
                if(i>100&&block.All(x=>x==0))empty++;
            }
            Check(empty==0&&voice.DroppedFrames==0&&voice.BufferedFrames<rate*150/1000,$"ten simulated minutes at {rate} Hz and {ppm} ppm remain bounded without voice dropouts");
        }
        var overflow=new VirtualVoiceBuffer(48000);overflow.Append(VoiceBytes(48000),1,32,true);Check(overflow.BufferedFrames<=7200&&overflow.DroppedFrames>0,"capture overflow discards stale audio instead of building delayed playback");overflow.Clear();mixer.Connect(overflow,1);mixer.Read(output,0,output.Length);Check(output.All(x=>x==0),"reconnection starts with silence rather than old microphone audio");
        long clock=1000;var stalled=new VirtualClipMixer(()=>clock);stalled.Connect(PrimedVoice(),1);var stalledLease=stalled.Begin(100000,1,1,true);stalled.Write(stalledLease,new float[240]);stalled.Activate(stalledLease);
        for(int i=0;i<65;i++){clock+=10;stalled.Read(output,0,240);}
        Check(stalled.Failure!=null&&stalled.Connected,"stalled clip input fails finitely while the microphone route remains open");
        Check(MicrophoneLifetime.HideOnClose(virtualConfig,false)&&!MicrophoneLifetime.HideOnClose(virtualConfig,true)&&!MicrophoneLifetime.HideOnClose(physical,false),"only enabled virtual routing changes close-to-tray behavior; explicit Exit and physical close retain shutdown");
        var events=new List<string>();int number=0;bool reject=false;var persisted=virtualConfig;
        var controller=new MicrophoneRoutingController(virtualConfig,()=>new RouteFixture(events,(++number).ToString()));await controller.InitializeAsync();
        await controller.ApplyAsync(physical,c=>{persisted=c;return Task.CompletedTask;});
        Check(events.SequenceEqual(new[]{"start:1","stop:1"})&&controller.Mixer==null&&persisted.Backend==MicrophoneBackend.NativeMicrophone,"virtual-to-physical stops virtual capture before committing the native backend");
        await controller.ApplyAsync(virtualConfig,c=>{persisted=c;return Task.CompletedTask;});Check(controller.Mixer?.Connected==true&&events.Last()=="start:2","physical-to-virtual starts only the selected continuous backend");
        try{reject=true;await controller.ApplyAsync(virtualConfig with{VirtualVoiceEndpointId="new-voice"},c=>{persisted=c;if(reject&&c.VirtualVoiceEndpointId=="new-voice")throw new IOException("save fixture");return Task.CompletedTask;});}catch(IOException){}
        Check(persisted.VirtualVoiceEndpointId=="voice"&&controller.Mixer?.Connected==true&&events.TakeLast(4).SequenceEqual(new[]{"stop:2","start:3","stop:3","start:4"}),"failed persistence stops the candidate and restarts the restored previous route");await controller.StopAsync();
        var stubborn=new RouteFixture(events,"stubborn");var factoryCalls=0;var protectedController=new MicrophoneRoutingController(virtualConfig,()=>{factoryCalls++;return stubborn;});await protectedController.InitializeAsync();stubborn.FailStop=true;
        try{await protectedController.ApplyAsync(physical,_=>Task.CompletedTask);}catch(IOException){}
        Check(factoryCalls==1&&protectedController.Mixer==stubborn.Mixer&&!protectedController.SafeForTransmission,"a route that cannot stop blocks replacement rather than allowing simultaneous transmission");
        var blocked=new SoundboardCoordinator(()=>physical,transmissionAllowed:()=>protectedController.SafeForTransmission);blocked.Play("unused.wav");Check(!blocked.Active,"unconfirmed route shutdown also blocks subsequent physical clip transmission");
        stubborn.FailStop=false;await protectedController.StopAsync();Check(protectedController.SafeForTransmission,"confirmed shutdown permits transmission again");
        events.Clear();number=0;var recovering=new MicrophoneRoutingController(virtualConfig,()=>new RouteFixture(events,(++number).ToString(),number==2));await recovering.InitializeAsync();
        try{await recovering.ApplyAsync(virtualConfig with{VirtualVoiceEndpointId="missing"},_=>Task.CompletedTask);}catch(IOException){}
        Check(recovering.Mixer?.Connected==true&&events.SequenceEqual(new[]{"start:1","stop:1","start:2","stop:2","start:3"}),"failed device startup closes its candidate and restarts the previous saved route");await recovering.StopAsync();
        await VirtualCoordinatorChecks(virtualConfig);
        var main=new MainWindow{ShowActivated=false};main.Show();await Task.Delay(100);var window=new SoundboardSettingsWindow(main){ShowActivated=false};window.Show();await Task.Delay(80);
        var modes=Descendants<ComboBox>(window).Single(c=>c.Name=="MicrophoneMode");
        Check(modes.Items.Count==2&&modes.SelectedIndex==0&&!Descendants<CheckBox>(window).Single(c=>c.Name=="EnableVirtualMic").IsChecked==true,"new setup recommends virtual routing with enablement off");
        Check(Descendants<Button>(window).Any(b=>Equals(b.Content,"Get VB-CABLE (Official)"))&&Descendants<Button>(window).Any(b=>Equals(b.Content,"Refresh Devices")),"virtual settings expose official driver link and device refresh without installing a driver");Snapshot(window,"virtual-mic-settings.png");
        modes.SelectedIndex=1;await Task.Delay(30);Check(Descendants<TextBlock>(window).Any(t=>t.IsVisible&&t.Text.Contains("unsigned experimental setup disables protected AudioDG")),"physical mode retains its explicit unsigned AudioDG warning");Snapshot(window,"physical-mic-settings.png");
        window.Close();Check(main.Settings.Soundboard.Backend==MicrophoneBackend.NotConfigured,"Cancel leaves the current routing configuration unchanged");
        main.Settings.Soundboard=virtualConfig;main.Close();await Task.Delay(50);Check(!main.IsVisible&&Application.Current.Windows.OfType<MainWindow>().Contains(main),"closing an enabled virtual setup creates its Windows tray icon and hides the window");
        main.OpenFromTray();Check(main.IsVisible,"Open Soundshark restores the tray-hidden main window");
        await main.ApplySoundboardSettings(virtualConfig with{VirtualRoutingEnabled=false});main.Close();await Until(()=>!Application.Current.Windows.OfType<MainWindow>().Contains(main),"disabled virtual window did not shut down");Check(true,"disabled virtual shutdown releases tray resources and closes the main window");
        using var devices=new MMDeviceEnumerator();
        foreach(var flow in new[]{DataFlow.Capture,DataFlow.Render})foreach(var device in devices.EnumerateAudioEndPoints(flow,DeviceState.Active)){using(device)Console.WriteLine("DEVICE "+flow+": "+device.FriendlyName+" | "+device.ID);}
        Console.WriteLine("POLICY READ ONLY: "+MicrophoneSetup.ProtectionStatus);
        Console.WriteLine(passed+" virtual microphone checks passed");
    }
    static async Task VirtualCoordinatorChecks(SoundboardConfiguration settings)
    {
        string file=Path.Combine(Paths.Library,"Virtual PTT fixture.wav");
        using(var writer=new NAudio.Wave.WaveFileWriter(file,new NAudio.Wave.WaveFormat(48000,16,2))){var frames=new byte[48000*4/2];for(int i=0;i<frames.Length;i+=2){short sample=(short)(Math.Sin(i/4d*2*Math.PI*997/48000)*3000);frames[i]=(byte)sample;frames[i+1]=(byte)(sample>>8);}writer.Write(frames,0,frames.Length);}
        var voice=PrimedVoice();var mixer=new VirtualClipMixer();mixer.Connect(voice,1);var keys=new VirtualKeys();
        var current=settings with{SoundPtt=new(StringComparer.OrdinalIgnoreCase){[Path.GetFileName(file)]=new(true,new SoundboardChord(0,0x78))},RepeatSounds=true,StopHotkeySoundsAtEnd=false};
        var coordinator=new SoundboardCoordinator(()=>current,()=>mixer,keys);int started=0;coordinator.Started+=_=>Interlocked.Increment(ref started);
        using var lifetime=new CancellationTokenSource();var readTask=Task.Run(async()=>{var output=new float[960];var input=VoiceBytes(480);while(!lifetime.IsCancellationRequested){voice.Append(input,1,32,true);mixer.Read(output,0,output.Length);await Task.Delay(10);}});
        try{
            coordinator.Play(file);await Until(()=>Volatile.Read(ref started)>=1,"virtual clip did not start");coordinator.TogglePause();await Until(()=>keys.Count==0,"pause did not release PTT");
            Check(mixer.Connected&&keys.Count==0,"pausing virtual playback releases per-sound PTT and keeps voice running");
            coordinator.TogglePause();await Until(()=>keys.Count>0,"resume did not reacquire PTT");Check(keys.Count>0,"resuming virtual playback reacquires per-sound PTT");
            await Until(()=>Volatile.Read(ref started)>=2,"repeat did not start");await coordinator.StopAsync();Check(keys.Count==0&&mixer.Connected&&keys.Downs==keys.Ups,"repeat and Stop All release every PTT press while preserving the virtual voice route");
            coordinator.Play(file);await Until(()=>Volatile.Read(ref started)>=3,"clip restart did not start");mixer.FailClip(new IOException("injected clip failure"));await Until(()=>!coordinator.Active,"failed clip did not stop");Check(keys.Count==0&&mixer.Connected,"clip errors clean up PTT without shutting down the voice route");
            bool changed=false;await coordinator.ChangeRouteAsync(async()=>{coordinator.Play(file);await Task.Delay(25);changed=true;});Check(changed&&!coordinator.Active,"routing transaction rejects new transmission until the previous route change finishes");
        }finally{await coordinator.StopAsync();lifetime.Cancel();await readTask;}
    }
}
