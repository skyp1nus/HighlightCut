# Changelog

What changed in each version of HighlightCut, for the people who use it. The app shows these notes in "What’s new"
after an update, and every release on GitHub uses them.

Every change you would notice adds a line under Unreleased in the same pull request, in New, Improved or Fixed, that
starts with a short bold phrase. The same line goes into [CHANGELOG.uk.md](CHANGELOG.uk.md) in Ukrainian. See
[docs/releasing.md](docs/releasing.md).

## Unreleased

### Improved

- **Uses less memory.** Thumbnails on the timeline no longer keep a second, greyed-out copy that nothing showed. With every timeline chip on, a 30-minute video with its transcript now takes about 185 MB instead of 210 MB.
- **Up-to-date video engine.** ffmpeg, which cuts and exports, moves to 9.0.2, and mpv, which plays the video, to its latest build, with their newest fixes.

## 0.1.0 — 2026-09-30 — First Cut ✂️

### New

- **Claude edits with you.** Connect Claude over MCP and it adds, trims, splits and reorders clips, cuts by the transcript, finds pauses and exports. Every edit shows up in the Claude panel with its own Undo.
- **One click connects Claude.** Add to Claude Code and Add to Claude Desktop, in the welcome tour and Settings → MCP server, replace an old or moved entry and say clearly when Claude Code isn’t installed. Claude Desktop keeps your other servers and settings, and the old file is kept as a backup.
- **Transcription on your computer.** Parakeet or Whisper, with a model you download in Settings → Transcription. It runs on AMD, Intel and NVIDIA graphics cards through DirectML after a quick check that the card works, and otherwise on every core of the processor.
- **The Transcript tab.** Click a word to jump there, search, and select words to play them, keep them as a clip or cut them out. Filler words are underlined.
- **Clips snap together.** Trim a clip and it snaps onto its neighbours (the magnet), J joins a clip with the next one, and clips never overlap, so the export never plays a moment twice.
- **Names and colours for clips.** Every clip gets its own, on the timeline and in the Clips list.
- **Volume per audio track.** A slider on each track. You hear it while you play, and the export and the project keep it.
- **Pauses and scene changes.** Silences show as bands on the audio tracks and scene changes as markers on the ruler.
- **Export your way.** One file or separate files, lossless (no re-encoding) or re-encoded on the graphics card when there is one, with a file name pattern, a choice for when the file already exists and a choice for what happens after export.
- **A welcome tour.** On the first start it shows opening and cutting, the timeline chips, the main shortcuts, downloading the transcription model and connecting Claude. It works from the keyboard, fits small windows and comes back from the project menu → Welcome tour.
- **What’s new.** These notes, once after an update, and from the project menu → What’s new.
- **Settings.** Startup, recent files, playback, export, transcription, the keyboard and the MCP server. Shortcuts follow Settings → Keyboard, and the version number is at the bottom of the list.

### Improved

- **A new name.** The app is now called HighlightCut. Settings, recent files, models and the cache move over from OurCut by themselves, and .ourcut.json projects still open.
- **Videos open at once.** Keyframes, frames, silences and scenes are only worked out when their chip on the timeline is on, and the chips are remembered. A progress screen shows the time left while a video is prepared.
- **The waveform is on from the start.** You see loud and quiet moments right away, it fills in faster while the video already plays, and its chip turns it off.
- **Playback settings apply at once.** They take effect while the video plays.

### Fixed

- **Clicks on the timeline.** A click always moves the playhead to where you clicked.
- **The transcript.** It no longer strikes words through before you have marked any clips.
- **The video renderer.** Switching it no longer closes the open video.
- **Cancelled exports.** They always remove their unfinished files.
