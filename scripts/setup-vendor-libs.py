#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = [
#   "requests>=2.31",
# ]
# ///
"""
Provision vendored *source* dependencies WITHOUT committing their code to the repo.

Sibling to scripts/setup-native-libs.py (which fetches native raylib libraries):
this fetches third-party source libraries, builds OUR OWN copies, and drops the
built artifacts into the gitignored build/ tree so they are referenced by the
game but never checked in.

Managed dependencies (like ink below) compile to platform-agnostic IL, so one
build serves every target (win/linux/mac, x64/arm64). If a native source dep is
added later, it would build per-RID here the way setup-native-libs.py does.

Currently managed
─────────────────
  ink   inkle's ink narrative engine — runtime + compiler (MIT). Fetched from the
        latest GitHub release source tarball and built to netstandard2.0 DLLs the
        game references via HintPath (see JohnnyAppleseed.csproj). NuGet is not
        usable: nuget.org only carries a stale 2017 runtime and no compiler.

Usage
─────
    uv run scripts/setup-vendor-libs.py            # all deps
    uv run scripts/setup-vendor-libs.py ink        # one dep
    uv run scripts/setup-vendor-libs.py --rebuild  # force re-download + rebuild

Requirements: the .NET SDK (dotnet) on PATH. No packages are installed
system-wide; everything lands under ./build/ (gitignored).
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tarfile
from pathlib import Path

import requests

# ── paths ────────────────────────────────────────────────────────────────────
REPO_ROOT  = Path(__file__).resolve().parent.parent
BUILD_DIR  = REPO_ROOT / "build"
VENDOR_DIR = BUILD_DIR / "vendor"       # gitignored (build/ is in .gitignore)
CACHE_DIR  = BUILD_DIR / "cache"

GITHUB_LATEST = "https://api.github.com/repos/{repo}/releases/latest"
SOURCE_TARBALL = "https://github.com/{repo}/archive/refs/tags/{tag}.tar.gz"

# ── dependency registry ──────────────────────────────────────────────────────
#   repo          GitHub owner/name; the latest release tag is fuzzy-resolved.
#   build_project project to build (its ProjectReferences are built too).
#   tfm           target framework whose output DLLs we collect.
#   artifacts     built DLL names to copy out (fuzzy-located under bin/).
#   dest          gitignored folder the game references by HintPath.
#   patch         optional (file, old, new) source edit applied before building
#                 (here: drop the runtime's ancient netstandard1.0 target).
VENDOR_DEPS: dict[str, dict] = {
    "ink": {
        "repo":          "inkle/ink",
        "build_project": "compiler/ink_compiler.csproj",
        "tfm":           "netstandard2.0",
        "artifacts":     ["ink-engine-runtime.dll", "ink_compiler.dll"],
        "dest":          VENDOR_DIR / "ink",
        "patch":         ("ink-engine-runtime/ink-engine-runtime.csproj",
                          "netstandard1.0;netstandard2.0", "netstandard2.0"),
    },
}


# ── helpers ──────────────────────────────────────────────────────────────────
def die(msg: str) -> None:
    print(f"ERROR: {msg}", file=sys.stderr)
    sys.exit(1)


def fetch_latest_tag(repo: str) -> str:
    print(f"  querying GitHub for the latest {repo} release …")
    r = requests.get(GITHUB_LATEST.format(repo=repo), timeout=30,
                     headers={"Accept": "application/vnd.github.v3+json"})
    r.raise_for_status()
    tag = r.json()["tag_name"]
    print(f"  latest: {tag}")
    return tag


def download(url: str, dest: Path, desc: str) -> Path:
    CACHE_DIR.mkdir(parents=True, exist_ok=True)
    if dest.exists() and dest.stat().st_size > 0:
        print(f"  cached: {dest.name}")
        return dest
    print(f"  downloading {desc} …")
    r = requests.get(url, stream=True, timeout=300)
    r.raise_for_status()
    with open(dest, "wb") as f:
        for chunk in r.iter_content(65536):
            f.write(chunk)
    return dest


def download_source(name: str, repo: str, tag: str) -> Path:
    tarball = download(SOURCE_TARBALL.format(repo=repo, tag=tag),
                       CACHE_DIR / f"{name}-{tag}.tar.gz", f"{repo} {tag} source")
    src_parent = VENDOR_DIR / f"{name}-src"
    if src_parent.exists():
        shutil.rmtree(src_parent)
    src_parent.mkdir(parents=True, exist_ok=True)
    with tarfile.open(tarball, "r:gz") as tf:
        tf.extractall(src_parent)
    roots = [p for p in src_parent.iterdir() if p.is_dir()]
    if not roots:
        die(f"{name}: source tarball contained no directory")
    return roots[0]


def apply_patch(src_root: Path, patch: tuple[str, str, str]) -> None:
    rel, old, new = patch
    f = src_root / rel
    if not f.exists():
        print(f"  note: patch target {rel} not found — skipping (upstream may have changed)")
        return
    text = f.read_text()
    if old in text:
        f.write_text(text.replace(old, new))
        print(f"  patched {rel}: '{old}' → '{new}'")


def build_project(src_root: Path, project: str) -> None:
    proj = src_root / project
    if not proj.exists():
        die(f"build project not found: {proj}")
    print(f"  dotnet build {project} -c Release")
    result = subprocess.run(
        ["dotnet", "build", str(proj), "-c", "Release", "--nologo", "-v", "quiet"],
        cwd=src_root)
    if result.returncode != 0:
        die(f"`dotnet build {project}` failed with code {result.returncode}")


def collect_artifacts(src_root: Path, artifacts: list[str], tfm: str, dest: Path) -> None:
    dest.mkdir(parents=True, exist_ok=True)
    for name in artifacts:
        # Fuzzy-locate: prefer the requested TFM's Release output, then anything.
        candidates = (list(src_root.glob(f"**/bin/Release/{tfm}/{name}"))
                      or list(src_root.glob(f"**/bin/**/{name}"))
                      or list(src_root.glob(f"**/{name}")))
        if not candidates:
            die(f"built artifact not found after build: {name}")
        shutil.copy2(candidates[0], dest / name)
        size = (dest / name).stat().st_size // 1024
        print(f"  ✓ {dest.relative_to(REPO_ROOT)}/{name}  ({size} KB)")


def provision(name: str, cfg: dict, rebuild: bool) -> None:
    dest: Path = cfg["dest"]
    marker = dest / cfg["artifacts"][-1]
    if marker.exists() and not rebuild:
        print(f"  already built: {dest.relative_to(REPO_ROOT)}  — pass --rebuild to force")
        return

    tag = fetch_latest_tag(cfg["repo"])
    src_root = download_source(name, cfg["repo"], tag)
    if cfg.get("patch"):
        apply_patch(src_root, cfg["patch"])
    build_project(src_root, cfg["build_project"])
    collect_artifacts(src_root, cfg["artifacts"], cfg["tfm"], dest)


# ── main ─────────────────────────────────────────────────────────────────────
def main() -> None:
    parser = argparse.ArgumentParser(
        description="Fetch + build vendored source dependencies into build/ (gitignored).",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=f"Available deps: {' '.join(VENDOR_DEPS)}")
    parser.add_argument("deps", nargs="*", default=list(VENDOR_DEPS),
                        help="Which deps to provision (default: all)")
    parser.add_argument("--rebuild", action="store_true",
                        help="Re-download and rebuild even if artifacts already exist")
    args = parser.parse_args()

    unknown = [d for d in args.deps if d not in VENDOR_DEPS]
    if unknown:
        parser.error(f"unknown dep(s): {', '.join(unknown)}. valid: {', '.join(VENDOR_DEPS)}")

    if shutil.which("dotnet") is None:
        die("the .NET SDK (dotnet) is required and was not found on PATH")

    os.chdir(REPO_ROOT)
    for name in args.deps:
        print(f"── [{name}] " + "─" * max(1, 56 - len(name)))
        provision(name, VENDOR_DEPS[name], args.rebuild)
        print()

    print("Done. Built libraries live under build/vendor/ (gitignored).")
    print("The game references them by HintPath; now run a normal `dotnet build`.")


if __name__ == "__main__":
    main()
