# Architecture

OurCut has four parts. Only the App knows about Avalonia; Core has no dependencies at all, so the same
editing operations are driven by the UI and by Claude through the MCP server.

```
OurCut.App    Avalonia views and view models ──┐
OurCut.Media  libmpv, ffprobe/ffmpeg, previews ─┼──> OurCut.Core   project model, commands, undo/redo, .ourcut.json
OurCut.Mcp    MCP tools, pipe server, bridge ───┘
```

## Core

- **Model** (`OurCut.Core.Model`): immutable records. A `Project` has one `SourceMedia` (path, duration,
  frame rate, audio tracks) and an ordered list of `Clip`s. List order is output order. Times are
  seconds on the source timeline. Excluded clips (`IsIncluded = false`) stay in the project but are not exported.
  `Project.AudioMix` holds a `TrackMix` (volume in dB, muted) for each audio track that is not at the default
  (0 dB, unmuted), keyed by stream index. The volume runs from −40 dB, which means silent (−∞), to +12 dB.
- **Commands** (`OurCut.Core.Editing`): every change is an `IEditCommand` that turns one `Project` into the
  next, or throws `EditException` with a readable reason. Commands never clamp or guess; callers do that.
- **Session**: `EditorSession` holds the current project and a linear `History`. `Execute` applies a command
  and records it with its origin (`User` or `Assistant`). Edits sharing a merge key (one drag) become one
  undo step. `Changed` fires after every edit, undo, redo and load. `SetTrackMix` changes a track's volume or mute
  (`ProjectChangeKind.Mixed`). That is a mixer setting, not an edit: it is saved with the project but not recorded
  in the history, and undo and redo keep the current mix (as mute always behaved).
- **Files**: `ProjectFile` reads and writes `.ourcut.json` (see below).

The session is not thread-safe. The MCP server runs every tool call on the UI thread (see below).

### Commands (and their MCP tools)

| Command | `Name` | What it does |
| --- | --- | --- |
| `AddClipCommand` | `add_segment` | Adds a clip for a source range, at the end or at a position |
| `RemoveClipCommand` | `remove_segment` | Removes a clip from the project |
| `SetClipRangeCommand` | `trim_segment` | Sets a clip's in- and out-point |
| `SplitClipCommand` | `split_segment` | Splits a clip in two at a source time |
| `SetClipIncludedCommand` | `set_included` | Excludes a clip from the export or keeps it again |
| `MoveClipCommand` | `move_segment` | Moves a clip to another output position |
| `RenameClipCommand` | `set_label` | Renames a clip |
| `BatchCommand` | any | Several commands as one undo step |
| `RevertEditCommand` | `revert_action` | Reverts one earlier edit and keeps the edits made after it |
| `CutRangesCommand` | `cut_silences`, `cut_ranges`, `cut_filler_words` | Cuts source ranges out of the clips they touch, splitting them |

`EditorSession` wraps these with UI-friendly helpers (`Trim` clamps and snaps to keyframes, `KeepRange`
inserts by source position, `Split` returns the new clip).

`Revert(entry)` is the Undo on a single card in the Claude panel. Unlike `Undo`, which steps back through the
history, it applies a `RevertEditCommand`: the clips that edit added, removed, changed or reordered go back to how
they were before it, and every other clip stays as it is now. The revert is an ordinary edit, so Ctrl+Z undoes it
and it can be reverted in turn (`RevertOf`, `IsReverted`). If a later edit changed one of the same clips there is no
single right answer, so the command refuses with an `EditException` and the UI shows the reason.

## Media

`OurCut.Media` runs ffprobe and ffmpeg as separate processes, always off the UI thread. Paths go through
`ProcessStartInfo.ArgumentList` (or FFMpegCore's quoting), never through a shell.

- **Probing** (`MediaProbe`): `ffprobe -show_format -show_streams` as JSON → `MediaInfo`: container family,
  the first real video stream (cover art is skipped), audio streams with their titles (MP4 keeps track names in
  the handler name), subtitles, rotation, B-frames. `ToSourceMedia()` gives Core's description.
- **Keyframes** (`KeyframeScanner`): packet flags, no decoding. An MP4 or MOV without B-frames lists its
  keyframes in the index, so ffmpeg's demuxer skips the other samples (`-discard nokey`, copied into `framecrc`) and
  only a few percent of the file is read; with B-frames ffmpeg 6.x's MOV demuxer gets the times of skipped samples
  wrong, so those files, like every other container, are read through with ffprobe. Times are relative to the
  file's start time, like everything else in OurCut.
- **Previews**: `WaveformExtractor` decodes every audio stream to 8 kHz mono and keeps one peak per 10 ms
  (`WaveformData`, drawn on a dB scale). Audio decodes on one core per ffmpeg, so a long file is split into
  stretches of at least 30 s (up to one per core, at most 8), decoded at once and filling in side by side; the timeline
  draws each lane's bars as two shapes (in and out of clips) and lays out ruler labels once. `ThumbnailExtractor` reads (`-discard nokey`, where the container
  allows) and decodes (`-skip_frame nokey`) only keyframes, to raw BGRA, at most about 300 per file. Both stream
  their results as they arrive.
  Thumbnails and scene detection decode on the GPU when there is one (`FfmpegText.GpuDecoding`, `-hwaccel auto`,
  which falls back to the CPU by itself), and every analysis process (`ToolProcess`) runs below normal priority so
  playback and the UI keep the CPU they need.
- **Cache** (`MediaCache`): keyframes, waveform and a JPEG thumbnail atlas per file in
  `%LOCALAPPDATA%\OurCut\cache`, keyed by path, size and modification time.
- **Export**: `ExportPlanner` turns the project and `ExportSettings` into an `ExportPlan` (every step, output
  and temporary file decided up front, so it can be tested and shown); `FfmpegCommands` builds each step's
  ffmpeg command with FFMpegCore; `ExportRunner` runs the steps with progress and cancellation.
- **Audio levels** (`AudioLevels`): a rough loudness per stream from the waveform peaks (the power average of the
  peaks above −45 dBFS) and the volumes that bring the streams to the average of their levels, a boost never passing
  0 dBFS at the loudest peak. The editor's "Even out all tracks" uses it.

In the App, `FfmpegMediaOpener` probes a file and creates a `MediaPreview`, which runs the analyses in
parallel (or reads them from the cache) and raises `Changed` as results arrive; the timeline redraws, and the
keyframes are handed to the editing session for snapping. How long each part took, or that it came from the cache,
is in Copy diagnostics ("Analysis keyframes 0.2 s · thumbnails 0.3 s · …").

Keyframes (needed for lossless cuts) and the waveform are always read. Thumbnails are made only while the timeline's
Frames chip is on (off at first: the player shows the picture anyway, and without them a 10-minute 1080p file opens in
about 1.4 s instead of 2.1 s). The editor asks for them as it loads the file (`MediaPreview.ExtractThumbnails`), so
they are then part of opening it: the processing screen and its progress include "Making thumbnails". Turned on later,
the open file's are made in the background (status bar: "making thumbnails 40%"), or read from the cache at once.
Turned off, a run under way stops (`StopThumbnails`) and keeps nothing of it, nothing cached; a finished set is kept
in memory, only not drawn. With the chip off the video track is a plain strip of the same height, keyframe ticks and
scene markers on it as before, and without thumbnails the player shows black until mpv's first frame.

While that runs for more than 0.4 s, a processing screen covers the editor below the title bar (`ProcessingOverlay`,
design "OurCut — екран обробки", X1): a slowly changing blob (`BlobView`, drawn every frame while shown), the file,
a progress line and "Reading the audio · 72% · about 8 s left". The part named is the one furthest behind
(`MediaPreview.AnalysisStage`); the time left comes from the rate of the last few seconds, smoothed so it counts down
(`TimeLeftEstimator`). It fades and settles in, and fades out growing a little into the editor; a file read from the
cache never shows it.

### Lossless cuts

With `-ss` before `-i` and `-c copy`, ffmpeg starts every stream at a keyframe. OurCut makes that explicit:
`CutPlanner` moves each clip's in-point back to the keyframe at or before it (nothing is lost; a short lead-in
is added) and computes the `-ss` value that makes ffmpeg land exactly on that keyframe. MP4/MOV seek by
presentation time, so a value just after the keyframe works. Matroska and most other demuxers seek
3/23 s earlier when the video has B-frames; the planner adds that back. Transport streams have no index, so
their cut points are marked approximate.

ffmpeg ends a stream copy by decode time, so with B-frames each clip comes out a few frames longer than planned.
A merged lossless export cuts every clip to a temporary file and joins them with the concat demuxer; chapters are
written afterwards from the real length of each cut, so they start exactly where their clip does.

A re-encoded merge is one ffmpeg pass: each clip is its own frame-accurately seeked input, joined with the concat
filter. With `ExportSettings.GpuEncoder` (NVENC, AMF, Quick Sync or VideoToolbox) the video is encoded on the GPU at a
constant quality near the preset's CRF (`GpuEncoder.Arguments`); if a step fails there, the CPU encodes it and the
rest. At start the app looks for one (`GpuEncoderProbe`): ffmpeg lists the encoders it was built with, and each listed
one is tried on a few frames with the export's own arguments, since most builds list them all whatever the hardware.
Settings → Export shows what was found; "Use the GPU encoder when available" decides whether re-encoding uses it. Re-encoding one clip per file copies audio when asked, dropping packets before the in-point
(`-copypriorss 0`).

Output names come from Settings → Export's file name pattern (`ExportSettings.FileNamePattern`, filled by
`ExportFileNames.Fill`; `ExportPlanner.OutputNames`): `{project}-cut-{n}` by default, which a merged file reads as
`{project}-cut`. A name that is taken gets " (2)"; with `ExportSettings.Overwrite` the output is written under a
temporary name and moved over the old file once complete (`ExportPlan.Replacements`), so a failed or cancelled
export leaves the old file as it was. Outputs never share a name, and an export never writes over its source.
Temporary files (`.ourcut-tmp-*`) and any half-written output are removed on failure or cancel.

Audio tracks are written as separate streams, never mixed, so a track's volume applies to its own stream
(`ExportSettings.AudioGainsDb`, by stream index; `FfmpegCommands.AudioGains` maps it to the output's audio positions).
A stream at 0 dB is left as the mode says. A stream with a volume goes through ffmpeg's `volume` filter, which needs
decoding: where it would be copied (lossless, or re-encoding with audio set to Copy) only that stream is re-encoded,
as AAC 192 kb/s (the re-encode choice, and a codec every output container takes), with `-filter:a:K` and
`-c:a:K` while the video and the other streams are still copied. A lossless merge copies the pieces as they are
and applies the volume once, in the concat step, so the re-encoded audio is continuous (no encoder priming at each
join). A re-encoded merge puts a `volume` filter after the concat filter. A silent track (−40 dB) is kept, at
`volume=0`; left out altogether it is only with "Only unmuted lanes" and the lane muted.

In the app, "If the file exists" decides `Overwrite`: Add a number, Overwrite, or Ask, where the Export dialog lists
the files that exist (`ExportViewModel.ExistingFiles`) and waits for Add a number or Overwrite before anything is
written. Claude's exports always add a number. "After export: Show in folder" reveals the user's export
(`EditorViewModel.RevealInFolder`); Claude's card has its own Show in folder.

## Playback

`OurCut.Media.Playback` talks to libmpv directly (`LibraryImport`, client API 2.x; `libmpv-2.dll` from
`fetch-deps.ps1`, `libmpv.so.2` on Linux).

- **`MpvPlayer`** owns one mpv core. Everything that controls playback (load, play, pause, seek, frame step,
  volume, speed, audio tracks) goes through `mpv_command_async`, so the calling thread never waits for the core.
  That matters because the UI thread also renders video, and mpv's render API forbids waiting for the core on a
  render thread. State comes back as observed properties (`time-pos`, `pause`, `duration`, `eof-reached`, video
  size) on a background event thread, which raises `StateChanged`.
- **Seeking** is exact (`hr-seek`). While a seek is in flight, `Position` reports the target and `IsSeeking` is
  true; only the playback restart after the newest seek settles the position, so a dragged playhead never jumps
  back to stale positions. mpv can send that restart before the new `time-pos`, so the settled position stays the
  target until mpv reports the next one (`SeekState`).
- **Audio tracks**: the timeline's lanes map to mpv's `aid1…N` (`AudioMix`). One unmuted lane at 0 dB plays
  directly (`aid`), none sets `aid=no`; anything else goes through `lavfi-complex`: a `volume` filter on each lane
  whose volume is not 0 dB, then `amix` (`normalize=0`) when there are several, e.g.
  `[aid1]volume=-6dB[g1];[g1][aid2]amix=inputs=2:normalize=0[ao]`. mpv rebuilds the graph whenever the property
  changes (a short gap in the sound), so `SetAudioTracks` sends nothing when the mix is unchanged, and the editor
  applies a volume slider's changes at most every 250 ms (`EditorViewModel.VolumeApplyDelay`); mute applies at once.
  Muting leaves a lane out of the preview; an export leaves it out only with "Only unmuted lanes".
- **Video** goes through the render API into OurCut's own view: `MpvOpenGlRenderer` draws into Avalonia's OpenGL
  framebuffer; `MpvSoftwareRenderer` renders BGRX frames into memory on a background thread. `vo=libmpv` without
  a render context fails the whole file, so the player uses `vo=null` until a renderer is attached and reopens
  the file where it was when one attaches or detaches. A renderer detaches before it frees its context (freeing it
  takes the output away, and mpv may end the file with an error), and the reopen goes by the file the app opened
  (`_openPath`), not `LoadedPath`, which that error clears.

In the App, `IPlayer` is what `EditorViewModel` uses (`MpvPlaybackEngine` in the app, a fake in tests). The view
model keeps the playhead: user moves become seeks, the player's positions come back as `Time` without seeking
again. On `TimelineControl` a press moves the playhead to the time under the pointer anywhere on the ruler or the
tracks (the whole control takes the pointer, not just what it drew); it only becomes a scrub or a trim once the pointer
has moved 4 px, so a click on a trim handle seeks and selects that clip. `VideoView` shows the video (OpenGL first, software if OpenGL is not there within two seconds or fails);
until its first frame the thumbnail preview underneath shows through (black if no thumbnails were made). mpv allows one render context per player and
refuses a second one while the old exists, so the software view keeps trying for a few seconds while the OpenGL view
it replaces lets go. `VideoView.Output` tells the editor what draws the video ("OpenGL · " and the GPU as the
driver names it, "software", or "none: why", which puts a message in the status bar rather than leaving blurry
thumbnails unexplained); Copy diagnostics includes it and mpv's `hwdec-current` (`MpvPlayer.CurrentDecoder`). Without libmpv, or for the design's
sample, playback is simulated over the thumbnails.

## Silence and scene detection

Both live in `OurCut.Media.Analysis` and keep their raw measurements, so a different sensitivity is instant.

- **Silences** (`SilenceDetector`) come from the waveform the timeline already has: 10 ms peak buckets per audio
  track, like ffmpeg's `silencedetect` but without decoding the audio again. A stretch counts as silent when every
  chosen track stays under the level for at least the minimum length (1 s on the timeline). The default level is
  12 dB over the noise floor (the level of the quietest 5 % of the audio), kept between −55 and −35 dBFS, so a
  noisy microphone still has pauses and quiet music is not taken for one.
- **Scene changes** (`SceneDetector`) need the whole video decoded, so they are found only when asked for: the
  timeline's Scenes chip (off at first; while it is on, every video opened is searched) or Claude's
  `find_scene_changes` (`MediaPreview.DetectScenes`); scene changes cached from an earlier run are read straight away.
  Turning the chip off stops a search under way (`MediaPreview.StopScenes`) and keeps nothing of it. Detection waits for the rest of the analysis (status bar: "detecting
  scenes 34%") and runs on the GPU or every CPU core (below normal priority). ffmpeg shrinks every frame (at the video's own
  rate, up to 60 fps, timed from the file start like keyframes) to 64×36 grey and pipes it out; each frame is
  scored against the one before as ffmpeg's `scdet` does: the mean difference, but no more than its jump from the
  previous frame's, so steady motion (scrolling, panning) scores low and a cut scores high. The per-frame scores
  are cached (`scenes.bin`); changes are the frames at or over the threshold (10 by default, `scdet`'s), with
  changes less than 0.5 s apart counted once.
- `CutRangesCommand` (Core) cuts source ranges out of clips: it trims or splits every clip a range touches and
  drops leftovers shorter than the minimum clip length. `cut_silences` uses it (after keeping the whole video if
  there are no clips yet), so removing every pause is one undo step.

## MCP server

Claude edits the open project through MCP tools (`OurCut.Mcp.EditorTools`). There are two processes:

```
Claude ──stdio──> OurCut.exe mcp (McpBridge) ──named pipe──> OurCut editor (McpPipeServer → EditorMcpHost → UI thread)
```

- **The bridge** is what Claude starts (`OurCut.exe mcp`; `Program.Main` never starts Avalonia in this mode). It
  lists the tools itself, from the same `EditorTools` definitions, so Claude Desktop can start its MCP servers at
  launch without OurCut popping up. The first tool call connects to the editor's pipe, starting the editor
  (`EditorLauncher`) if it is not running, and every call is forwarded as is. If the editor was closed since,
  the next call starts it again.
- **The pipe server** runs in the editor while Settings → MCP server → "Let Claude connect" is on (not in `--demo`
  runs; `EditorMcpServer` stops and restarts it). The pipe is `ourcut-mcp-<user>`, created with
  `PipeOptions.CurrentUserOnly`, so only the same user account can connect. One editor serves it: the one that
  holds `<temp>/ourcut-mcp-<user>.lock` (released by the system when that editor exits, even if it crashes). A
  second window shows "MCP · In another window" and takes over when the first one closes. On Windows the first pipe
  instance is also created with `FirstPipeInstance`.
- **The host** (`EditorMcpHost` in the App) runs each tool call on the UI thread, where the session and the view
  models live, so a tool call never lands in the middle of a user edit. Edits go through `EditorSession.Execute`
  with `EditOrigin.Assistant`: the Claude panel shows each one as a highlighted card with its own Undo
  (`EditorSession.Revert`), the clips of the latest one get a pulsing blue ring on the timeline, and the title bar
  badge shows "MCP · Claude editing" for a few seconds after each call.

| Tool | Does |
| --- | --- |
| `get_project` | Source (with each audio track's volume and mute), playhead, selection and clips in output order |
| `get_history` | Recent edits (user's and Claude's) with ids for `revert_action` |
| `find_keyframes` | Keyframe times in a range (lossless cuts start on them) |
| `find_silences` | Pauses at a minimum length and level (automatic by default), on all or some audio tracks |
| `find_scene_changes` | Scene changes at a sensitivity; what is found so far while detection runs |
| `cut_silences` | Cuts the pauses out of the included (or given) clips as one undo step, keeping some padding |
| `get_transcript` | What is said, as timed sentences (and words on request), in parts of about 20,000 characters; starts transcription if needed |
| `search_transcript`, `find_filler_words` | Where a word or phrase (case and punctuation ignored), or the user's filler words (Settings → Transcription), are said |
| `cut_ranges`, `cut_filler_words` | Cut any source ranges (e.g. from the transcript), or the filler words, out of the clips as one undo step |
| `list_videos` | Video files in a folder, newest first |
| `add_segment`, `remove_segment`, `trim_segment`, `split_segment`, `set_included`, `move_segment`, `set_label` | One edit each (the commands above) |
| `edit_timeline` | Several edits as one undo step, all or nothing |
| `revert_action`, `undo`, `redo` | Take edits back |
| `seek`, `set_playing` | Show a frame or play |
| `open_file`, `save_project` | Open a video or project; save as `.ourcut.json` (full paths only); the user may be asked first |
| `export`, `get_export_status`, `cancel_export` | Export like the Export button (runs in the background; the Claude panel shows it); choices left out keep the dialog's; waits up to 20 s, then Claude polls |

A refused edit (`EditException`, e.g. "Clip 7 does not exist") goes back to Claude as a tool error it can act on.
Results are JSON; times are seconds, rounded to milliseconds, with `MM:SS.mmm` ranges for talking to the user.

## App

`OurCut.App` is MVVM (CommunityToolkit.Mvvm). `EditorViewModel` owns the session, the player and the panels; views
bind to view models and never change the project themselves.

- **Sidebar tabs**: `EditorViewModel.Tab` switches between Clips and Transcript (`EditorViewModel.Transcript.cs`).
  `TranscriptPanelViewModel` follows the open file's transcript as it arrives, lays it out in paragraphs
  (`TranscriptLayout`, Core), searches it (`TranscriptSearch`, Core: case and punctuation ignored, phrases across
  words) and marks the filler words of `SettingsViewModel.FillerWords` (defaults: `Core.Transcripts.FillerWords`).
  Words outside every included clip are struck through (`TranscriptWordViewModel.IsOut`, also used by the lane);
  with no clips at all nothing is marked yet, so no word is struck.
  Clicking a word seeks. A selection can be played (`PlayRange` pauses at its end), kept as a clip (`KeepWords`) or
  cut out (`CutWords`, a `CutRangesCommand`), each one undo step through `EditorSession`. Without a model the tab
  offers Parakeet's download (Retry after a failed one, and why it does not fit when there is no space) and transcribes
  the video once it is installed.
- **Audio lanes**: one per audio stream (`AudioLaneViewModel`, `EditorViewModel.Audio.cs`). The header has the
  mute button (A1, A2, …), a volume slider from −∞ (−40) to +12 dB in 0.5 dB steps, and the value in dB; the wheel
  moves it half a decibel, a double-click resets it to 0 dB, and the right-click menu has Reset volume and Even out
  all tracks. Each lane mirrors the project's `TrackMix` both ways; the waveform is drawn at the lane's volume
  (`WaveformData.WithGain`) and a muted lane at 30 % opacity. Changes mark the project unsaved and autosave it.
- **Transcript lane**: the timeline's TX row (`ShowTranscriptLane`) is drawn by `TimelineControl.DrawTranscriptLane`,
  words packed by width from the start of each chunk. A click there seeks to a word; it never splits or trims.
- **Claude's export**: `IEditorContext.StartExportAsync` (`EditorMcpHost`) fills the export settings from the request
  (`ExportViewModel.PrepareForClaude`, which starts from Settings → Export like the dialog does, and refuses while the
  user has the dialog open), asks (`AskToExportAsync`: Settings → MCP server → Export; Never refuses before anything
  is prepared), then runs it with the dialog hidden
  (`StartPreparedForClaude`: `IsByClaude`, `IsHidden`). When it does not start (denied, withdrawn, refused),
  `AbandonPreparedForClaude` puts the dialog's own choices back. `ClaudeExportViewModel` (`ClaudePanelViewModel.Export`) drives the request banner over
  the preview (Allow, Deny, Always allow: `AskAsync`) and the card at the top of the Claude log (running, done,
  failed, denied, cancelled). The Export button reads "Exporting 45%" over a progress strip. ✕ and Esc on the
  export dialog hide a running export (`Dismiss`), and the button or Ctrl+E shows it again. `cancel_export` and the
  card's Cancel call `ExportViewModel.CancelExport`.
- **Claude's other requests**: `open_file` and `save_project` follow Settings → MCP server → Open files and Save
  project. For Ask, `EditorMcpHost.PermitAsync` shows `ClaudeFileRequestViewModel` (`ClaudePanelViewModel.Files`) in
  the same banner: the banner binds to `ClaudePanelViewModel.Request` (`IClaudeRequest`), the open/save request while
  one waits, else the export's. Claude asks one thing at a time; a request is withdrawn when Claude cancels the call
  or another file opens. Save checks the path first (`EditorViewModel.SavePathForClaude`), so a save that cannot
  happen is refused without asking.
- **MCP status**: `ClaudePanelViewModel.Status` (`McpStatus`: Off, Waiting, Connected, Editing, OtherWindow) follows
  `IsServerOn` ("Let Claude connect"), `IsConnected`, `IsWorking`, `IsListening` and `IsServedElsewhere`. The title
  bar badge (`McpText`, `IsMcpOn`) and the status card in Settings → MCP server show it. `EditorMcpServer` starts
  and stops the pipe server when `IsServerOn` changes, one switch at a time. The bridge connects to the editor with
  Claude's own clientInfo, so `McpPipeServer.ClientName` (`McpEndpoint.ClientTitle`: "claude-ai" is Claude Desktop,
  "claude-code" Claude Code) names the client. The editor that holds the pipe lock writes its project (file name) to
  `<pipe>.owner` next to it; a waiting window reads it (`OtherOwnerLabel`) for "The OurCut window with … has the
  server".
- **Settings dialog**: `SettingsViewModel` is split by section (`SettingsViewModel.<Section>.cs`, views in
  `Views/Settings/<Section>Section.axaml`, shared styles in `Theme/Controls.axaml`). Every change goes through
  `UpdateSettings(change)`, which applies it to `Current` and saves. Segmented controls use `ChoiceSet<T>`.
- **General**: at start `EditorViewModel.StartAsync` opens the file given on the command line, or else the newest
  recent file when "On startup" is "Open the last project". `RecentFilesStore` keeps 20 files and lists `Limit` of
  them (Recent files: 5, 10 or 20). The cache card measures `MediaCache.Measure` (one folder per video) off the UI
  thread; Clear cache (`MediaCache.Clear`) keeps the open video's folder and every `transcript-*.json`.
- **Settings file**: `AppSettings(Transcription, General?, Playback?, Export?, Keyboard?, Mcp?, Timeline?)` (records and enums
  in `Services/Settings/`), saved by `AppSettingsStore` to `%LOCALAPPDATA%\OurCut\settings.json` with
  source-generated JSON, enums by name (`LenientEnumConverter`). A section missing from the file (an older version
  wrote it, or it is at its defaults) reads as null and means the defaults; a value this version does not know falls
  back to its default, and the rest of the file is kept.
  `Timeline` is the timeline toolbar's chips (Frames, Keyframes, Silence, Scenes, Snap), saved as they are clicked and put
  back for every project and run (`SettingsViewModel.ApplyTimeline`); the design's screens show them all and save none.
  Playback is read before the player is created, which starts with the saved decoding and audio device. Changes apply
  while it plays: `IPlayer.SetHardwareDecoding` (mpv `hwdec`), `SetAudioDevice` (`audio-device`, one of
  `AudioDevices`, mpv's `audio-device-list`, listed again whenever Settings opens; the setting keeps mpv's name and the
  list shows its description), and the renderer through `VideoView.SoftwareOnly`, which rebuilds the view.
- **Key map**: `KeyMap` (each `ShortcutAction`'s `KeyCombo`s; the defaults are `KeyMap.Catalog`) is what Settings →
  Keyboard (`KeyboardSettingsViewModel`) edits: search, recording (while `IsRecording`, `Shortcuts.Handle` hands every
  key to `Record`), conflicts (Replace takes the key from the other action, so no key runs two actions) and reset.
  It is saved as `KeyboardSettings`: only the actions that differ from the defaults, by enum name. The editor runs
  whatever the map gives a key (`Shortcuts.Handle` → `KeyMap.Find` → `Shortcuts.Run`), and the hints (status bar,
  empty screen, mark buttons, the Jump chips) show the map's keys (`SettingsViewModel.Keys`, `ShortcutLabels`).
- **Demo mode**: `--demo <screen>` loads the design's sample (`DesignSample`, `DesignTranscript`,
  `DesignSettingsSample`) for a `DesignScreen`. `DemoScenario.Apply` does the common setup, then one partial hook per
  area (`ApplyTranscriptionMcp`, `ApplyTranscript`, `ApplyClaude`, `ApplyGeneralPlaybackExport`, `ApplyKeyboard`).
  `DesignScreensTests` renders every screen to `artifacts/screenshots/<screen>.png`.

### Not wired up yet

Places where the UI and the setting exist but the behaviour does not are marked with a one-line `// STUB:` comment
(`grep -rn "// STUB:" src`):

- **Transcription**: the Transcript tab's status says CPU until transcription runs on a GPU.

## Extension points
- **Smart cut**: `CutMode.SmartCut` exists in the export settings; the planner rejects it for now. It becomes
  a third kind of plan (re-encode the GOP around each cut, copy the rest, concat). The dialog lists it as not
  yet available.
- **GPU transcription**: OurCut ships the CPU-only sherpa-onnx runtime. The Device setting is wired through
  (`RecognizerPlan`): with a GPU build of sherpa-onnx 1.13.8 beside the app (`onnxruntime_providers_cuda` or
  `DirectML.dll`), Auto and GPU use it. sherpa-onnx publishes CUDA builds (NVIDIA; they need CUDA 12 or 13 and
  cuDNN 9 installed) but no DirectML build.

## Transcription

`OurCut.Transcription` (no UI references) turns speech into words with times, locally, with sherpa-onnx (ONNX
Runtime, CPU; see GPU transcription above). Settings → Transcription lists the models (`ModelCatalog`), all int8 builds from the sherpa-onnx
GitHub releases (.tar.bz2): Parakeet TDT 0.6B v3 (25 European languages including Ukrainian and English; the default)
and Whisper large-v3-turbo, small and base.en (99 languages; base.en English only).

- **Audio**: ffmpeg mixes every audio track to 16 kHz mono float (`SpeechAudio`), timed from the file start like
  keyframes, and pipes it out.
- **Pieces** (`TranscriptionPipeline`): the stream is cut into pieces of at most 28 s, each ending at the quietest
  100 ms after 18 s, so words are not cut in half and memory stays small. Up to four pieces are recognized at once
  on threads below normal priority (`nice` 10 on Linux), sharing one model, and their words are handed on in order.
- **Device** (`RecognizerPlan`, from Settings → Transcription → Device): on the CPU, every core: up to four pieces at
  once (each holds 100–200 MB), the cores shared among them (16 cores: 4 pieces × 4 threads; ONNX Runtime's own
  threads are lowered too). Auto uses a GPU runtime when one is installed and falls back to the CPU if it does not
  start; GPU fails with a clear message without one; CPU stays on the CPU. The setting's note says which it is
  ("CPU · 16 threads").
- **Words** (`WordBuilder`): the models give subword tokens (a leading space starts a word) with start times;
  punctuation joins the word before it; a word followed by a pause ends after about as long as it takes to say.
  The published Whisper models give no times, so their words get estimated ones: the speech in the piece (stretches
  louder than the background) is shared out in proportion to word length, and the transcript says its times are
  approximate.
- **Data**: `Word`, `Phrase` and `Transcript` live in Core (`OurCut.Core.Transcripts`), so the MCP tools use them
  without the engine. Phrases end at . ! ? … or pauses of 0.8 s.
- **In the editor** (`MediaPreview`): transcription starts when asked for (the Transcript tab's Transcribe, or a
  Claude transcript tool), or when a file is opened with "Transcribe when a video is opened" on (off by default;
  saved as `transcribeWhenOpened`, so files from when it was on by default read as off), once a model is installed.
  The timeline's Transcript chip is the same setting: turned on, the open video is transcribed too; turned off, a
  transcription under way stops (`MediaPreview.StopTranscription`).
  A transcript cached earlier with the chosen model is shown when the file opens either way. It runs after
  keyframes, waveform and thumbnails (it may overlap scene detection), on every core below normal priority. The status bar shows "transcribing 34%", the transcript fills in piece by piece
  and is cached per model and language (`transcript-<model>-<language>.json`). Installing a model or changing the
  model or language starts it (with "Transcribe when a video is opened" off, only a transcript already asked for).

- `ModelInstaller` downloads into `<models folder>/<id>.partial`, continuing an interrupted download with an HTTP
  range request, unpacks archives there (SharpZipLib's bzip2 + `System.Formats.Tar`, without the archive's top
  folder, refusing entries that point outside it) and renames the folder to `<id>` only when every file the model
  needs is there. It checks free space first and explains failures (HTTP status, lost connection, full disk).
- `ModelStore` answers whether a model is installed (all its files present), its size, and deletes it.
- The settings (engine, model, device, language, models folder) are saved to `%LOCALAPPDATA%\OurCut\settings.json`;
  models go to `%LOCALAPPDATA%\OurCut\models` unless another folder is chosen. Cancelling a download removes it;
  a failed one is kept and continues on the next try.

## Project file (`.ourcut.json`)

```json
{
  "format": "ourcut-project",
  "version": 1,
  "name": "launch-keynote",
  "source": {
    "path": "media/keynote_final_4k.mp4",
    "duration": 872.48,
    "frameRate": 29.97,
    "audioStreams": [ { "index": 1, "label": "Mic" }, { "index": 2, "label": "Game", "gainDb": -6, "muted": true } ]
  },
  "clips": [
    { "id": 1, "label": "Intro", "start": 12.04, "end": 45.32, "included": true }
  ]
}
```

- `path` is relative to the project file when the video is on the same drive, otherwise absolute.
- Times are seconds with an invariant decimal point.
- `gainDb` (a track's volume) and `muted` are left out at their defaults (0 dB, not muted), so files from before
  they existed load with every track at 0 dB. Out-of-range volumes are clamped to −40…+12 dB.
- Readers ignore unknown fields. Files with a higher `version` than the app supports are rejected.
- Files are written atomically (temporary file, then replace). A saved project is autosaved after edits.
