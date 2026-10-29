using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Soundshark;

public sealed class MediaToolException(string tool, int exitCode, string diagnostics) : Exception($"{tool} exited with code {exitCode}.\n{diagnostics}")
{
    public string Tool { get; } = tool;
    public string Diagnostics { get; } = diagnostics;
}
public sealed record UserError(string Message, string Reference)
{
    public string Inline => $"{Message} (Ref: {Reference})";
    public string Display => $"{Message}\n\nReference: {Reference}\nUse Open Logs to find the details.";
}
public static class UserErrors
{
    public static UserError Report(Exception ex, string operation, object? context = null) => new(Message(ex, operation), AppLog.Error(operation, ex, context));
    public static string Message(Exception ex, string operation)
    {
        if (ex is SoundboardException) return ex.Message;
        if (operation.StartsWith("clipper", StringComparison.OrdinalIgnoreCase))
            return ex is ArgumentException && ex.Message.StartsWith("This device's audio format", StringComparison.Ordinal)
                ? ex.Message : "Clipper couldn't capture PC audio. Check the selected audio device, then turn Clipper off and on to reconnect.";
        string detail = ex is MediaToolException tool ? tool.Diagnostics : ex.Message;
        bool Has(string text) => detail.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (Has("No space left") || Has("disk is full") || ex is IOException io && (io.HResult & 0xffff) is 0x70 or 0x27)
            return "There isn't enough free disk space. Free some space and try again.";
        if (ex is MediaToolException media)
        {
            if (media.Tool == "yt-dlp")
            {
                if (Has("blocked") || Has("bot")) return "The website blocked this download. Try another public link or update the import tools.";
                if (Has("unavailable") || Has("private") || Has("sign in") || Has("login")) return "This video isn't available without signing in. Try another public video link.";
                return "Couldn't download this video. Check your connection and the public link, or update the import tools.";
            }
            if (media.Tool == "ffprobe") return "Couldn't read audio from this file. It may be damaged or contain no audio. Try another file.";
            return "Couldn't process this audio. Check the file and available disk space, then try again.";
        }
        // Only messages authored by Soundshark's validators may pass through unchanged.
        if (ex is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            string[] trusted = ["Encoded MP3 is ", "MP3 is ", "Audio is ", "This audio is ", "Enter one public HTTPS ", "This link does not identify ", "Live streams and playlists ", "There is no audio to save.", "Enter a filename.", "Unsupported audio format.", "Choose a supported output format.", "Output filename does not match ", "Discord Soundboard requires MP3.", "No previous downloader toolset is available."];
            if (trusted.Any(prefix => ex.Message.StartsWith(prefix, StringComparison.Ordinal))) return ex.Message;
            if (ex.Message is "This file contains no audio stream." or "No audio could be decoded." or "The encoder produced no valid audio.") return "This file doesn't contain readable audio. Try another file.";
            if (ex.Message == "The website returned no audio.") return "This video didn't provide downloadable audio. Try another public video link.";
        }
        if (ex is UnauthorizedAccessException)
            return operation.Contains("record", StringComparison.OrdinalIgnoreCase) ? "Recording access isn't available. Check device permissions in Windows Settings and try again." : "Soundshark can't access that file or folder. Check its permissions or choose another location.";
        if (ex is FileNotFoundException or DirectoryNotFoundException) return "A required file or folder couldn't be found. It may have been moved or deleted.";
        if (ex is Win32Exception win && win.NativeErrorCode is 2 or 193) return "A required audio tool is missing or damaged. Reinstall Soundshark and try again.";
        if (ex is HttpRequestException or TimeoutException) return "Couldn't connect. Check your internet connection and try again.";
        if (operation.Contains("record", StringComparison.OrdinalIgnoreCase)) return "Recording stopped or couldn't start. Check the selected device, Windows microphone permissions, and other apps using the device.";
        if (operation.Contains("play", StringComparison.OrdinalIgnoreCase)) return "Couldn't play this audio. Check your output device and try again.";
        if (operation.Contains("recover", StringComparison.OrdinalIgnoreCase) || ex is JsonException) return "Couldn't restore that workspace. Your Library files are still available.";
        if (operation.Contains("snapshot", StringComparison.OrdinalIgnoreCase)) return "Couldn't save a recovery copy. Keep the app open and save your audio to Library.";
        if (operation.Contains("update", StringComparison.OrdinalIgnoreCase)) return "Couldn't update the import tools. Your previous tools are still available. Check your connection and try again.";
        if (ex is IOException) return "Couldn't read or save the file. Check folder access and available disk space, then try again.";
        return "Something went wrong. Please try again. If it continues, use Open Logs to share the details.";
    }
}
