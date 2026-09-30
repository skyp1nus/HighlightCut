# HighlightCut

Avalonia 12 / .NET 10 video editor. Read [README.md](README.md) and [docs/architecture.md](docs/architecture.md) first.

## Build and test

```sh
dotnet build HighlightCut.slnx -c Release          # warnings are errors
dotnet test HighlightCut.slnx -c Release --no-build
```

On Linux the media and playback tests need `ffmpeg` and `libmpv2` (apt). UI tests render headlessly and write
screenshots to `artifacts/screenshots/`: look at the ones your change affects. `--demo <screen>` shows a design screen.

## Conventions

- **Changelog**: every user-visible change adds a bullet under `## Unreleased` in [CHANGELOG.md](CHANGELOG.md), in
  New, Improved or Fixed, in the same pull request. Plain words about what users notice. A PR without one needs
  `[no changelog]` in its title or the `no-changelog` label. Releasing: [docs/releasing.md](docs/releasing.md).
- **Version**: only `<Version>` in Directory.Build.props; bump it only when releasing.
- Match the surrounding code: its comment density and naming, CommunityToolkit `[ObservableProperty]` /
  `[RelayCommand]`, and plain English UI text with curly apostrophes (’).
- Settings change only through `UpdateSettings(s => s with { ... })`; the JSON is camelCase.
- Commits: English, a short subject and a body of bullets. No `Co-Authored-By` or `Claude-Session` lines in commits,
  and no "Generated with Claude Code" footer or session link in pull request descriptions.
