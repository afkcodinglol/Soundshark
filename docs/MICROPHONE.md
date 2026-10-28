# Experimental Physical Microphone Playback — 1.6.0

Library's Preview button plays locally. Play Through Mic adds a clip to the selected physical microphone, which remains selected in Discord/games. This is an unsigned experimental native APO; no paid signing is required for this approved test configuration.

In Soundboard Settings, choose your microphone and Enable Microphone Playback. Setup requires administrator access and a Windows restart. It explicitly sets DisableProtectedAudioDG=1 system-wide, reducing Windows audio protection and potentially affecting protected-media playback. Each recipient must understand this change before enabling it. The installer never changes Secure Boot or kernel driver-signature enforcement.

Before modification, the protected installer records the exact original microphone effects and the original audio-policy value/type/absence. Original endpoint effects run before Soundshark's mix; their processing-mode declarations are preserved. Legacy-only/composite effects or unsupported mode declarations are refused. Remove Microphone Support restores the endpoint; removing the last Soundshark endpoint restores the saved audio policy. Uninstall also restores configuration. Later differing third-party values are preserved. Backups remain under Program Files/SoundsharkAudio/EndpointBackups and PolicyBackup. Setup diagnostics are in SetupLogs.

After restarting, select the same physical microphone in Soundshark and your voice app, save Settings, and test a short clip. Preview remains full length. The Discord toggle still limits recordings/exports, without truncating playback. Voice should pass through while idle, paused, stopped, after EOF or publisher timeout. The native callback does no allocation, blocking, file access or logging. Its compact locked ring uses protocol version 2 and independent graph readers. Failure to map/lock memory leaves voice unchanged. Peak protection is a block limiter, not a true-peak limiter.

The UI includes volume matching, independent voice/clip/preview levels, keyboard shortcuts, auto push-to-talk, timing and repeat. See the app settings pages. No full Soundpad parity is claimed: mouse shortcuts, categories/numeric-code playback and progressive loudness estimation are not implemented.

Controlled checks cover the app, mixer/APO lifecycle, producer IPC, locked-ring allocation in the test host, key cleanup through a fake transport, and isolated registry backup/restoration. These do not prove protected-process compatibility, OEM effect chaining, real hotkeys/PTT, sustained device playback or Discord/game acceptance. Live physical microphone transmission passed on the Stealth Pro Xbox at 48 kHz as a normal user, through the WPF Library button. Independent capture detected the known 997 Hz signal; EOF, pause/resume and stop passed. Discord/game reception, spoken-voice mixing, other devices, prolonged playback and Windows 11 remain unverified. If playback does not work, keep the diagnostic reference and use Remove Microphone Support to restore configuration.

Build with scripts/BuildNative.ps1 -Checks, run tests/CheckNativeSetup.ps1 and the Soundshark.Checks app suite, then scripts/Package.ps1. Both installer and portable include the native setup. Portable app use still requires machine-wide microphone setup. Ordinary signing remains optional for the app and is not required by the experimental setup; see SIGNING.md for the separate release-signing research.

## Microphone EQ

Open Soundboard Settings → Microphone EQ. Enable it, choose Clear Voice/Warm Voice or adjust the five bands, then Save Settings. EQ affects your voice while Soundshark is open; open the microphone in your voice app to activate it. It defaults off and automatically bypasses on close or lost control connection. Version 1.6.0 requires the matching updated microphone component (IPC v3); run Enable Microphone Playback to update it.

Saving or converting audio now automatically reduces excessive loudness and verifies the encoded file. Quiet audio is never boosted and originals are preserved.
