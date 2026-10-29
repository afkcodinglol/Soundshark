using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Soundshark;
using Soundshark.Core;

internal static partial class Program
{
    static async Task TrackVolumeChecks()
    {
        var ct=CancellationToken.None;
        string loud=Path.Combine(Paths.Cache,"loud-track.wav");
        await MediaTools.Run(Paths.Tool("ffmpeg"),["-nostdin","-v","error","-f","lavfi","-i","sine=frequency=997:duration=2:sample_rate=48000","-af","volume=7","-c:a","pcm_f32le",loud],ct);
        string pcm=await MediaTools.Decode(loud,ct);var original=SHA256.HashData(await File.ReadAllBytesAsync(pcm));
        var before=await SavedAudioSafety.Measure(pcm,ct,raw:true);
        string prepared=await TrackAudio.Prepare(pcm,ct);
        var after=await SavedAudioSafety.Measure(prepared,ct,raw:true);
        Check(after.Safe&&after.Lufs<before.Lufs-8,"loud track is normalized to controlled loudness before Studio preview");
        var retainedHash=SHA256.HashData(await File.ReadAllBytesAsync(pcm));
        Check(original.SequenceEqual(retainedHash)&&new FileInfo(prepared).Length==new FileInfo(pcm).Length,"normalization preserves the source and exact track duration");
        Check(await TrackAudio.Prepare(prepared,ct)==prepared,"already controlled tracks are not attenuated repeatedly");
        string quiet=Paths.NewPcm();await MediaTools.Run(Paths.Tool("ffmpeg"),["-nostdin","-v","error","-f","f32le","-ar","48000","-ac","2","-i",prepared,"-af","volume=-20dB","-f","f32le",quiet],ct);
        Check(await TrackAudio.Prepare(quiet,ct)==quiet,"quiet tracks are not boosted by normalization");
        var workspace=new Workspace();workspace.Load(prepared,"Controlled track",false);
        string full=Path.Combine(Paths.Library,"full.mp3"),half=Path.Combine(Paths.Library,"half.mp3"),mute=Path.Combine(Paths.Library,"mute.mp3");
        await MediaTools.Export(workspace,full,192,false,false,ct);workspace.Volume=.5;await MediaTools.Export(workspace,half,192,false,false,ct);
        var fullLevel=await SavedAudioSafety.Measure(full,ct);var halfLevel=await SavedAudioSafety.Measure(half,ct);
        Check(fullLevel.Safe&&halfLevel.Safe&&Math.Abs((fullLevel.Lufs-halfLevel.Lufs)-6.02)<.35,"50 percent track volume saves about 6 dB quieter and remains safe");
        workspace.Volume=0;await MediaTools.Export(workspace,mute,192,false,false,ct);
        var muted=await SavedAudioSafety.Measure(mute,ct);Check(double.IsNegativeInfinity(muted.Lufs)&&double.IsNegativeInfinity(muted.TruePeak),"zero percent volume saves actual silence");
        workspace.Volume=.37;var restored=new Workspace();restored.Restore(workspace.Snapshot());Check(restored.Volume==.37,"track volume is retained in the workspace journal");
        var old=workspace.Snapshot() with {Volume=1};var serialized=System.Text.Json.JsonSerializer.Serialize(old);
        using(var document=System.Text.Json.JsonDocument.Parse(serialized)){
            var fields=document.RootElement.EnumerateObject().Where(p=>p.Name!="Volume").ToDictionary(p=>p.Name,p=>p.Value.Clone());
            var legacy=System.Text.Json.JsonSerializer.Deserialize<WorkspaceState>(System.Text.Json.JsonSerializer.Serialize(fields))!;restored.Restore(legacy);
            Check(restored.Volume==1,"older workspace journals default to full controlled volume");
        }
        var main=new MainWindow{Left=-20000,Top=-20000,ShowActivated=false,Width=1920,Height=1040};main.Show();await main.Studio.LoadPcm(pcm,"Normalized audio track");await Task.Delay(100);
        var volume=Descendants<Slider>(main).Single(s=>s.Name=="TrackVolume");var zoom=Descendants<Slider>(main).Single(s=>s.Name=="WaveZoom");var scroll=Descendants<Slider>(main).Single(s=>s.Name=="WaveScroll");
        Check(volume.IsEnabled&&new[]{zoom,scroll}.All(s=>Math.Abs(s.TransformToAncestor(main).Transform(new Point()).Y-volume.TransformToAncestor(main).Transform(new Point()).Y)<2),"Volume Zoom and Scroll align on the same row");
        Check(main.Studio.Wave.ActualHeight<=180&&main.Studio.Wave.ActualHeight>=100,"loaded waveform has a bounded normal track height");
        var controls=Descendants<StackPanel>(main.Studio).Single(p=>p.Name=="StudioEditingControls");
        Check(controls.TransformToAncestor(main.Studio).Transform(new Point()).Y<main.Studio.Wave.TransformToAncestor(main.Studio).Transform(new Point(0,180)).Y+120,"editing toolbar stays directly beneath the track");
        volume.Value=25;Check(main.Studio.Workspace.Volume==.25&&main.Studio.Workspace.Dirty,"volume adjustment changes the current track and marks it unsaved");
        main.Studio.TogglePlay();Check(main.Player.Active&&Math.Abs(main.Player.OutputGain-.25)<.001,"preview starts with the track volume setting");volume.Value=10;
        Check(main.Player.Active&&main.Studio.Workspace.Volume==.1&&Math.Abs(main.Player.OutputGain-.1)<.001,"volume changes take effect during ongoing preview without restarting it");main.Player.Stop();
        Snapshot(main,"track-volume-normal-height.png");await main.Studio.LoadPcm(quiet,"Another track");Check(volume.Value==100&&main.Studio.Workspace.Volume==1,"loading another track resets only its volume to the controlled baseline");
        main.Studio.Workspace.Dirty=false;main.Close();Console.WriteLine($"{passed} track volume checks passed");
    }
}
