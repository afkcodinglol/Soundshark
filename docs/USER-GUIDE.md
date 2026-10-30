# Using Soundshark

[Back to Soundshark](../README.md)

## Record or import

In **The Studio**, upload an audio file, paste an individual public YouTube/Shorts/TikTok HTTPS link and choose **Import URL**, or select **PC Audio** / **Microphone** and press **Record**. PC Audio captures the selected speaker or headphone output; Microphone captures the selected input without monitoring it.

All sources use one shared track. Switching source preserves the track and undo history. Recording or uploading replaces it after an unsaved-audio prompt. Previews are disabled during capture. Public-link imports do not support playlists, live streams, or authenticated/cookie imports.

Enable **Clipper** under PC Audio to keep the last 1–60 seconds ready to capture with **Clip Last**. Its fixed memory buffer discards older audio. Taking a clip leaves capture running; turn Clipper off to stop it.

## Edit a clip

Drag over the waveform to select audio. **Trim Selected** removes the selection and joins the remainder; **Keep Selected** removes everything outside it. Undo and redo let you revise edits, including **Delete All**. Original imports remain unchanged.

Use the numeric start/end fields for precise selection. Tracks start fitted to their full length; use **Zoom** and **Scroll** to work on a smaller section. **Volume** controls Studio preview and saved output.

**Space** previews, **Ctrl+Z / Ctrl+Y** undo and redo, **Ctrl+S** saves, and double-click selects everything. Use **Save To Library** to keep wanted audio before closing; new launches start with an empty Studio track.

## Use the Library

Search your saved audio, preview locally, or use the optional **Play Through Mic** route. Each sound has its own Stop control; **Stop All** stops all playback. Use the three-dot menu for more actions, including button colors. Enable **Group By Color** to keep matching colors together.

Choose **Shortcut** from a sound's three-dot menu to assign keyboard or mouse shortcuts with Mic, Preview, or Both routing, and optional push-to-talk keys held during playback. Input boxes capture mouse buttons directly; see the detailed shortcut instructions below.

Soundboard Settings offers **Soundshark Virtual Mic — VB-CABLE** (continuous voice and clips; select **CABLE Output** in Discord/games) and **Physical Mic — Experimental** (select your physical microphone). VB-CABLE is installed separately and virtual routing stays off until enabled. Physical setup uses the existing unsigned component and disables protected AudioDG. Read the [microphone guide](MICROPHONE.md) for setup, tray behavior and restoring previous audio settings.

## Convert audio

In **Audio Converter**, add files, choose MP3, WAV, FLAC, M4A, OGG, Opus, or AIFF, then convert. Settings are automatic. Compatible streams can be preserved without re-encoding; conversion cannot recover detail missing from a source. Duplicate filenames receive numbered suffixes and originals stay untouched. Converted files appear in Library.

## Prepare Discord Soundboard clips

The remembered **Discord Soundboard** toggle in Studio’s **Record Audio** panel applies to Studio and Converter. Recording stops after five seconds of input samples. Saves and conversions require MP3, at most five encoded seconds, and no more than 512,000 bytes. Encoding never silently shortens audio; MP3 padding can require a little extra trimming. Previews still play in full.

Turn the toggle off for full-length recording and other conversion formats. Its setting is locked during recording so the take keeps the limit it started with.

## Customize and update

Use the sun/moon switch in the main window for light/dark mode. **UI Settings** previews accents, fonts, text size, and Performance Mode; **Custom Font** imports TTF, OTF, TTC, or OTC files. Save keeps changes across launches; Cancel restores the previous appearance. Library layout controls are in Library. Performance Mode reduces motion and disables drag arranging.

Use **Update Soundshark** for app updates and **Update Import Tools** when website changes affect imports. Verified local backups support app/tool rollback. See [updates and rollback](UPDATES.md) for details.

## Files and troubleshooting

The default Library is `%LOCALAPPDATA%/Programs/Soundshark/Library`; settings and recovery data live under `%LOCALAPPDATA%/Soundshark`. These survive uninstall.

Errors show a reference ID. **Open Logs** finds the corresponding local diagnostic record under `%LOCALAPPDATA%/Soundshark/Logs`, with `%TEMP%/Soundshark/Logs` as a fallback. Logs rotate and do not record audio or upload themselves. Review diagnostic files for personal information before sharing them in an issue.

## Library layout and sound shortcuts

The Library toolbar groups **Group By Color**, **Compact Library Tiles** and **Newest Sounds First**.

Use **List View** / **Grid View** to switch layouts; Soundshark remembers your choice. In List view,
drag a sound by its left grip to arrange it. Neighboring rows slide aside, and holding near an edge
scrolls the list. Drop to save the order, or press Escape to cancel. You can also arrange search
results while hidden sounds keep their positions. New sounds append to your custom order.
Choosing Group By Color or Newest Sounds First returns to automatic sorting.
Performance Mode disables drag ordering and its animations; the view switch remains available.
File details are always displayed, followed by that sound’s **Play Count**. Preview and microphone
playback starts increment its count; stopping and failed playback do not.

Choose **Shortcut** from a sound’s menu to bind keyboard/mouse inputs and its Mic / Preview / Both route.
Enable **Hold down PTT button** there to hold a key combination during that sound’s playback.
Click a box to listen; click it again while listening to bind the left mouse button. Other mouse buttons
and wheel inputs bind directly. Use **Clear** to remove a binding. Wheel inputs cannot be held as PTT.
Global shortcuts, pass-through and left/right modifier detection are automatic. Soundboard Settings
contains Audio, playback Hotkeys and Advanced tabs; global Auto Keys and microphone EQ have been removed.

## Studio track volume

Loud tracks are brought down before preview without changing the imported original. **Volume**, beside
Zoom and Scroll, controls both live Studio preview and saved output. Use 100% for the controlled baseline,
50% for about 6 dB less volume, or 0% to mute. Saved output still receives a final encoded loudness/peak check.
The waveform stays at a compact track height and the editing controls sit directly below it.
