#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = [
#   "requests>=2.31",
# ]
# ///
"""
Johnny Appleseed — Aseprite build-and-launch wrapper.

Aseprite (the pixel-art / sprite-animation editor we author game art in) ships no
free Linux binary, so this downloads its source + the matching prebuilt Skia,
compiles it under ./build/ (gitignored), and launches the built binary — then on
every subsequent run skips straight to launching it.

Like scripts/probe.py, this doubles as a *binary intermediary*: any argument it
doesn't recognise is forwarded to the aseprite executable, so it can stand in for
`aseprite` in future scripting (e.g. headless sprite-sheet export via the CLI):

    uv run scripts/run-aseprite.py                       # launch the editor (build first if needed)
    uv run scripts/run-aseprite.py graphics/foo.aseprite # open a file
    uv run scripts/run-aseprite.py -- -b sheet.aseprite \\
        --sheet sheet.png --data sheet.json              # CLI export (args after -- are verbatim)
    uv run scripts/run-aseprite.py --no-run              # build only, don't launch (CI/scripting)
    uv run scripts/run-aseprite.py --rebuild             # wipe extracted src + build, recompile
    uv run scripts/run-aseprite.py -j 4                  # cap ninja parallelism

Our own flags (parsed and consumed before forwarding):
    --rebuild            re-extract sources and recompile from scratch
    --no-run/--setup-only  provision + build but do not launch
    -j / --jobs N        ninja parallel job count (default: all cores)
    --                   everything after this is forwarded to aseprite verbatim

Steps are individually resumable — each checks for its own output and is skipped
if already present, so an interrupted build continues where it left off, and a
fully-built tree launches with NO network access at all:

    1. source   download the latest Aseprite release Source.zip → build/aseprite/src
    2. skia     download the matching prebuilt Skia (x64 only) → build/aseprite/skia
    3. compile  cmake + ninja                                  → build/aseprite/build/bin/aseprite
    4. launch   exec the binary, forwarding arguments

System requirements (this script cannot install them — they need root):
    A C++ toolchain + build tools. clang is preferred (the officially-supported
    Linux path); g++ is used as a fallback. Plus cmake, ninja, and X11/GL/
    fontconfig headers. If any are missing, the script prints the exact package
    command for your distro and stops. See https://github.com/aseprite/aseprite/blob/main/INSTALL.md

Version handling: the Aseprite release and the Skia release are both resolved to
their *latest* GitHub release at build time (no hardcoded versions, matching
setup-native-libs.py). Aseprite and Skia are cut in lockstep, so latest+latest
matches in practice; if a future Skia release ever races ahead of Aseprite, pin
Skia by setting SKIA_RELEASE below (or the ASEPRITE_SKIA_RELEASE env var).
"""

from __future__ import annotations

import argparse
import os
import platform
import shutil
import stat
import subprocess
import sys
import zipfile
from pathlib import Path

import requests

# ── paths ────────────────────────────────────────────────────────────────────
REPO_ROOT     = Path(__file__).resolve().parent.parent
BUILD_DIR     = REPO_ROOT / "build"
CACHE_DIR     = BUILD_DIR / "cache"            # shared with the other setup scripts
ASEPRITE_DIR  = BUILD_DIR / "aseprite"         # all Aseprite state (gitignored)
SRC_DIR       = ASEPRITE_DIR / "src"           # extracted Aseprite source tree
SKIA_DIR      = ASEPRITE_DIR / "skia"          # extracted prebuilt Skia
CMAKE_BUILD   = ASEPRITE_DIR / "build"         # cmake/ninja out-of-source build dir
SRC_STAMP     = SRC_DIR / ".aseprite-version"  # records the extracted release tag

ASEPRITE_REPO = "aseprite/aseprite"
SKIA_REPO     = "aseprite/skia"

# Pin Skia here (a release tag like "m124-abcdef") only if latest-vs-latest ever
# breaks; None → auto-resolve the latest Skia release. Env var wins if set.
SKIA_RELEASE: str | None = os.environ.get("ASEPRITE_SKIA_RELEASE") or None

GITHUB_LATEST  = "https://api.github.com/repos/{repo}/releases/latest"
GITHUB_BY_TAG  = "https://api.github.com/repos/{repo}/releases/tags/{tag}"

# distro-id → the package-install command that provides Aseprite's build deps.
# (from aseprite/INSTALL.md; this script only *prints* these — installing needs root.)
DISTRO_PACKAGES = {
    "arch":   "sudo pacman -S --needed gcc clang cmake ninja unzip libx11 libxcursor "
              "libxi libxrandr mesa fontconfig libwebp",
    "debian": "sudo apt-get install -y g++ clang cmake ninja-build unzip libx11-dev "
              "libxcursor-dev libxi-dev libxrandr-dev libgl1-mesa-dev libfontconfig1-dev",
    "fedora": "sudo dnf install -y gcc-c++ clang libcxx-devel cmake ninja-build unzip "
              "libX11-devel libXcursor-devel libXi-devel libXrandr-devel "
              "mesa-libGL-devel fontconfig-devel",
    "suse":   "sudo zypper install gcc-c++ clang cmake ninja unzip libX11-devel "
              "libXcursor-devel libXi-devel libXrandr-devel Mesa-libGL-devel fontconfig-devel",
}


# ── helpers ──────────────────────────────────────────────────────────────────
def die(msg: str) -> None:
    print(f"ERROR: {msg}", file=sys.stderr)
    sys.exit(1)


def hr(label: str) -> None:
    print(f"── {label} " + "─" * max(1, 60 - len(label)))


def arch_token() -> str:
    """Map this machine to Aseprite's Skia arch naming (x64 / arm64)."""
    m = platform.machine().lower()
    if m in ("x86_64", "amd64"):
        return "x64"
    if m in ("aarch64", "arm64"):
        return "arm64"
    die(f"unsupported CPU architecture for a prebuilt Skia: {m}")


def distro_id() -> str:
    """Best-effort /etc/os-release ID → one of DISTRO_PACKAGES keys (or '')."""
    try:
        fields = dict(
            line.split("=", 1)
            for line in Path("/etc/os-release").read_text().splitlines()
            if "=" in line
        )
    except OSError:
        return ""
    ids = (fields.get("ID", "") + " " + fields.get("ID_LIKE", "")).replace('"', "").lower()
    for key in ("arch", "debian", "fedora", "suse"):
        if key in ids or (key == "debian" and "ubuntu" in ids):
            return key
    return ""


def release_json(repo: str, tag: str | None) -> dict:
    url = GITHUB_BY_TAG.format(repo=repo, tag=tag) if tag else GITHUB_LATEST.format(repo=repo)
    print(f"  querying GitHub for {repo} {tag or 'latest'} release …")
    r = requests.get(url, timeout=30, headers={"Accept": "application/vnd.github.v3+json"})
    r.raise_for_status()
    return r.json()


def pick_asset(rel: dict, must_contain: list[str]) -> tuple[str, str]:
    """Return (name, download-url) for the single asset matching all keywords."""
    hits = [
        a for a in rel.get("assets", [])
        if all(kw.lower() in a["name"].lower() for kw in must_contain)
    ]
    if not hits:
        names = ", ".join(a["name"] for a in rel.get("assets", [])) or "(none)"
        die(f"no {rel.get('tag_name', '?')} asset matched {must_contain}. assets: {names}")
    return hits[0]["name"], hits[0]["browser_download_url"]


def download(url: str, dest: Path, desc: str) -> Path:
    CACHE_DIR.mkdir(parents=True, exist_ok=True)
    if dest.exists() and dest.stat().st_size > 0:
        print(f"  cached: {dest.name}  ({dest.stat().st_size // (1024*1024)} MB)")
        return dest
    print(f"  downloading {desc} …")
    tmp = dest.with_suffix(dest.suffix + ".part")
    with requests.get(url, stream=True, timeout=600) as r:
        r.raise_for_status()
        with open(tmp, "wb") as f:
            for chunk in r.iter_content(1 << 20):
                f.write(chunk)
    tmp.replace(dest)  # atomic → a killed download never looks "cached"
    print(f"  downloaded {dest.stat().st_size // (1024*1024)} MB")
    return dest


def extract_zip(archive: Path, dest: Path) -> None:
    """Extract a zip, preserving unix permission bits (Skia/build tools rely on them)."""
    if dest.exists():
        shutil.rmtree(dest)
    dest.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive) as z:
        for info in z.infolist():
            z.extract(info, dest)
            mode = info.external_attr >> 16
            if mode:
                (dest / info.filename).chmod(mode)


def find_dir_containing(root: Path, needle: str) -> Path:
    """Locate the directory that holds `needle` (root itself, or one level down)."""
    if (root / needle).exists():
        return root
    for child in sorted(p for p in root.iterdir() if p.is_dir()):
        if (child / needle).exists():
            return child
    die(f"could not find '{needle}' under {root} after extraction")


# ── build tooling checks ─────────────────────────────────────────────────────
def pick_compiler() -> tuple[str, str, bool]:
    """Return (cc, cxx, is_clang). Prefer clang (official Linux path), else gcc."""
    if shutil.which("clang") and shutil.which("clang++"):
        return "clang", "clang++", True
    if shutil.which("gcc") and shutil.which("g++"):
        print("  note: clang not found — falling back to gcc (also builds fine with libstdc++ Skia)")
        return "gcc", "g++", False
    return "", "", False


def check_tools() -> tuple[str, str, bool]:
    missing = [t for t in ("cmake", "ninja") if shutil.which(t) is None]
    cc, cxx, is_clang = pick_compiler()
    if not cc:
        missing.append("clang (or gcc)")
    if missing:
        print(f"ERROR: missing build tools: {', '.join(missing)}", file=sys.stderr)
        hint = DISTRO_PACKAGES.get(distro_id())
        if hint:
            print(f"\nInstall them with:\n    {hint}\n", file=sys.stderr)
        else:
            print("\nInstall a C++ toolchain, cmake, ninja, and the X11/GL/fontconfig "
                  "dev headers.\nSee https://github.com/aseprite/aseprite/blob/main/INSTALL.md\n",
                  file=sys.stderr)
        sys.exit(1)
    return cc, cxx, is_clang


# ── steps ────────────────────────────────────────────────────────────────────
def ensure_source(rebuild: bool) -> None:
    """Download + extract the latest Aseprite release Source.zip into SRC_DIR."""
    rel = release_json(ASEPRITE_REPO, None)
    tag = rel["tag_name"]
    if not rebuild and (SRC_DIR / "CMakeLists.txt").exists() \
            and SRC_STAMP.exists() and SRC_STAMP.read_text().strip() == tag:
        print(f"  source ready: Aseprite {tag}")
        return
    name, url = pick_asset(rel, ["source", ".zip"])
    archive = download(url, CACHE_DIR / name, f"Aseprite {tag} source")

    print(f"  extracting {name} …")
    staging = ASEPRITE_DIR / "_src_staging"
    extract_zip(archive, staging)
    root = find_dir_containing(staging, "CMakeLists.txt")
    if SRC_DIR.exists():
        shutil.rmtree(SRC_DIR)
    root.replace(SRC_DIR)
    shutil.rmtree(staging, ignore_errors=True)
    SRC_STAMP.write_text(tag + "\n")
    print(f"  ✓ source: {SRC_DIR.relative_to(REPO_ROOT)}  (Aseprite {tag})")


def ensure_skia(rebuild: bool) -> Path:
    """Download + extract the matching prebuilt Skia. Return its Release-<arch> lib dir."""
    arch = arch_token()
    lib_dir = SKIA_DIR / "out" / f"Release-{arch}"
    if not rebuild and (lib_dir / "libskia.a").exists():
        print(f"  skia ready: {lib_dir.relative_to(REPO_ROOT)}")
        return lib_dir

    if arch != "x64":
        die("aseprite/skia ships no prebuilt Linux-arm64 Skia (only x64/x86). "
            "You'd have to compile Skia yourself — see "
            "https://github.com/aseprite/aseprite/blob/main/INSTALL.md#skia-on-linux")

    rel = release_json(SKIA_REPO, SKIA_RELEASE)
    # Linux asset is e.g. "Skia-Linux-Release-x64.zip". The libstdc++ requirement is
    # a *compiler* choice (handled by the cmake -stdlib flag), not part of the name.
    name, url = pick_asset(rel, ["linux", "release", arch, ".zip"])
    archive = download(url, CACHE_DIR / name, f"Skia {rel['tag_name']} ({arch})")

    print(f"  extracting {name} …")
    extract_zip(archive, SKIA_DIR)
    if not (lib_dir / "libskia.a").exists():
        # Some Skia zips wrap everything in a top dir — relocate to SKIA_DIR root.
        inner = find_dir_containing(SKIA_DIR, "out")
        if inner != SKIA_DIR:
            for child in inner.iterdir():
                child.replace(SKIA_DIR / child.name)
    if not (lib_dir / "libskia.a").exists():
        die(f"libskia.a not found at {lib_dir} after extracting Skia")
    print(f"  ✓ skia: {SKIA_DIR.relative_to(REPO_ROOT)}  ({rel['tag_name']})")
    return lib_dir


def compile_aseprite(skia_lib_dir: Path, cc: str, cxx: str, is_clang: bool, jobs: int) -> Path:
    binary = CMAKE_BUILD / "bin" / "aseprite"
    CMAKE_BUILD.mkdir(parents=True, exist_ok=True)

    env = os.environ.copy()
    env["CC"], env["CXX"] = cc, cxx

    if not (CMAKE_BUILD / "CMakeCache.txt").exists():
        print("  configuring (cmake) …")
        cmake_cmd = [
            "cmake",
            "-DCMAKE_BUILD_TYPE=RelWithDebInfo",
            "-DLAF_BACKEND=skia",
            f"-DSKIA_DIR={SKIA_DIR}",
            f"-DSKIA_LIBRARY_DIR={skia_lib_dir}",
            f"-DSKIA_LIBRARY={skia_lib_dir / 'libskia.a'}",
            "-G", "Ninja",
        ]
        if is_clang:
            # Prebuilt Skia is libstdc++; tell clang to match (gcc uses it by default).
            cmake_cmd[1:1] = [
                "-DCMAKE_CXX_FLAGS:STRING=-stdlib=libstdc++",
                "-DCMAKE_EXE_LINKER_FLAGS:STRING=-stdlib=libstdc++",
            ]
        cmake_cmd.append(str(SRC_DIR))
        if subprocess.run(cmake_cmd, cwd=CMAKE_BUILD, env=env).returncode != 0:
            die("cmake configuration failed")
    else:
        print("  cmake already configured — skipping")

    print(f"  building (ninja -j{jobs} aseprite) — this can take a while …")
    if subprocess.run(["ninja", f"-j{jobs}", "aseprite"],
                      cwd=CMAKE_BUILD, env=env).returncode != 0:
        die("ninja build failed")
    if not binary.exists():
        die(f"build finished but binary missing at {binary}")
    print(f"  ✓ built: {binary.relative_to(REPO_ROOT)}")
    return binary


def ensure_built(rebuild: bool, jobs: int) -> Path:
    """Provision + compile as needed; return the aseprite binary path."""
    binary = CMAKE_BUILD / "bin" / "aseprite"

    # Fast path: already built and not forcing a rebuild → launch with zero network.
    if binary.exists() and not rebuild:
        return binary

    if rebuild:
        for d in (SRC_DIR, SKIA_DIR, CMAKE_BUILD):
            shutil.rmtree(d, ignore_errors=True)  # cached zips are kept; only re-extract/rebuild

    cc, cxx, is_clang = check_tools()
    ASEPRITE_DIR.mkdir(parents=True, exist_ok=True)

    hr("source")
    ensure_source(rebuild)
    hr("skia")
    skia_lib_dir = ensure_skia(rebuild)
    hr("compile")
    return compile_aseprite(skia_lib_dir, cc, cxx, is_clang, jobs)


# ── arg parsing (probe.py-style: consume our flags, forward the rest) ─────────
def parse_args(argv: list[str]) -> tuple[argparse.Namespace, list[str]]:
    ns = argparse.Namespace(rebuild=False, no_run=False, jobs=os.cpu_count() or 4)
    forwarded: list[str] = []
    i = 0
    while i < len(argv):
        arg = argv[i]
        if arg == "--":                       # everything after is verbatim aseprite args
            forwarded.extend(argv[i + 1:])
            break
        if arg == "--rebuild":
            ns.rebuild = True
        elif arg in ("--no-run", "--setup-only"):
            ns.no_run = True
        elif arg in ("-j", "--jobs"):
            if i + 1 >= len(argv):
                die(f"{arg} needs a value")
            ns.jobs = int(argv[i + 1]); i += 1
        else:
            forwarded.append(arg)             # unknown → forward to aseprite
        i += 1
    return ns, forwarded


def main(argv: list[str]) -> int:
    ns, forwarded = parse_args(argv)
    os.chdir(REPO_ROOT)

    binary = ensure_built(ns.rebuild, ns.jobs)

    if ns.no_run:
        print(f"\nBuilt: {binary}")
        print("(--no-run) skipping launch.")
        return 0

    cmd = [str(binary), *forwarded]
    print("+ " + " ".join(cmd), file=sys.stderr)
    try:
        return subprocess.call(cmd)
    except KeyboardInterrupt:
        return 130


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
