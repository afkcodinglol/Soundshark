# Microphone playback

Preview plays clips locally. **Play Through Mic** uses the mode selected in **Soundboard Settings → Audio**. Windows default devices and Discord/game settings are never changed automatically.

| Mode | Setup | Microphone to select in your voice app |
| --- | --- | --- |
| **Soundshark Virtual Mic — VB-CABLE** | Install VB-CABLE separately; enable routing inside Soundshark | **CABLE Output** |
| **Physical Mic — Experimental** | Explicit unsigned native component installation, administrator access and restart | Your selected physical microphone |

New setups recommend VB-CABLE with routing **off**. Existing physical setups retain their mode and device. Device selections for both modes are saved independently. Switching stops clips and releases their PTT keys before replacing the route. Cancel does not change routing. A failed save restores the previous configuration and attempts to restart its route; check status if a device remains unavailable.

## Soundshark Virtual Mic — VB-CABLE

1. Download VB-CABLE from its [official website](https://vb-audio.com/Cable/). Follow its separate administrator installation and restart instructions. Soundshark does not bundle or install the driver.
2. In Soundboard Settings, choose **Soundshark Virtual Mic — VB-CABLE**, then **Refresh Devices**.
3. Set **Physical Voice Source** to your real microphone; **Cable Playback Endpoint** to **CABLE Input**; and **Cable Recording Endpoint** to the corresponding **CABLE Output**.
4. Choose headphones or speakers for local clip monitoring. Never monitor clips to CABLE Input.
5. Check **Enable Virtual Mic**, save, and confirm the connected status. In Discord/games select **CABLE Output** as the microphone. Do not select your physical source in the voice app for this mode.

“Soundshark Virtual Mic” names the routing choice inside Soundshark. It does not create or rename a Windows device. VB-CABLE's playback Input carries the mix into its recording Output.

While enabled, Soundshark continuously sends your physical voice through the cable, including between clips. Pause, Stop All Sounds, clip EOF and clip failures stop clips while voice continues. **Block My Voice** mutes voice only during active clip playback. Monitoring contains clips, never live voice. Volume matching, clip output volume, timing, repeat and per-sound PTT remain available.

The virtual route uses shared-mode WASAPI, bounded queues, sample conversion, microphone clock-drift compensation and mix peak protection. A disconnected or sleeping device stops the current clip and discards stale audio. Reconnection retries only the saved endpoints; it does not silently choose a replacement microphone. Re-select devices in settings if their Windows identities change.

Closing the main window keeps enabled virtual routing running in the Windows tray. The tray menu provides **Open Soundshark**, **Enable/Disable Virtual Mic**, **Stop All Sounds**, and **Exit**. Explicit Exit warns that CABLE Output becomes silent, then performs the usual recording/unsaved-audio checks. Disabled virtual routing and physical mode use the usual close behavior. Soundshark must remain running for the virtual route to carry voice.

## Physical Mic — Experimental

Select your physical microphone and choose **Enable Microphone Playback**. This is the existing unsigned native APO setup. It explicitly sets `DisableProtectedAudioDG=1` system-wide, reducing Windows audio protection and potentially affecting protected-media playback. Administrator access and a Windows restart are required. It does not change Secure Boot or kernel driver-signature enforcement. Portable app copies require the same machine-wide native setup.

After restart, select that same physical microphone in Soundshark and your voice app. This mode remains selectable after removal, but transmission requires its explicit setup again. Its native callback preserves ordinary voice while idle, paused or stopped and bypasses safely when its control connection is lost. Per-sound PTT, automatic volume matching and the existing playback controls remain available.

## Restore previous audio settings

Selecting virtual mode does **not** uninstall native support or restore protected AudioDG. Settings display the actual policy status separately. Virtual routing performs no AudioDG policy writes and requires no protection change.

Use **Remove Experimental Support & Restore Previous Settings** for explicit elevated cleanup of every configured Soundshark microphone. It restores backed-up endpoint effects and the saved policy value, type or original absence, then invokes component removal. Restart Windows when prompted. The individual **Remove Microphone Support** action restores one device; the saved policy is restored after the last Soundshark endpoint is removed.

Protected backups remain under `%ProgramFiles%/SoundsharkAudio/EndpointBackups` and `PolicyBackup`; diagnostics are in `SetupLogs`. Cleanup checks all known endpoints and policy before restoration. Unrelated properties remain untouched. Conflicting later effect/policy changes and missing backups stop cleanup instead of guessing original registry values.

If repair is required, preserve those folders and logs. Recover the original matching backups from the installation or a system backup, then review the reported conflict with support. Do not delete effect keys or guess the original AudioDG policy. Missing component uninstallers require installation repair even if endpoint/policy restoration has succeeded.

## Validation and local rollout

This feature is delivered as a **local preview portable build** before publication. See [virtual microphone validation](VIRTUAL-MIC-VALIDATION.md) for the exact checks and outstanding hardware/platform coverage. Automated mixer and fake-device checks do not prove actual VB-CABLE, Discord/game reception, OEM compatibility, or Windows 11 behavior.

## Rebuild the preview from a fresh Git clone

The repository contains source, tests and build scripts. Generated portable ZIPs, media tools, native build outputs, personal sounds and settings are excluded from Git.

On Windows, install the pinned .NET SDK from `global.json`, Visual Studio C++ x64 build tools, Inno Setup 6, and the pinned FFmpeg build described in [dependencies](DEPENDENCIES.md). From the cloned repository root, run:

```powershell
.\scripts\Fetch-Tools.ps1
.\scripts\BuildNative.ps1 -Checks
dotnet restore src/Soundshark -r win-x64
.\scripts\Package-VirtualMicPreview.ps1
```

The portable preview appears under `outputs/virtual-mic-local/<timestamp>/`. Building and packaging do not install microphone support, install VB-CABLE, or change Windows audio policy. Preserve your Library, `%LOCALAPPDATA%/Soundshark`, and protected native setup backups separately before removing an old local copy.
