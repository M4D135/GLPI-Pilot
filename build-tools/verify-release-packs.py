#!/usr/bin/env python3
"""Inspect original pack bytes without extraction or executing a packaged file."""
from __future__ import annotations
import argparse
import hashlib
import json
import pathlib
import re
import stat
import zipfile

EDITIONS = ('PROD-LIGHT',)


def digest(stream) -> str:
    value = hashlib.sha256()
    for block in iter(lambda: stream.read(1024 * 1024), b''):
        value.update(block)
    return value.hexdigest()


def checked_path(name: str) -> str:
    if not name or name.startswith('/') or '\\' in name or ':' in name or any(ord(c) < 32 for c in name):
        raise ValueError('Unsafe archive or manifest path')
    parts = name.rstrip('/').split('/')
    if any(part in ('', '.', '..') or part.endswith((' ', '.')) for part in parts):
        raise ValueError('Non-canonical archive or manifest path')
    return name


def sums(text: str) -> dict[str, str]:
    result = {}
    for line in text.splitlines():
        match = re.fullmatch(r'([0-9a-fA-F]{64})  (.+)', line)
        if not match:
            raise ValueError('Invalid SHA-256 manifest line')
        name = checked_path(match[2])
        if name in result:
            raise ValueError('Duplicate manifest entry')
        result[name] = match[1].lower()
    return result


def verify_pack(path: pathlib.Path, version: str, commit: str, edition: str) -> dict:
    with path.open('rb') as stream:
        outer_digest = digest(stream)
    with zipfile.ZipFile(path) as archive:
        items = archive.infolist()
        if len(items) > 20000 or sum(item.file_size for item in items) > 12 * 1024**3:
            raise ValueError('Pack capacity exceeded')
        names, seen = set(), set()
        for item in items:
            name = checked_path(item.filename)
            if name.casefold() in seen or stat.S_ISLNK(item.external_attr >> 16) or item.flag_bits & 1:
                raise ValueError('Duplicate, symlink or encrypted ZIP entry')
            seen.add(name.casefold())
            if item.file_size > 2 * 1024**3:
                raise ValueError('Individual ZIP entry is too large')
            if not item.is_dir():
                names.add(name)
        if {name.split('/')[0] for name in names} != {'GLPI-Pilot.exe', 'GLPI-Pilot-Admin.exe', 'wad'}:
            raise ValueError('Incorrect pack root or nested artifact wrapper')
        private = {'sites.csv', 'glpi-pilot.deployment.json', 'workplace.json', 'scheduled-tours.json', 'dispatch-scenario.json', 'technician-preferences.json'}
        if any(pathlib.PurePosixPath(name).name.casefold() in private or name.lower().endswith(('.sqlite', '.sqlite3', '.db', '.glpilibrary.zip'))
               or re.search(r'(^|/)(?:pack[-_ ]?sites|sites[-_ ]?pack)(?:[./_-]|$)', name, re.I)
               or 'bootstrapstaging' in [part.casefold() for part in pathlib.PurePosixPath(name).parts] for name in names):
            raise ValueError('Operational/private data name found in a public pack')
        manifest = sums(archive.read('wad/SHA256SUMS.txt').decode('utf-8-sig'))
        if set(manifest) != names - {'wad/SHA256SUMS.txt'}:
            raise ValueError('Internal manifest does not cover exactly every packaged file')
        for name, expected in manifest.items():
            with archive.open(name) as stream:
                if digest(stream) != expected:
                    raise ValueError('Internal file digest mismatch: ' + name)
        info = json.loads(archive.read('wad/version.json'))
        if (info.get('version'), info.get('source_commit'), info.get('distribution')) != (version, commit, edition):
            raise ValueError('Version, source commit or distribution mismatch')
        # PROD and DEV carry the fixed WebView2 runtime; PROD-LIGHT relies on the runtime installed on Windows.
        bundled = any(name.startswith('wad/runtime/') for name in names)
        if edition == 'PROD-LIGHT':
            if info.get('webview2_loader') != 'evergreen-installed' or info.get('webview2_runtime') or bundled:
                raise ValueError('Light pack must declare the installed runtime and carry no bundled runtime')
        elif info.get('webview2_loader') != 'bundled-native-x64' or 'wad/runtime/webview2/msedgewebview2.exe' not in names:
            raise ValueError('Complete pack must declare and carry its fixed WebView2 runtime')
        if edition != 'DEV' and any(name.startswith('wad/sources/') for name in names):
            raise ValueError('Only the DEV pack carries the source export')
        pair = [manifest['GLPI-Pilot.exe'], manifest['GLPI-Pilot-Admin.exe']]
        if pair[0] == pair[1] or pair != [info.get('executable_sha256'), info.get('admin_executable_sha256')]:
            raise ValueError('Standard/Admin executable identity mismatch')
        if tuple(map(int, version.split('.'))) >= (3, 13, 0):
            required = ['css/ergonomics.css', 'js/reading-mode.js', 'js/pages/workspace-widget-reading.js',
                        'js/pages/ticket-control-hub.js', 'js/pages/field-preparation-editor.js', 'js/pages/terrain-tours.js',
                        'css/library-collection.css', 'js/pages/library-collection.js', 'js/pages/data-workspace.js']
            if any('wad/web/' + name not in names for name in required):
                raise ValueError('Missing ergonomics/ticket/collection production module')
            index = archive.read('wad/web/index.html').decode('utf-8')
            if any(style not in index for style in ('css/ergonomics.css', 'css/library-collection.css')):
                raise ValueError('Required styles are not connected to the host page')
            workspace = archive.read('wad/web/js/pages/data-workspace.js').decode('utf-8')
            if 'renderLibraryCollection' not in workspace or "./library-collection.js" not in workspace:
                raise ValueError('Collection menu is not connected to the data workspace')
            if 'wad/docs/Collecte-des-tickets.md' not in names:
                raise ValueError('Collection and private-transfer operating guide is missing')
        source_count = 0
        if edition == 'DEV':
            source_manifest = 'wad/sources/GLPI-Pilot.sources.json'
            source = json.loads(archive.read(source_manifest))
            if source.get('source_commit') != commit or not re.fullmatch(r'[0-9a-f]{40}', source.get('source_tree', '')):
                raise ValueError('DEV source identity mismatch')
            source_files = {}
            for item in source['files']:
                name = 'wad/sources/' + checked_path(item['path'])
                if name in source_files or manifest.get(name) != item['sha256']:
                    raise ValueError('DEV source manifest does not match original source bytes')
                source_files[name] = item['sha256']
                if item['path'].startswith('web/') and manifest.get('wad/' + item['path']) != item['sha256']:
                    raise ValueError('DEV sources and runtime web content differ')
            if set(source_files) != {name for name in names if name.startswith('wad/sources/')} - {source_manifest}:
                raise ValueError('DEV source coverage is incomplete')
            source_count = len(source_files)
        return {'name': path.name, 'sha256': outer_digest, 'size': path.stat().st_size,
                'version': version, 'source_commit': commit, 'distribution': edition,
                'manifest_files': len(manifest), 'source_files': source_count, 'executables': pair}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=pathlib.Path)
    parser.add_argument('version')
    parser.add_argument('commit')
    parser.add_argument('--output', type=pathlib.Path)
    args = parser.parse_args()
    if not re.fullmatch(r'\d+\.\d+\.\d+', args.version) or not re.fullmatch(r'[0-9a-f]{40}', args.commit):
        parser.error('A release version and exact commit are required')
    result = [verify_pack(args.directory / f'GLPI-Pilot_{args.version}_{edition}_Windows-x64.zip', args.version, args.commit, edition)
              for edition in EDITIONS]
    expected = sums((args.directory / 'SHA256SUMS.txt').read_text(encoding='utf-8-sig'))
    if expected != {item['name']: item['sha256'] for item in result}:
        raise ValueError('SHA256SUMS must identify exactly the original PROD-LIGHT ZIP file')
    report = json.dumps({'packs': result, 'executed_binaries': False}, indent=2) + '\n'
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(report, encoding='utf-8')
    print(report, end='')


if __name__ == '__main__':
    main()
