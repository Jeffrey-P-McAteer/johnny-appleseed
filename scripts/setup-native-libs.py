#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = [
#   "requests>=2.31",
# ]
# ///
"""
Provision Raylib native shared libraries for all supported platforms.

Queries the Raylib GitHub API for the latest release, fuzzy-matches each
platform's asset by filename keywords, and downloads it.  The one exception
is linux-wayland, which must be built from source (using Zig) because no
pre-built binary ships with both X11 and Wayland GLFW backends compiled in.

Targets
───────
  linux-x64       Download official release binary
  linux-arm64     Download official release binary  ← was broken on 5.5; fixed for 6.0+
  linux-wayland   Build from source (Zig) — X11 + Wayland multi-platform backend
  win-x64         Download official release binary (mingw-w64 build)
  win-x64-ndebug  Build from source (Zig) — mingw ABI with NDEBUG (asserts OFF), so
                  no-OpenGL machines fail gracefully instead of abort()ing in GLFW
  win-arm64       Download official release binary  ← was Zig cross-compile; now download
  macos-x64       Download official release binary (universal fat Mach-O)
  macos-arm64     Download official release binary (same fat binary as macos-x64)

Usage
─────
    uv run scripts/setup-native-libs.py                   # all targets
    uv run scripts/setup-native-libs.py linux-arm64       # one target
    uv run scripts/setup-native-libs.py win-arm64 macos-x64 macos-arm64
    uv run scripts/setup-native-libs.py --rebuild         # force re-download/build

System requirements
───────────────────
  All targets except linux-wayland require no system packages — they are pure
  HTTP downloads.

  linux-wayland requires:
    • wayland-scanner   (generates Wayland protocol headers at build time)
    • X11 headers       (for the dual X11+Wayland backend)
      Arch:   sudo pacman -S wayland libx11
      Debian: sudo apt install libwayland-dev libx11-dev

  Zig 0.16.0 (required by Raylib 6.0's build.zig) is downloaded automatically to
  ./build/toolchains/ and is only needed when linux-wayland is in the requested
  targets.
"""

from __future__ import annotations

import argparse
import fnmatch
import os
import platform
import shutil
import stat
import struct
import subprocess
import sys
import tarfile
import zipfile
from pathlib import Path

import requests

# ── paths ──────────────────────────────────────────────────────────────────────

REPO_ROOT  = Path(__file__).resolve().parent.parent
BUILD_DIR  = REPO_ROOT / "build"
CACHE_DIR  = BUILD_DIR / "cache"
TOOLS_DIR  = BUILD_DIR / "toolchains"
SRC_DIR    = BUILD_DIR / "raylib-src"
RUNTIMES   = REPO_ROOT / "src" / "JohnnyAppleseed" / "runtimes"

GITHUB_API = "https://api.github.com/repos/raysan5/raylib/releases/latest"

# Raylib 6.0's build.zig.zon declares `.minimum_zig_version = "0.16.0"`; older Zig
# (0.14/0.15) fails to compile it (`std.array_list.Managed`, `b.graph.environ_map`
# are newer-Zig APIs). Zig 0.16.0 also RENAMED its release archives from
# `zig-<os>-<arch>-<ver>` (0.14) to `zig-<arch>-<os>-<ver>`.
ZIG_VERSION  = "0.16.0"
ZIG_BASE_URL = f"https://ziglang.org/download/{ZIG_VERSION}"
ZIG_ARCHIVES = {
    "linux-x64":   f"zig-x86_64-linux-{ZIG_VERSION}.tar.xz",
    "linux-arm64": f"zig-aarch64-linux-{ZIG_VERSION}.tar.xz",
    "macos-x64":   f"zig-x86_64-macos-{ZIG_VERSION}.tar.xz",
    "macos-arm64": f"zig-aarch64-macos-{ZIG_VERSION}.tar.xz",
}

# ── target definitions ─────────────────────────────────────────────────────────
#
# DOWNLOAD_TARGETS — fetched from the latest GitHub release.
#   keywords   All must appear (case-insensitive) in the asset filename.
#   exclude    Any match disqualifies the asset.
#   lib_glob   Filename pattern (fnmatch) to locate the lib inside the archive.
#   dest       Where to place the extracted file in the project runtimes/ tree.
#   elf_machine / pe_machine — architecture constant for binary verification.
#              macOS libs (fat Mach-O) are verified by magic-byte check instead.
#
# BUILD_TARGETS — built from Raylib source via `zig build`.
#   zig_target  Passed to -Dtarget=; None means native host build.
#   display     Passed to -Dlinux_display_backend=  (Linux only).
#   out_name    fnmatch glob for the library name in zig-out/lib/ or zig-out/bin/.
#   sentinel    Optional marker file written alongside dest.
#   config      Extra compile-time -D macros joined into raylib's build.zig
#               `-Dconfig=` option. raylib's src/config.h guards every flag with
#               `#ifndef`, so a -D here overrides the default. We use this to turn
#               ON the image loaders raylib disables by default (JPG/BMP/TGA/PSD),
#               so original art (e.g. embedded .jpg) decodes in our from-source
#               builds — the stock download builds already ship these enabled.

DOWNLOAD_TARGETS: dict[str, dict] = {
    "linux-x64": {
        "keywords":    ["linux", "amd64"],
        "exclude":     ["i386", "i686"],
        "lib_glob":    "libraylib.so*",
        "dest":        RUNTIMES / "linux-x64"  / "native" / "libraylib.so",
        "elf_machine": 0x3E,   # EM_X86_64
    },
    "linux-arm64": {
        "keywords":    ["linux", "arm64"],
        "lib_glob":    "libraylib.so*",
        "dest":        RUNTIMES / "linux-arm64" / "native" / "libraylib.so",
        "elf_machine": 0xB7,   # EM_AARCH64
    },
    # Prefer the mingw-w64 build for win-x64: produces a standard cdecl DLL
    # that is compatible with .NET P/Invoke without MSVC redistributables.
    "win-x64": {
        "keywords":    ["win64", "mingw"],
        "lib_glob":    "raylib.dll",
        "dest":        RUNTIMES / "win-x64"    / "native" / "raylib.dll",
        "pe_machine":  0x8664,  # IMAGE_FILE_MACHINE_AMD64
    },
    "win-arm64": {
        "keywords":    ["winarm64"],
        "lib_glob":    "raylib.dll",
        "dest":        RUNTIMES / "win-arm64"  / "native" / "raylib.dll",
        "pe_machine":  0xAA64,  # IMAGE_FILE_MACHINE_ARM64
    },
    # Raylib ships a single universal (fat) Mach-O for macOS containing both
    # x86_64 and arm64 slices.  We copy it to both osx-x64 and osx-arm64 so
    # dotnet finds the right runtime asset regardless of build RID.
    "macos-x64": {
        "keywords":    ["macos"],
        "lib_glob":    "libraylib*.dylib",
        "dest":        RUNTIMES / "osx-x64"    / "native" / "libraylib.dylib",
    },
    "macos-arm64": {
        "keywords":    ["macos"],
        "lib_glob":    "libraylib*.dylib",
        "dest":        RUNTIMES / "osx-arm64"  / "native" / "libraylib.dylib",
    },
}

BUILD_TARGETS: dict[str, dict] = {
    # From-source win-x64 built with Zig's NDEBUG (Release) mode so GLFW/raylib
    # assert()s are compiled OUT. The stock mingw download ships with asserts ON,
    # so on a machine with no usable OpenGL, a raylib call that receives GLFW's
    # NULL window handle (e.g. glfwSetWindowSizeLimits) abort()s with a raw
    # "Assertion failed" box that our managed startup-error dialog can't catch.
    # With asserts stripped, those paths no longer abort — belt-and-suspenders on
    # top of the IsWindowReady() guard in Game.Run. Zig bundles the MinGW + Windows
    # SDK headers, so this cross-compiles from Linux with NO system packages, and
    # produces the same GNU/cdecl ABI DLL the download target does (P/Invoke-safe,
    # no MSVC redistributable). Shares the win-x64 dest, so package.py's existing
    # RID override picks it up unchanged.
    "win-x64-ndebug": {
        "zig_target":  "x86_64-windows-gnu",
        "out_name":    "raylib.dll",     # Windows DLLs land in zig-out/bin/
        "dest":        RUNTIMES / "win-x64" / "native" / "raylib.dll",
        "sentinel":    RUNTIMES / "win-x64" / "native" / ".ndebug-enabled",
        "pe_machine":  0x8664,           # IMAGE_FILE_MACHINE_AMD64
        # From-source builds default these image loaders OFF (raylib config.h
        # #ifndef guards); the download build ships them ON. Re-enable so the
        # embedded still-life .jpg still decodes — same fix as linux-wayland.
        "config": [
            "-DSUPPORT_FILEFORMAT_JPG=1",
            "-DSUPPORT_FILEFORMAT_BMP=1",
            "-DSUPPORT_FILEFORMAT_TGA=1",
            "-DSUPPORT_FILEFORMAT_PSD=1",
        ],
    },
    # Build natively (no -Dtarget) so Zig uses pkg-config to discover system
    # libraries (X11, GLX, wayland-client, xkbcommon, …).  An explicit
    # cross-target breaks system-library discovery on Linux and produces errors.
    "linux-wayland": {
        "zig_target":  None,
        "display":     "Both",          # GLFW 3.4 X11 + Wayland multi-platform
        "out_name":    "libraylib.so*", # glob handles versioned names like .so.6.0
        "dest":        RUNTIMES / "linux-x64"  / "native" / "libraylib.so",
        "sentinel":    RUNTIMES / "linux-x64"  / "native" / ".wayland-enabled",
        "elf_machine": 0x3E,
        # Enable raylib's off-by-default image loaders so embedded original art
        # (JPEG photos, BMP/TGA/PSD exports) decodes. Without these, raylib's
        # config.h leaves them at 0 and LoadImageFromMemory(".jpg", …) returns
        # "Data format not supported". (PNG/GIF/QOI are already on by default.)
        "config": [
            "-DSUPPORT_FILEFORMAT_JPG=1",
            "-DSUPPORT_FILEFORMAT_BMP=1",
            "-DSUPPORT_FILEFORMAT_TGA=1",
            "-DSUPPORT_FILEFORMAT_PSD=1",
        ],
    },
}

ALL_TARGETS = {**DOWNLOAD_TARGETS, **BUILD_TARGETS}

# ── GitHub release query ───────────────────────────────────────────────────────

def fetch_latest_release() -> tuple[str, list[dict]]:
    """Return (tag_name, assets) for the latest Raylib GitHub release."""
    print("  Querying GitHub API for latest Raylib release …")
    r = requests.get(
        GITHUB_API, timeout=30,
        headers={"Accept": "application/vnd.github.v3+json"},
    )
    r.raise_for_status()
    data = r.json()
    tag  = data["tag_name"]
    print(f"  Latest: {tag}  ({len(data['assets'])} assets)")
    return tag, data["assets"]


def match_asset(assets: list[dict], keywords: list[str],
                exclude: list[str] | None = None) -> dict | None:
    """
    Return the best-matching asset whose filename contains ALL keywords and
    NONE of the excluded strings (all comparisons case-insensitive).
    When multiple assets match, prefer the one with the shortest filename
    (tends to be the most specific / canonical one).
    """
    exclude = exclude or []
    matches = []
    for asset in assets:
        name = asset["name"].lower()
        if any(e.lower() in name for e in exclude):
            continue
        if all(kw.lower() in name for kw in keywords):
            matches.append(asset)
    if not matches:
        return None
    return min(matches, key=lambda a: len(a["name"]))


# ── download helpers ───────────────────────────────────────────────────────────

def download(url: str, dest: Path, desc: str) -> Path:
    CACHE_DIR.mkdir(parents=True, exist_ok=True)
    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.exists():
        print(f"  Cached: {dest.name}")
        return dest
    print(f"  Downloading {desc} …")
    r = requests.get(url, stream=True, timeout=300)
    r.raise_for_status()
    total = int(r.headers.get("content-length", 0))
    done  = 0
    with open(dest, "wb") as f:
        for chunk in r.iter_content(65536):
            f.write(chunk)
            done += len(chunk)
            if total:
                print(f"\r  {done*100//total:3d}%  "
                      f"{done//1_048_576} / {total//1_048_576} MB",
                      end="", flush=True)
    print()
    return dest


def _archive_contents(archive: Path) -> str:
    """Return a short listing of filenames inside an archive (for error messages)."""
    try:
        if archive.suffix in (".gz", ".tgz") or archive.name.endswith(".tar.gz"):
            with tarfile.open(archive, "r:gz") as tf:
                names = [m.name for m in tf.getmembers() if m.isfile()]
        else:
            with zipfile.ZipFile(archive) as zf:
                names = [i.filename for i in zf.infolist() if not i.is_dir()]
        return ", ".join(sorted(names)[:20])
    except Exception:
        return "(could not list)"


def extract_lib(archive: Path, lib_glob: str, dest: Path) -> None:
    """
    Find the first file in *archive* whose basename matches *lib_glob*
    (fnmatch, case-sensitive) and write it to *dest*, preserving Unix mode.
    Handles .tar.gz and .zip archives.
    """
    dest.parent.mkdir(parents=True, exist_ok=True)

    if archive.name.endswith((".tar.gz", ".tgz")):
        with tarfile.open(archive, "r:gz") as tf:
            for member in sorted(tf.getmembers(), key=lambda m: m.name):
                base = member.name.rsplit("/", 1)[-1]
                if not fnmatch.fnmatch(base, lib_glob) or not member.isfile():
                    continue
                fobj = tf.extractfile(member)
                if fobj is None:
                    continue
                dest.write_bytes(fobj.read())
                if member.mode:
                    dest.chmod(member.mode | stat.S_IRUSR | stat.S_IRGRP)
                print(f"  Extracted {member.name}  →  {dest.relative_to(REPO_ROOT)}")
                return

    elif archive.name.endswith(".zip"):
        with zipfile.ZipFile(archive) as zf:
            for info in sorted(zf.infolist(), key=lambda i: i.filename):
                base = info.filename.rsplit("/", 1)[-1]
                if not fnmatch.fnmatch(base, lib_glob) or info.is_dir():
                    continue
                dest.write_bytes(zf.read(info.filename))
                unix_mode = (info.external_attr >> 16) & 0xFFFF
                dest.chmod(unix_mode if unix_mode else 0o755)
                print(f"  Extracted {info.filename}  →  {dest.relative_to(REPO_ROOT)}")
                return

    else:
        raise ValueError(f"Unsupported archive format: {archive.name}")

    raise FileNotFoundError(
        f"No file matching {lib_glob!r} found in {archive.name}.\n"
        f"  Archive contents: {_archive_contents(archive)}"
    )


# ── binary verification ────────────────────────────────────────────────────────

def verify_elf(path: Path, expected_machine: int) -> bool:
    try:
        with open(path, "rb") as f:
            if f.read(4) != b"\x7fELF":
                return False
            f.seek(18)
            return struct.unpack("<H", f.read(2))[0] == expected_machine
    except Exception:
        return False


def verify_pe(path: Path, expected_machine: int) -> bool:
    try:
        with open(path, "rb") as f:
            f.seek(0x3C)
            pe_off = struct.unpack("<I", f.read(4))[0]
            f.seek(pe_off + 4)
            return struct.unpack("<H", f.read(2))[0] == expected_machine
    except Exception:
        return False


# Accepted Mach-O magic bytes (fat binaries and thin 32/64-bit, BE and LE).
_MACHO_MAGICS = {
    b"\xCA\xFE\xBA\xBE",  # fat binary
    b"\xCA\xFE\xBA\xBF",  # fat64
    b"\xFE\xED\xFA\xCE",  # 32-bit BE (big-endian)
    b"\xFE\xED\xFA\xCF",  # 64-bit BE
    b"\xCE\xFA\xED\xFE",  # 32-bit LE (little-endian)
    b"\xCF\xFA\xED\xFE",  # 64-bit LE
}

def verify_macho(path: Path) -> bool:
    try:
        with open(path, "rb") as f:
            return f.read(4) in _MACHO_MAGICS
    except Exception:
        return False


def verify_lib(path: Path, cfg: dict) -> bool:
    if "elf_machine" in cfg:
        return verify_elf(path, cfg["elf_machine"])
    if "pe_machine" in cfg:
        return verify_pe(path, cfg["pe_machine"])
    # macOS fat Mach-O — check magic bytes only (no single-arch machine field)
    return verify_macho(path)


# ── download-based provisioning ────────────────────────────────────────────────

def provision_download(name: str, cfg: dict, tag: str, assets: list[dict]) -> None:
    asset = match_asset(assets, cfg["keywords"], cfg.get("exclude"))
    if asset is None:
        available = [a["name"] for a in assets]
        raise RuntimeError(
            f"No asset matched for {name!r} in release {tag}.\n"
            f"  Keywords: {cfg['keywords']}   Exclude: {cfg.get('exclude', [])}\n"
            f"  Available: {available}"
        )

    print(f"  Asset: {asset['name']}  ({asset['size'] // 1024} KB)")
    archive = download(
        asset["browser_download_url"],
        CACHE_DIR / asset["name"],
        asset["name"],
    )

    dest: Path = cfg["dest"]
    extract_lib(archive, cfg["lib_glob"], dest)

    if not verify_lib(dest, cfg):
        dest.unlink(missing_ok=True)
        raise RuntimeError(
            f"Binary verification failed for {dest.name}.\n"
            "  The downloaded asset may be for the wrong architecture."
        )

    # Ensure the shared library is executable (ZIP archives sometimes lose this).
    dest.chmod(dest.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    print(f"  ✓ {dest.relative_to(REPO_ROOT)}  ({dest.stat().st_size // 1024} KB)")


# ── Zig toolchain (linux-wayland build only) ───────────────────────────────────

def host_key() -> str:
    s = platform.system().lower()
    m = platform.machine().lower()
    os_ = {"darwin": "macos", "linux": "linux"}.get(s, s)
    arch = "arm64" if m in ("aarch64", "arm64") else "x64"
    return f"{os_}-{arch}"


def ensure_zig() -> Path:
    zig_dir = TOOLS_DIR / f"zig-{ZIG_VERSION}"
    zig_bin = zig_dir / "zig"
    if zig_bin.exists():
        print(f"  Zig {ZIG_VERSION}: {zig_dir}")
        return zig_bin

    archive_name = ZIG_ARCHIVES.get(host_key())
    if not archive_name:
        raise RuntimeError(f"No Zig {ZIG_VERSION} archive known for host {host_key()!r}")

    archive = download(
        f"{ZIG_BASE_URL}/{archive_name}",
        CACHE_DIR / archive_name,
        f"Zig {ZIG_VERSION}",
    )
    print("  Extracting Zig …")
    zig_dir.mkdir(parents=True, exist_ok=True)
    with tarfile.open(archive, "r:xz") as tf:
        # Strip the leading zig-linux-x86_64-0.14.0/ prefix from all members
        prefix = tf.getmembers()[0].name.split("/")[0] + "/"
        for m in tf.getmembers():
            m.name = m.name[len(prefix):]
            if m.name:
                tf.extract(m, zig_dir)

    if not zig_bin.exists():
        candidates = list(zig_dir.glob("*/zig")) + list(zig_dir.glob("zig"))
        if not candidates:
            raise RuntimeError(f"zig binary not found after extraction in {zig_dir}")
        zig_bin = candidates[0]

    zig_bin.chmod(zig_bin.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    print("  Zig ready")
    return zig_bin


def ensure_raylib_source(tag: str) -> Path:
    src = SRC_DIR / f"raylib-{tag}"
    if (src / "src" / "raylib.h").exists():
        print(f"  Raylib {tag} source: {src}")
        return src

    url = f"https://github.com/raysan5/raylib/archive/refs/tags/{tag}.tar.gz"
    archive = download(url, CACHE_DIR / f"raylib-{tag}.tar.gz", f"Raylib {tag} source")
    print("  Extracting Raylib source …")
    SRC_DIR.mkdir(parents=True, exist_ok=True)
    with tarfile.open(archive, "r:gz") as tf:
        tf.extractall(SRC_DIR)

    if not src.exists():
        found = sorted(SRC_DIR.glob("raylib-*"))
        if not found:
            raise RuntimeError("Raylib source directory not found after extraction")
        found[0].rename(src)
    return src


def check_wayland_requirements() -> None:
    missing = []
    if not any(Path(p).exists() for p in [
        "/usr/include/X11/Xlib.h",
        "/usr/local/include/X11/Xlib.h",
    ]):
        missing.append("X11 headers  →  sudo pacman -S libx11  /  sudo apt install libx11-dev")
    if not (shutil.which("wayland-scanner") or
            Path("/usr/sbin/wayland-scanner").exists()):
        missing.append("wayland-scanner  →  sudo pacman -S wayland  /  sudo apt install libwayland-dev")
    if missing:
        raise RuntimeError(
            "Missing requirements for linux-wayland build:\n  " +
            "\n  ".join(missing)
        )


def disable_zig_examples(raylib: Path) -> None:
    """
    raylib's build.zig installs every example program into the default build
    step, so a plain `zig build` also compiles ~250 example exes.  We only want
    the library, and on the Windows cross-target one example (rlgl_standalone,
    which reaches into GLFW internals) fails to *link* — which would fail the
    whole build even though libraylib built fine.

    Neutralize the per-example install so the default step builds only the lib.
    Best-effort and idempotent: patches the single `b.installArtifact(exe);`
    line (the lib uses a different variable, `lib`, and is left intact).  If a
    future raylib restructures build.zig and the line isn't found, we leave it
    alone and rely on the post-build library verification below.
    """
    build_zig = raylib / "build.zig"
    text = build_zig.read_text()
    needle = "        b.installArtifact(exe);\n"
    if needle in text:
        text = text.replace(
            needle,
            "        // b.installArtifact(exe);  // disabled by "
            "setup-native-libs.py — build the library only\n",
            1,
        )
        build_zig.write_text(text)
        print("  Patched build.zig: examples excluded from the default build")
    elif "disabled by setup-native-libs.py" not in text:
        print("  Note: example-install line not found in build.zig — relying on "
              "post-build library verification instead")


def provision_zig_build(zig: Path, raylib: Path, cfg: dict) -> None:
    disable_zig_examples(raylib)

    zig_out = raylib / "zig-out"
    if zig_out.exists():
        shutil.rmtree(zig_out)

    # raylib 6.0 (Zig 0.16) replaced the old `-Dshared=true` with `-Dlinkage=<mode>`
    # (std.builtin.LinkMode: dynamic|static).
    cmd = [str(zig), "build", "-Doptimize=ReleaseSafe", "-Dlinkage=dynamic"]
    if cfg.get("zig_target"):
        cmd += [f"-Dtarget={cfg['zig_target']}"]
    if "display" in cfg:
        cmd += [f"-Dlinux_display_backend={cfg['display']}"]
    # Extra config macros (e.g. enabling optional image formats). build.zig's
    # -Dconfig takes a single space-separated string of raw compiler flags.
    if cfg.get("config"):
        cmd += [f"-Dconfig={' '.join(cfg['config'])}"]

    env = os.environ.copy()
    env["PATH"] = "/usr/sbin:/usr/local/sbin:/usr/local/bin:/usr/bin:/bin:" + env.get("PATH", "")
    env["ZIG_GLOBAL_CACHE_DIR"] = str(BUILD_DIR / "zig-cache")

    target_label = cfg.get("zig_target") or "native"
    extra = f" -Dlinux_display_backend={cfg['display']}" if "display" in cfg else ""
    if cfg.get("config"):
        extra += f" -Dconfig='{' '.join(cfg['config'])}'"
    print(f"  zig build -Dtarget={target_label} -Dlinkage=dynamic{extra}")
    print("  (streaming output — first run may take a minute)\n")

    result = subprocess.run(cmd, cwd=raylib, env=env)
    # Don't hard-fail on a non-zero exit: we only need the library, and a stray
    # example that fails to build/link must not sink the whole run. The real
    # gate is whether a verified library artifact was produced (checked below).
    if result.returncode != 0:
        print(f"  Note: `zig build` exited {result.returncode} — checking for the "
              "library artifact anyway (an example may have failed to build)")

    # Search for the output lib (glob handles versioned names like libraylib.so.6.0)
    out_glob = cfg["out_name"]
    candidates: list[Path] = []
    for subdir in ("lib", "bin"):
        d = zig_out / subdir
        if d.exists():
            candidates.extend(d.glob(out_glob))
    if not candidates:
        found = [f.name for sd in ("lib", "bin")
                 for f in (zig_out / sd).glob("*") if (zig_out / sd).exists()]
        raise RuntimeError(
            f"Expected {out_glob!r} in zig-out/lib/ or zig-out/bin/\n"
            f"  Found: {found}"
        )

    lib_file = candidates[0]
    if not verify_lib(lib_file, cfg):
        raise RuntimeError(f"ELF machine check failed for {lib_file.name}")

    dest: Path = cfg["dest"]
    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(lib_file, dest)
    print(f"\n  ✓ {dest.relative_to(REPO_ROOT)}  ({dest.stat().st_size // 1024} KB)")

    if sentinel := cfg.get("sentinel"):
        sentinel.write_text("wayland-enabled\n")
        print(f"  ✓ {sentinel.relative_to(REPO_ROOT)}")


# ── main ───────────────────────────────────────────────────────────────────────

def main() -> None:
    parser = argparse.ArgumentParser(
        description="Download or build Raylib native libs for all supported platforms",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=f"Available targets: {' '.join(ALL_TARGETS)}",
    )
    parser.add_argument(
        "targets", nargs="*",
        default=list(ALL_TARGETS),
        help="Targets to provision (default: all)",
    )
    parser.add_argument(
        "--rebuild", action="store_true",
        help="Re-download/rebuild even if the output file already exists",
    )
    args = parser.parse_args()

    unknown = [t for t in args.targets if t not in ALL_TARGETS]
    if unknown:
        parser.error(
            f"Unknown target(s): {', '.join(unknown)}\n"
            f"Valid: {', '.join(ALL_TARGETS)}"
        )

    os.chdir(REPO_ROOT)

    # ── fetch release metadata (always needed — even for build targets, we pull
    #    source at the same tag as the latest release for consistency) ─────────
    print("── Raylib release ──────────────────────────────────────────────────")
    tag, assets = fetch_latest_release()
    print()

    # ── set up Zig only when a build target is requested ─────────────────────
    zig = raylib_src = None
    if any(t in BUILD_TARGETS for t in args.targets):
        print("── Zig toolchain ───────────────────────────────────────────────────")
        zig         = ensure_zig()
        raylib_src  = ensure_raylib_source(tag)
        print()

    # linux-wayland is built from source into the SAME file as the linux-x64
    # download (runtimes/linux-x64/native/libraylib.so) — it is meant to REPLACE
    # the stock lib with an X11+Wayland build that also enables extra image formats
    # (JPG/BMP/TGA/PSD). If both are requested, the download runs first and the
    # Wayland build is then skipped as "already have", silently leaving the stock
    # (JPEG-less) lib in place. So when linux-wayland is requested, drop the plain
    # linux-x64 download and let the Wayland build own the shared file.
    targets = list(args.targets)
    if "linux-wayland" in targets and "linux-x64" in targets:
        targets.remove("linux-x64")
        print("  Note: linux-wayland supersedes the linux-x64 download (shared lib "
              "file) — skipping the plain download.\n")
    # Same story for Windows: the NDEBUG from-source build and the mingw download
    # write the same runtimes/win-x64/native/raylib.dll. If both are requested the
    # download would run first and the build would then skip as "already have",
    # silently leaving the asserts-enabled stock DLL in place — so drop it.
    if "win-x64-ndebug" in targets and "win-x64" in targets:
        targets.remove("win-x64")
        print("  Note: win-x64-ndebug supersedes the win-x64 download (shared lib "
              "file) — skipping the plain download.\n")

    # ── provision each target ─────────────────────────────────────────────────
    for name in targets:
        cfg      = ALL_TARGETS[name]
        dest     = cfg["dest"]
        sentinel = cfg.get("sentinel")

        bar = "─" * max(1, 60 - len(name))
        print(f"── [{name}] {bar}")

        # "Already have" must also see any required sentinel: a bare libraylib.so
        # with no .wayland-enabled is a leftover stock download, not the Wayland
        # build we want here — so let it (re)build rather than skipping.
        have = dest.exists() and (sentinel is None or sentinel.exists())
        if have and not args.rebuild:
            size = dest.stat().st_size // 1024
            print(f"  Already have: {dest.relative_to(REPO_ROOT)} ({size} KB)"
                  "  — pass --rebuild to force")
            print()
            continue

        # Preserve an existing good lib: a failed (re)build must NOT destroy it.
        backup = dest.parent / (dest.name + ".bak") if dest.exists() else None
        if backup:
            shutil.copy2(dest, backup)

        try:
            if name in BUILD_TARGETS:
                # Only the Linux X11/Wayland backend build needs host system
                # headers; the Windows cross-compile is fully self-contained (Zig
                # bundles MinGW + the Windows SDK).
                if "display" in cfg:
                    check_wayland_requirements()
                provision_zig_build(zig, raylib_src, cfg)
            else:
                provision_download(name, cfg, tag, assets)
            if backup:
                backup.unlink(missing_ok=True)          # success — drop the backup
        except Exception as exc:
            print(f"\n  ERROR: {exc}", file=sys.stderr)
            if backup and backup.exists():
                backup.replace(dest)                    # restore previous good lib
                print("  (kept the previous library — the failed build did not "
                      "delete it)", file=sys.stderr)
            else:
                dest.unlink(missing_ok=True)
                if sentinel and sentinel.exists():
                    sentinel.unlink()

        print()

    print("Done.")
    print("Run `uv run scripts/package.py` to build distribution archives.")


if __name__ == "__main__":
    main()
