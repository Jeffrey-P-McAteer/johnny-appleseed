#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = [
#   "pillow>=10.0",
# ]
# ///
"""
Johnny Appleseed - icon builder.

Turns a source image into the raster formats each platform's build pipeline needs
to embed an application / window icon:

    icon.png    -> embedded resource, used at runtime via Raylib.SetWindowIcon
                  (window title bar on Windows + X11)
    icon.ico    -> <ApplicationIcon> - stamped into the Windows apphost .exe
    AppIcon.icns-> copied into JohnnyAppleseed.app/Contents/Resources (Finder/Dock)

Accepted sources (see load_source):
    .svg  - rasterised by the small built-in vector renderer below.
    .xcf  - composited by GIMP via the sibling _xcf.py (the project's icon source
            of truth is graphics/icon.xcf).
    .png/.jpg/... - loaded directly with Pillow.

Design notes
------------
Raylib cannot load SVG/XCF, so the source must be rasterised ahead of time. For
SVG, rather than pull in a heavy native stack (cairo/librsvg) this module ships a
small, dependency-free rasteriser for the *linear* path subset
(M/m L/l H/h V/v Z/z) and fills the polygons with Pillow. For XCF it defers to
GIMP (the real tool for its own format, mirroring scripts/aseprite-export.py).

CLI:
    uv run scripts/_icons.py graphics/icon.xcf \
        --png obj/icon.png --png-size 256 --ico obj/icon.ico --icns obj/AppIcon.icns
"""

from __future__ import annotations

import argparse
import io
import re
import struct
import xml.etree.ElementTree as ET
from pathlib import Path

# -- SVG parsing -----------------------------------------------------------------

_NUM = re.compile(r"[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?")
_CMDS = "MmLlHhVvZzCcSsQqTtAa"

_NAMED_COLORS = {
    "black": (0, 0, 0),
    "white": (255, 255, 255),
    "red":   (255, 0, 0),
    "none":  None,
}


def _parse_color(value: str | None):
    """Return an (r, g, b) tuple, or None for 'none' / unset."""
    s = (value or "black").strip().lower()
    if s in _NAMED_COLORS:
        return _NAMED_COLORS[s]
    if s.startswith("#"):
        h = s[1:]
        if len(h) == 3:
            h = "".join(c * 2 for c in h)
        if len(h) >= 6:
            return int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16)
    # Unknown colour keyword - fall back to opaque black rather than crash.
    return (0, 0, 0)


def _tokenize(d: str):
    """Split a path 'd' string into command letters (str) and numbers (float)."""
    out: list = []
    pos, n = 0, len(d)
    while pos < n:
        ch = d[pos]
        if ch in _CMDS:
            out.append(ch)
            pos += 1
        elif ch in " ,\t\r\n":
            pos += 1
        else:
            m = _NUM.match(d, pos)
            if not m:
                pos += 1
                continue
            out.append(float(m.group()))
            pos = m.end()
    return out


def _parse_path(d: str) -> list[list[tuple[float, float]]]:
    """
    Parse the linear subset of an SVG path into a list of subpaths, each a list
    of (x, y) points.  Curve commands (C/S/Q/T/A) are approximated by a straight
    line to their final coordinate so a richer SVG still degrades gracefully
    rather than crashing.
    """
    toks = _tokenize(d)
    subs: list[list[tuple[float, float]]] = []
    sub: list[tuple[float, float]] = []
    cur = (0.0, 0.0)
    start = (0.0, 0.0)
    i = 0
    cmd: str | None = None

    def take() -> float:
        nonlocal i
        v = toks[i]
        i += 1
        return float(v)

    while i < len(toks):
        t = toks[i]
        if isinstance(t, str):
            cmd = t
            i += 1

        if cmd in ("M", "m"):
            x, y = take(), take()
            if cmd == "m" and sub:
                x, y = x + cur[0], y + cur[1]
            if sub:
                subs.append(sub)
            cur = start = (x, y)
            sub = [cur]
            cmd = "L" if cmd == "M" else "l"  # subsequent pairs are implicit linetos
        elif cmd in ("L", "l"):
            x, y = take(), take()
            if cmd == "l":
                x, y = x + cur[0], y + cur[1]
            cur = (x, y)
            sub.append(cur)
        elif cmd in ("H", "h"):
            x = take()
            if cmd == "h":
                x += cur[0]
            cur = (x, cur[1])
            sub.append(cur)
        elif cmd in ("V", "v"):
            y = take()
            if cmd == "v":
                y += cur[1]
            cur = (cur[0], y)
            sub.append(cur)
        elif cmd in ("Z", "z"):
            if sub:
                sub.append(start)
                subs.append(sub)
                sub = []
            cur = start
        else:
            # Unsupported command (curve/arc): best-effort skip of one number so
            # we can't loop forever, then continue.
            if i < len(toks) and not isinstance(toks[i], str):
                take()

    if sub:
        subs.append(sub)
    return subs


def load_svg(path: str | Path):
    """Return (paths, (min_x, min_y, width, height)) in document (paint) order."""
    root = ET.parse(str(path)).getroot()

    vb = root.get("viewBox")
    if vb:
        min_x, min_y, w, h = (float(v) for v in re.split(r"[ ,]+", vb.strip()))
    else:
        min_x = min_y = 0.0
        w = float(root.get("width", 256))
        h = float(root.get("height", 256))

    paths = []
    for el in root.iter():  # depth-first == SVG paint order
        if el.tag.split("}")[-1] != "path":
            continue
        d = el.get("d")
        if not d:
            continue
        paths.append((_parse_color(el.get("fill")), _parse_path(d)))

    return paths, (min_x, min_y, w, h)


# -- rasterisation ---------------------------------------------------------------

def rasterize(paths, viewbox, size: int):
    """Render parsed paths into a `size`x`size` RGBA Pillow image, aspect-preserved."""
    from PIL import Image, ImageDraw

    min_x, min_y, w, h = viewbox
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    scale = size / max(w, h)
    off_x = (size - w * scale) / 2 - min_x * scale
    off_y = (size - h * scale) / 2 - min_y * scale

    for fill, subpaths in paths:
        if fill is None:
            continue
        color = (*fill, 255)
        for sub in subpaths:
            if len(sub) < 2:
                continue
            pts = [(x * scale + off_x, y * scale + off_y) for x, y in sub]
            if len(pts) == 2:
                draw.line(pts, fill=color)
            else:
                draw.polygon(pts, fill=color)  # aliased fill -> crisp pixel edges

    return img


def _fit(img, size: int):
    """Aspect-preserve `img` into a transparent `size`x`size` RGBA canvas,
    centred - the same framing the SVG rasteriser produces, so raster and vector
    sources yield interchangeable icons."""
    from PIL import Image

    img = img.convert("RGBA")
    w, h = img.size
    scale = size / max(w, h)
    nw, nh = max(1, round(w * scale)), max(1, round(h * scale))
    resized = img.resize((nw, nh), Image.LANCZOS)
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.paste(resized, ((size - nw) // 2, (size - nh) // 2), resized)
    return canvas


def load_source(src: str | Path):
    """Return a `render(size) -> RGBA Image` function for a source of any kind.

    - `.svg`  : rasterised per size from the vector paths (crisp at every size).
    - `.xcf`  : composited once by GIMP (via _xcf.py), then scaled per size.
    - other   : loaded once with Pillow (`.png`/`.jpg`/...), then scaled per size.

    So the icon pipeline works unchanged whether the source of truth is an SVG,
    a GIMP document, or a plain raster.
    """
    p = str(src).lower()
    if p.endswith(".svg"):
        paths, viewbox = load_svg(src)
        return lambda size: rasterize(paths, viewbox, size)

    if p.endswith(".xcf"):
        import _xcf  # sibling module; GIMP-backed .xcf compositor
        base = _xcf.to_image(src)
    else:
        from PIL import Image
        base = Image.open(src).convert("RGBA")
    return lambda size: _fit(base, size)


def render(src: str | Path, size: int):
    """Render any supported source to a single `size`x`size` RGBA image."""
    return load_source(src)(size)


def _png_bytes(img) -> bytes:
    buf = io.BytesIO()
    img.save(buf, "PNG")
    return buf.getvalue()


# -- format writers --------------------------------------------------------------

def write_png(src: str | Path, out: Path, size: int = 256) -> Path:
    out.parent.mkdir(parents=True, exist_ok=True)
    load_source(src)(size).save(out, "PNG")
    return out


def write_ico(src: str | Path, out: Path,
              sizes=(16, 32, 48, 64, 128, 256)) -> Path:
    """Multi-resolution Windows .ico, embedded into the apphost via ApplicationIcon."""
    out.parent.mkdir(parents=True, exist_ok=True)
    base = load_source(src)(max(sizes))
    base.save(out, format="ICO", sizes=[(s, s) for s in sizes])
    return out


def write_icns(src: str | Path, out: Path) -> Path:
    """
    Hand-rolled Apple .icns with PNG-encoded entries at several sizes.

    macOS 10.7+ accepts PNG data for these OSTypes.  Writing the container
    directly (rather than via Pillow's ICNS encoder) keeps every size crisp and
    avoids encoder-version quirks - same philosophy as scripts/_dmg.py.
    """
    entries = [
        (b"icp4", 16), (b"icp5", 32), (b"ic07", 128),
        (b"ic08", 256), (b"ic09", 512), (b"ic10", 1024),
    ]
    src_fn = load_source(src)
    body = b""
    for ostype, sz in entries:
        png = _png_bytes(src_fn(sz))
        body += ostype + struct.pack(">I", len(png) + 8) + png

    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(b"icns" + struct.pack(">I", len(body) + 8) + body)
    return out


# -- CLI -------------------------------------------------------------------------

def main() -> None:
    ap = argparse.ArgumentParser(description="Render a source image into icon formats")
    ap.add_argument("source", help="source image path (.svg / .xcf / .png / .jpg ...)")
    ap.add_argument("--png", help="write a PNG here")
    ap.add_argument("--png-size", type=int, default=256, help="PNG edge length (px)")
    ap.add_argument("--ico", help="write a Windows .ico here")
    ap.add_argument("--icns", help="write a macOS .icns here")
    args = ap.parse_args()

    if args.png:
        print(f"  icon -> {write_png(args.source, Path(args.png), args.png_size)}")
    if args.ico:
        print(f"  icon -> {write_ico(args.source, Path(args.ico))}")
    if args.icns:
        print(f"  icon -> {write_icns(args.source, Path(args.icns))}")


if __name__ == "__main__":
    main()
