# Changelog

What changed in each version of HighlightCut, for the people who use it. The app shows these notes in "What’s new"
after an update, and every release on GitHub uses them.

Every change you would notice adds a line under Unreleased in the same pull request, in New, Improved or Fixed.
See [docs/releasing.md](docs/releasing.md).

## Unreleased

### Improved

- The welcome tour offers the transcription model download on its Transcript step and explains the timeline chips.
- The welcome tour works from the keyboard (Enter, the arrow keys, Tab stays in the dialog) and fits smaller windows.
- Connect Claude, in the welcome tour and Settings → MCP server, adds HighlightCut to Claude Code and Claude Desktop in one click. It replaces an old or moved entry, and says clearly when Claude Code isn’t installed.
- Adding to Claude Desktop keeps your other servers and settings and saves a backup of the old file.
- The waveform is on by default, so you can see loud and quiet moments right away; turn it off with its chip. The video still plays at once while the waveform fills in.

## 0.1.0 — 2026-09-30

### New

- A short welcome tour on the first start: opening and cutting, the main shortcuts, the transcript and connecting Claude. Reopen it from the project menu → Welcome tour.
- “What’s new” after an update, with these notes. Reopen it from the project menu → What’s new.
- Clips snap onto their neighbours when you trim them (the magnet), J joins a clip with the next one, and clips never overlap, so the export never plays a moment twice.
- Every clip gets its own name and colour, on the timeline and in the Clips list.
- A volume slider on each audio track. You hear it while you play, and the export and the project keep it.
- Transcription with Parakeet or Whisper on your computer. Download a model in Settings → Transcription.
- The Transcript tab: click a word to jump there, search, and select words to play them, keep them as a clip or cut them out. Filler words are underlined.
- Transcription on AMD, Intel and NVIDIA graphics cards through DirectML, after a quick check that the card works. Otherwise it uses every core of the processor.
- Claude edits the timeline through MCP: adding, trimming, splitting and reordering clips, cutting by the transcript, finding pauses and exporting. Every edit shows up in the Claude panel with its own Undo.
- Silence and scene detection: pauses show as bands on the audio tracks, scene changes as markers on the ruler.
- Export as one file or separate files, lossless (no re-encoding) or re-encoded on the graphics card when there is one. It has a file name pattern, a choice for when the file already exists and a choice for what happens after export.
- Settings for startup, recent files, playback, export, transcription, the keyboard and the MCP server. Shortcuts follow Settings → Keyboard.
- The version number at the bottom of the Settings list.

### Improved

- The app is now called HighlightCut. Settings, recent files, models and the cache move over from OurCut by themselves, and .ourcut.json projects still open.
- Videos open at once. Keyframes, the waveform, frames, silences and scenes are only worked out when their chip on the timeline is on, and the chips are remembered.
- A progress screen with the time left while a video is prepared.
- The audio track fills in faster.
- Playback settings take effect while the video plays.

### Fixed

- A click on the timeline always moves the playhead to where you clicked.
- The transcript no longer strikes words through before you have marked any clips.
- Switching the video renderer no longer closes the open video.
- A cancelled export always removes its unfinished files.
