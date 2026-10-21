"""Assemble the already-published Windows release and verify the complete ZIP."""
from pathlib import Path
import hashlib
import json
import shutil
import struct
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = '1.4.0'
ART = ROOT / 'artifacts'
PUBLISH = ART / f'publish-{VERSION}-ready'
STAGE = ART / f'release-{VERSION}' / 'GLPI-Pilot'
WAD = STAGE / 'wad'

def digest(path):
    hasher = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            hasher.update(block)
    return hasher.hexdigest()

def pe(path, gui=False):
    with path.open('rb') as stream:
        dos = stream.read(64)
        assert dos[:2] == b'MZ', path
        offset = struct.unpack_from('<I', dos, 60)[0]
        stream.seek(offset)
        header = stream.read(100)
    assert header[:4] == b'PE\0\0', path
    assert struct.unpack_from('<H', header, 4)[0] == 0x8664, path
    if gui:
        assert struct.unpack_from('<H', header, 24 + 68)[0] == 2, path

# Verification performed on this release is separate from unchanged browser/CSV baselines.
logs = {
    'automation-checks-1.4.0.log': ART / 'automation-checks-1.4.0.log',
    'startup-diagnostics-checks-1.4.0.log': ART / 'startup-diagnostics-checks-1.4.0.log',
    'configuration-checks-1.4.0.log': ART / 'configuration-checks-1.4.0.log',
    'dashboard-checks-1.4.0.log': ART / 'dashboard-checks-1.4.0.log',
    'dashboard-layout-checks-1.4.0.log': ART / 'dashboard-layout-checks-1.4.0.log',
    'preview-query-checks-1.4.0.log': ART / 'preview-query-checks-1.4.0.log',
    'technician-workload-checks-1.4.0.log': ART / 'technician-workload-checks-1.4.0.log',
    'city-detection-checks-1.4.0.log': ART / 'city-detection-checks-1.4.0.log',
    'tour-planning-checks-1.4.0.log': ART / 'tour-planning-checks-1.4.0.log',
    'query-checks-1.4.0.log': ART / 'query-checks-1.4.0.log',
    'windows-startup-build-1.4.0.log': ART / 'windows-startup-build-1.4.0.log',
    'build-publish-wpf-1.4.0.log': ART / 'publish-1.4.0.log',
    'packaging-script-checks-1.4.0.log': ART / 'packaging-script-checks-1.4.0.log',
    'browser-checks-baseline-1.3.0.log': ART / 'browser-checks-1.3.0.log',
    'export-checks-baseline-1.3.0.log': ART / 'export-checks-1.3.0.log',
    'native-import-checks-baseline-1.3.0.log': ART / 'native-import-checks-1.3.0.log',
    'ticket-detail-checks-baseline-1.3.0.log': ART / 'ticket-detail-checks-1.3.0.log',
    'native-dom-checks-previous-1.1.1.log': ROOT / 'tests/NativeDomChecks/result-20261021-v111.log',
    'dom-checks-previous-1.1.0.log': ROOT / 'tests/DomChecks/result-20261021.log',
}
# Preflight before creating a partial stage if a publication or its evidence is absent.
required = [PUBLISH / 'GLPI-Pilot.exe', PUBLISH / '.playwright/node/win32_x64/node.exe',
            ROOT / 'support/DIAGNOSTIC_DEMARRAGE.cmd',
            ROOT / 'support/Diagnostic-Demarrage.ps1',
            ROOT / 'support/LIRE-LE-DIAGNOSTIC.txt',
            ROOT / 'docs/Rapport-verification.txt', *logs.values()]
missing = [str(path.relative_to(ROOT)) for path in required if not path.is_file()]
assert not missing, 'Required published files or validation logs are absent: ' + ', '.join(missing)
assert not STAGE.exists(), 'A release stage already exists; inspect it before repackaging.'
assert not (ART / f'GLPI-Pilot_{VERSION}_Windows-x64.zip').exists(), 'The output ZIP already exists.'
WAD.mkdir(parents=True)
shutil.copy2(PUBLISH / 'GLPI-Pilot.exe', STAGE / 'GLPI-Pilot.exe')
shutil.copytree(PUBLISH / '.playwright', WAD / '.playwright', ignore=shutil.ignore_patterns('.node.exe.*'))
for folder in ('docs', 'licenses', 'support'):
    shutil.copytree(ROOT / folder, WAD / folder)
shutil.copy2(ROOT / 'README.md', WAD / 'docs' / 'LISEZ-MOI.md')
for name, source in logs.items():
    shutil.copy2(source, WAD / 'docs' / name)

(WAD / 'docs' / 'INSTALLATION.txt').write_text("""GLPI Pilot 1.4.0 — pack complet

1. Fermer l'ancienne version de GLPI Pilot si elle est ouverte.
2. Extraire entièrement ce ZIP dans un nouveau dossier.
3. Ouvrir GLPI-Pilot.exe. Garder tout le dossier wad à côté de l'EXE.
4. Vérifier la version 1.4.0, puis cliquer Extraire tout.

L'adresse source, les réglages, les dispositions et le profil SSO sont repris
à partir de %LOCALAPPDATA%\\GLPI-Pilot. L'ancienne configuration est conservée.
Aucun fichier de l'ancien pack n'est nécessaire.

SI LA FENETRE NE S'OUVRE PAS
Lancer wad\\support\\DIAGNOSTIC_DEMARRAGE.cmd, sans mode administrateur.
Attendre jusqu'à 20 secondes, puis copier le rapport ouvert dans le Bloc-notes.
Le lanceur conserve les réglages et la session SSO. Il respecte la politique
PowerShell du poste. Les détails sont dans wad\\support\\LIRE-LE-DIAGNOSTIC.txt.
La cause exacte du non-démarrage signalé sur le poste utilisateur n'est pas
encore confirmée ; le nouveau diagnostic doit permettre de l'identifier.

Le pack inclut son runtime : aucune installation de .NET, Python ou Node.js
n'est nécessaire. Edge ou Chrome doit être installé et autorisé sur le poste.
Guide complet : Guide-GLPI-Pilot.html dans ce même dossier docs.
Fonctions du dashboard : Dashboard-Builder.txt.
Le rapport de vérification distingue les tests relancés en 1.4.0 des tests
historiques 1.3.0. Les tests de rendu WPF restent à exécuter sous Windows.
""", encoding='utf-8')

(WAD / 'version.json').write_text(json.dumps({
    'application': 'GLPI Pilot', 'version': VERSION, 'target': 'win-x64',
    'sdk': '10.0.402', 'runtime': '10.0.13', 'playwright': '1.63.0',
    'date': '2026-10-21', 'self_contained': True,
    'distribution': 'complete',
}, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')

source_files = []
for folder in ('src', 'tests', 'docs', 'support', 'licenses', '.github'):
    for path in (ROOT / folder).rglob('*'):
        if path.is_file() and not set(path.relative_to(ROOT).parts).intersection({'bin', 'obj', 'node_modules', 'artifacts'}):
            source_files.append(path)
source_files.extend(ROOT / name for name in ('README.md', 'build.ps1'))
source_files.extend(p for p in (ROOT / 'build-tools').iterdir() if p.is_file() and p.suffix in ('.py', '.ps1'))
if (ROOT / '.gitignore').is_file():
    source_files.append(ROOT / '.gitignore')
(WAD / 'sources').mkdir()
with zipfile.ZipFile(WAD / 'sources' / 'GLPI-Pilot-Sources.zip', 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as bundle:
    for path in sorted(source_files):
        bundle.write(path, path.relative_to(ROOT).as_posix())

with zipfile.ZipFile(WAD / 'sources' / 'GLPI-Pilot-Sources.zip') as sources:
    assert sources.testzip() is None
    names = set(sources.namelist())
    required_sources = {
        'src/GlpiPilot/MainWindow.Cities.cs', 'src/GlpiPilot/MainWindow.Queries.cs',
        'src/GlpiPilot/Core/QueryService.cs', 'src/GlpiPilot/Core/CityDetectionService.cs',
        'src/GlpiPilot/Core/TourPlanningService.cs', 'src/GlpiPilot/UI/QueryWorkshopView.cs',
        '.github/workflows/windows.yml', 'build-tools/Invoke-WindowsCi.ps1',
        'tests/QueryChecks/Program.cs', 'tests/WindowsStartupChecks/Program.cs',
    }
    assert required_sources <= names, 'Missing source components: ' + str(required_sources - names)
    assert not any(set(Path(name).parts) & {'bin', 'obj', 'node_modules', 'Browser', 'Profiles', 'Exports'} for name in names)
    assert not any(Path(name).name.lower() in {'settings.json', 'technician-preferences.json', 'tour-order.json'} for name in names)

pe(STAGE / 'GLPI-Pilot.exe', gui=True)
pe(WAD / '.playwright/node/win32_x64/node.exe')
assert b'asInvoker' in (STAGE / 'GLPI-Pilot.exe').read_bytes()
assert VERSION.encode('utf-16-le') in (STAGE / 'GLPI-Pilot.exe').read_bytes(), 'Executable version resource mismatch'
old_driver = ART / 'release-1.1/GLPI-Pilot/wad/.playwright'
old_manifest = {p.relative_to(old_driver).as_posix(): digest(p) for p in old_driver.rglob('*') if p.is_file() and not p.name.startswith('.node.exe.')}
new_driver = WAD / '.playwright'
new_manifest = {p.relative_to(new_driver).as_posix(): digest(p) for p in new_driver.rglob('*') if p.is_file() and not p.name.startswith('.node.exe.')}
assert old_manifest == new_manifest, 'Unexpected browser driver change; inspect the complete distribution.'

hashes = [f'{digest(path)}  {path.relative_to(STAGE).as_posix()}' for path in sorted(STAGE.rglob('*')) if path.is_file() and not path.name.startswith('.node.exe.')]
(WAD / 'SHA256SUMS.txt').write_text('\n'.join(hashes) + '\n', encoding='utf-8')

deliverables = []
for suffix in ('Windows-x64',):
    path = ART / f'GLPI-Pilot_{VERSION}_{suffix}.zip'
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
        for item in sorted(STAGE.rglob('*')):
            if item.is_file() and not item.name.startswith('.node.exe.'):
                bundle.write(item, 'GLPI-Pilot/' + item.relative_to(STAGE).as_posix())
    with zipfile.ZipFile(path) as bundle:
        assert bundle.testzip() is None
        names = bundle.namelist()
        assert 'GLPI-Pilot/GLPI-Pilot.exe' in names
        assert 'GLPI-Pilot/wad/.playwright/node/win32_x64/node.exe' in names
        assert all(name.startswith('GLPI-Pilot/GLPI-Pilot.exe') or name.startswith('GLPI-Pilot/wad/') for name in names)
    deliverables.append({'path': str(path), 'bytes': path.stat().st_size, 'sha256': digest(path), 'members': len(names)})

manifest = {'version': VERSION, 'driver_identical_to_1_1': True, 'source_files': len(source_files), 'files': deliverables}
(ART / f'release-manifest-{VERSION}.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(json.dumps(manifest, ensure_ascii=False, indent=2))
