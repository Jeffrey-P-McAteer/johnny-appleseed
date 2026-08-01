#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = []
# ///
"""
Johnny Appleseed - build-time GIMP (.xcf) -> PNG/JPG exporter.

Rasterises every `*.xcf` under graphics/ into game-loadable images that the
csproj then embeds (key "graphics/<path>.png" / ".jpg"). The raw `.xcf` source
is NOT shipped (the csproj Removes it from the embed glob) - these generated
files are. So `Assets.LoadImage("graphics/icon.png")` transparently receives the
rendered copy of `graphics/icon.xcf`; no game code changes.

Each source yields BOTH outputs:
    <name>.png  -> the composited image WITH its alpha channel preserved.
    <name>.jpg  -> the same image flattened onto a solid background (JPEG has no
                  alpha), for callers that want an opaque copy.

Why GIMP (not a pure-Python parser like _dmg.py / _icons.py):
    XCF is GIMP's native, versioned, layered format; faithfully compositing it
    (layer modes, masks, precisions) is exactly what GIMP does and what a hand-
    rolled reader gets subtly wrong. GIMP renders it in one batch process, so we
    shell out to it - the same "use the real tool for its own format" choice
    scripts/aseprite-export.py makes for .aseprite. The binary is found via the
    GIMP_BIN env var or `gimp` on PATH.

BEST-EFFORT (CLI): if GIMP isn't installed, this prints a notice and exits 0 so a
plain `dotnet build` still succeeds (just without the generated art); the runtime
icon load is Exists()-gated. A render that actually *fails* under an installed
GIMP is a hard error (exit 1).

Usage
-----
    uv run scripts/_xcf.py --graphics-dir graphics --outdir obj/xcf
    uv run scripts/_xcf.py --force                       # re-export even if fresh
    uv run scripts/_xcf.py a.xcf b.xcf --outdir /tmp/art  # explicit files

Incremental: a file is skipped when both outputs already exist and are newer than
the source (override with --force).
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

# JPEG has no alpha; transparent pixels are flattened onto this colour. White
# suits an app icon on a light backdrop. (r, g, b), 0-255.
JPG_BACKGROUND = (255, 255, 255)


def note(msg: str) -> None:
    print(f"[xcf-export] {msg}", file=sys.stderr)


def find_gimp() -> str | None:
    """Locate the GIMP CLI: $GIMP_BIN, then common console/regular names."""
    env = os.environ.get("GIMP_BIN")
    if env:
        return env if (os.path.isfile(env) or shutil.which(env)) else None
    for name in ("gimp", "gimp-console", "gimp-console-3.0", "gimp-2.10",
                 "gimp-console-2.10"):
        if shutil.which(name):
            return name
    return None


def _scm_str(path: str | Path) -> str:
    """Quote a filesystem path as a Script-Fu string literal (escape \\ and \")."""
    s = str(path).replace("\\", "\\\\").replace('"', '\\"')
    return f'"{s}"'


def _job_script(src: Path, png_out: Path | None, jpg_out: Path | None) -> str:
    """Script-Fu to load one .xcf and export the requested formats.

    PNG is saved first so it keeps the composited alpha; the JPEG is written after
    a flatten (which fills transparency with the image's background colour, set to
    JPG_BACKGROUND below) since JPEG cannot store alpha. gimp-file-save picks the
    format from the file extension and exports the full visible composite.
    """
    r, g, b = JPG_BACKGROUND
    lines = [f"(let* ((image (car (gimp-file-load RUN-NONINTERACTIVE {_scm_str(src)} {_scm_str(src.name)}))))"]
    if png_out is not None:
        lines.append(f"  (gimp-file-save RUN-NONINTERACTIVE image {_scm_str(png_out)} {_scm_str(png_out.name)})")
    if jpg_out is not None:
        lines.append(f"  (gimp-context-set-background '({r} {g} {b}))")
        lines.append("  (gimp-image-flatten image)")
        lines.append(f"  (gimp-file-save RUN-NONINTERACTIVE image {_scm_str(jpg_out)} {_scm_str(jpg_out.name)})")
    lines.append("  (gimp-image-delete image))")
    return "\n".join(lines)


def _run_gimp(script: str, gimp: str | None = None, timeout: int = 600) -> None:
    gimp = gimp or find_gimp()
    if gimp is None:
        raise FileNotFoundError("GIMP not found (set GIMP_BIN or install `gimp`)")
    cmd = [
        gimp, "-i", "-d", "-f",
        "--batch-interpreter=plug-in-script-fu-eval",
        "-b", script,
        "-b", "(gimp-quit 0)",
    ]
    proc = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    # GIMP is chatty on stderr (missing optional GEGL codecs, plugin teardown
    # "unexpected EOF"); those are harmless. A real Script-Fu failure prints
    # "batch command experienced an execution error" and returns non-zero.
    if proc.returncode != 0 or "execution error" in (proc.stderr + proc.stdout):
        detail = "\n".join(
            l for l in (proc.stderr + proc.stdout).splitlines()
            if "error" in l.lower() and "GEGL" not in l and "dlopen" not in l
        )
        raise RuntimeError(detail.strip() or f"gimp exited {proc.returncode}")


def export(jobs: list[tuple[Path, Path | None, Path | None]],
           gimp: str | None = None) -> None:
    """Render a batch of (src_xcf, png_out|None, jpg_out|None) in ONE GIMP process.

    Amortises GIMP's multi-second startup across every source. Output directories
    are created as needed. Raises on the first failing render.
    """
    gimp = gimp or find_gimp()
    if gimp is None:
        raise FileNotFoundError("GIMP not found (set GIMP_BIN or install `gimp`)")
    for _, png_out, jpg_out in jobs:
        for out in (png_out, jpg_out):
            if out is not None:
                out.parent.mkdir(parents=True, exist_ok=True)
    script = "\n".join(_job_script(src, p, j) for src, p, j in jobs)
    _run_gimp(script, gimp)


def to_image(xcf: str | Path, gimp: str | None = None):
    """Render an .xcf to a PIL RGBA Image (composited, alpha preserved).

    Used by the icon pipeline (_icons.py). Pillow is imported lazily so this
    module stays import-safe for callers that only use the file-to-file `export`.
    """
    from PIL import Image
    with tempfile.TemporaryDirectory() as tmp:
        tmp_png = Path(tmp) / "render.png"
        export([(Path(xcf), tmp_png, None)], gimp=gimp)
        return Image.open(tmp_png).convert("RGBA")


# -- CLI -------------------------------------------------------------------------

def _is_fresh(src: Path, outs: list[Path]) -> bool:
    return all(o.exists() for o in outs) and \
        all(o.stat().st_mtime >= src.stat().st_mtime for o in outs)


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Export graphics/**/*.xcf -> embeddable .png/.jpg via GIMP.",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("files", nargs="*", type=Path,
                    help="Explicit .xcf files (default: scan --graphics-dir).")
    ap.add_argument("--graphics-dir", type=Path, default=REPO_ROOT / "graphics",
                    help="Root to scan for *.xcf (default: ./graphics).")
    ap.add_argument("--outdir", type=Path, default=REPO_ROOT / "obj" / "xcf",
                    help="Where to write generated images (mirrors the source tree).")
    ap.add_argument("--force", action="store_true",
                    help="Re-export even when outputs are newer than the source.")
    args = ap.parse_args()

    if args.files:
        sources = [(f, f.parent) for f in args.files]
    else:
        if not args.graphics_dir.exists():
            note(f"no graphics dir: {args.graphics_dir} - nothing to do.")
            return 0
        sources = [(f, args.graphics_dir)
                   for f in sorted(args.graphics_dir.rglob("*.xcf"))]

    if not sources:
        note("no .xcf files found - nothing to do.")
        return 0

    gimp = find_gimp()
    if gimp is None:
        note("GIMP not found (set GIMP_BIN or install `gimp`); skipping .xcf export.")
        return 0  # best-effort: absence of GIMP is not a build failure

    jobs: list[tuple[Path, Path | None, Path | None]] = []
    skipped = 0
    for src, base in sources:
        try:
            rel = src.relative_to(base)
        except ValueError:
            rel = Path(src.name)
        out_base = args.outdir / rel.with_suffix("")
        # Append rather than with_suffix() so variant tags in the stem
        # (e.g. "backdrop.rainy") survive instead of being clipped as a suffix.
        png_out = out_base.with_name(out_base.name + ".png")
        jpg_out = out_base.with_name(out_base.name + ".jpg")
        if not args.force and _is_fresh(src, [png_out, jpg_out]):
            print(f"  OK up-to-date: {src.name}  -> .png, .jpg")
            skipped += 1
            continue
        jobs.append((src, png_out, jpg_out))

    if not jobs:
        print(f"[xcf-export] done: 0 exported, {skipped} up-to-date.")
        return 0

    print(f"[xcf-export] {len(jobs)} file(s) -> {args.outdir}  (via {Path(gimp).name})")
    try:
        export(jobs, gimp=gimp)
    except Exception as exc:
        note(f"ERROR exporting .xcf: {exc}")
        return 1

    for src, png_out, jpg_out in jobs:
        if not (png_out.exists() and jpg_out.exists()):
            note(f"ERROR: expected outputs missing for {src.name}")
            return 1
        print(f"  OK {src.name}  -> {png_out.name}, {jpg_out.name}")

    print(f"[xcf-export] done: {len(jobs)} exported, {skipped} up-to-date.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
