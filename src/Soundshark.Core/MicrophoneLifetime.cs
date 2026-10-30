namespace Soundshark.Core;

public static class MicrophoneLifetime
{
    public static bool HideOnClose(SoundboardConfiguration settings, bool explicitExit) => settings.UsesContinuousVirtualMic && !explicitExit;
}
