"""Exercise disk retention and qualification of the exact bytes claimed by a real Windows job."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
import zipfile


def load(name):
    root = Path(__file__).resolve().parents[2] / 'build-tools'
    spec = importlib.util.spec_from_file_location(name, root / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


storage, selection = load('retain-ci'), load('select-verified-release')


class PrivateBenchChecks(unittest.TestCase):
    def setUp(self):
        temp = TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.work = self.root / 'checkout'
        self.artifacts = self.work / 'artifacts'
        self.packdir = self.artifacts / 'windows-ci/release'
        self.packdir.mkdir(parents=True)
        csproj = self.work / 'src/GlpiPilot/GlpiPilot.csproj'
        csproj.parent.mkdir(parents=True)
        csproj.write_text('<Project><PropertyGroup><Version>3.13.7</Version></PropertyGroup></Project>')
        self.sha = 'a' * 40
        self.env = dict(GITHUB_REPOSITORY=storage.REPO, GITHUB_JOB='windows', RUNNER_OS='Windows',
                        GITHUB_SHA=self.sha, GITHUB_RUN_ID='42', GITHUB_RUN_ATTEMPT='2',
                        GLPI_CI_EVIDENCE_ROOT=str(self.root / 'vault'))
        self.run = dict(id=42, run_attempt=2, head_sha=self.sha, path='.github/workflows/windows.yml',
                        status='completed', conclusion='success')
        self.jobs = [dict(id=i, name=n, run_id=42, run_attempt=2, head_sha=self.sha,
                          status='completed', conclusion='success', labels=['Windows','X64','self-hosted','glpi-pilot'])
                     for i, n in enumerate(('Windows x64 · contrôles réels et packs', 'PostgreSQL 17 · synchronisation réelle'), 1)]

        self.jobs[0]['steps'] = [dict(name=n,conclusion='success',started_at='2026-11-02T23:29:00Z',
                                     completed_at='2026-11-02T23:30:00Z') for n in
                                ('Conserver le pack PROD-LIGHT Windows (sans runtime WebView2)',)]

    def original(self, edition):
        path = self.packdir / f'GLPI-Pilot_3.13.7_{edition}_Windows-x64.zip'
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr('test.txt', edition + ': exact original')
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            receipt = storage.retain(self.work, dict(self.env, GLPI_RETAIN_PATH=str(path)))
        stored = self.root / 'vault' / '42' / '2' / self.sha / path.name
        self.assertEqual(path.read_bytes(), stored.read_bytes())
        self.assertIn(json.dumps(receipt, sort_keys=True, separators=(',', ':')), out.getvalue())
        return '2026-11-02T23:30:00.0000000Z ' + out.getvalue(), receipt

    def test_original_bound_to_completed_job_and_attempt(self):
        l, k = self.original('PROD-LIGHT')
        digests = {'PROD-LIGHT':k['sha256']}
        choose = lambda log, jobs, d: selection.select_stored(self.run, jobs, log, self.sha, 42, 2, '3.13.7', d)
        self.assertEqual(choose(l, self.jobs, digests), {'PROD-LIGHT':k})
        for log, jobs, ds in (('',self.jobs,digests), (l+l,self.jobs,digests),
                (l,[dict(self.jobs[0], run_attempt=1), self.jobs[1]],digests),
                (l,[self.jobs[0], dict(self.jobs[1], conclusion='failure')],digests),
                (l,[dict(self.jobs[0], steps=[]), self.jobs[1]],digests),
                (l,self.jobs,dict(digests,**{'PROD-LIGHT':'0'*64})),
                (l.replace('23:30:00','23:15:00'),self.jobs,digests),
                (l.replace(self.sha,'b'*40),self.jobs,digests),
                (l.replace('_PROD-LIGHT_','_PROD_'),self.jobs,digests)):  # A full PROD receipt is no longer a publishable original.
            with self.assertRaises(ValueError):
                choose(log,jobs,ds)

    def test_hosted_runner_is_accepted_but_not_an_arbitrary_one(self):
        l, k = self.original('PROD-LIGHT')
        digests = {'PROD-LIGHT':k['sha256']}
        choose = lambda jobs: selection.select_stored(self.run, jobs, l, self.sha, 42, 2, '3.13.7', digests)
        hosted = dict(self.jobs[0], labels=['windows-2025'], runner_group_name='GitHub Actions')
        self.assertEqual(choose([hosted, self.jobs[1]]), {'PROD-LIGHT':k})
        for job in (dict(hosted, runner_group_name='Default'), dict(hosted, labels=['windows-2025', 'self-hosted']),
                    dict(hosted, labels=['ubuntu-latest'])):
            with self.assertRaises(ValueError):
                choose([job, self.jobs[1]])

    def test_full_prod_pack_is_never_sealed(self):
        path = self.packdir / 'GLPI-Pilot_3.13.7_PROD_Windows-x64.zip'
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr('test.txt', 'PROD: bundled runtime')
        with self.assertRaises(ValueError):
            storage.retain(self.work, dict(self.env, GLPI_RETAIN_PATH=str(path)))
        self.assertFalse((self.root / 'vault').exists())

    def test_missing_second_directory_preserves_first_job_diagnostic_and_fails(self):
        logs = self.artifacts / 'first'; logs.mkdir()
        (logs/'failure.log').write_text('first failing assertion')
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaisesRegex(ValueError, 'Partial evidence retained'):
            storage.retain(self.work, dict(self.env, GLPI_RETAIN_NAME='trace',
                           GLPI_RETAIN_PATH='artifacts/first\nartifacts/not-created'))
        with zipfile.ZipFile(self.root / 'vault/42/2' / self.sha / 'trace.zip') as z:
            self.assertEqual(z.read('failure.log'), b'first failing assertion')

    def test_never_replace_a_different_sealed_original(self):
        _, r = self.original('PROD-LIGHT')
        path = self.packdir / r['name']
        path.write_bytes(b'rebuilt')
        with self.assertRaises(ValueError):
            storage.retain(self.work,dict(self.env, GLPI_RETAIN_PATH=str(path)))
        stored = self.root / 'vault/42/2' / self.sha / r['name']
        self.assertEqual(storage.digest(stored),r['sha256'])

    def test_evidence_survives_checkout_cleanup_and_rejects_external_data(self):
        logs = self.artifacts / 'interface-pr'
        logs.mkdir(); (logs/'suite.log').write_text('21/21 checks passed')
        env = dict(self.env, GITHUB_JOB='interface', GLPI_RETAIN_PATH=str(logs), GLPI_RETAIN_NAME='standard')
        with contextlib.redirect_stdout(io.StringIO()):
            out = storage.retain(self.work, env)
        (logs/'suite.log').unlink()
        stored = self.root / 'vault/42/2' / self.sha / out['name']
        with zipfile.ZipFile(stored) as z:
            self.assertEqual(z.read('suite.log'), b'21/21 checks passed')
        with self.assertRaises(ValueError):
            storage.retain(self.work, env)  # Empty evidence cannot qualify a job.
        secret = self.root / 'outside';secret.write_text('private')
        with self.assertRaises(ValueError):
            storage.retain(self.work, dict(env,GLPI_RETAIN_PATH=str(secret)))
        try:
            (logs/'external.log').symlink_to(secret)
        except OSError as e:
            if os.name == 'nt' and getattr(e, 'winerror', None) == 1314:
                return  # A non-elevated Windows runner cannot even create the forbidden link.
            raise
        with self.assertRaises(ValueError):
            storage.retain(self.work, env)


if __name__ == '__main__':
    unittest.main()
