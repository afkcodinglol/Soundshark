using System.IO;

namespace Soundshark;

/// <summary>Prepare a controlled Studio source without altering the imported file.</summary>
public static class TrackAudio
{
    public static async Task<string> Prepare(string pcm,CancellationToken ct,Action<string>? progress=null)
    {
        progress?.Invoke("Checking Track Volume…");
        var before=await SavedAudioSafety.Measure(pcm,ct,raw:true);
        if(before.Safe) return pcm;
        string adjusted=Paths.NewPcm();
        try {
            progress?.Invoke("Normalizing Track Volume…");
            await MediaTools.Run(Paths.Tool("ffmpeg"),["-nostdin","-v","error","-progress","pipe:1","-f","f32le","-ar","48000","-ac","2","-i",pcm,
                "-af",SavedAudioSafety.Filter(before.Reduction),"-f","f32le",adjusted],ct,progress);
            var after=await SavedAudioSafety.Measure(adjusted,ct,raw:true);
            if(!after.Safe||new FileInfo(adjusted).Length!=new FileInfo(pcm).Length)throw new InvalidDataException("Couldn't safely normalize this track. The original audio was retained.");
            ct.ThrowIfCancellationRequested();
            return adjusted;
        } catch {File.Delete(adjusted);throw;}
    }
}
