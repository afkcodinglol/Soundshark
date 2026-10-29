using System.IO;

namespace Soundshark;

/// <summary>Raise quiet PC captures once, off the capture thread, without compressing their dynamics.</summary>
public static class PcRecordingAudio
{
    public static double Boost(SavedAudioSafety.Level level)
    {
        // Silence, unmeasurable short sounds and near-silent noise must not receive a boost.
        if (!double.IsFinite(level.Lufs) || !double.IsFinite(level.TruePeak) || level.Lufs < -60) return 0;
        return Math.Max(0, Math.Min(18, Math.Min(-18 - level.Lufs, -2 - level.TruePeak)));
    }

    public static async Task<string> Prepare(string pcm, CancellationToken ct, Action<string>? progress = null)
    {
        progress?.Invoke("Checking Recording Volume…");
        var before = await SavedAudioSafety.Measure(pcm, ct, raw: true);
        double gain = Boost(before);
        if (gain < .1) return pcm;
        string adjusted = Paths.NewPcm();
        try
        {
            progress?.Invoke("Adjusting Recording Volume…");
            await MediaTools.Run(Paths.Tool("ffmpeg"), ["-nostdin", "-v", "error", "-progress", "pipe:1",
                "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", pcm,
                "-af", SavedAudioSafety.Filter(gain), "-f", "f32le", adjusted], ct, progress);
            var after = await SavedAudioSafety.Measure(adjusted, ct, raw: true);
            if (!after.Safe) throw new SoundboardException("Couldn't safely adjust this recording's volume. Try recording again.");
            ct.ThrowIfCancellationRequested();
            AppLog.Info("recording.volume", new { before.Lufs, before.TruePeak, gain, outputLufs = after.Lufs, outputPeak = after.TruePeak });
            // Keep the original PCM path so workspace recovery and cleanup retain their existing ownership.
            File.Move(adjusted, pcm, true);
            return pcm;
        }
        finally { File.Delete(adjusted); }
    }
}
