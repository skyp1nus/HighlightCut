# Memory benchmark

`HighlightCut.MemoryBench` runs a realistic editing session in the real editor and measures the process after each
step. The editor, the player (libmpv), the analyses (ffmpeg) and transcription (sherpa-onnx) are the app's own; only the
window is headless (Avalonia.Headless with Skia, redrawn 60 times a second), so it runs anywhere, CI included. It uses
the App's runtime settings (`build/HighlightCut.Runtime.props`).

The session:

1. Start, no file.
2. Open a long video (1080p, two audio tracks; made with ffmpeg's `testsrc2` and `sine` if it does not exist) with the
   default chips (Waveform), and wait for the audio.
3. Play 30 s, then 40 seeks to random places (the median and slowest seek are noted, and what mpv's demuxer holds).
4. Turn on Keyframes, Frames, Silence and Scenes, one at a time, waiting for each.
5. Transcribe with the best installed model (Parakeet), when there is a models folder.
6. Idle.
7. Open a second video (10 min, 720p) with every chip on, then reopen the long one (everything from the cache).
8. Close the project, idle, and a full collection: the closed files must be gone from memory.

## Run it

```sh
dotnet run --project scripts/memory -c Release -- --video /tmp/long90.mp4 --models ~/models --out artifacts/memory/now.md
```

| Option | |
|---|---|
| `--video <file>` | The long video; made there (and kept) if it does not exist. A temporary one otherwise. |
| `--minutes <n>` | Length of a long video that is made (90). |
| `--second <file>` | The second video, likewise. |
| `--models <folder>` | Models folder for the transcription (or `HIGHLIGHTCUT_MODELS_DIR`); `HIGHLIGHTCUT_NETWORK_TESTS=1 dotnet test tests/HighlightCut.Transcription.Tests --filter RealDownloadTests` downloads Parakeet into it. |
| `--idle <s>` | How long the idle steps are (30). |
| `--no-scenes` | Leaves out scene detection, which reads every frame (minutes for a long video). |
| `--out <file>` | Also writes the report there (Markdown). |
| `--wait-for-dump` | Waits two minutes at the end, for `dotnet-dump collect -p <pid>`. |

`compare.ps1` measures this version and another one (master by default) with the same benchmark and videos, and
writes `artifacts/memory/before.md` and `after.md`. On GitHub, Actions → CI → Run workflow with "memory" ticked does that
on Windows with a 30-minute video.

Runtime settings can be tried without rebuilding through environment variables, which the report lists:
`DOTNET_GCConserveMemory=5`, `DOTNET_gcConcurrent=0`, `DOTNET_TieredPGO=0`, `DOTNET_GCgen0size=0x400000`…

## Reading the report

- **Working set** is what Task Manager's "Memory" column shows on Windows; **Private** is private bytes on Windows, and
  the resident anonymous memory (RssAnon) on Linux. Both include native memory: mpv, ONNX Runtime, Skia.
- **Managed** is the .NET heap now, **GC committed** what the GC holds from the system, **Allocated** what the step
  allocated in all (churn, not size).
- **Held by malloc** (Linux): memory native code freed that glibc still keeps, counted in Private until it is handed back.
- The largest allocations are sampled by the runtime (one event per ~100 KB allocated), per step and for the session.
- Gen2 collections are mostly the headless window's doing: it allocates a new bitmap for every frame it draws, which
  asks for a collection. A real window does not.
- Video is drawn by mpv's software renderer (there is no OpenGL headless), and the audio goes nowhere (`ao=null`).
