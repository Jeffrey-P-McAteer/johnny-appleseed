#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = []
# ///
"""
Johnny Appleseed - weather / time-of-year art-variant coverage report.

Artwork can ship multiple *editions* selected at runtime by the current weather
and season (see src/JohnnyAppleseed/Ambient/ArtVariant.cs). An edition is encoded
as dot-separated tags between a file's stem and extension, e.g.

    graphics/main-menu/backdrop.jpg           <- default (normal weather, any season)
    graphics/main-menu/backdrop.rainy.png     <- rainy edition
    graphics/main-menu/backdrop.fall.png      <- autumn edition
    graphics/main-menu/backdrop.winter.snowy.gif  <- combined (bonus), animated

This tool groups files into art SETS (same directory + stem) and reports, for each
variant-managed set, which REQUIRED editions exist and which are missing, so artists
know what still needs drawing. Combined-tag files are listed as bonus editions.

Required editions (single-axis):
    weather:  normal (the untagged base), sunny, rainy      (snowy is optional)
    season:   spring, summer, fall, winter

The tag vocabulary below MUST stay in sync with ConditionVocab in
src/JohnnyAppleseed/Ambient/Conditions.cs.

Usage
-----
    uv run scripts/media-variants-report.py            # report (always exit 0)
    uv run scripts/media-variants-report.py --strict   # exit 1 if any required edition missing
    uv run scripts/media-variants-report.py --all      # also list single (non-variant) art
    uv run scripts/media-variants-report.py --include-snowy   # also require a snowy edition
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

# KEEP IN SYNC WITH src/JohnnyAppleseed/Ambient/Conditions.cs (ConditionVocab).
WEATHER_TAGS = ["sunny", "rainy", "snowy"]
SEASON_TAGS  = ["spring", "summer", "fall", "winter"]
KNOWN_TAGS   = set(WEATHER_TAGS) | set(SEASON_TAGS)

# "normal" weather is the untagged base; snowy is optional by default.
REQUIRED_WEATHER = ["normal", "sunny", "rainy"]

IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"}
DEFAULT_ROOTS = ["graphics", "obj/aseprite"]


def parse_tags(filename: str) -> tuple[str, frozenset[str], str]:
    """
    Split 'backdrop.rainy.png' -> ('backdrop', {'rainy'}, '.png').

    Trailing dot-segments that are known tags are the edition; the remainder
    (which may itself contain dots) is the stem.
    """
    p = Path(filename)
    ext = p.suffix.lower()
    segments = p.name[: -len(ext)].split(".") if ext else p.name.split(".")

    tags: list[str] = []
    while len(segments) > 1 and segments[-1].lower() in KNOWN_TAGS:
        tags.append(segments.pop().lower())
    stem = ".".join(segments)
    return stem, frozenset(tags), ext


def edition_of(tags: frozenset[str]) -> str | None:
    """The single-axis edition a file provides, or None if it's base/combined."""
    if not tags:
        return "normal"
    if len(tags) == 1:
        return next(iter(tags))
    return None  # combined (bonus) edition


def collect_sets(roots: list[Path]) -> dict[tuple[str, str], list[Path]]:
    """Map (dir, stem) -> member files across all roots."""
    sets: dict[tuple[str, str], list[Path]] = {}
    for root in roots:
        if not root.exists():
            continue
        for f in sorted(root.rglob("*")):
            if not f.is_file() or f.suffix.lower() not in IMAGE_EXTS:
                continue
            stem, _tags, _ext = parse_tags(f.name)
            key = (str(f.parent.relative_to(REPO_ROOT)), stem)
            sets.setdefault(key, []).append(f)
    return sets


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description="Report weather/season art-variant coverage.")
    ap.add_argument("--strict", action="store_true",
                    help="Exit non-zero if any variant-managed set is missing a required edition.")
    ap.add_argument("--all", action="store_true",
                    help="Also list single-image (non-variant) art sets.")
    ap.add_argument("--include-snowy", action="store_true",
                    help="Require a snowy weather edition too (optional by default).")
    ap.add_argument("--roots", nargs="*", default=DEFAULT_ROOTS,
                    help=f"Folders to scan (default: {' '.join(DEFAULT_ROOTS)}).")
    args = ap.parse_args(argv)

    required_weather = REQUIRED_WEATHER + (["snowy"] if args.include_snowy else [])
    required = required_weather + SEASON_TAGS

    roots = [REPO_ROOT / r for r in args.roots]
    sets = collect_sets(roots)

    total_missing = 0
    managed = 0
    singles: list[str] = []

    for (folder, stem), files in sorted(sets.items()):
        members = [(f, *parse_tags(f.name)[1:]) for f in files]  # (path, tags, ext)
        tagged = [m for m in members if m[1]]
        is_variant_managed = len(files) > 1 or bool(tagged)

        if not is_variant_managed:
            singles.append(f"{folder}/{stem}{members[0][2]}")
            continue

        managed += 1
        provided = {edition_of(tags) for _f, tags, _e in members}
        provided.discard(None)
        combined = [(f, tags) for f, tags, _e in members if edition_of(tags) is None]
        animated = any(ext == ".gif" for _f, _t, ext in members)

        missing = [e for e in required if e not in provided]
        total_missing += len(missing)

        flag = "OK  " if not missing else "GAP "
        kind = " (animated)" if animated else ""
        print(f"{flag}{folder}/{stem}{kind}")
        print(f"      present : {', '.join(e for e in required if e in provided) or '(none)'}")
        if missing:
            print(f"      MISSING : {', '.join(missing)}")
        if combined:
            print(f"      bonus   : {', '.join('.'.join(sorted(t)) for _f, t in combined)}")

    print()
    print(f"Scanned {len(sets)} art set(s): {managed} variant-managed, {len(singles)} single.")
    if args.all and singles:
        print("Single (non-variant) art:")
        for s in singles:
            print(f"  - {s}")
    if total_missing:
        print(f"{total_missing} required edition(s) missing across variant-managed sets.")

    return 1 if (args.strict and total_missing) else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
