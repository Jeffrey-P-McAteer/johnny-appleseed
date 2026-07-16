#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = [
#   "pillow>=10.0",
#   "ds-store>=1.3",
#   "mac-alias>=2.2",
# ]
# ///
"""
Johnny Appleseed — cross-platform packaging script.

Usage (from repo root):
    uv run scripts/package.py                     # all targets
    uv run scripts/package.py windows-x64         # specific target
    uv run scripts/package.py --skip-download      # skip arm64 native-lib download

Outputs:
    dist/windows-x64/windows-x64.zip
    dist/windows-arm64/windows-arm64.zip
    dist/linux-x64/linux-x64
    dist/linux-arm64/linux-arm64
    dist/macos-x64/macos-x64.dmg
    dist/macos-arm64/macos-arm64.dmg
"""

from __future__ import annotations

import argparse
import os
import platform
import plistlib
import shutil
import stat
import struct
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
import zipfile
from pathlib import Path

# ── configuration ─────────────────────────────────────────────────────────────

REPO_ROOT   = Path(__file__).resolve().parent.parent
PROJECT_CS  = REPO_ROOT / "src" / "JohnnyAppleseed" / "JohnnyAppleseed.csproj"
DIST_DIR    = REPO_ROOT / "dist"
NATIVE_DIR  = REPO_ROOT / "src" / "JohnnyAppleseed" / "runtimes"

APP_NAME      = "JohnnyAppleseed"
APP_ID        = "com.johnnyseed.game"
APP_VERSION   = "1.0.0"

# Raylib 6.0 GitHub release base URL for missing native-lib downloads
RAYLIB_RELEASE_BASE = "https://github.com/raysan5/raylib/releases/download/5.5"

# Map: (target_os, arch) → .NET RID
TARGETS: dict[tuple[str, str], str] = {
    ("windows", "x64"):   "win-x64",
    ("windows", "arm64"): "win-arm64",
    ("linux",   "x64"):   "linux-x64",
    ("linux",   "arm64"): "linux-arm64",
    ("macos",   "x64"):   "osx-x64",
    ("macos",   "arm64"): "osx-arm64",
}

# ── native-lib bootstrap for RIDs missing from the Raylib-cs NuGet package ───

def ensure_native_lib(target_os: str, arch: str, skip_download: bool) -> bool:
    """
    Return True if the native lib for this RID is available (either already
    bundled by the NuGet package or downloaded into the runtimes/ tree).
    """
    if target_os == "linux" and arch == "arm64":
        dest = NATIVE_DIR / "linux-arm64" / "native" / "libraylib.so"
        if dest.exists():
            return True
        if skip_download:
            print(f"  [warn] {dest} not found; skipping (use --skip-download=false to attempt download)")
            return False
        return download_linux_arm64_raylib(dest)

    if target_os == "windows" and arch == "arm64":
        dest = NATIVE_DIR / "win-arm64" / "native" / "raylib.dll"
        if dest.exists():
            return True
        if skip_download:
            print(f"  [warn] {dest} not found; win-arm64 requires manual Raylib 6.0 ARM64 cross-compilation")
            return False
        return download_win_arm64_raylib(dest)

    # All other targets are bundled in the Raylib-cs NuGet package
    return True


def download_linux_arm64_raylib(dest: Path) -> bool:
    """Download the linux-arm64 (aarch64) libraylib.so from Raylib releases."""
    url = f"{RAYLIB_RELEASE_BASE}/raylib-5.5_linux_aarch64.tar.gz"
    print(f"  Downloading linux-arm64 libraylib.so from {url} …")
    dest.parent.mkdir(parents=True, exist_ok=True)
    try:
        with tempfile.NamedTemporaryFile(suffix=".tar.gz", delete=False) as tmp:
            urllib.request.urlretrieve(url, tmp.name)
            with tarfile.open(tmp.name, "r:gz") as tf:
                for member in tf.getmembers():
                    if member.name.endswith("libraylib.so") or member.name.endswith("libraylib.so.500"):
                        member.name = "libraylib.so"
                        tf.extract(member, dest.parent)
                        print(f"  Extracted libraylib.so → {dest}")
                        return True
        print(f"  [warn] libraylib.so not found inside the archive; skipping linux-arm64")
        return False
    except Exception as e:
        print(f"  [warn] download failed ({e}); skipping linux-arm64")
        return False


def download_win_arm64_raylib(dest: Path) -> bool:
    """
    Windows ARM64 Raylib requires MSVC cross-compilation; no pre-built binary
    is available in the official releases. We skip gracefully here.
    """
    print("  [warn] win-arm64 raylib.dll must be cross-compiled with MSVC.")
    print("  Place it at:", dest)
    return False


# ── dotnet publish ─────────────────────────────────────────────────────────────

def publish(rid: str, output_dir: Path) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    cmd = [
        "dotnet", "publish", str(PROJECT_CS),
        "-c", "Release",
        "-r", rid,
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:SuppressTrimAnalysisWarnings=true",
        "-o", str(output_dir),
        "--nologo",
    ]
    print(f"  dotnet publish -r {rid} …")
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        print(result.stdout[-3000:] if result.stdout else "")
        print(result.stderr[-3000:] if result.stderr else "", file=sys.stderr)
        raise RuntimeError(f"dotnet publish failed for {rid}")


# ── packagers ─────────────────────────────────────────────────────────────────

def package_windows(target_name: str, rid: str) -> None:
    """Build a .zip containing the single-file .exe."""
    dist_target = DIST_DIR / target_name
    dist_target.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory() as tmp:
        build_out = Path(tmp) / "build"
        publish(rid, build_out)

        exe = build_out / f"{APP_NAME}.exe"
        if not exe.exists():
            exe = next(build_out.glob("*.exe"), None)
            if exe is None:
                raise FileNotFoundError(f"No .exe found in {build_out}")

        zip_path = dist_target / f"{target_name}.zip"
        with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
            zf.write(exe, exe.name)

        print(f"  → {zip_path}")


def package_linux(target_name: str, rid: str) -> None:
    """Copy the single-file binary directly to dist/."""
    dist_target = DIST_DIR / target_name
    dist_target.mkdir(parents=True, exist_ok=True)
    out_bin = dist_target / target_name

    with tempfile.TemporaryDirectory() as tmp:
        build_out = Path(tmp) / "build"
        publish(rid, build_out)

        binary = build_out / APP_NAME
        if not binary.exists():
            raise FileNotFoundError(f"No binary found at {binary}")

        shutil.copy2(binary, out_bin)
        out_bin.chmod(out_bin.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

    print(f"  → {out_bin}")


def package_macos(target_name: str, rid: str) -> None:
    """Build a .dmg with a custom background and an Applications symlink."""
    dist_target = DIST_DIR / target_name
    dist_target.mkdir(parents=True, exist_ok=True)
    dmg_path = dist_target / f"{target_name}.dmg"

    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        build_out = tmp_path / "build"
        publish(rid, build_out)

        binary = build_out / APP_NAME
        if not binary.exists():
            raise FileNotFoundError(f"No binary found at {binary}")

        # ── .app bundle ───────────────────────────────────────────────────────
        app_bundle = tmp_path / f"{APP_NAME}.app"
        macos_dir  = app_bundle / "Contents" / "MacOS"
        res_dir    = app_bundle / "Contents" / "Resources"
        macos_dir.mkdir(parents=True)
        res_dir.mkdir(parents=True)

        # Copy binary
        app_binary = macos_dir / APP_NAME
        shutil.copy2(binary, app_binary)
        app_binary.chmod(app_binary.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

        # Info.plist
        plist = {
            "CFBundleName":             APP_NAME,
            "CFBundleDisplayName":      "Johnny Appleseed",
            "CFBundleExecutable":       APP_NAME,
            "CFBundleIdentifier":       APP_ID,
            "CFBundleVersion":          APP_VERSION,
            "CFBundleShortVersionString": APP_VERSION,
            "CFBundlePackageType":      "APPL",
            "CFBundleSignature":        "????",
            "NSHighResolutionCapable":  True,
            "NSPrincipalClass":         "NSApplication",
            "LSMinimumSystemVersion":   "10.15",
        }
        with open(app_bundle / "Contents" / "Info.plist", "wb") as f:
            plistlib.dump(plist, f)

        # ── DMG staging area ─────────────────────────────────────────────────
        staging = tmp_path / "staging"
        staging.mkdir()

        # Copy the .app
        shutil.copytree(app_bundle, staging / f"{APP_NAME}.app")

        # Symlink to /Applications
        (staging / "Applications").symlink_to("/Applications")

        # Background image in a hidden folder (Finder convention)
        bg_dir = staging / ".background"
        bg_dir.mkdir()
        bg_image = bg_dir / "background.png"
        create_dmg_background(bg_image, target_name)

        # .DS_Store for Finder window layout
        write_ds_store(staging / ".DS_Store", bg_image_relative=".background/background.png")

        # ── create the DMG ───────────────────────────────────────────────────
        create_dmg(staging, dmg_path, label=APP_NAME)

    print(f"  → {dmg_path}")


# ── DMG creation helpers ──────────────────────────────────────────────────────

def create_dmg(staging: Path, output: Path, label: str) -> None:
    """
    Create a DMG from a staging directory.
    On Linux we use genisoimage (HFS+ hybrid ISO) because hdiutil is macOS-only.
    On macOS, hdiutil is preferred for a proper compressed UDIF image.
    """
    if platform.system() == "Darwin":
        _create_dmg_hdiutil(staging, output, label)
    else:
        _create_dmg_genisoimage(staging, output, label)


def _create_dmg_hdiutil(staging: Path, output: Path, label: str) -> None:
    with tempfile.NamedTemporaryFile(suffix=".dmg", delete=False) as tmp:
        rw_dmg = tmp.name

    size_mb = max(256, sum(f.stat().st_size for f in staging.rglob("*") if f.is_file()) // (1024 * 1024) + 80)

    subprocess.run([
        "hdiutil", "create",
        "-srcfolder", str(staging),
        "-volname", label,
        "-fs", "HFS+",
        "-fsargs", "-c c=64,a=16,b=16",
        "-format", "UDRW",
        "-size", f"{size_mb}m",
        rw_dmg,
    ], check=True, capture_output=True)

    if output.exists():
        output.unlink()
    subprocess.run([
        "hdiutil", "convert", rw_dmg,
        "-format", "UDZO",
        "-imagekey", "zlib-level=9",
        "-o", str(output),
    ], check=True, capture_output=True)

    os.unlink(rw_dmg)


def _create_dmg_genisoimage(staging: Path, output: Path, label: str) -> None:
    tool = shutil.which("genisoimage") or shutil.which("mkisofs")
    if tool is not None:
        subprocess.run([
            tool,
            "-V", label[:32],
            "-D", "-r",
            "--hfs", "--hfs-volid", label[:27],
            "--mac-name", "-no-pad", "-apple", "-probe",
            "-o", str(output),
            str(staging),
        ], check=True, capture_output=True)
        return

    # Fallback: ZIP containing the .app bundle (not a real DMG but portable).
    # We only include the .app tree — the Applications symlink is only useful
    # inside a Finder-opened DMG, not inside a ZIP.
    zip_output = output.with_suffix(".zip")
    app_root = staging / f"{APP_NAME}.app"
    with zipfile.ZipFile(zip_output, "w", zipfile.ZIP_DEFLATED) as zf:
        for f in sorted(app_root.rglob("*")):
            if f.is_symlink() or not f.is_file():
                continue
            arc = f.relative_to(staging)
            zf.write(f, arc)
    # Rename to .dmg so the output path is consistent with the other targets
    zip_output.rename(output)
    print(
        "  [note] genisoimage/mkisofs not found — produced a ZIP-format .dmg\n"
        "         For a proper HFS+ disk image run: sudo apt install genisoimage\n"
        "         or build on macOS (the script uses hdiutil there automatically)"
    )


# ── DMG background image ──────────────────────────────────────────────────────

def create_dmg_background(dest: Path, target_name: str) -> None:
    """Generate a 540×380 background PNG with drag-to-Applications instructions."""
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError:
        # Pillow unavailable — write a 1×1 transparent placeholder
        dest.write_bytes(_minimal_png())
        return

    W, H = 540, 380
    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # Gradient background (dark blue → deep purple)
    for y in range(H):
        t = y / H
        r = int(8  + t * 12)
        g = int(8  + t * 4)
        b = int(30 + t * 30)
        draw.line([(0, y), (W, y)], fill=(r, g, b, 255))

    # Round-rect panel border
    draw.rounded_rectangle([(10, 10), (W - 10, H - 10)],
                            radius=18, outline=(255, 210, 80, 60), width=1)

    # Title
    font_big = _get_font(size=28)
    font_med = _get_font(size=16)
    font_sml = _get_font(size=13)

    title = "JOHNNY APPLESEED"
    draw.text((W // 2, 52), title, fill=(255, 210, 80, 255),
              font=font_big, anchor="mm")
    draw.line([(W // 2 - 120, 72), (W // 2 + 120, 72)],
              fill=(255, 210, 80, 60), width=1)

    # .app box (left)
    app_cx, app_cy = 155, 210
    _draw_icon_box(draw, app_cx, app_cy, "JohnnyAppleseed\n.app", font_sml)

    # Applications box (right)
    apl_cx, apl_cy = 385, 210
    _draw_icon_box(draw, apl_cx, apl_cy, "Applications", font_sml,
                   fill=(60, 90, 160, 160))

    # Arrow from .app → Applications
    ax0, ay = app_cx + 58, app_cy
    ax1     = apl_cx - 58
    _draw_arrow(draw, ax0, ay, ax1, ay)

    # Instruction text
    draw.text((W // 2, H - 52),
              "Drag Johnny Appleseed into Applications to install",
              fill=(200, 200, 220, 230), font=font_med, anchor="mm")
    draw.text((W // 2, H - 30),
              target_name,
              fill=(120, 120, 140, 160), font=font_sml, anchor="mm")

    img.save(dest, "PNG")


def _draw_icon_box(draw, cx: int, cy: int, label: str, font,
                   fill=(40, 60, 40, 160)) -> None:
    half = 46
    draw.rounded_rectangle(
        [(cx - half, cy - half), (cx + half, cy + half)],
        radius=14, fill=fill, outline=(180, 180, 220, 120), width=1)
    # Draw label lines centred under the box (multiline anchor unsupported)
    lines = label.split("\n")
    line_h = 15
    y = cy + half + 8
    for line in lines:
        try:
            bbox = draw.textbbox((0, 0), line, font=font)
            lw = bbox[2] - bbox[0]
        except Exception:
            lw = len(line) * 7
        draw.text((cx - lw // 2, y), line, fill=(200, 200, 220, 230), font=font)
        y += line_h


def _draw_arrow(draw, x0: int, y0: int, x1: int, y1: int) -> None:
    col = (200, 200, 200, 200)
    draw.line([(x0, y0), (x1, y1)], fill=col, width=3)
    # Arrowhead
    aw, ah = 14, 9
    draw.polygon([
        (x1, y0),
        (x1 - aw, y0 - ah),
        (x1 - aw, y0 + ah),
    ], fill=col)


def _get_font(size: int):
    from PIL import ImageFont
    # Try common system fonts in order of preference
    candidates = [
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
        "/System/Library/Fonts/Helvetica.ttc",
        "C:\\Windows\\Fonts\\arial.ttf",
    ]
    for path in candidates:
        if os.path.exists(path):
            try:
                return ImageFont.truetype(path, size)
            except Exception:
                continue
    return ImageFont.load_default()


def _minimal_png() -> bytes:
    """Return a 1×1 transparent PNG as bytes (no Pillow required)."""
    import zlib, struct as st
    def chunk(tag: bytes, data: bytes) -> bytes:
        c = zlib.crc32(tag + data) & 0xFFFFFFFF
        return st.pack(">I", len(data)) + tag + data + st.pack(">I", c)
    sig    = b"\x89PNG\r\n\x1a\n"
    ihdr   = chunk(b"IHDR", st.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0))
    idat   = chunk(b"IDAT", zlib.compress(b"\x00\x00\x00\x00"))
    iend   = chunk(b"IEND", b"")
    return sig + ihdr + idat + iend


# ── .DS_Store creation ────────────────────────────────────────────────────────

def write_ds_store(dest: Path, bg_image_relative: str) -> None:
    """
    Write a .DS_Store that configures the Finder DMG window:
      • custom background image
      • icon positions for .app and Applications
      • window bounds
    """
    try:
        from ds_store import DSStore
        from mac_alias import Alias
    except ImportError:
        # Without ds_store the DMG still works; background won't auto-show
        return

    with DSStore.open(str(dest), "w+") as d:
        # Finder window geometry
        d["."]["bwsp"] = {
            "ShowTabView":   False,
            "ShowToolbar":   False,
            "ShowSidebar":   False,
            "WindowBounds":  "{{200, 120}, {740, 500}}",
        }
        # Icon view settings with custom background
        d["."]["icvp"] = {
            "backgroundType":       2,
            "backgroundColorRed":   0.0,
            "backgroundColorGreen": 0.0,
            "backgroundColorBlue":  0.0,
            "backgroundImageAlias": bg_image_relative,
            "arrangeBy":            "none",
            "gridOffsetX":          0.0,
            "gridOffsetY":          0.0,
            "gridSpacing":          100.0,
            "iconSize":             128.0,
            "labelOnBottom":        True,
            "showIconPreview":      True,
            "showItemInfo":         False,
            "textSize":             12.0,
            "viewOptionsVersion":   1,
        }
        # Icon positions
        d[f"{APP_NAME}.app"]["Iloc"] = (155, 210)
        d["Applications"]["Iloc"]    = (385, 210)


# ── main ──────────────────────────────────────────────────────────────────────

ALL_TARGET_NAMES = [f"{os_}-{arch}" for (os_, arch) in TARGETS]


def main() -> None:
    parser = argparse.ArgumentParser(description="Package Johnny Appleseed for all platforms")
    parser.add_argument(
        "targets", nargs="*",
        default=ALL_TARGET_NAMES,
        help="Which targets to build (default: all). E.g. windows-x64 macos-arm64",
    )
    parser.add_argument(
        "--skip-download", action="store_true",
        help="Don't attempt to download missing arm64 native libs",
    )
    args = parser.parse_args()

    # Validate requested targets
    unknown = [t for t in args.targets if t not in ALL_TARGET_NAMES]
    if unknown:
        parser.error(f"Unknown target(s): {', '.join(unknown)}\nValid: {', '.join(ALL_TARGET_NAMES)}")

    os.chdir(REPO_ROOT)
    DIST_DIR.mkdir(parents=True, exist_ok=True)

    for target_name in args.targets:
        os_name, arch = target_name.split("-", 1)
        rid = TARGETS[(os_name, arch)]

        print(f"\n[{target_name}]")

        if not ensure_native_lib(os_name, arch, args.skip_download):
            print(f"  Skipping {target_name} — native lib unavailable")
            continue

        try:
            if os_name == "windows":
                package_windows(target_name, rid)
            elif os_name == "linux":
                package_linux(target_name, rid)
            elif os_name == "macos":
                package_macos(target_name, rid)
        except Exception as e:
            print(f"  ERROR: {e}", file=sys.stderr)
            if "--verbose" in sys.argv:
                import traceback
                traceback.print_exc()

    print("\nDone. Distribution artifacts:")
    for p in sorted(DIST_DIR.rglob("*")):
        if p.is_file():
            size = p.stat().st_size
            print(f"  {p.relative_to(REPO_ROOT)}  ({size // 1024} KB)")


if __name__ == "__main__":
    main()
