#!/usr/bin/env python3
"""The GitHub release notes of a version, from CHANGELOG.md and its Ukrainian mirror CHANGELOG.uk.md.

    python3 scripts/release-notes.py              # the Version in Directory.Build.props
    python3 scripts/release-notes.py 0.1.0        # another version
    python3 scripts/release-notes.py --title      # only the release title: "V0.1.0 First Cut ✂️"

It first checks both files (docs/releasing.md) and fails on a mismatch: CHANGELOG.uk.md must have the same "## "
sections, the groups in the same order (New/Нове, Improved/Покращено, Fixed/Виправлено) and as many bullets in each,
and every bullet starts with a bold lead ("- **Clips snap together.** ..."). The release job of CI publishes the
output, and the Changelog workflow runs it on every pull request.
"""

import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TAGLINE = "Cut the best moments out of long videos — with Claude."
LOGO = "src/HighlightCut.App/Assets/highlightcut-256.png"
# The English groups, their Ukrainian names and the one emoji each gets in the release.
GROUPS = {"New": ("Нове", "✨"), "Improved": ("Покращено", "⚡"), "Fixed": ("Виправлено", "🐛")}
HEADING = re.compile(r"^(?P<version>\S+)(?:\s+[—–]\s+(?P<date>[^—–]+?))?(?:\s+[—–]\s+(?P<codename>.+))?$")
LEAD = re.compile(r"^\*\*[^*]+\*\*")


class Section:
    def __init__(self, heading):
        self.heading = heading
        match = HEADING.match(heading)
        self.version = match["version"]
        self.date = match["date"]
        self.codename = match["codename"]
        self.groups = []  # [(title, [bullet])]


def parse(path):
    """The "## " sections, their "### " groups and "- " bullets, as the app reads them (Services/Changelog.cs)."""
    sections = []
    items = None
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.rstrip()
        if line.startswith("## "):
            sections.append(Section(line[3:].strip()))
            items = None
        elif not sections:
            continue
        elif line.startswith("### "):
            items = []
            sections[-1].groups.append((line[4:].strip(), items))
        elif items is not None and line.startswith(("- ", "* ")):
            items.append(line[2:].strip())
        elif items and line and line[0].isspace():
            items[-1] += " " + line.strip()
    return sections


def check(en, uk):
    """What is wrong with the two files; nothing when they match."""
    errors = []
    en_headings = [s.heading for s in en]
    uk_headings = [s.heading for s in uk]
    if en_headings != uk_headings:
        errors.append(f"CHANGELOG.uk.md has the sections {uk_headings}, CHANGELOG.md has {en_headings}.")
    for e, u in zip(en, uk):
        if e.heading != u.heading:
            continue
        for title, _ in e.groups:
            if title not in GROUPS:
                errors.append(f"## {e.heading}: the group \"### {title}\" is not one of {', '.join(GROUPS)}.")
        expected = [(GROUPS.get(t, (t,))[0], len(b)) for t, b in e.groups]
        actual = [(t, len(b)) for t, b in u.groups]
        if expected != actual:
            errors.append(f"## {e.heading}: CHANGELOG.uk.md has the groups and bullets {actual}, "
                          f"CHANGELOG.md needs {expected}.")
    for name, sections in (("CHANGELOG.md", en), ("CHANGELOG.uk.md", uk)):
        for s in sections:
            for title, bullets in s.groups:
                for b in bullets:
                    if not LEAD.match(b):
                        errors.append(f"{name}, ## {s.heading}, ### {title}: this bullet does not start with a bold "
                                      f"lead (**Like this.**): {b[:60]}")
    return errors


def version_in_props():
    return ET.parse(ROOT / "Directory.Build.props").find(".//Version").text.strip()


def title(section):
    return f"V{section.version} {section.codename}" if section.codename else f"HighlightCut v{section.version}"


def numbered(groups):
    lines = []
    for title_, bullets in groups:
        if not bullets:
            continue
        english = next((k for k, v in GROUPS.items() if title_ in (k, v[0])), None)
        emoji = f"{GROUPS[english][1]} " if english else ""
        lines += ["", f"#### {emoji}{title_}", ""]
        lines += [f"{i}. {b}" for i, b in enumerate(bullets, 1)]
    return lines


def notes(en, uk, repo, tag):
    asset = f"HighlightCut-{en.version}-windows-x64.zip"
    raw = f"https://raw.githubusercontent.com/{repo}/{tag}"
    blob = f"https://github.com/{repo}/blob/{tag}"
    lines = [
        f'<img src="{raw}/{LOGO}" width="96" alt="HighlightCut logo">',
        "",
        "# HighlightCut",
        "",
        f"_{TAGLINE}_",
        "",
        "### Download",
        "",
        f"**Windows 10/11, 64-bit:** [{asset}](https://github.com/{repo}/releases/download/{tag}/{asset})",
        "",
        "1. Unzip it anywhere and run `HighlightCut.exe`. Nothing to install.",
        "2. The build isn’t signed yet, so Windows SmartScreen may warn you: click **More info** → **Run anyway**.",
        "",
        "Transcription runs on AMD, Intel and NVIDIA graphics cards through DirectML, and on the processor otherwise.",
        "",
        "### What’s new",
    ]
    lines += numbered(en.groups)
    lines += ["", "### Що нового"]
    lines += numbered(uk.groups)
    lines += [
        "",
        "---",
        "",
        f"[Changelog]({blob}/CHANGELOG.md) · [Журнал змін]({blob}/CHANGELOG.uk.md) · [README]({blob}/README.md)",
    ]
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description="Builds the GitHub release notes of a version from both changelogs.")
    parser.add_argument("version", nargs="?", help="X.Y.Z (default: the Version in Directory.Build.props)")
    parser.add_argument("--title", action="store_true", help="print only the release title")
    parser.add_argument("--output", help="write the notes to this file instead of printing them")
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY") or "skyp1nus/HighlightCut")
    args = parser.parse_args()
    # Emoji and Ukrainian in any console, the Windows one too.
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")

    en_all = parse(ROOT / "CHANGELOG.md")
    uk_all = parse(ROOT / "CHANGELOG.uk.md")
    errors = check(en_all, uk_all)
    version = (args.version or version_in_props()).removeprefix("v")
    en = next((s for s in en_all if s.version == version), None)
    uk = next((s for s in uk_all if s.version == version), None)
    if en is None or not any(b for _, b in en.groups):
        errors.append(f"CHANGELOG.md has no notes under ## {version}.")
    if errors:
        for e in errors:
            # GitHub Actions shows these as annotations; locally they are plain lines.
            print(f"::error::{e}" if os.environ.get("GITHUB_ACTIONS") else f"error: {e}", file=sys.stderr)
        return 1

    if args.title:
        print(title(en))
        return 0
    text = notes(en, uk, args.repo, f"v{version}")
    if args.output:
        Path(args.output).write_text(text, encoding="utf-8")
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
