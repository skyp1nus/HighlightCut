# Third-party notices

HighlightCut is licensed under GPL-3.0-or-later. It depends on the components below.

## Native binaries (downloaded by `scripts/fetch-deps.ps1`, not stored in this repository)

| Component | Version | License | Source |
| --- | --- | --- | --- |
| FFmpeg (`ffmpeg.exe`, `ffprobe.exe`) | n9.0.1-11-ge47273f4d9, [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) `autobuild-2026-08-31-13-27`, win64-gpl | GPL-3.0-or-later | [FFmpeg e47273f4d9](https://github.com/FFmpeg/FFmpeg/commit/e47273f4d9) |
| libmpv (`libmpv-2.dll`) | v0.41.0-1050-ge76a35ec9, [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake) `20260920` | GPL (built with GPL FFmpeg) | [mpv e76a35ec95](https://github.com/mpv-player/mpv/commit/e76a35ec95) |
| Vulkan loader (`vulkan-fallback/vulkan-1.dll`) | 1.3.283, from `Silk.NET.Vulkan.Loader.Native` 2025.9.12 | Apache-2.0 | [KhronosGroup/Vulkan-Loader](https://github.com/KhronosGroup/Vulkan-Loader) |
| sherpa-onnx with DirectML (`sherpa-onnx-c-api.dll`) | v1.13.8, built by `.github/workflows/sherpa-directml.yml` | Apache-2.0 | [k2-fsa/sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) |
| ONNX Runtime DirectML (`onnxruntime.dll`) | 1.24.4, from `Microsoft.ML.OnnxRuntime.DirectML` | MIT | [microsoft/onnxruntime](https://github.com/microsoft/onnxruntime) |
| DirectML (`DirectML.dll`) | 1.15.4, from `Microsoft.AI.DirectML` | Microsoft DirectML license (redistributable), `licenses/DirectML-LICENSE.txt` | [microsoft/DirectML](https://github.com/microsoft/DirectML) |
| Visual C++ runtime (`msvcp140*.dll`, `vcruntime140*.dll`) | Visual Studio 2022 redistributable files | Microsoft Visual Studio license terms | [Microsoft](https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files) |
| 7-Zip (`7zr.exe`, build tool only, never shipped) | 26.03 | Public domain | [ip7z/7zip](https://github.com/ip7z/7zip) |

This software uses code of FFmpeg (https://ffmpeg.org) licensed under the GPLv3, and its source can be
downloaded from the links above. FFmpeg and mpv are trademarks of their respective owners; HighlightCut is not
affiliated with or endorsed by either project.

Anyone distributing HighlightCut builds that include these binaries must also offer their corresponding source
code (GPLv3 section 6).

## NuGet packages

| Package | License |
| --- | --- |
| Avalonia | MIT |
| org.k2fsa.sherpa.onnx (C# API; CPU runtime on Linux) | Apache-2.0 |
| CommunityToolkit.Mvvm | MIT |
| FFMpegCore | MIT |
| SkiaSharp | MIT |
| xUnit | Apache-2.0 |

## Fonts

| Font | License |
| --- | --- |
| Inter (The Inter Project Authors) | SIL Open Font License 1.1, see `src/HighlightCut.App/Assets/Fonts/OFL-Inter.txt` |
| JetBrains Mono (The JetBrains Mono Project Authors) | SIL Open Font License 1.1, see `src/HighlightCut.App/Assets/Fonts/OFL-JetBrainsMono.txt` |
