#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = []
# ///
"""
Johnny Appleseed - asset-source guard (fails the build on bad graphics sources).

Two checks, both run at build time (the csproj ValidateAssetSources target runs
this WITHOUT IgnoreExitCode, so a non-zero exit fails `dotnet build`):

1. NAME COLLISIONS. Several build steps turn a *source* file under graphics/ into
   an embedded asset whose key is derived from the source's base name:
       graphics/<name>.xcf      -> graphics/<name>.png + .jpg   (_xcf.py)
       graphics/<name>.aseprite -> graphics/<name>.png / .gif   (aseprite-export.py)
       graphics/icon.svg        -> the app/window icon          (_icons.py)
   If two sources share a base name in one directory - e.g. graphics/a.svg and
   graphics/a.xcf - they fight over the same generated name and silently overwrite
   one another. That is made a hard failure here.

2. PROGRESSIVE JPEGs. The game decodes images with raylib, whose JPEG loader is
   stb_image - and stb_image CANNOT decode progressive JPEGs. Such a file loads
   fine in every editor/browser but fails at runtime with raylib's
   "IMAGE: Data format not supported", so the art just doesn't appear. Since the
   two encodings are visually identical, this is caught at build time instead of
   shipping blank backgrounds. Fix a flagged file with a lossless re-save, e.g.:
       jpegtran -copy all path/to/image.jpg > baseline.jpg && mv baseline.jpg path/to/image.jpg

Usage:
    uv run scripts/check-asset-sources.py [graphics_dir ...]   (defaults to ./graphics)
"""

from __future__ import annotations

import sys
from collections import defaultdict
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

# Extensions that are compiled into some *other* embedded asset (and so can
# overwrite a same-named sibling). Kept in sync with the csproj art targets.
BUILD_SOURCE_EXTS = {".xcf", ".aseprite", ".svg"}
JPEG_EXTS = {".jpg", ".jpeg"}


def find_collisions(root: Path) -> list[tuple[Path, str, list[Path]]]:
    """Return (directory, base_name, files) for every colliding group found."""
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


def is_progressive_jpeg(path: Path) -> bool:
    """True if `path` is a progressive JPEG (SOF2 marker).

    Walks the JPEG marker segments up to the first scan (SOS). The frame header
    (SOFn) always precedes the entropy-coded data, so we never have to parse past
    SOS - which avoids mistaking a 0xFFC2 byte inside compressed data for a marker.
    """
    try:
        d = path.read_bytes()
    except OSError:
        return False
    if len(d) < 4 or d[0] != 0xFF or d[1] != 0xD8:  # not a JPEG (no SOI)
        return False
    i = 2
    n = len(d)
    while i + 1 < n:
        if d[i] != 0xFF:
            i += 1
            continue
        marker = d[i + 1]
        if marker == 0xFF:            # fill byte
            i += 1
            continue
        if marker == 0xC2:            # SOF2 = progressive
            return True
        if marker in (0xC0, 0xC1, 0xC3) or marker == 0xDA:
            return False              # baseline/extended/lossless SOF, or start of scan
        if marker in (0xD8, 0xD9, 0x01) or 0xD0 <= marker <= 0xD7:
            i += 2                    # standalone marker, no length
            continue
        if i + 3 >= n:
            break
        seg_len = (d[i + 2] << 8) | d[i + 3]
        i += 2 + seg_len              # skip this segment's payload
    return False


def find_progressive_jpegs(root: Path) -> list[Path]:
    return sorted(f for f in root.rglob("*")
                  if f.is_file() and f.suffix.lower() in JPEG_EXTS and is_progressive_jpeg(f))


def _rel(p: Path) -> Path:
    try:
        return p.relative_to(REPO_ROOT)
    except ValueError:
        return p


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    roots = [Path(a) for a in argv] or [REPO_ROOT / "graphics"]

    collisions = []
    progressive = []
    for root in roots:
        if root.exists():
            collisions.extend(find_collisions(root))
            progressive.extend(find_progressive_jpegs(root))

    if not collisions and not progressive:
        return 0

    if collisions:
        print("ERROR: colliding asset sources detected - two or more sources would "
              "generate the same asset name and overwrite each other:", file=sys.stderr)
        for parent, _stem, files in collisions:
            names = ", ".join(f.name for f in files)
            print(f"  {_rel(parent)}/  ->  {names}", file=sys.stderr)
        print("Rename or remove all but one source per base name.", file=sys.stderr)

    if progressive:
        print("ERROR: progressive JPEG(s) detected - raylib/stb_image cannot decode "
              "these at runtime, so the art fails to load ('Data format not supported'):",
              file=sys.stderr)
        for f in progressive:
            print(f"  {_rel(f)}", file=sys.stderr)
        print("Re-save each as a BASELINE JPEG, e.g. losslessly with:\n"
              "  jpegtran -copy all FILE > /tmp/b.jpg && mv /tmp/b.jpg FILE", file=sys.stderr)

    return 1


if __name__ == "__main__":
    raise SystemExit(main())
