#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = [
#   "requests>=2.31",
# ]
# ///
"""
Download Zig 0.13.0 and use Raylib's own build.zig to produce native Raylib
shared libraries for platforms missing from the Raylib-cs NuGet package.

Using build.zig (instead of manual zig cc) means Raylib handles all the details:
  • rglfw.c (GLFW unity build) — selected automatically per platform
  • Wayland protocol headers — generated at build time from bundled XMLs via wayland-scanner
  • Library discovery — via pkg-config (wayland-client, xkbcommon, X11, etc.)
  • Platform-specific compile flags and defines

Targets
───────
  linux-wayland   linux-x64 with GLFW 3.4 X11+Wayland multi-platform backend.
                  Replaces the NuGet X11-only lib.  Writes .wayland-enabled so
                  the game prefers Wayland at runtime when safe.

  linux-arm64     linux/aarch64 cross-compiled from this x64 host.

  win-arm64       Windows/aarch64 cross-compiled (Zig bundles the Windows SDK).

Usage
─────
    uv run scripts/setup-native-libs.py                      # all three
    uv run scripts/setup-native-libs.py linux-wayland
    uv run scripts/setup-native-libs.py linux-arm64 win-arm64

System requirements
───────────────────
  linux-wayland   wayland-scanner must be on PATH (generates protocol headers
                  from the XMLs already bundled in the Raylib source tree):
      Arch:   sudo pacman -S wayland
      Debian: sudo apt install libwayland-dev

                  X11 headers for the dual backend (X11+Wayland):
      Arch:   sudo pacman -S libx11
      Debian: sudo apt install libx11-dev

  linux-arm64     X11 headers (used as cross-target stubs; architecture-neutral):
      Arch:   sudo pacman -S libx11
      Debian: sudo apt install libx11-dev

  win-arm64       No system dependencies — Zig bundles the Windows SDK.

  Zig 0.13.0 is downloaded automatically to ./build/toolchains/ (git-ignored).
"""

from __future__ import annotations

import argparse
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

# ── configuration ─────────────────────────────────────────────────────────────

REPO_ROOT  = Path(__file__).resolve().parent.parent
BUILD_DIR  = REPO_ROOT / "build"
TOOLS_DIR  = BUILD_DIR / "toolchains"
SRC_DIR    = BUILD_DIR / "raylib-src"
RUNTIMES   = REPO_ROOT / "src" / "JohnnyAppleseed" / "runtimes"

ZIG_VERSION = "0.13.0"
ZIG_BASE    = f"https://ziglang.org/download/{ZIG_VERSION}"
ZIG_ARCHIVES = {
    "linux-x64":   f"zig-linux-x86_64-{ZIG_VERSION}.tar.xz",
    "linux-arm64": f"zig-linux-aarch64-{ZIG_VERSION}.tar.xz",
    "macos-x64":   f"zig-macos-x86_64-{ZIG_VERSION}.tar.xz",
    "macos-arm64": f"zig-macos-aarch64-{ZIG_VERSION}.tar.xz",
}

# Must match the version the Raylib-cs 8.0.0 NuGet package was compiled from.
RAYLIB_VERSION = "5.5"
RAYLIB_URL     = f"https://github.com/raysan5/raylib/archive/refs/tags/{RAYLIB_VERSION}.tar.gz"

# ── target definitions ────────────────────────────────────────────────────────
#
# zig_target:   value passed to `zig build -Dtarget=…`
# display:      value passed to `-Dlinux_display_backend=…` (Linux only)
# out_name:     filename produced in zig-out/lib/ after the build
# dest:         where we copy the result in the project's runtimes/ tree
# sentinel:     optional file to create alongside dest (for runtime detection)
# elf_machine:  ELF e_machine constant to verify (Linux libs)
# pe_machine:   PE Machine constant to verify (Windows DLLs)

TARGETS: dict[str, dict] = {
    # ── linux-wayland ──────────────────────────────────────────────────────────
    # Build natively (no -Dtarget): Zig treats this as a host build and discovers
    # system libraries via pkg-config automatically.  Passing an explicit
    # x86_64-linux-gnu target triggers Zig's cross-compilation path which uses
    # the "paths_first" strategy with searched paths: none — breaking all
    # linkSystemLibrary() calls (X11, GLX, wayland-client, etc.).
    "linux-wayland": {
        "zig_target": None,            # native — Zig uses pkg-config for system libs
        "display":    "Both",          # X11 + Wayland multi-platform GLFW 3.4
        "out_name":   "libraylib.so",
        "dest":       RUNTIMES / "linux-x64"  / "native" / "libraylib.so",
        "sentinel":   RUNTIMES / "linux-x64"  / "native" / ".wayland-enabled",
        "elf_machine": 0x3E,            # EM_X86_64
    },

    # ── linux-arm64 ────────────────────────────────────────────────────────────
    # Cannot be cross-compiled from a Linux x64 host via zig build: Zig's
    # --search-prefix mechanism adds the host's x64 glibc headers to the
    # include path, which conflicts with Zig's bundled aarch64 glibc headers
    # and produces 50+ errors.  The only reliable path from a Linux x64 host
    # is to download the pre-built library from Raylib's GitHub release.
    # Alternatively, build natively on arm64 hardware or inside a Docker
    # container with QEMU emulation (see build_linux_arm64_download below).
    "linux-arm64": {
        "download": True,
        "out_name":   "libraylib.so",
        "dest":       RUNTIMES / "linux-arm64" / "native" / "libraylib.so",
        "elf_machine": 0xB7,
    },

    # ── win-arm64 ──────────────────────────────────────────────────────────────
    # Zig bundles the Windows SDK (kernel32, gdi32, opengl32, etc.) so
    # cross-linking from Linux to aarch64-windows works without any extra setup.
    "win-arm64": {
        "zig_target": "aarch64-windows-gnu",
        "out_name":   "raylib.dll",
        "dest":       RUNTIMES / "win-arm64" / "native" / "raylib.dll",
        "pe_machine":  0xAA64,          # IMAGE_FILE_MACHINE_ARM64
    },
}

# ── utilities ─────────────────────────────────────────────────────────────────

def download(url: str, dest: Path, desc: str) -> Path:
    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.exists():
        print(f"  Cached: {dest.name}")
        return dest
    print(f"  Downloading {desc} …")
    r = requests.get(url, stream=True, timeout=180)
    r.raise_for_status()
    total = int(r.headers.get("content-length", 0))
    done  = 0
    with open(dest, "wb") as f:
        for chunk in r.iter_content(65536):
            f.write(chunk)
            done += len(chunk)
            if total:
                pct = done * 100 // total
                mb  = done // 1_048_576
                print(f"\r  {pct:3d}%  {mb} / {total // 1_048_576} MB", end="", flush=True)
    print()
    return dest


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
        print(f"  Zig {ZIG_VERSION} at {zig_dir}")
        return zig_bin

    archive_name = ZIG_ARCHIVES.get(host_key())
    if not archive_name:
        raise RuntimeError(f"No Zig archive known for host {host_key()!r}")

    archive = download(f"{ZIG_BASE}/{archive_name}",
                       BUILD_DIR / "cache" / archive_name, f"Zig {ZIG_VERSION}")
    print("  Extracting Zig …")
    zig_dir.mkdir(parents=True, exist_ok=True)
    if archive_name.endswith(".tar.xz"):
        with tarfile.open(archive, "r:xz") as tf:
            prefix = tf.getmembers()[0].name.split("/")[0] + "/"
            for m in tf.getmembers():
                m.name = m.name[len(prefix):]
                if m.name:
                    tf.extract(m, zig_dir)
    else:
        with zipfile.ZipFile(archive) as zf:
            zf.extractall(zig_dir)

    if not zig_bin.exists():
        candidates = list(zig_dir.glob("*/zig")) + list(zig_dir.glob("zig"))
        if not candidates:
            raise RuntimeError(f"zig binary not found in {zig_dir}")
        zig_bin = candidates[0]

    zig_bin.chmod(zig_bin.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    print(f"  Zig ready")
    return zig_bin


def ensure_raylib_source() -> Path:
    src = SRC_DIR / f"raylib-{RAYLIB_VERSION}"
    if (src / "src" / "raylib.h").exists():
        print(f"  Raylib {RAYLIB_VERSION} source at {src}")
        return src

    archive = download(RAYLIB_URL, BUILD_DIR / "cache" / f"raylib-{RAYLIB_VERSION}.tar.gz",
                       f"Raylib {RAYLIB_VERSION} source")
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


# ── binary verification ───────────────────────────────────────────────────────

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


# ── build ─────────────────────────────────────────────────────────────────────

def build_linux_arm64_download(cfg: dict) -> None:
    """
    Download the pre-built Raylib arm64 shared library from the official
    GitHub release.  This is the only reliable approach when building from
    a Linux x64 host; cross-compilation via zig build introduces header
    conflicts between Zig's bundled aarch64 glibc and the host's x64 headers.

    If the download fails (release asset not found), the fallback is to build
    natively on arm64 hardware or in an arm64 Docker container:

        docker run --rm --platform linux/arm64 \\
            -v "$(pwd)/build/raylib-src:/raylib" \\
            -v "$(pwd)/src/JohnnyAppleseed/runtimes/linux-arm64/native:/out" \\
            alpine sh -c \\
            "apk add zig libx11-dev && \\
             cd /raylib/raylib-{RAYLIB_VERSION} && \\
             zig build -Dshared=true -Dlinux_display_backend=X11 \\
                       -Doptimize=ReleaseSafe && \\
             cp zig-out/lib/libraylib.so /out/"
    """
    dest: Path = cfg["dest"]
    dest.parent.mkdir(parents=True, exist_ok=True)

    # Raylib GitHub releases use various naming conventions across versions.
    candidates = [
        f"https://github.com/raysan5/raylib/releases/download/{RAYLIB_VERSION}/raylib-{RAYLIB_VERSION}_linux_aarch64.tar.gz",
        f"https://github.com/raysan5/raylib/releases/download/{RAYLIB_VERSION}/raylib-{RAYLIB_VERSION}_linux_arm64.tar.gz",
    ]

    import tarfile as tf_mod
    for url in candidates:
        print(f"  Trying: {url}")
        try:
            r = requests.get(url, stream=True, timeout=120, allow_redirects=True)
            if r.status_code == 404:
                print("  Not found, trying next …")
                continue
            r.raise_for_status()
            archive = BUILD_DIR / "cache" / url.rsplit("/", 1)[-1]
            archive.parent.mkdir(parents=True, exist_ok=True)
            total = int(r.headers.get("content-length", 0))
            done  = 0
            with open(archive, "wb") as f:
                for chunk in r.iter_content(65536):
                    f.write(chunk)
                    done += len(chunk)
                    if total:
                        print(f"\r  {done*100//total:3d}%", end="", flush=True)
            print()

            # Extract the shared library from the archive
            with tf_mod.open(archive, "r:gz") as tar:
                for member in tar.getmembers():
                    if member.name.endswith("libraylib.so") or \
                       "libraylib.so." in member.name.split("/")[-1]:
                        member.name = "libraylib.so"
                        tar.extract(member, dest.parent)
                        print(f"  ✓ {dest.relative_to(REPO_ROOT)}  "
                              f"({dest.stat().st_size // 1024} KB)")
                        # Verify
                        if not verify_elf(dest, cfg["elf_machine"]):
                            dest.unlink()
                            raise RuntimeError("ELF architecture check failed")
                        return

            print("  Archive did not contain libraylib.so")
        except requests.RequestException as e:
            print(f"  Download error: {e}")

    raise RuntimeError(
        f"Could not download linux-arm64 libraylib.so for Raylib {RAYLIB_VERSION}.\n\n"
        "  To build it yourself on arm64 hardware or in Docker:\n\n"
        f"    docker run --rm --platform linux/arm64 \\\n"
        f"        -v \"$(pwd)/build/raylib-src:/raylib\" \\\n"
        f"        -v \"$(pwd)/src/JohnnyAppleseed/runtimes/linux-arm64/native:/out\" \\\n"
        f"        alpine sh -c \\\n"
        f"        \"apk add zig libx11-dev && \\\n"
        f"         cd /raylib/raylib-{RAYLIB_VERSION} && \\\n"
        f"         zig build -Dshared=true -Dlinux_display_backend=X11 \\\n"
        f"                   -Doptimize=ReleaseSafe && \\\n"
        f"         cp zig-out/lib/libraylib.so /out/\"\n"
    )


def build(zig: Path, raylib: Path, name: str, cfg: dict) -> None:
    # Clean stale zig-out so we always pick up a fresh library
    zig_out = raylib / "zig-out"
    if zig_out.exists():
        shutil.rmtree(zig_out)

    cmd = [str(zig), "build", "-Doptimize=ReleaseSafe", "-Dshared=true"]
    if cfg.get("zig_target"):
        cmd.append(f"-Dtarget={cfg['zig_target']}")
    if "display" in cfg:
        cmd.append(f"-Dlinux_display_backend={cfg['display']}")
    for prefix in cfg.get("search_prefixes", []):
        cmd += ["--search-prefix", prefix]

    # Build the subprocess environment:
    # • PATH must include /usr/sbin and /usr/local/bin so that wayland-scanner
    #   and pkg-config are findable (they live in /usr/sbin on some distros).
    # • ZIG_GLOBAL_CACHE_DIR keeps Zig's cache inside our build/ tree.
    env = os.environ.copy()
    extra_path = "/usr/sbin:/usr/local/sbin:/usr/local/bin:/usr/bin:/bin"
    env["PATH"] = extra_path + ":" + env.get("PATH", "")
    env["ZIG_GLOBAL_CACHE_DIR"] = str(BUILD_DIR / "zig-cache")

    target_str = cfg.get("zig_target") or "native"
    print(f"  Running: zig build -Dtarget={target_str} -Dshared=true"
          + (f" -Dlinux_display_backend={cfg['display']}" if "display" in cfg else ""))
    print("  (output streamed below — this may take a minute on first run)\n")

    # Stream output directly to terminal so the user can see progress and errors
    result = subprocess.run(cmd, cwd=raylib, env=env)
    if result.returncode != 0:
        raise RuntimeError(f"`zig build` exited with code {result.returncode}")

    # Locate the produced library.
    # Linux shared libs → zig-out/lib/libraylib.so
    # Windows DLLs      → zig-out/bin/raylib.dll  (import lib in zig-out/lib/)
    out_name = cfg["out_name"]
    candidates: list[Path] = []
    for subdir in ("lib", "bin"):
        d = zig_out / subdir
        candidates.extend(d.glob(out_name) if d.exists() else [])
    if not candidates:
        all_files = []
        for subdir in ("lib", "bin"):
            d = zig_out / subdir
            all_files += list(d.glob("*")) if d.exists() else []
        raise RuntimeError(
            f"Expected {out_name} in zig-out/lib/ or zig-out/bin/\n"
            f"Found: {[f.name for f in all_files]}"
        )
    lib_file = candidates[0]

    # Verify the binary architecture
    if "elf_machine" in cfg:
        if not verify_elf(lib_file, cfg["elf_machine"]):
            raise RuntimeError(f"ELF machine check failed for {lib_file}")
    elif "pe_machine" in cfg:
        if not verify_pe(lib_file, cfg["pe_machine"]):
            raise RuntimeError(f"PE machine check failed for {lib_file}")

    # Copy to project runtimes/
    dest: Path = cfg["dest"]
    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(lib_file, dest)
    print(f"\n  ✓ {dest.relative_to(REPO_ROOT)}  ({dest.stat().st_size // 1024} KB)")

    # Write the sentinel that LinuxDisplay.cs reads at runtime
    if sentinel := cfg.get("sentinel"):
        sentinel.write_text("wayland-enabled\n")
        print(f"  ✓ {sentinel.relative_to(REPO_ROOT)}")


# ── requirements checks ───────────────────────────────────────────────────────

def check_requirements(name: str) -> None:
    """Raise with a clear message if a required tool is missing."""
    if name == "linux-wayland":
        missing = []
        if not Path("/usr/include/X11/Xlib.h").exists() and \
           not Path("/usr/local/include/X11/Xlib.h").exists():
            missing.append("X11 headers (libx11-dev / libx11)")
        scanner = shutil.which("wayland-scanner") or \
                  (Path("/usr/sbin/wayland-scanner").exists()
                   and "/usr/sbin/wayland-scanner")
        if not scanner:
            missing.append("wayland-scanner (libwayland-dev / wayland package)")
        if missing:
            lines = "\n  ".join(missing)
            raise RuntimeError(
                f"Missing requirements for {name}:\n  {lines}\n"
                "  Arch:   sudo pacman -S libx11 wayland\n"
                "  Debian: sudo apt install libx11-dev libwayland-dev"
            )


# ── main ──────────────────────────────────────────────────────────────────────

def main() -> None:
    parser = argparse.ArgumentParser(
        description="Build missing Raylib native libs using Raylib's build.zig + Zig 0.13.0"
    )
    parser.add_argument(
        "targets", nargs="*",
        default=list(TARGETS),
        help=f"Targets to build (default: all). Choices: {' '.join(TARGETS)}",
    )
    parser.add_argument(
        "--rebuild", action="store_true",
        help="Rebuild even if the output already exists",
    )
    args = parser.parse_args()

    unknown = [t for t in args.targets if t not in TARGETS]
    if unknown:
        parser.error(f"Unknown target(s): {', '.join(unknown)}.  Valid: {', '.join(TARGETS)}")

    os.chdir(REPO_ROOT)

    print("── Toolchain setup ─────────────────────────────────────────────────")
    zig    = ensure_zig()
    raylib = ensure_raylib_source()
    print()

    for name in args.targets:
        cfg  = TARGETS[name]
        dest = cfg["dest"]

        print(f"── [{name}] ──────────────────────────────────────────────────────────")

        if dest.exists() and not args.rebuild:
            print(f"  Already built: {dest.relative_to(REPO_ROOT)} "
                  f"(pass --rebuild to force)")
            continue

        try:
            if cfg.get("download"):
                build_linux_arm64_download(cfg)
            else:
                check_requirements(name)
                build(zig, raylib, name, cfg)
        except Exception as e:
            print(f"\n  ERROR: {e}", file=sys.stderr)
            if dest.exists():
                dest.unlink()
            if (sentinel := cfg.get("sentinel")) and sentinel.exists():
                sentinel.unlink()

        print()

    print("Done.  Run `uv run scripts/package.py` to create distribution archives.")


if __name__ == "__main__":
    main()
