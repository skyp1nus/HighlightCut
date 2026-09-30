# Versions and releasing

HighlightCut has one version number: `<Version>` in [Directory.Build.props](../Directory.Build.props). The app shows it
(Settings, at the bottom of the section list), CI builds it, and release tags must match it.

- A build of a `vX.Y.Z` tag is version X.Y.Z. CI fails if the tag is not the Version in Directory.Build.props.
- Every other CI build is `<Version>-dev.<run number>` (0.1.0-dev.42). Local builds are just `<Version>`.

## Release notes

Release notes are written in two files at the repository root: [CHANGELOG.md](../CHANGELOG.md) in English and
[CHANGELOG.uk.md](../CHANGELOG.uk.md), its Ukrainian mirror. The app embeds the English one and shows the new sections
once after an update ("What’s new"); the GitHub release has both.

```markdown
## Unreleased

### New

- **Chapters in the export.** Something people will notice, in plain words.

## 0.1.0 — 2026-09-30 — First Cut ✂️

### New
### Improved
### Fixed
```

- Every pull request that changes something users would notice adds a bullet under `## Unreleased`, in New,
  Improved or Fixed, **and the same bullet in Ukrainian** in CHANGELOG.uk.md, under Нове, Покращено or Виправлено.
  The Changelog workflow fails a pull request that changes `src/` without touching CHANGELOG.md, or CHANGELOG.md
  without CHANGELOG.uk.md, unless its title contains `[no changelog]` or it has the `no-changelog` label (for changes
  nobody would notice: refactoring, tests, build).
- Every bullet starts with a short bold lead that says what changed (`**Clips snap together.**`), then a sentence or
  two. What’s new shows the lead in bold.
- Write for the people using the app: what they can do now or what works better, not how it was done.
- The two files have the same `## ` headings (the Ukrainian one keeps the English codename), the same groups in the
  same order and as many bullets in each. [scripts/release-notes.py](../scripts/release-notes.py) checks this; the
  Changelog workflow runs it on every pull request, even with `[no changelog]`, so a mismatch fails there.
- A test (`ChangelogTests.This_version_has_notes`) fails when the Version has no section with at least one bullet, so
  the version cannot be bumped without notes.

## Making a release

1. In both changelogs, move the bullets under `## Unreleased` into a new `## X.Y.Z — YYYY-MM-DD — Codename 🎬`
   section below it, and leave `## Unreleased` empty at the top. The codename is a word or two and one emoji; the
   release is titled after it and What’s new shows it (without the emoji).
2. Set `<Version>X.Y.Z</Version>` in Directory.Build.props.
3. Preview the release notes, then open a pull request with all of it and merge it once CI is green:

   ```sh
   python3 scripts/release-notes.py          # the notes of the Version in Directory.Build.props
   python3 scripts/release-notes.py --title  # V0.1.0 First Cut ✂️
   ```

4. Tag the merge commit and push the tag:

   ```sh
   git tag vX.Y.Z origin/master
   git push origin vX.Y.Z
   ```

   CI builds the tag, checks it against the Version, and creates the GitHub release "VX.Y.Z Codename 🎬", marked
   Latest, with the Windows build as `HighlightCut-X.Y.Z-windows-x64.zip` and the notes from
   `scripts/release-notes.py`: the logo and tagline, how to download and start it, the English notes and the
   Ukrainian ones, numbered. It fails when that section is missing or empty, or the two files do not match.

People who update see What’s new with every section newer than the version they had. New users get the welcome tour
instead.
