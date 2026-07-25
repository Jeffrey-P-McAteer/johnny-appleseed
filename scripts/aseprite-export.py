#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = []
# ///
"""
Johnny Appleseed — build-time Aseprite → PNG/GIF exporter.

Rasterises every `*.aseprite` under graphics/ into game-loadable images that the
csproj then embeds (key "graphics/<path>.png" / ".gif"). The raw `.aseprite`
source is NOT shipped (the csproj Removes it from the embed glob) — these
generated files are.

Frame-count rule (read straight from the documented .aseprite header, no tool
needed just to branch):
    • 1 frame   → export BOTH .png and .gif (a static image); the runtime picks
                  whichever suits the moment (crisp PNG vs. GIF path).
    • >1 frame  → export ONLY .gif (an animated sprite); a single flat .png can't
                  represent multiple frames.

The actual raster export needs the real Aseprite binary (proprietary format), which
scripts/run-aseprite.py builds under build/aseprite/. This is BEST-EFFORT by
default: if that binary isn't built yet, we print a notice and exit 0 so a normal
`dotnet build` never blocks on a 20-minute Aseprite compile. Provision it once with
`uv run scripts/run-aseprite.py --no-run` (or pass --ensure-build here) and art
regenerates automatically on every build thereafter.

Usage
─────
    uv run scripts/aseprite-export.py --graphics-dir graphics --outdir obj/aseprite
    uv run scripts/aseprite-export.py --ensure-build      # build Aseprite if missing, then export
    uv run scripts/aseprite-export.py --force             # re-export even if outputs are fresh
    uv run scripts/aseprite-export.py a.aseprite b.aseprite --outdir /tmp/art   # explicit files

Incremental: a file is skipped when all its expected outputs already exist and are
newer than the source (override with --force).
"""

from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

REPO_ROOT    = Path(__file__).resolve().parent.parent
# Same location scripts/run-aseprite.py compiles the editor to.
ASEPRITE_BIN = REPO_ROOT / "build" / "aseprite" / "build" / "bin" / "aseprite"
RUN_ASEPRITE = REPO_ROOT / "scripts" / "run-aseprite.py"

ASE_MAGIC = 0xA5E0  # WORD at header offset 4 (little-endian)


def note(msg: str) -> None:
    print(f"[aseprite-export] {msg}", file=sys.stderr)


def frame_count(path: Path) -> int | None:
    """Frames from the .aseprite header (DWORD size, WORD 0xA5E0 magic, WORD frames)."""
    try:
        head = path.read_bytes()[:8]
    except OSError:
        return None
    if len(head) < 8 or int.from_bytes(head[4:6], "little") != ASE_MAGIC:
        return None
    return int.from_bytes(head[6:8], "little")


def outputs_for(frames: int) -> list[str]:
    """Extensions to produce for a file with `frames` frames."""
    return [".gif"] if frames > 1 else [".png", ".gif"]


def is_fresh(src: Path, outs: list[Path]) -> bool:
    """True if every output exists and is at least as new as the source."""
    return all(o.exists() for o in outs) and \
        all(o.stat().st_mtime >= src.stat().st_mtime for o in outs)


def ensure_binary(ensure_build: bool) -> Path | None:
    if ASEPRITE_BIN.exists():
        return ASEPRITE_BIN
    if not ensure_build:
        note(f"Aseprite binary not built ({ASEPRITE_BIN.relative_to(REPO_ROOT)}); "
             "skipping art export.")
        note("Build it once with:  uv run scripts/run-aseprite.py --no-run")
        return None
    note("Aseprite binary missing — building it (uv run scripts/run-aseprite.py --no-run) …")
    if subprocess.run(["uv", "run", str(RUN_ASEPRITE), "--no-run"]).returncode != 0 \
            or not ASEPRITE_BIN.exists():
        note("ERROR: Aseprite build failed.")
        return None
    return ASEPRITE_BIN


def export_one(binary: Path, src: Path, out_base: Path, force: bool) -> tuple[int, bool]:
    """Export one file. Returns (frames, produced_something). Raises on aseprite failure."""
    frames = frame_count(src)
    if frames is None:
        note(f"skip (not a valid .aseprite): {src}")
        return 0, False
    outs = [out_base.with_suffix(ext) for ext in outputs_for(frames)]

    if not force and is_fresh(src, outs):
        print(f"  ✓ up-to-date: {src.name}  ({frames} frame{'s' if frames != 1 else ''} "
              f"→ {', '.join(o.suffix for o in outs)})")
        return frames, False

    out_base.parent.mkdir(parents=True, exist_ok=True)
    # One batch invocation writes every target (aseprite allows repeated --save-as).
    cmd = [str(binary), "-b", str(src)]
    for o in outs:
        cmd += ["--save-as", str(o)]
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0 or not all(o.exists() for o in outs):
        note(f"ERROR exporting {src.name}: {result.stderr.strip() or result.stdout.strip()}")
        raise RuntimeError(f"aseprite export failed for {src}")

    kind = "animated" if frames > 1 else "static"
    print(f"  ✓ {src.name}  → {kind}: {', '.join(o.name for o in outs)}")
    return frames, True


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Export graphics/**/*.aseprite → embeddable .png/.gif.",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("files", nargs="*", type=Path,
                    help="Explicit .aseprite files (default: scan --graphics-dir).")
    ap.add_argument("--graphics-dir", type=Path, default=REPO_ROOT / "graphics",
                    help="Root to scan for *.aseprite (default: ./graphics).")
    ap.add_argument("--outdir", type=Path, default=REPO_ROOT / "obj" / "aseprite",
                    help="Where to write generated images (mirrors the source tree).")
    ap.add_argument("--ensure-build", action="store_true",
                    help="Build Aseprite via run-aseprite.py if it isn't built yet.")
    ap.add_argument("--force", action="store_true",
                    help="Re-export even when outputs are newer than the source.")
    args = ap.parse_args()

    # Collect sources and the base dir their relative paths are computed against.
    if args.files:
        sources = [(f, f.parent) for f in args.files]
    else:
        if not args.graphics_dir.exists():
            note(f"no graphics dir: {args.graphics_dir} — nothing to do.")
            return 0
        sources = [(f, args.graphics_dir)
                   for f in sorted(args.graphics_dir.rglob("*.aseprite"))]

    if not sources:
        note("no .aseprite files found — nothing to do.")
        return 0

    binary = ensure_binary(args.ensure_build)
    if binary is None:
        return 0  # best-effort: absence of the editor is not a build failure

    print(f"[aseprite-export] {len(sources)} file(s) → {args.outdir}")
    exported = skipped = 0
    for src, base in sources:
        try:
            rel = src.relative_to(base)
        except ValueError:
            rel = Path(src.name)
        out_base = args.outdir / rel.with_suffix("")
        try:
            _, produced = export_one(binary, src, out_base, args.force)
        except RuntimeError:
            return 1
        exported += produced
        skipped += (not produced)

    print(f"[aseprite-export] done: {exported} exported, {skipped} up-to-date.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
