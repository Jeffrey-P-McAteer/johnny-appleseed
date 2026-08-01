#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = [
#   "pillow>=10.0",
#   "pycdlib>=1.14.0",
#   "ds-store>=1.3",
#   "mac-alias>=2.2",
# ]
# ///
"""
Johnny Appleseed - cross-platform packaging script.

Usage (from repo root):
    uv run scripts/package.py                     # all targets
    uv run scripts/package.py windows-x64         # specific target

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
import subprocess
import sys
import tempfile
import urllib.request
import zipfile
from pathlib import Path

import importlib.util

# SVG -> .icns rasteriser (shared with the MSBuild icon pipeline)
from _icons import write_icns

# -- configuration -------------------------------------------------------------

REPO_ROOT   = Path(__file__).resolve().parent.parent
PROJECT_CS  = REPO_ROOT / "src" / "JohnnyAppleseed" / "JohnnyAppleseed.csproj"
DIST_DIR    = REPO_ROOT / "dist"
NATIVE_DIR  = REPO_ROOT / "src" / "JohnnyAppleseed" / "runtimes"
ICON_SVG    = REPO_ROOT / "graphics" / "icon.svg"

APP_NAME      = "JohnnyAppleseed"
APP_ID        = "com.johnnyseed.game"
APP_VERSION   = "1.0.0"

# -- DMG (macOS) layout configuration -----------------------------------------
# All .dmg building is delegated to build/dmg-constructor.py, a validated,
# pure-Python UDIF/UDF builder that needs no macOS tooling.
DMG_CONSTRUCTOR_PY = REPO_ROOT / "build" / "dmg-constructor.py"

# Finder background shown behind the .dmg window. This is a PLACEHOLDER; an
# artist will supply a purpose-built image later - just repoint this path.
DMG_BACKGROUND      = REPO_ROOT / "graphics" / "view-of-the-natural-bridge-dc4df5.jpg"

# Finder window geometry and icon layout. Tune these freely.
DMG_WINDOW_POSITION = (200, 120)   # (x, y) top-left of the Finder window, in points
DMG_WINDOW_SIZE     = (740, 500)   # (width, height) of the window content, in points
DMG_ICON_SIZE       = 128          # icon size, in points
DMG_TEXT_SIZE       = 16           # icon label text size, in points
# Positions keyed by the visible top-level item name (points, window-relative).
DMG_ICON_POSITIONS  = {
    f"{APP_NAME}.app": (185, 220),
    "Applications":    (555, 220),
}


# Map: (target_os, arch) -> .NET RID
TARGETS: dict[tuple[str, str], str] = {
    ("windows", "x64"):   "win-x64",
    ("windows", "arm64"): "win-arm64",
    ("linux",   "x64"):   "linux-x64",
    ("linux",   "arm64"): "linux-arm64",
    ("macos",   "x64"):   "osx-x64",
    ("macos",   "arm64"): "osx-arm64",
}

# -- native-lib bootstrap for RIDs missing from the Raylib-cs NuGet package ---

def ensure_native_lib(target_os: str, arch: str, skip_download: bool) -> bool:
    """
    Return True if the native lib for this RID is available.

    For linux-x64:   the Raylib-cs NuGet supplies it; setup-native-libs.py
                     optionally replaces it with the Wayland-capable version.
    For linux-arm64: must be built by `uv run scripts/setup-native-libs.py linux-arm64`
                     (Raylib 5.5 does not publish pre-built arm64 Linux binaries).
    For win-arm64:   must be built by `uv run scripts/setup-native-libs.py win-arm64`.
    All others:      bundled in the Raylib-cs NuGet package automatically.
    """
    if target_os == "linux" and arch == "arm64":
        dest = NATIVE_DIR / "linux-arm64" / "native" / "libraylib.so"
        if dest.exists():
            return True
        print(
            f"  [warn] linux-arm64 native lib not found at {dest.relative_to(REPO_ROOT)}\n"
            "         Run: uv run scripts/setup-native-libs.py linux-arm64"
        )
        return False

    if target_os == "windows" and arch == "arm64":
        dest = NATIVE_DIR / "win-arm64" / "native" / "raylib.dll"
        if dest.exists():
            return True
        print(
            f"  [warn] win-arm64 native lib not found at {dest.relative_to(REPO_ROOT)}\n"
            "         Run: uv run scripts/setup-native-libs.py win-arm64"
        )
        return False

    # linux-x64, win-x64, osx-x64, osx-arm64 - bundled in the NuGet package.
    # setup-native-libs.py linux-wayland may replace linux-x64's lib with the
    # Wayland-capable version; that's handled by the OverrideWaylandLib MSBuild target.
    return True


# -- dotnet publish -------------------------------------------------------------

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
    print(f"  dotnet publish -r {rid} ...")
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        print(result.stdout[-3000:] if result.stdout else "")
        print(result.stderr[-3000:] if result.stderr else "", file=sys.stderr)
        raise RuntimeError(f"dotnet publish failed for {rid}")


# -- packagers -----------------------------------------------------------------

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

        print(f"  -> {zip_path}")


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

    print(f"  -> {out_bin}")


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

        # -- .app bundle -------------------------------------------------------
        app_bundle = tmp_path / f"{APP_NAME}.app"
        macos_dir  = app_bundle / "Contents" / "MacOS"
        res_dir    = app_bundle / "Contents" / "Resources"
        macos_dir.mkdir(parents=True)
        res_dir.mkdir(parents=True)

        # Copy binary
        app_binary = macos_dir / APP_NAME
        shutil.copy2(binary, app_binary)
        app_binary.chmod(app_binary.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

        # App icon: rasterise graphics/icon.svg -> Contents/Resources/AppIcon.icns
        # (Raylib can't load SVG; Finder/Dock read the .icns from the bundle).
        # Best-effort - a missing/failed icon must not fail packaging.
        icon_file = None
        if ICON_SVG.exists():
            try:
                write_icns(ICON_SVG, res_dir / "AppIcon.icns")
                icon_file = "AppIcon"
            except Exception as e:
                print(f"  [warn] .icns generation failed ({e}); .app will use the default icon")

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
        if icon_file:
            plist["CFBundleIconFile"] = icon_file
        with open(app_bundle / "Contents" / "Info.plist", "wb") as f:
            plistlib.dump(plist, f)

        # PkgInfo - legacy 8-byte bundle type/creator file expected by macOS
        (app_bundle / "Contents" / "PkgInfo").write_bytes(b"APPL????")

        # Ad-hoc code-sign so Gatekeeper doesn't report the bundle as "damaged".
        # An ad-hoc signature (-) is not trusted by Gatekeeper for internet-sourced
        # apps, but it eliminates the spurious "damaged" error for local/LAN use and
        # is required for any Gatekeeper path to succeed at all.
        if platform.system() == "Darwin":
            codesign = shutil.which("codesign")
            if codesign:
                subprocess.run(
                    [codesign, "--deep", "--force", "--sign", "-", str(app_bundle)],
                    check=True, capture_output=True,
                )
            else:
                print("  [warn] codesign not found - .app will be unsigned (Gatekeeper may block it)")

        # -- DMG staging area -------------------------------------------------
        # dmg-constructor builds the Finder background, .DS_Store, icon layout,
        # and the drag-to-Applications shortcut itself, so the staging tree only
        # needs to contain the .app bundle.
        staging = tmp_path / "staging"
        staging.mkdir()
        shutil.copytree(app_bundle, staging / f"{APP_NAME}.app", symlinks=True)

        # -- create the DMG ---------------------------------------------------
        actual_out = create_dmg(staging, dmg_path, label=APP_NAME)

    print(f"  -> {actual_out}")


# -- DMG creation helpers ------------------------------------------------------

def _load_dmg_constructor():
    """Import build/dmg-constructor.py as a module.

    It lives outside scripts/ and its filename contains a hyphen, so a plain
    `import` won't reach it - load it by path instead.
    """
    spec = importlib.util.spec_from_file_location("dmg_constructor", DMG_CONSTRUCTOR_PY)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load DMG builder at {DMG_CONSTRUCTOR_PY}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def create_dmg(source_dir: Path, output: Path, label: str) -> Path:
    """
    Build a real, mountable .dmg from source_dir using build/dmg-constructor.py.

    dmg-constructor is a validated, pure-Python UDIF/UDF builder that needs no
    macOS tooling (no hdiutil, no mkisofs) and works identically on Windows,
    Linux, and macOS. It writes an actual filesystem into the image - the
    failure mode of the previous builders was emitting a container whose
    partition held no mountable filesystem - and also embeds the Finder
    background image, positions the icons, and adds the drag-to-Applications
    shortcut itself. Layout is driven by the DMG_* globals above.
    """
    dmgc = _load_dmg_constructor()

    win_x, win_y = DMG_WINDOW_POSITION
    win_w, win_h = DMG_WINDOW_SIZE

    argv = [
        str(source_dir), str(output),
        "--volume-name", label,
        "--window-position", f"{win_x},{win_y}",
        "--window-size", f"{win_w}x{win_h}",
        "--icon-size", str(DMG_ICON_SIZE),
        "--text-size", str(DMG_TEXT_SIZE),
        "--applications-symlink",
    ]

    if DMG_BACKGROUND.is_file():
        argv += ["--background", str(DMG_BACKGROUND)]
    else:
        print(f"  [warn] DMG background image not found: {DMG_BACKGROUND} - building without one")

    for name, (x, y) in DMG_ICON_POSITIONS.items():
        argv += ["--icon-position", f"{name}:{x}:{y}"]

    rc = dmgc.main(argv)
    if rc != 0:
        raise RuntimeError(f"dmg-constructor.py failed (exit {rc}) building {output}")
    return output


# -- main ----------------------------------------------------------------------

ALL_TARGET_NAMES = [f"{os_}-{arch}" for (os_, arch) in TARGETS]


def main() -> None:
    parser = argparse.ArgumentParser(description="Package Johnny Appleseed for all platforms")
    parser.add_argument(
        "targets", nargs="*",
        default=ALL_TARGET_NAMES,
        help="Which targets to build (default: all). E.g. windows-x64 macos-arm64",
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

        if not ensure_native_lib(os_name, arch, False):
            print(f"  Skipping {target_name} - native lib unavailable")
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
