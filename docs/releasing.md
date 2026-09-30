# Versions and releasing

HighlightCut has one version number: `<Version>` in [Directory.Build.props](../Directory.Build.props). The app shows it
(Settings, at the bottom of the section list), CI builds it, and release tags must match it.

- A build of a `vX.Y.Z` tag is version X.Y.Z. CI fails if the tag is not the Version in Directory.Build.props.
- Every other CI build is `<Version>-dev.<run number>` (0.1.0-dev.42). Local builds are just `<Version>`.

## Release notes

[CHANGELOG.md](../CHANGELOG.md) at the repository root is the only place release notes are written. The app embeds it
and shows the new sections once after an update ("What’s new"), and the release job publishes that version's section
on GitHub.

```markdown
## Unreleased

### New

- Something people will notice, in plain words.

## 0.1.0 — 2026-09-30

### New
### Improved
### Fixed
```

- Every pull request that changes something users would notice adds a bullet under `## Unreleased`, in New,
  Improved or Fixed. The Changelog workflow fails a pull request that changes `src/` without touching CHANGELOG.md,
  unless its title contains `[no changelog]` or it has the `no-changelog` label (for changes nobody would notice:
  refactoring, tests, build).
- Write for the people using the app: what they can do now or what works better, not how it was done.
- A test (`ChangelogTests.This_version_has_notes`) fails when the Version has no section with at least one bullet, so
  the version cannot be bumped without notes.

## Making a release

1. Move the bullets under `## Unreleased` into a new `## X.Y.Z — YYYY-MM-DD` section below it, and leave
   `## Unreleased` empty at the top.
2. Set `<Version>X.Y.Z</Version>` in Directory.Build.props.
3. Open a pull request with both, and merge it once CI is green.
4. Tag the merge commit and push the tag:

   ```sh
   git tag vX.Y.Z origin/master
   git push origin vX.Y.Z
   ```

   CI builds the tag, checks it against the Version, and creates the GitHub release "HighlightCut vX.Y.Z" with the
   Windows build and the notes of `## X.Y.Z` from CHANGELOG.md. It fails when that section is missing or empty.

People who update see What’s new with every section newer than the version they had. New users get the welcome tour
instead.
