import copy
import importlib.util
import pathlib
import unittest

path = pathlib.Path(__file__).resolve().parents[2] / 'build-tools/select-verified-release.py'
spec = importlib.util.spec_from_file_location('selection', path)
selection = importlib.util.module_from_spec(spec)
spec.loader.exec_module(selection)


class VerifiedReleaseChecks(unittest.TestCase):
    def setUp(self):
        self.commit = 'a' * 40
        self.run = dict(id=12, run_attempt=1, head_sha=self.commit, head_branch='main',
                        path='.github/workflows/windows.yml', status='completed', conclusion='success')
        self.jobs = [dict(name=name, status='completed', conclusion='success') for name in (
            'Windows x64 · contrôles réels et packs', 'PostgreSQL 17 · synchronisation réelle', 'Ergonomie')]
        self.digests = {'PROD-LIGHT': 'e' * 64}
        self.artifacts = [dict(id=100, name='GLPI-Pilot_3.13.1_PROD-LIGHT_Windows-x64.zip', expired=False,
                               digest='sha256:' + self.digests['PROD-LIGHT'],
                               workflow_run=dict(id=12, head_sha=self.commit, head_branch='main'))]

    def choose(self):
        return selection.select(self.run, self.jobs, self.artifacts, self.commit, 12, 1, '3.13.1', self.digests)

    def test_exact_complete_run_and_inspected_archive(self):
        self.assertEqual(selection.EDITIONS, ('PROD-LIGHT',))
        self.assertEqual(self.choose(), {'PROD-LIGHT': 100})

    def test_internal_prod_and_dev_artifacts_are_not_selected(self):
        for edition in ('PROD', 'DEV'):
            self.artifacts.append(dict(self.artifacts[0], id=999, name=f'GLPI-Pilot_3.13.1_{edition}_Windows-x64.zip',
                                       digest='sha256:' + 'b' * 64))
        self.assertEqual(self.choose(), {'PROD-LIGHT': 100})

    def test_accepts_the_same_commit_qualified_on_its_release_branch(self):
        # Fast-forward publication: the branch run qualified the exact commit that main now points to.
        self.run['head_branch'] = 'codex/release-3.13.1'
        for artifact in self.artifacts:
            artifact['workflow_run']['head_branch'] = 'codex/release-3.13.1'
        self.assertEqual(self.choose(), {'PROD-LIGHT': 100})

    def test_rejects_other_commit_workflow_attempt_or_incomplete_run(self):
        original = copy.deepcopy(self.run)
        for key, value in [('head_sha', 'd' * 40), ('id', 13),
                           ('run_attempt', 2), ('path', '.github/workflows/interface-pr-check.yml'),
                           ('status', 'in_progress'), ('conclusion', 'failure')]:
            with self.subTest(key=key):
                self.run = dict(original, **{key: value})
                with self.assertRaises(ValueError): self.choose()

    def test_rejects_missing_native_or_postgres_job(self):
        original = copy.deepcopy(self.jobs)
        for index in (0, 1):
            self.jobs = [j for i, j in enumerate(original) if i != index]
            with self.subTest(index=index), self.assertRaises(ValueError): self.choose()

    def test_rejects_failed_cancelled_skipped_or_pending_job(self):
        for conclusion in ('failure', 'cancelled', 'skipped', None):
            self.jobs[-1]['conclusion'] = conclusion
            with self.subTest(conclusion=conclusion), self.assertRaises(ValueError): self.choose()

    def test_rejects_missing_or_ambiguous_archive(self):
        original = copy.deepcopy(self.artifacts)
        for artifacts in ([], original + [original[0]]):
            self.artifacts = artifacts
            with self.assertRaises(ValueError): self.choose()

    def test_rejects_expired_or_changed_archive(self):
        original = copy.deepcopy(self.artifacts)
        for key, value in [('expired', True), ('digest', 'sha256:' + 'd' * 64)]:
            self.artifacts = copy.deepcopy(original)
            self.artifacts[0][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError): self.choose()

    def test_rejects_archive_from_another_run_or_source(self):
        original = copy.deepcopy(self.artifacts)
        for key, value in [('id', 13), ('head_sha', 'd' * 40)]:
            self.artifacts = copy.deepcopy(original)
            self.artifacts[0]['workflow_run'][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError): self.choose()

    def test_requires_the_inspected_digest(self):
        for digest in ('', 'e' * 63, 'not-a-digest'):
            self.digests = {'PROD-LIGHT': digest}
            with self.subTest(digest=digest), self.assertRaises(ValueError): self.choose()


if __name__ == '__main__':
    unittest.main()
