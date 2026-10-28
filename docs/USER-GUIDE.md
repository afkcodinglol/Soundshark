# Using Soundshark

[Back to Soundshark](../README.md)

## Record or import

In **The Studio**, upload an audio file, paste an individual public YouTube/Shorts/TikTok HTTPS link and choose **Import URL**, or select **PC Audio** / **Microphone** and press **Record**. PC Audio captures the selected speaker or headphone output; Microphone captures the selected input without monitoring it.

All sources use one shared track. Switching source preserves the track and undo history. Recording or uploading replaces it after an unsaved-audio prompt. Previews are disabled during capture. Public-link imports do not support playlists, live streams, or authenticated/cookie imports.

Enable **Clipper** under PC Audio to keep the last 1–60 seconds ready to capture with **Clip Last**. Its fixed memory buffer discards older audio. Taking a clip leaves capture running; turn Clipper off to stop it.

## Edit a clip

Drag over the waveform to select audio. **Trim Selected** removes the selection and joins the remainder; **Keep Selected** removes everything outside it. Undo and redo let you revise edits, including **Delete All**. Original imports remain unchanged.

Use **Precise Selection** for numeric start/end times. Long tracks use a fifteen-second view: scroll the mouse wheel to move through the track. The view follows playback.

**Space** previews, **Ctrl+Z / Ctrl+Y** undo and redo, **Ctrl+S** saves, and double-click selects everything. Use **Save To Library** to keep wanted audio before closing; new launches start with an empty Studio track.

## Use the Library

Search your saved audio, preview locally, or use the optional **Play Through Mic** route. Each sound has its own Stop control; **Stop All** stops all playback. Use the three-dot menu for more actions, including button colors. Enable **Group By Color** to keep matching colors together.

Each row's **Shortcut** button assigns keyboard or mouse shortcuts with Mic, Preview, or Both routing. Middle and side buttons can be captured directly; right-click a shortcut box for all mouse-button choices. Global shortcut options and automatic input settings are in **Soundboard Settings**.

Physical microphone playback is experimental and requires separate system setup. Read the [microphone guide](MICROPHONE.md) before enabling it.

## Convert audio

In **Audio Converter**, add files, choose MP3, WAV, FLAC, M4A, OGG, Opus, or AIFF, then convert. Settings are automatic. Compatible streams can be preserved without re-encoding; conversion cannot recover detail missing from a source. Duplicate filenames receive numbered suffixes and originals stay untouched. Converted files appear in Library.

## Prepare Discord Soundboard clips

The remembered **Discord Soundboard** toggle applies to Studio and Converter. Recording stops after five seconds of input samples. Saves and conversions require MP3, at most five encoded seconds, and no more than 512,000 bytes. Encoding never silently shortens audio; MP3 padding can require a little extra trimming. Previews still play in full.

Turn the toggle off for full-length recording and other conversion formats. Its setting is locked during recording so the take keeps the limit it started with.

## Customize and update

**UI Settings** previews dark/light mode, accents, fonts, text size, and Library layout. Save keeps changes across launches; Cancel restores the previous appearance. Performance Mode reduces motion.

Use **Update Soundshark** for app updates and **Update Import Tools** when website changes affect imports. Verified local backups support app/tool rollback. See [updates and rollback](UPDATES.md) for details.

## Files and troubleshooting

The default Library is `%LOCALAPPDATA%/Programs/Soundshark/Library`; settings and recovery data live under `%LOCALAPPDATA%/Soundshark`. These survive uninstall.

Errors show a reference ID. **Open Logs** finds the corresponding local diagnostic record under `%LOCALAPPDATA%/Soundshark/Logs`, with `%TEMP%/Soundshark/Logs` as a fallback. Logs rotate and do not record audio or upload themselves. Review diagnostic files for personal information before sharing them in an issue.
