# Local virtual microphone preview — 2026-10-29

This build is local-only. No GitHub release, VB-CABLE installation, endpoint cleanup, AudioDG policy change, Windows default-device change or Discord/game setting change was performed.

## Automated and local results

| Coverage | Result | Evidence under outputs/qa |
| --- | --- | --- |
| Full existing desktop regression | 421 passed | virtual-regression.txt |
| New virtual configuration, mixer, route transaction, PTT and tray checks | 45 passed | virtual-mic-checks.txt |
| Existing native APO callback and component lifecycle | 32 passed | virtual-native-regression.txt |
| Isolated HKCU registry cleanup fixtures | 20 passed; no live endpoint or policy writes | native-cleanup-checks.txt |
| Existing per-sound PTT/settings reorganization | 27 passed | virtual-reorg.txt |
| Live physical microphone route on this Windows 10 PC | 10 passed | virtual-physical-live.txt |

Virtual checks cover continuous voice without clips; prepared, active, paused and completed clip blocking; finite clip starvation; peak protection; generation fencing; bounded overflow and stale-audio reset; PCM 16/24/32-bit and float conversion; mono/stereo; output-rate differences; and ten simulated minutes at each of 44.1/48/96 kHz with ±1000 ppm input-clock drift. These are deterministic simulations, not prolonged hardware sessions.

Transaction fixtures cover both switch directions, failed device startup, failed persistence with previous-route restart, stopping before starting a replacement, and blocked future transmission when shutdown is unconfirmed. Coordinator checks run actual FFmpeg clip preparation with injected key input, pause/resume, repeat, Stop All, clip errors and route-change exclusion. Tray checks create/remove the real Windows tray icon and verify hide/reopen/normal disabled shutdown. Explicit Exit warnings and unsaved-recording prompts still need manual acceptance testing.

Cleanup fixtures cover saved policy absence and explicit zero, original effects, multiple configured endpoints, missing endpoint/policy backups, later slot/mode/policy conflicts, all-endpoint preflight, legacy version 2 backups and idempotent repeat cleanup. Helpers refuse live writes outside their protected installation. Only explicitly invoked setup/cleanup can write audio policy; the virtual route contains no registry or setup-policy writes.

The physical test used the already-installed Stealth Pro Xbox native component at 48 kHz. Independent WASAPI capture detected the 997 Hz clip signal at amplitude 0.104275 versus expected 0.104145. EOF, pause/resume and Stop passed. This verifies clip transmission through the retained physical backend; it does not establish spoken voice quality or Discord/game reception. Its obsolete EQ assertion was updated to check current producer/native graph heartbeats because global microphone EQ was previously removed.

The full suite's two-hour disk fixture was corrected to explicitly mark its file sparse before extending it; previously it allocated several gigabytes despite its sparse-file comment. The corrected suite completed all 421 checks.

## Outstanding before publication

This PC has **no VB-CABLE endpoints installed**, and its legacy native setup has **protected AudioDG disabled**. The policy was read and left unchanged. Selecting virtual mode alone must not be represented as restoring protection.

- Install VB-CABLE separately on a test machine, use a physical voice source and matching CABLE Input/Output, and independently capture CABLE Output. Verify voice alone, voice plus clips, voice blocking, clip-only headphone monitoring, pause/stop/EOF/repeat, timing offsets, and no delayed-audio buildup.
- Repeat the virtual test with protected AudioDG enabled, observing that Soundshark performs no policy writes. The current disabled-policy machine cannot supply this proof.
- Exercise actual device unplug/replug, cable service loss, sleep/resume and several hours of capture at differing hardware rates. The implementation uses saved-device-only retries, bounded queues and power notifications; automated reset tests are not physical reconnection proof.
- Manually exercise every tray command, explicit Exit's silence warning, recording/unsaved-audio prompts, and process shutdown while devices are unavailable.
- Test elevated legacy removal and restart on a disposable Windows image, including missing backups/conflicts. Isolated registry fixtures do not validate every OEM effect pack or the real elevated uninstaller.
- Validate microphone-plus-clip reception in Discord and a game on **Windows 10 and Windows 11**. Neither external-app reception nor Windows 11 has been tested here.

Publish only after completing hardware/platform acceptance and receiving a separate publication request.
