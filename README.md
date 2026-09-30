# HighlightCut

HighlightCut is an open-source desktop video editor for cutting long recordings down to the parts worth keeping.
You mark segments on a timeline of the whole source file, arrange them, and export them as one merged file
or as separate files. Cuts are lossless by default (stream copy, no re-encoding); re-encoding is optional.

It is inspired by [LosslessCut](https://github.com/mifi/lossless-cut). Every edit is a command in a UI-independent
core, and Claude can edit the same timeline through the same operations over MCP: its edits show up in the
editor as they happen, each one undoable.

> **Status:** Phase 1 is feature-complete and being tested on Windows; Phase 2 adds Claude editing over MCP and
> silence and scene detection. Windows only for now; the code is kept cross-platform so macOS and Linux can follow. Opening videos,
> playback (libmpv: frame-exact seeking and stepping, speed, volume, per-track mute), the timeline (thumbnails,
> waveforms, keyframes), editing with undo/redo, project files, export (lossless or re-encoded, merged or
> separate), silence and scene detection and Claude's edits through MCP work.

## Phase 1 scope

- Open a video (file dialog or drag and drop) and probe it with ffprobe: duration, frame rate, codecs, keyframes.
- Player: play/pause, frame step, precise seek, `HH:MM:SS.mmm` timecode, volume, speed.
- Timeline: thumbnails, audio waveform per audio track, playhead, zoom, keyframe markers,
  clips with draggable in/out handles. Parts that are not kept stay visible as "Excluded".
- Clips panel: reorder output by drag, include/exclude, remove, total output duration.
- Claude panel: every edit as a card with its own undo, which reverts just that edit.
- Export: lossless copy (cut points on keyframes) or re-encode; merge into one file or separate files;
  progress and cancel.
- Projects saved as `.highlightcut.json`. Undo/redo for every edit.

## Phase 2

- **Claude via MCP** (done): Claude reads the project and edits the timeline — add, trim, split, exclude,
  reorder and rename clips, several edits as one undo step, revert any earlier edit, move the playhead, open
  videos, save the project and export it. Claude's export runs in the background: a card in the Claude panel and
  the Export button show its progress, and files are never overwritten. See [Connecting Claude](#connecting-claude).
- **Silence and scene detection** (done): pauses (from the waveform, at a level that follows the recording's
  background noise) show as hatched bands on the audio lanes, scene changes (a cut, a new slide or window) as
  markers on the ruler. Scene detection decodes the video once in the background and is cached. Claude can
  query both at any sensitivity and cut out the pauses in one undoable step.

- **Transcription** (done): Parakeet or Whisper, locally. Download a model in Settings → Transcription (or from
  the Transcript tab) and every video you open is transcribed in the background (or only when you or Claude ask,
  if you turn that off). The **Transcript** tab next to Clips shows the text by paragraph: click a word to seek,
  search it, or select words to play them, keep them as a clip or cut them out. Filler words (set per language in
  Settings → Transcription) are underlined, and words outside the output are struck through. The timeline can show
  the words on a transcript lane. Claude reads the transcript, searches it, finds filler words and cuts by it (a
  sentence, an aside, every "um") in one undoable step.
- **Settings**: General (startup, recent files, preview cache, diagnostics), Playback (hardware decoding, renderer,
  audio output, jump length), Export (the defaults the Export dialog starts with, including a file name pattern),
  Transcription, Keyboard (search the shortcuts, record new keys, conflicts are caught) and MCP server. Some of
  these are saved but not applied yet; each such place is marked `// STUB:` in the code.

Not yet: smart cut. The UI already has a place for it.

## Connecting Claude

HighlightCut is an MCP server: `HighlightCut.exe mcp` speaks MCP over stdio and forwards Claude's tool calls to the editor,
starting HighlightCut when Claude first uses it. The welcome tour's last step and **Settings → MCP server** add it for you,
and show whether Claude is connected. Settings → MCP server also turns the server off ("Let Claude connect").

- **Claude Code**: **Add to Claude Code** runs `claude mcp add --scope user highlightcut -- "C:\path\to\HighlightCut.exe" mcp`
  (after removing an earlier `highlightcut` entry, so adding again fixes a moved install). If the `claude` command isn't
  installed it links to [its install page](https://code.claude.com/docs/en/setup). **Open terminal** runs the same
  steps in a terminal window, and Copy gives you the command to run yourself. Then start a new Claude Code chat.
- **Claude Desktop**: **Add to Claude Desktop** adds the entry below to `%APPDATA%\Claude\claude_desktop_config.json`
  (and to the Microsoft Store app's copy), keeping your other servers and settings and saving the old file as
  `claude_desktop_config.json.bak`. If the file can't be read, it's left as it is: HighlightCut opens it and copies
  the entry for you to add. Then quit Claude Desktop fully (right-click its tray icon → Quit) and start it again.

  ```json
  {
    "mcpServers": {
      "highlightcut": { "command": "C:\\path\\to\\HighlightCut.exe", "args": ["mcp"] }
    }
  }
  ```

Settings → MCP server also has what Claude may do without asking (open files, save the project, export). They are
saved, but not enforced yet: Claude's exports start right away.

Then ask Claude something like "open my latest recording in HighlightCut, cut out the pauses and the ums, split it
into chapters where I change topic, and export it". Claude does not see the video itself: it works from the
transcript, silences, scene changes and keyframes HighlightCut finds. The badge in the title bar shows the connection
(MCP · Waiting for Claude / Claude connected / Claude editing / In another window / Off), and every edit Claude makes appears in the Claude
panel with its own Undo. Only your own user account can connect to the editor.

## Keyboard shortcuts

| Key | Action |
| --- | --- |
| Space | Play / pause |
| I / O | Set in-point / out-point |
| ← / → | Previous / next frame |
| Shift+← / Shift+→ | Jump back / forward (Settings → Playback → Jump length, 1 second by default) |
| S | Split clip at playhead |
| J | Join the selected clip with the next one (they must touch, or be less than 0.5 s apart) |
| E | Exclude / keep the selected clip |
| Del or Backspace | Delete the selected clip |
| V | Select tool (the Split tool cuts a clip where you click it) |
| Ctrl+Z / Ctrl+Y (or Ctrl+Shift+Z) | Undo / redo |
| Ctrl+O | Open video |
| Ctrl+Shift+O | Open project |
| Ctrl+S / Ctrl+Shift+S | Save project / save as |
| Ctrl+E | Export (while an export runs: show it again) |
| Esc | Close a dialog; a running export's dialog is hidden and the export goes on |

Settings → Keyboard lists every shortcut and records new keys. They are saved, but the editor still uses the keys
above for now.

## Download

Every CI run builds a ready-to-run Windows folder: open the latest run under
[Actions](https://github.com/skyp1nus/OurCut/actions), download **HighlightCut-win-x64**, unzip it and start
`HighlightCut.exe`. It includes .NET, ffmpeg, ffprobe and libmpv, so nothing else needs installing. Tagged versions
(`v*`) are published under [Releases](https://github.com/skyp1nus/OurCut/releases).

If something goes wrong, the details are in `%LOCALAPPDATA%\HighlightCut\logs`. Settings, the recent files list and
the preview cache are in `%LOCALAPPDATA%\HighlightCut`.

HighlightCut used to be called OurCut. The first start after the rename moves `%LOCALAPPDATA%\OurCut` (settings,
recent files, models, cache and logs) to `%LOCALAPPDATA%\HighlightCut`, and `.ourcut.json` projects still open. Claude
knows the app by its old name until you add it again: Add to Claude Code and Add to Claude Desktop in Settings → MCP
server also remove the old `ourcut` entries.

## Building

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download) and PowerShell (Windows PowerShell 5.1
that ships with Windows, or PowerShell 7).

```powershell
git clone https://github.com/skyp1nus/OurCut.git
cd OurCut
powershell -ExecutionPolicy Bypass -File scripts\fetch-deps.ps1   # or: pwsh scripts/fetch-deps.ps1
dotnet run --project src/HighlightCut.App
```

`fetch-deps.ps1` downloads ffmpeg, ffprobe, libmpv and the DirectML build of sherpa-onnx (speech recognition on any
DirectX 12 GPU, built by this repo's CI) into `deps/win-x64/`. They are not stored in the
repository. The build copies them next to `HighlightCut.exe`. Versions, URLs and SHA-256 hashes are pinned in
[`scripts/deps.json`](scripts/deps.json), and the script refuses anything that does not match.
Useful options: `-Check` (verify only, no downloads), `-Force` (reinstall), `-Component ffmpeg`,
`-Proxy http://proxy:8080`. Downloads are cached in `deps/.cache`.

A self-contained build like the one CI publishes:
`dotnet publish src/HighlightCut.App -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o out/HighlightCut`.

`dotnet run --project src/HighlightCut.App -- path/to/video.mp4` opens a video (or a `.highlightcut.json` project) at start.

To see the UI with the sample project from the design, start it in demo mode:
`dotnet run --project src/HighlightCut.App -- --demo editing`. The other screens: `empty`, `ai`, `export`, `exporting`,
`transcript`, `transcribing`, `no-model`, `claude-request`, `claude-exporting`, `claude-export-failed`, and the
settings sections `settings` (Transcription), `settings-general`, `settings-playback`, `settings-export`,
`settings-keyboard`, `settings-keyboard-recording`, `settings-keyboard-conflict` and `settings-mcp`. Names are
not case-sensitive and the dashes are optional (`--demo ClaudeExportFailed` works too).

Run the tests with `dotnet test HighlightCut.slnx`. The UI tests render the app headlessly and write screenshots to
`artifacts/screenshots/`, one per demo screen under the same name (`claude-export-failed.png`) and a few more states. Tests that run ffmpeg generate their own small videos; they are skipped when ffmpeg
is not found (in `deps/`, `HIGHLIGHTCUT_FFMPEG_DIR` or `PATH`). Playback tests also need libmpv (in `deps/`,
`HIGHLIGHTCUT_MPV_DIR` or the system; on Ubuntu `apt install libmpv2`).

Without libmpv the editor still works; playback is then simulated over the thumbnails. Video is drawn with
OpenGL when available and with mpv's software renderer otherwise; `HIGHLIGHTCUT_VIDEO=software` forces the latter.

Avalonia's build tooling sends anonymous build telemetry. Set `AVALONIA_TELEMETRY_OPTOUT=1` to turn it off
(CI does this).

## Project layout

```
src/HighlightCut.App      Avalonia UI: views and view models (CommunityToolkit.Mvvm)
src/HighlightCut.Core     Project model, timeline and edit commands with undo/redo. No UI references.
src/HighlightCut.Mcp      MCP server (the editor tools) and the stdio bridge Claude starts. No UI references.
src/HighlightCut.Media    libmpv playback; ffprobe/ffmpeg: probing, keyframes, export (FFMpegCore), thumbnails,
                    waveforms, cache (SkiaSharp)
src/HighlightCut.Transcription  Speech-to-text models (download, install) and engines. No UI references.
tests/              xUnit tests for Core, Media, Mcp, Transcription and headless UI tests for App
scripts/            fetch-deps.ps1 and the pinned dependency manifest
design/             The Claude Design export the UI is built from
```

See [docs/architecture.md](docs/architecture.md) for how the layers fit together, the list of edit commands
and the `.highlightcut.json` format.

## License

HighlightCut is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any
later version. See [LICENSE](LICENSE).

HighlightCut uses FFmpeg and mpv, which are downloaded separately and are also GPL-licensed. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for all third-party components and their licenses.
