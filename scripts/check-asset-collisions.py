#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = []
# ///
"""
Johnny Appleseed - asset-source collision guard.

Several build steps turn a *source* file under graphics/ into an embedded asset
whose key is derived from the source's base name:

    graphics/<name>.xcf      -> graphics/<name>.png + graphics/<name>.jpg  (_xcf.py)
    graphics/<name>.aseprite -> graphics/<name>.png / .gif                 (aseprite-export.py)
    graphics/icon.svg        -> the app/window icon                        (_icons.py)

If two sources share the same base name in the same directory - e.g.
`graphics/a.svg` and `graphics/a.xcf` - they would fight over the same generated
asset name and silently overwrite one another. This guard makes that a hard build
FAILURE (exit 1) instead, so the ambiguity is caught the moment it is introduced.

Rule: within any single directory, group files by their base name (the filename
with its extension removed, compared case-insensitively). A group is a collision
when it holds more than one file AND at least one of them is a *build source*
(.xcf / .aseprite / .svg) - i.e. something that generates another asset and could
clobber a sibling. Two plain static files that merely share a stem (say `a.png`
and `a.jpg`) are left alone: they are distinct assets, not competing sources.

Usage:
    uv run scripts/check-asset-collisions.py [graphics_dir ...]
    (defaults to ./graphics)
"""

from __future__ import annotations

import sys
from collections import defaultdict
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

# Extensions that are compiled into some *other* embedded asset (and so can
# overwrite a same-named sibling). Kept in sync with the csproj art targets.
BUILD_SOURCE_EXTS = {".xcf", ".aseprite", ".svg"}


def find_collisions(root: Path) -> list[tuple[Path, str, list[Path]]]:
    """Return (directory, base_name, files) for every colliding group found."""
    # (dir, lowercased stem) -> list of files
    groups: dict[tuple[Path, str], list[Path]] = defaultdict(list)
    for f in root.rglob("*"):
        if not f.is_file() or f.name.startswith("."):
            continue  # ignore .gitkeep and other dotfiles
        groups[(f.parent, f.stem.lower())].append(f)

    collisions = []
    for (parent, stem), files in sorted(groups.items()):
        if len(files) < 2:
            continue
        if any(f.suffix.lower() in BUILD_SOURCE_EXTS for f in files):
            collisions.append((parent, stem, sorted(files)))
    return collisions


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    roots = [Path(a) for a in argv] or [REPO_ROOT / "graphics"]

    all_collisions = []
    for root in roots:
        if root.exists():
            all_collisions.extend(find_collisions(root))

    if not all_collisions:
        return 0

    print("ERROR: colliding asset sources detected - two or more sources would "
          "generate the same asset name and overwrite each other:", file=sys.stderr)
    for parent, _stem, files in all_collisions:
        try:
            shown = parent.relative_to(REPO_ROOT)
        except ValueError:
            shown = parent
        names = ", ".join(f.name for f in files)
        print(f"  {shown}/  ->  {names}", file=sys.stderr)
    print("Rename or remove all but one source per base name, then rebuild.",
          file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
