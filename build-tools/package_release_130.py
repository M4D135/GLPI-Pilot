"""Assemble the already-published Windows release and verify the complete ZIP."""
from pathlib import Path
import hashlib
import json
import shutil
import struct
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = '1.3.0'
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

assert not STAGE.exists(), 'A release stage already exists; inspect it before repackaging.'
WAD.mkdir(parents=True)
shutil.copy2(PUBLISH / 'GLPI-Pilot.exe', STAGE / 'GLPI-Pilot.exe')
shutil.copytree(PUBLISH / '.playwright', WAD / '.playwright', ignore=shutil.ignore_patterns('.node.exe.*'))
for folder in ('docs', 'licenses', 'support'):
    shutil.copytree(ROOT / folder, WAD / folder)
shutil.copy2(ROOT / 'README.md', WAD / 'docs' / 'LISEZ-MOI.md')

logs = {
    'automation-checks.log': ART / 'automation-checks-1.3.0.log',
    'browser-checks.log': ART / 'browser-checks-1.3.0.log',
    'native-dom-checks-previous-1.1.1.log': ROOT / 'tests/NativeDomChecks/result-20261021-v111.log',
    'dom-checks-previous-1.1.0.log': ROOT / 'tests/DomChecks/result-20261021.log',
    'export-checks.log': ART / 'export-checks-1.3.0.log',
    'native-import-checks.log': ART / 'native-import-checks-1.3.0.log',
    'dashboard-checks.log': ART / 'dashboard-checks-1.3.0.log',
    'dashboard-layout-checks.log': ART / 'dashboard-layout-checks-1.3.0.log',
    'configuration-checks.log': ART / 'configuration-checks-1.3.0.log',
    'ticket-detail-checks.log': ART / 'ticket-detail-checks-1.3.0.log',
    'build-wpf.log': ART / 'build-1.3.0.log',
}
for name, source in logs.items():
    shutil.copy2(source, WAD / 'docs' / name)

(WAD / 'docs' / 'INSTALLATION.txt').write_text("""GLPI Pilot 1.3.0 — pack complet

1. Fermer l'ancienne version de GLPI Pilot si elle est ouverte.
2. Extraire entièrement ce ZIP dans un nouveau dossier.
3. Ouvrir GLPI-Pilot.exe. Garder tout le dossier wad à côté de l'EXE.
4. Le journal doit indiquer GLPI Pilot 1.3.0.
5. Garder Export GLPI — toutes les pages, puis cliquer Extraire tout.

Le profil SSO, l'adresse source et les réglages restent dans
%LOCALAPPDATA%\\GLPI-Pilot et sont automatiquement repris.
Aucun fichier de l'ancien pack n'est nécessaire.

TABLEAUX DE BORD PERSONNALISABLES
Le dashboard et sa météo opérationnelle se calculent sur le relevé complet.
Personnaliser active les poignées de déplacement et de redimensionnement.
+ Widget ajoute un indicateur, une répartition, la météo, une note ou une liste.
L'éditeur intégré règle titre, source, taille, style, couleur et présentation.
Les dispositions nommées, widgets masqués/verrouillés et réglages sont sauvegardés.
Cliquer un indicateur ou une barre retrouve ses lignes dans Extraction.
Bilan Excel exporte les chiffres et avertissements du périmètre consulté.
Une colonne absente rend l'indicateur correspondant indisponible.
Réglages > Dashboard permet d'ajuster les colonnes et les seuils.

TECHNICIENS ET VUE PERSONNELLE
Après extraction, ouvrir Techniciens, choisir son nom puis C'est moi.
Ce choix reste mémorisé sur le poste. Mon tableau de bord retrouve cette vue.
Les onglets donnent accès aux autres techniciens détectés ; la recherche porte
sur tous les noms. Les onglets sont paginés par groupes de 24.
Statistiques, répartitions et météo se recalculent sur le technicien choisi.
Les dispositions sont communes aux vues équipe et technicien.
Un ticket partagé peut apparaître chez plusieurs personnes : ne pas additionner
leurs totaux. Une identité absente du relevé produit une vue vide expliquée.

DETAIL DES TICKETS ET TRAITEMENT
Sélectionner une ligne prépare automatiquement sa fiche.
Double-cliquer ou cliquer Détail du ticket pour ouvrir l'onglet applicatif.
Le menu présente aperçu, description, traitement et rubriques détectées.
Le traitement visible est lu automatiquement lorsqu'il est reconnu ; les autres
onglets de consultation se chargent à la demande.
Les suivis, tâches, solutions, validations et documents sont présentés selon
les données effectivement disponibles. Aucun bouton métier d'ajout n'est activé.
La recherche reste dans son onglet ; la fiche utilise un onglet dédié.
Actualiser relit GLPI ; une fiche déjà lue peut sinon provenir de la mémoire.
Rechercher filtre la rubrique chargée. Exporter la fiche inclut les sections
déjà lues ; un onglet non chargé n'est pas déclaré complet.
Réglages permet toujours de définir des sections propres au portail.

PARTAGER UNE CONFIGURATION
Réglages > Exporter la configuration crée un JSON pour un collègue.
Importer reprend source, sections, correspondances, widgets et préférences.
Chaque poste conserve son identité personnelle, son dossier d'export et sa
propre session SSO. Les anciens réglages sont sauvegardés avant remplacement.

Le navigateur reste ouvert derrière GLPI Pilot, sans être réduit.
Voir le navigateur permet une intervention SSO, y compris pendant la lecture
d'une fiche. Revenir à GLPI Pilot reprend le suivi. La priorité de fenêtre
est relâchée au repos. Aucun identifiant n'est saisi par le logiciel.

Aucune installation de Python, Node.js ou .NET n'est nécessaire.
Edge ou Chrome doit être installé et autorisé sur le poste.
Guide complet : Guide-GLPI-Pilot.html dans ce même dossier docs.
En cas de problème : Copier le diagnostic dans la fenêtre du logiciel.
""", encoding='utf-8')

(WAD / 'version.json').write_text(json.dumps({
    'application': 'GLPI Pilot', 'version': VERSION, 'target': 'win-x64',
    'sdk': '10.0.402', 'runtime': '10.0.13', 'playwright': '1.63.0',
    'date': '2026-10-21', 'self_contained': True,
    'distribution': 'complete',
}, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')

source_files = []
for folder in ('src', 'tests', 'docs', 'support', 'licenses'):
    for path in (ROOT / folder).rglob('*'):
        if path.is_file() and not set(path.relative_to(ROOT).parts).intersection({'bin', 'obj', 'node_modules', 'artifacts'}):
            source_files.append(path)
source_files.extend(ROOT / name for name in ('README.md', 'build.ps1'))
(WAD / 'sources').mkdir()
with zipfile.ZipFile(WAD / 'sources' / 'GLPI-Pilot-Sources.zip', 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as bundle:
    for path in sorted(source_files):
        bundle.write(path, path.relative_to(ROOT).as_posix())

pe(STAGE / 'GLPI-Pilot.exe', gui=True)
pe(WAD / '.playwright/node/win32_x64/node.exe')
assert b'asInvoker' in (STAGE / 'GLPI-Pilot.exe').read_bytes()
assert VERSION.encode('utf-16-le') in (STAGE / 'GLPI-Pilot.exe').read_bytes(), 'Executable version resource mismatch'
old_driver = ART / 'release-1.1/GLPI-Pilot/wad/.playwright'
old_manifest = {p.relative_to(old_driver).as_posix(): digest(p) for p in old_driver.rglob('*') if p.is_file() and not p.name.startswith('.node.exe.')}
new_driver = WAD / '.playwright'
new_manifest = {p.relative_to(new_driver).as_posix(): digest(p) for p in new_driver.rglob('*') if p.is_file()}
assert old_manifest == new_manifest, 'Unexpected browser driver change; inspect the complete distribution.'

hashes = [f'{digest(path)}  {path.relative_to(STAGE).as_posix()}' for path in sorted(STAGE.rglob('*')) if path.is_file()]
(WAD / 'SHA256SUMS.txt').write_text('\n'.join(hashes) + '\n', encoding='utf-8')

deliverables = []
for suffix in ('Windows-x64',):
    path = ART / f'GLPI-Pilot_{VERSION}_{suffix}.zip'
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
        for item in sorted(STAGE.rglob('*')):
            if item.is_file():
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
