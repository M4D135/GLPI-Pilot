"""Fetch Microsoft's SDK to the local build directory and verify its published hash."""
import hashlib
import json
import pathlib
import tarfile
import urllib.request

base = pathlib.Path(__file__).resolve().parent
dest = base / 'dotnet'
dest.mkdir(parents=True, exist_ok=True)
with urllib.request.urlopen('https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json', timeout=60) as response:
    releases = json.load(response)
release = next(x for x in releases['releases'] if '-' not in x['sdk']['version'])
sdk = release['sdk']
archive = next(x for x in sdk['files'] if x['rid'] == 'linux-x64' and x['name'].endswith('.tar.gz'))
target = base / 'dotnet-sdk.tar.gz'
print('Downloading official .NET SDK', sdk['version'], flush=True)
with urllib.request.urlopen(archive['url'], timeout=120) as response, target.open('wb') as output:
    while chunk := response.read(1024 * 1024):
        output.write(chunk)
digest = hashlib.file_digest(target.open('rb'), 'sha512').hexdigest()
if digest.lower() != archive['hash'].lower():
    raise SystemExit('SDK SHA512 mismatch; no extraction performed')
with tarfile.open(target) as package:
    package.extractall(dest, filter='data')
(base / 'sdk-info.json').write_text(json.dumps({'sdk': sdk['version'], 'runtime': release['runtime']['version'], 'sha512': digest}, indent=2))
print('SDK ready:', dest, flush=True)
with urllib.request.urlopen('https://api.nuget.org/v3-flatcontainer/microsoft.playwright/index.json', timeout=60) as response:
    versions = json.load(response)['versions']
version = next(v for v in reversed(versions) if '-' not in v)
(base / 'playwright-version.txt').write_text(version)
print('Playwright version:', version, flush=True)
