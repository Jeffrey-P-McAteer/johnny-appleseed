#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = []
# ///
"""
Johnny Appleseed — hardware probe runner.

A thin wrapper that builds and runs the debug measurement binary
(src/JohnnyAppleseed.Probe). Unlike the game, the probe is a plain local exe and
is NOT part of the cross-platform packaging pipeline — it is meant to run on the
machine whose hardware you are testing.

Usage (from repo root):
    uv run scripts/probe.py                 # interactive gamepad probe (window)
    uv run scripts/probe.py list            # enumerate gamepads + input devices
    uv run scripts/probe.py raw             # raw kernel events from /dev/input/js0
    uv run scripts/probe.py raw /dev/input/js1

    uv run scripts/probe.py -c Debug        # pick a build config (default: Debug)

Any argument that isn't a known runner flag is forwarded to the probe binary.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
PROJECT = REPO_ROOT / "src" / "JohnnyAppleseed.Probe" / "JohnnyAppleseed.Probe.csproj"


def main(argv: list[str]) -> int:
    config = "Debug"
    forwarded: list[str] = []

    # Pull out our own -c/--config flag; forward everything else to the probe.
    i = 0
    while i < len(argv):
        arg = argv[i]
        if arg in ("-c", "--config"):
            if i + 1 >= len(argv):
                print("error: -c/--config needs a value (Debug|Release)", file=sys.stderr)
                return 2
            config = argv[i + 1]
            i += 2
            continue
        forwarded.append(arg)
        i += 1

    if not PROJECT.exists():
        print(f"error: probe project not found at {PROJECT}", file=sys.stderr)
        return 1

    cmd = [
        "dotnet", "run",
        "--project", str(PROJECT),
        "-c", config,
        "--",
        *forwarded,
    ]
    print("+ " + " ".join(cmd), file=sys.stderr)
    try:
        return subprocess.call(cmd)
    except FileNotFoundError:
        print("error: 'dotnet' not found on PATH. Install the .NET 9 SDK.", file=sys.stderr)
        return 127
    except KeyboardInterrupt:
        return 130


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
