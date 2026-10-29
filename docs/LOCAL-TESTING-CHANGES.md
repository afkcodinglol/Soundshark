# Library Grid / List views

The Library toolbar now switches between **List View** and **Grid View**, remembering the chosen view.
In List view, drag the grip at the left of a sound to arrange it. A floating row follows the pointer,
other rows slide into place, and the list scrolls when you hold near an edge. Drop to save; Escape,
switching tabs, dropping outside the list, or leaving the window cancels the drag.
Custom order survives refreshes and restarts. Filtered arranging preserves hidden sounds' slots;
new sounds append, and renames retain their position. Choosing Group By Color or Newest Sounds First
returns to automatic sorting. Performance Mode disables rearranging and drag animations, while
both views remain available. Sliders are unchanged.

# Track volume and waveform size

Studio now has **Volume** beside Zoom and Scroll. Loud imports are attenuated to the existing
controlled baseline before preview; original files remain unchanged. The 0–100% slider affects live
Studio preview and saved MP3 output. 100% is the controlled level, 50% is about 6 dB lower, and 0% is silence.
New tracks start at 100%. Track volume is retained in workspace journals. The waveform is 160 pixels high,
with its editing toolbar directly underneath; source panels remain expandable.

# Current local testing changes

No GitHub upload or release was made.

Library:
- Each sound shows **Play Count: N** under its file information; there is no total-play counter.
- Counts start from per-sound tracking in this build. The former total cannot be attributed to individual files.
- Counts are saved, follow renames, and are removed when their sound is deleted.
- Group By Color, Compact Library Tiles and Newest Sounds First are grouped in the Library toolbar.
- File details are always shown. UI Settings now contains appearance/font/text-size/performance preferences.

Studio:
- Discord Soundboard is in the Record Audio panel. Its remembered setting still controls Discord output limits.
- The waveform still starts fitted to the entire track with large zoom/scroll controls.

Soundboard Settings:
- Microphone EQ and Auto Keys have been removed.
- Global shortcuts, pass-through and left/right Ctrl/Alt/Shift detection are automatically enabled.
- Hotkeys contains playback controls only. Old global Auto Keys are discarded on upgrade.

Sound Shortcuts:
- **Hold down PTT button** and its key box configure PTT for that sound across playback routes.
- Only keys pressed by Soundshark are released. Stop, completion and startup failure clean up held keys.
- Clicking a shortcut box arms it; a second left click binds Mouse Left. Right, middle and side buttons bind
  directly. Wheel directions, modifier-only keys, Escape, F12 and Windows-mapped media keys are supported.
- **Clear** removes a binding. Wheel inputs are impulses and cannot be used as a held PTT button.

Submissions:
- A Type selector separates problem reports from suggestions.
- Suggestions: 5 per rolling 24 hours. Problem and sound reports: 6 combined per rolling 12 hours.
- Owner remains exempt. Hosted limits need **SubmissionLimits.sql** and the regenerated **sounds-hub.ts**.
  See `outputs/hub-deployment/LOCAL-SETUP.md` for the incremental deployment steps.

Existing custom fonts, theme switch, Owner crown, private report inbox, refresh behavior and update glow remain.
