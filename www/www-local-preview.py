#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.12"
# dependencies = [
#    "cairosvg",
#    "pillow",
# ]
# ///

import http.server
import socketserver
import pathlib
import urllib.parse
import webbrowser
import threading
import os
import io
import json
import tempfile
import subprocess
import traceback
import time

PORT = 8000

ROOT = pathlib.Path(__file__).parent.resolve()
TEMP_FOLDER = tempfile.gettempdir()

# Main website
WEB_ROOT = ROOT

# Additional virtual mounts
MOUNTS = {
    "/graphics": ROOT.parent / "graphics",
    "/historic-progress": ROOT.parent / "historic-progress",
}

def startup_tmp_asset_renders():
    en = dict(os.environ)

    en.pop('VIRTUAL_ENV', None)
    en.pop('PYTHONHOME', None)
    en.pop('PYTHONPATH', None)
    en.pop('PYTHONSAFEPATH', None)
    en['PATH'] = os.pathsep.join(en['PATH'].split(os.pathsep)[1:])

    en['GIMP_INPUT'] = os.path.join(WEB_ROOT, '..', 'graphics', 'icon.xcf')
    en['GIMP_OUTPUT'] = os.path.join(TEMP_FOLDER, 'icon.png')

    if not os.path.exists(en['GIMP_OUTPUT']) or os.path.getmtime(en['GIMP_INPUT']) > os.path.getmtime(en['GIMP_OUTPUT']):
        subprocess.run([
            'gimp', '--no-interface',
                    '--batch-interpreter=python-fu-eval',
                    '-b', "exec(open('/j/claude/johnny-appleseed/scripts/gimp-automata/export-png.py').read())",
                    '--quit',
        ], env=en, check=True)


class VirtualDirectoryHandler(http.server.SimpleHTTPRequestHandler):
    def translate_path(self, path: str) -> str:
        # Remove query string and fragment
        path = urllib.parse.urlparse(path).path

        # Special-case grpahics/icon.png, same as the .github/workflows/static.yml
        if path == 'graphics/icon.png' or path == '/graphics/icon.png':
            return os.path.join(TEMP_FOLDER, 'icon.png')

        # Check mounted directories
        for prefix, directory in MOUNTS.items():
            if path == prefix or path.startswith(prefix + "/"):
                relative = path[len(prefix):].lstrip("/")
                return str(directory / relative)

        # Otherwise serve from main website
        relative = path.lstrip("/")
        return str(WEB_ROOT / relative)

    def log_message(self, fmt, *args):
        print(f"[HTTP] {self.address_string()} - {fmt % args}")

    def send_head(self):
        if self.path == "/site-index.json":
            return self.send_site_index()

        return super().send_head()

    def send_site_index(self):
        data = self.build_site_index()
        payload = json.dumps(data, indent=2).encode("utf-8")

        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()

        return io.BytesIO(payload)

    def build_site_index(self):
        urls = set()

        def add_directory(root: pathlib.Path, prefix: str):
            if not root.exists():
                return

            for file in root.rglob("*"):
                if not file.is_file():
                    continue

                rel = file.relative_to(root).as_posix()

                if prefix:
                    url = f"{prefix}/{rel}"
                else:
                    url = f"/{rel}"

                urls.add(url)

        # Main website
        add_directory(WEB_ROOT, "")

        # Mounted folders
        for prefix, directory in MOUNTS.items():
            add_directory(directory, prefix)

        return sorted(urls)

    def end_headers(self):
        # Disable all client-side caching.
        self.send_header("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0")
        self.send_header("Pragma", "no-cache")   # HTTP/1.0 clients
        self.send_header("Expires", "0")
        super().end_headers()


def open_browser():
    webbrowser.open(f"http://localhost:{PORT}/")


def main():
    os.chdir(WEB_ROOT)

    startup_tmp_asset_renders()

    while True:
        try:
            with socketserver.ThreadingTCPServer(("127.0.0.1", PORT), VirtualDirectoryHandler) as httpd:
                print(f"Serving on http://localhost:{PORT}")
                print(f"Website: {WEB_ROOT}")

                for prefix, directory in MOUNTS.items():
                    print(f"Mounted {prefix:<10} -> {directory}")

                threading.Timer(0.5, open_browser).start()

                try:
                    httpd.serve_forever()
                except KeyboardInterrupt:
                    print("\nStopping server...")
                    break
        except OSError:
            if 'already in use' in traceback.format_exc().lower():
                time.sleep(0.5)
                continue
            else:
                raise


if __name__ == "__main__":
    main()


