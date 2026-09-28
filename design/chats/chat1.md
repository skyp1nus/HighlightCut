# HighlightCut Desktop Editor

_Started 2026-09-24 14:24 UTC_

---

## User

Design HighlightCut, a desktop video editor inspired by LosslessCut. It's fast and minimal, focused on cutting segments and merging them without re-encoding. A built-in AI assistant (Claude via MCP) edits the timeline alongside the user.

Visual style: follow the attached DESIGN.md (dark "mission control" style: matte-black surfaces, hairline borders, Inter at light weights, rationed blue accent). It describes a marketing site, so adapt it to a desktop app:
- Ignore hero, logo strip, announcement bar, footer, display-size type and large section gaps.
- UI text 11–14px, compact density, 4px grid.
- Signal Blue only for:
  - playhead
  - selected segment outline
  - active tool/tab
  - focus rings
  - links
  Timeline segments get a low-alpha blue tint.
- Export is the only bone (#f2f2f2) pill button. Other buttons are ghost.
- Green/red only for status (MCP connected, export done) and destructive actions (delete, errors).
- Timecodes use tabular numbers (Inter tnum or JetBrains Mono), format HH:MM:SS.mmm.

Platform: Windows desktop, window 1440×900, dark only.

Layout:
1. Top bar (48px):
   - app name, Open file, project name
   - MCP status badge
   - settings icon
   - Export as a bone pill on the right
2. Video preview (center, largest area):
   - player with play/pause, frame step ←/→
   - current timecode / duration
   - volume, playback speed
3. Timeline (bottom, full width):
   - thumbnails strip, audio waveform, playhead, zoom slider
   - keyframe markers as tiny hairline ticks
   - silence/scene markers as subtle bands
   - segments with draggable in/out handles
4. Clips panel (right sidebar), styled as data-table rows:
   - index, start–end, duration, label
   - drag to reorder, include/exclude toggle, delete
   - total output duration at the bottom
5. AI panel (collapsible):
   - log of Claude's actions as small node-cards, e.g. "Added clip 00:12–00:45", "Removed 3 silences"
   - undo per action
   - changed segments get a thin pulsing blue ring on the timeline
6. Export dialog:
   - mode: lossless (copy, keyframe-snapped) / re-encode
   - merge into one file or export clips separately
   - output path, progress bar, cancel
7. Settings → Transcription:
   - engine (Auto / Parakeet / Whisper)
   - model
   - device (Auto / GPU / CPU)
   - language
   - models folder with download/delete buttons

States to show:
1. empty (drop a video here)
2. editing with 5 clips
3. AI actively editing
4. exporting with progress
5. settings open

Show keyboard hints subtly: Space play, I/O in/out, Del remove, Ctrl+Z undo, ←/→ frame step.

## Assistant

_[tool: read_file]_

_[tool: update_todos]_

_[tool: dc_write]_

_[tool: show_html]_

_[tool: ready_for_verification]_

I've built `HighlightCut.dc.html` as one working prototype of the 1440×900 Windows window. A switcher above it jumps between the five states. It's out for a background check now.

- **Empty:** a drop zone where dropping a file or clicking Open file loads the video.
- **Editing:** the five clips are live. You can scrub, play, drag in/out handles (with an optional snap to keyframes), drag rows to reorder, turn clips on or off, and delete them. The shortcuts work: Space, I/O, ←/→ (Shift steps by one second), Del and Ctrl+Z.
- **AI editing:** a scripted run plays out on its own. Claude removes 3 silences, adds a clip, trims one, then keeps a "reviewing" step running. Changed segments get a pulsing blue ring on the timeline, and each action has its own Undo.
- **Exporting:** the dialog shows progress and turns green when done. In lossless mode it says how many in points will snap back to a keyframe, and by how much. Cancel works.
- **Settings → Transcription:** the model list is live. Download shows a progress bar and Delete (in red) removes an installed model.

A few things to know:
- The video frame and thumbnails are striped placeholders, and the waveform is generated rather than real audio.
- The other Settings sections in the side menu are labels only; just Transcription is designed.
- I didn't add a place to type instructions to Claude in the AI panel, because you'd talk to Claude through its own MCP client. The panel only logs what it does.

## User

Found issues — fixing…

## Assistant

_[tool: dc_js_str_replace]_

_[tool: run_script]_

_[tool: ready_for_verification]_

I fixed the three problems from the check, and it's running again now:
- **Speed button:** it read "1.×" at normal speed and now reads 1×, 1.5×, 0.5× and 2×.
- **Model table in Settings:** long model names like parakeet-tdt-0.6b-v2 were cut off next to the Installed badge. I narrowed the other columns to make room.
- **Timeline scrollbar:** a horizontal scrollbar showed even at 1× zoom. It should now only appear when you zoom in.

