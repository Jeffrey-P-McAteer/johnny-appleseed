#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = []
# ///

import tomllib
import os
import sys
import pathlib

REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
LICENSES_FILE = os.path.join(REPO_ROOT, 'licenses.toml')

IGNORED_FILE_NAMES = [
    '.gitkeep'
]

def lookup_file_author_and_license(filename):
    with open(LICENSES_FILE, 'rb') as fd:
        license_data = tomllib.load(fd)

    licenses = license_data.get('license', [])
    for license in licenses:
        if license.get('file', '').casefold() == filename.casefold():
            # Found a match! Ensure it has all fields
            author = license.get('author', '')
            license_spdx_id = license.get('license_spdx_id', '')
            license_file = license.get('license_file', '')

            license_details = license_spdx_id
            if len(license_details) < 1:
                license_details = license_file

            return (author, license_spdx_id)

    return None


def is_file_source_cited(filename):
    return lookup_file_author_and_license(filename) is not None

def template_toml_block(filename):
    return f'''
[[license]]
file = '{filename}'
license_spdx_id = '' # See https://spdx.org/licenses/ for a list of license IDs
license_file = '/dev/null' # TODO someday we will need to keep actual copies of the relevant license text in here, this will point to the correct license file path.
author = ''

'''.strip()

def main(argv: list[str]) -> int:
    artwork_and_audio_folders = [
        os.path.join(REPO_ROOT, 'graphics'),
        os.path.join(REPO_ROOT, 'audio'),
    ]
    for folder in artwork_and_audio_folders:
        for root, dirs, files in os.walk(folder):
            files = [f for f in files if not f in IGNORED_FILE_NAMES]
            for filename in files:
                full_path = os.path.join(root, filename)
                if not is_file_source_cited(filename):
                    print(f'NOT CITED: {full_path}')
                else:
                    author, license = lookup_file_author_and_license(filename)
                    warnings = False
                    if len(author) < 1:
                        print(f'WARNING: File {filename} has empty author! Set the author field to a value.')
                        warnings = True
                    if len(license) < 1:
                        print(f'WARNING: File {filename} has empty license details! Either license_spdx_id or license_file must be filled out')
                        warnings = True

                    if not warnings:
                        print(f'Good: {filename}')


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
