"""Seal the exact tested ZIP, or a bundle of synthetic evidence, on the private runner."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import tempfile
import xml.etree.ElementTree as ET
import zipfile

REPO = 'M4D135/GLPI-Pilot'


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def store_dir(workspace, env):
    # The operator may provision a separate persistent volume. Never hide evidence inside _work.
    value = env.get('GLPI_CI_EVIDENCE_ROOT')
    root = Path(value) if value else Path.home() / 'GLPI-Pilot-Evidence'
    if not root.is_absolute() or root.resolve().is_relative_to(workspace):
        raise ValueError('The evidence directory must be absolute and outside checkout')
    return root


def require_plain(path):
    info = path.lstat()
    if (path.is_symlink() or getattr(info, 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT
            or not (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode))):
        raise ValueError('Refusing linked or special evidence: ' + path.name)


def evidence_files(path):
    def failed(error):
        raise error
    for parent, children, files in os.walk(path, topdown=True, followlinks=False, onerror=failed):
        children.sort()
        for name in children:
            require_plain(Path(parent) / name)  # Reject junctions before the walk can enter them.
        for name in sorted(files):
            entry = Path(parent) / name
            require_plain(entry)
            yield entry


def seal(temp, dest):
    expected = digest(temp)
    try:
        os.link(temp, dest)  # Exclusive, atomic publication after a complete write, on the same volume.
    except FileExistsError:
        require_plain(dest)
    if digest(dest) != expected:
        raise ValueError('Different bytes already stored, or incomplete evidence: ' + dest.name)
    return expected


def retain(workspace, env):
    sha, run, attempt = (env[k] for k in ('GITHUB_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT'))
    if (env['GITHUB_REPOSITORY'] != REPO or not re.fullmatch('[0-9a-f]{40}', sha)
            or not all(re.fullmatch('[1-9][0-9]*', s) for s in (run, attempt))):
        raise ValueError('Invalid CI identity')
    requested_workspace = Path(os.path.abspath(workspace))
    workspace = requested_workspace.resolve()
    vault = store_dir(workspace, env)
    store = vault / run / attempt / sha
    sources, missing = [], []
    for raw in env['GLPI_RETAIN_PATH'].splitlines():
        if not raw.strip():
            continue
        rel = Path(os.path.abspath(requested_workspace / raw.strip())).relative_to(requested_workspace)
        path = workspace / rel
        if not rel.parts or rel.parts[0] != 'artifacts':
            raise ValueError('Refusing non-test data')
        current = workspace
        for part in rel.parts:
            current /= part
            try:
                require_plain(current)
            except FileNotFoundError:
                missing.append(rel.as_posix())
                break
        else:
            sources.append(path)
    name = env.get('GLPI_RETAIN_NAME', '').strip()
    pack = not name
    if not sources:
        raise ValueError('No evidence source')
    if pack:
        version = ET.parse(workspace / 'src/GlpiPilot/GlpiPilot.csproj').findtext('PropertyGroup/Version')
        name = sources[0].name
        if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', version or '') or (
                missing or len(sources) != 1 or sources[0].parent != workspace / 'artifacts/windows-ci/release'
                or name != f'GLPI-Pilot_{version}_PROD-LIGHT_Windows-x64.zip'
                or env['GITHUB_JOB'] != 'windows' or env['RUNNER_OS'] != 'Windows'):
            raise ValueError('Only the original PROD-LIGHT pack from the Windows build job can be sealed')
    else:
        if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,159}', name):
            raise ValueError('Invalid evidence name')
        name += '.zip'
    vault.mkdir(mode=0o700, parents=True, exist_ok=True)
    require_plain(vault)
    parent = vault
    for part in (run, attempt, sha):
        parent /= part
        parent.mkdir(mode=0o700, exist_ok=True)
        require_plain(parent)
    dest = store / name
    with tempfile.TemporaryDirectory(dir=store) as staging:
        temp = Path(staging) / name
        if pack:
            if shutil.disk_usage(store).free < sources[0].stat().st_size:
                raise OSError('Insufficient space to retain the original Windows pack')
            shutil.copyfile(sources[0], temp)
            if digest(temp) != digest(sources[0]):
                raise ValueError('The original pack changed during retention')
        else:
            seen = set()
            with zipfile.ZipFile(temp, 'w', zipfile.ZIP_DEFLATED, compresslevel=3) as archive:
                for path in sources:
                    if not path.is_dir():
                        raise ValueError('Expected a synthetic evidence directory')
                    for entry in evidence_files(path):
                        member = entry.relative_to(path if len(sources) == 1 else path.parent).as_posix()
                        if member in seen:
                            raise ValueError('Duplicate evidence member: ' + member)
                        seen.add(member)
                        archive.write(entry, member)
            if not seen:
                raise ValueError('No evidence files; the job is not verified')
        receipt = dict(repository=REPO, run=int(run), attempt=int(attempt), commit=sha, job=env['GITHUB_JOB'],
                       name=name, bytes=temp.stat().st_size, sha256=seal(temp, dest))
        if missing:
            receipt['missing'] = missing
        raw = json.dumps(receipt, sort_keys=True, separators=(',', ':'))
        metadata = Path(staging) / 'receipt.json'
        metadata.write_bytes((raw + '\n').encode('ascii'))
        seal(metadata, store / (name + '.receipt.json'))
    print(('GLPI_CI_ORIGINAL ' if pack else 'GLPI_CI_EVIDENCE ') + raw, flush=True)
    if env.get('GITHUB_STEP_SUMMARY'):
        with open(env['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as summary:
            summary.write(f"Conservé sur le banc : `{name}`, {receipt['bytes']} octets, SHA-256 `{receipt['sha256']}`.\n")
    if missing:
        raise ValueError('Partial evidence retained; missing: ' + ', '.join(missing))
    return receipt


if __name__ == '__main__':
    retain(Path(os.environ['GITHUB_WORKSPACE']), os.environ)
