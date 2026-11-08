"""Synthetic archive corruption checks. Nothing here executes a packaged file."""
import contextlib
import hashlib
import importlib.util
import io
import json
import pathlib
import shutil
import tempfile
import unittest
from unittest import mock
import zipfile

spec = importlib.util.spec_from_file_location('verify_packs', pathlib.Path(__file__).resolve().parents[2] / 'build-tools/verify-release-packs.py')
verify = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify)
VERSION, COMMIT = '3.13.0', 'a' * 40
sha = lambda data: hashlib.sha256(data).hexdigest()


class PackChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='glpi-pack-verifier-test-')
        self.path = pathlib.Path(self.temporary.name) / 'fixture.zip'

    def tearDown(self):
        self.temporary.cleanup()

    def files(self, edition='PROD'):
        files = {'GLPI-Pilot.exe': b'MZ_SYNTHETIC_STANDARD', 'GLPI-Pilot-Admin.exe': b'MZ_SYNTHETIC_ADMIN'}
        light = edition == 'PROD-LIGHT'
        files['wad/version.json'] = json.dumps({'version': VERSION, 'source_commit': COMMIT, 'distribution': edition,
            'executable_sha256': sha(files['GLPI-Pilot.exe']), 'admin_executable_sha256': sha(files['GLPI-Pilot-Admin.exe']),
            'webview2_loader': 'evergreen-installed' if light else 'bundled-native-x64',
            'webview2_runtime': '' if light else '154.0.4258.62'}).encode()
        if not light:
            files['wad/runtime/webview2/msedgewebview2.exe'] = b'MZ_SYNTHETIC_RUNTIME'
        files['wad/web/index.html'] = b'<link href="css/ergonomics.css"><link href="css/library-collection.css">'
        for name in ['css/ergonomics.css', 'js/reading-mode.js', 'js/pages/workspace-widget-reading.js',
                     'js/pages/ticket-control-hub.js', 'js/pages/field-preparation-editor.js', 'js/pages/terrain-tours.js',
                     'css/library-collection.css', 'js/pages/library-collection.js']:
            files['wad/web/' + name] = b'/* synthetic test only */'
        files['wad/web/js/pages/data-workspace.js'] = b"import { renderLibraryCollection } from './library-collection.js';"
        files['wad/docs/Collecte-des-tickets.md'] = b'Synthetic guide fixture only.'
        if edition == 'DEV':
            sources = []
            for name, data in list(files.items()):
                if name.startswith('wad/web/'):
                    relative = name.removeprefix('wad/')
                    files['wad/sources/' + relative] = data
                    sources.append({'path': relative, 'sha256': sha(data)})
            files['wad/sources/GLPI-Pilot.sources.json'] = json.dumps({'source_commit': COMMIT, 'source_tree': 'b' * 40, 'files': sources}).encode()
        return files

    def write(self, files, extra=None, manifest=True):
        if manifest:
            files['wad/SHA256SUMS.txt'] = ''.join(sha(data) + '  ' + name + '\n' for name, data in sorted(files.items()) if name != 'wad/SHA256SUMS.txt').encode()
        with zipfile.ZipFile(self.path, 'w') as archive:
            for name, data in files.items():
                archive.writestr(name, data)
            if extra:
                archive.writestr(*extra)

    def check(self, files, edition='PROD'):
        self.write(files)
        return verify.verify_pack(self.path, VERSION, COMMIT, edition)

    def test_prod(self):
        self.assertGreater(self.check(self.files())['manifest_files'], 5)

    def test_dev(self):
        self.assertGreater(self.check(self.files('DEV'), 'DEV')['source_files'], 5)

    def test_light(self):
        self.assertEqual(self.check(self.files('PROD-LIGHT'), 'PROD-LIGHT')['distribution'], 'PROD-LIGHT')

    def test_light_with_bundled_runtime(self):
        files = self.files('PROD-LIGHT'); files['wad/runtime/webview2/msedgewebview2.exe'] = b'MZ_STRAY_RUNTIME'
        with self.assertRaises(ValueError): self.check(files, 'PROD-LIGHT')

    def test_light_declaring_a_fixed_runtime(self):
        files = self.files('PROD-LIGHT'); info = json.loads(files['wad/version.json']); info['webview2_runtime'] = '154.0.4258.62'
        files['wad/version.json'] = json.dumps(info).encode()
        with self.assertRaises(ValueError): self.check(files, 'PROD-LIGHT')

    def test_complete_pack_without_runtime(self):
        files = self.files(); del files['wad/runtime/webview2/msedgewebview2.exe']
        with self.assertRaises(ValueError): self.check(files)

    def test_sources_outside_dev(self):
        files = self.files('PROD-LIGHT'); files['wad/sources/README.md'] = b'stray sources'
        with self.assertRaises(ValueError): self.check(files, 'PROD-LIGHT')

    def test_changed_file(self):
        files = self.files(); self.write(files); files['GLPI-Pilot.exe'] += b'changed'; self.write(files, manifest=False)
        with self.assertRaises(ValueError): verify.verify_pack(self.path, VERSION, COMMIT, 'PROD')

    def test_unlisted_file(self):
        files = self.files(); self.write(files); files['wad/unlisted.txt'] = b'x'; self.write(files, manifest=False)
        with self.assertRaises(ValueError): verify.verify_pack(self.path, VERSION, COMMIT, 'PROD')

    def test_wrong_commit(self):
        self.write(self.files())
        with self.assertRaises(ValueError): verify.verify_pack(self.path, VERSION, 'c' * 40, 'PROD')

    def test_wrong_edition(self):
        self.write(self.files())
        with self.assertRaises(ValueError): verify.verify_pack(self.path, VERSION, COMMIT, 'DEV')

    def test_identical_executables(self):
        files = self.files(); files['GLPI-Pilot-Admin.exe'] = files['GLPI-Pilot.exe']
        with self.assertRaises(ValueError): self.check(files)

    def test_path_traversal(self):
        files = self.files(); files['wad/../private.txt'] = b'x'
        with self.assertRaises(ValueError): self.check(files)

    def test_case_collision(self):
        files = self.files(); files['GLPI-PILOT.exe'] = b'x'
        with self.assertRaises(ValueError): self.check(files)

    def test_private_data(self):
        for name in ['wad/sites.csv', 'wad/GLPI-Pilot.deployment.json', 'wad/data.sqlite', 'wad/pack-sites.zip',
                     'wad/tickets.glpilibrary.zip', 'wad/BootstrapStaging/private.jsonl']:
            with self.subTest(name=name):
                files = self.files(); files[name] = b'synthetic'
                with self.assertRaises(ValueError): self.check(files)

    def test_symlink(self):
        item = zipfile.ZipInfo('wad/link'); item.create_system = 3; item.external_attr = 0o120777 << 16
        self.write(self.files(), (item, b'elsewhere'))
        with self.assertRaises(ValueError): verify.verify_pack(self.path, VERSION, COMMIT, 'PROD')

    def test_runtime_source_divergence(self):
        files = self.files('DEV'); files['wad/web/js/reading-mode.js'] = b'changed runtime'
        with self.assertRaises(ValueError): self.check(files, 'DEV')

    def test_source_coverage(self):
        files = self.files('DEV'); files['wad/sources/unlisted.cs'] = b'x'
        with self.assertRaises(ValueError): self.check(files, 'DEV')

    def test_missing_module(self):
        files = self.files(); del files['wad/web/js/reading-mode.js']
        with self.assertRaises(ValueError): self.check(files)

    def test_disconnected_styles(self):
        files = self.files(); files['wad/web/index.html'] = b'<html></html>'
        with self.assertRaises(ValueError): self.check(files)

    def test_missing_collection_module(self):
        files = self.files(); del files['wad/web/js/pages/library-collection.js']
        with self.assertRaises(ValueError): self.check(files)

    def test_disconnected_collection_styles(self):
        files = self.files(); files['wad/web/index.html'] = b'<link href="css/ergonomics.css">'
        with self.assertRaises(ValueError): self.check(files)

    def test_disconnected_collection_route(self):
        files = self.files(); files['wad/web/js/pages/data-workspace.js'] = b'/* not connected */'
        with self.assertRaises(ValueError): self.check(files)

    def test_missing_collection_guide(self):
        files = self.files(); del files['wad/docs/Collecte-des-tickets.md']
        with self.assertRaises(ValueError): self.check(files)

    def test_release_directory_lists_only_the_light_pack(self):
        self.assertEqual(verify.EDITIONS, ('PROD-LIGHT',))
        self.write(self.files('PROD-LIGHT'))
        directory = self.path.parent / 'release'
        directory.mkdir()
        name = f'GLPI-Pilot_{VERSION}_PROD-LIGHT_Windows-x64.zip'
        shutil.copy(self.path, directory / name)
        digest = sha((directory / name).read_bytes())

        def run(sums):
            (directory / 'SHA256SUMS.txt').write_text(sums, encoding='utf-8')
            with mock.patch('sys.argv', ['verify-release-packs.py', str(directory), VERSION, COMMIT]), contextlib.redirect_stdout(io.StringIO()):
                verify.main()
        run(f'{digest}  {name}\n')
        full = f'{digest}  GLPI-Pilot_{VERSION}_PROD_Windows-x64.zip\n'
        for sums in (f'{digest}  {name}\n' + full, full, ''):
            with self.subTest(sums=sums), self.assertRaises(ValueError): run(sums)

    def test_manifest_parse(self):
        with self.assertRaises(ValueError): verify.sums('bad manifest\n')
        line = 'a' * 64 + '  wad/test\n'
        with self.assertRaises(ValueError): verify.sums(line + line)


if __name__ == '__main__':
    unittest.main(verbosity=2)
