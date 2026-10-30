using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace Soundshark;
public static class MicrophoneSetup
{
    public static string ProtectionStatus
    {
        get {
            try {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio");
                var value = key?.GetValue("DisableProtectedAudioDG");
                return value is int number && number == 1
                    ? "Protected AudioDG is currently DISABLED by Windows policy. Choosing virtual routing alone does not re-enable it. Use the explicit cleanup action to restore the saved settings."
                    : value == null || value is int zero && zero == 0
                        ? "Protected AudioDG is enabled by policy. Virtual routing performs no audio-policy writes."
                        : "Windows has a custom AudioDG policy value. Virtual routing leaves it unchanged; review it before claiming protection is enabled.";
            } catch { return "AudioDG protection status could not be read. Virtual routing does not change this policy."; }
        }
    }
    public static void RemoveAll()
    {
        string setup = Path.Combine(AppContext.BaseDirectory, "Native", "Soundshark-Microphone-Setup-x64.exe");
        if (!File.Exists(setup)) throw new SoundboardException("The experimental cleanup package is missing. Use the complete local portable package. No Windows policy was changed.");
        var start = new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Normal };
        start.ArgumentList.Add("/REMOVE_ALL_SUPPORT=1");
        using var identity = WindowsIdentity.GetCurrent(); start.ArgumentList.Add("/USER_SID=" + identity.User!.Value); Process.Start(start);
    }
    public const string Clsid = "{A08799CD-8535-492D-A1B4-F4C8B51E8779}";
    public static bool DevelopmentBuild => File.Exists(Path.Combine(AppContext.BaseDirectory, "Native", "UNSIGNED-DEVELOPMENT.txt"));
    public static bool ExperimentalEnabled {
        get { using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio"); return key?.GetValue("DisableProtectedAudioDG") is int value && value == 1; }
    }
    public static string Status(string endpoint)
    {
        try {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\" + NativeAudioBridge.EndpointGuid(endpoint) + @"\FxProperties");
            string? value = key?.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7") as string;
            return string.Equals(value, Clsid, StringComparison.OrdinalIgnoreCase) ? ExperimentalEnabled ? "Experimental microphone support installed. Restart Windows after setup, then test playback." : "Microphone support is installed, but experimental audio policy is not enabled." : "Enable experimental microphone playback for this device. Setup needs administrator access and a restart.";
        } catch (Exception ex) { AppLog.Warn("soundboard.setup.status", ex); return "Couldn't check microphone support for this device."; }
    }
    public static void Launch(string endpoint, bool remove)
    {
        string setup = Path.Combine(AppContext.BaseDirectory, "Native", "Soundshark-Microphone-Setup-x64.exe");
        if (!File.Exists(setup)) throw new SoundboardException("The microphone setup package is missing. Install the complete Soundshark build and try again.");
        using var identity = WindowsIdentity.GetCurrent();
        var start = new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Normal };
        start.ArgumentList.Add("/ENDPOINT=" + NativeAudioBridge.EndpointGuid(endpoint));
        start.ArgumentList.Add("/USER_SID=" + identity.User!.Value);
        if (remove) start.ArgumentList.Add("/REMOVE_SUPPORT=1");
        else start.ArgumentList.Add("/EXPERIMENTAL=1");
        Process.Start(start);
    }
}
