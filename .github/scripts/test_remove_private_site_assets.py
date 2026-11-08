"""Offline behavioral tests. All release/artifact records below are fictitious."""
import copy
import importlib.util
from pathlib import Path
import unittest


SPEC = importlib.util.spec_from_file_location("removal", Path(__file__).with_name("remove_private_site_assets.py"))
removal = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(removal)
PREFIX = "/repos/" + removal.REPOSITORY


def asset(identity, name):
    return {"id": identity, "name": name, "state": "uploaded", "size": 123,
            "digest": "sha256:" + "a" * 64, "created_at": "2026-01-01T00:00:00Z",
            "updated_at": "2026-01-01T00:00:00Z", "browser_download_url": "https://example.invalid/" + name}


class FakeAPI:
    def __init__(self, assets=None, artifacts=None):
        self.releases = [{"id": 1, "tag_name": "v9.0.0", "target_commitish": "a" * 40,
                          "name": "Fictional release", "body": "Unchanged generic notes.", "draft": False,
                          "prerelease": False, "published_at": "2026-01-01T00:00:00Z"}]
        self.assets = {1: copy.deepcopy(assets or [])}
        self.artifacts = copy.deepcopy(artifacts or [])
        self.calls, self.deleted = [], []
        self.before_asset_get = None
        self.keep_deleted = False
        self.after_delete = None

    def request(self, method, path, *, missing=False):
        self.calls.append((method, path))
        route, _, query = path.partition("?")
        if query:
            page = int(query.split("page=")[-1])
            if route == PREFIX + "/releases":
                values, key = self.releases, None
            elif route == PREFIX + "/actions/artifacts":
                values, key = self.artifacts, "artifacts"
            else:
                identity = int(route.split("/")[-2])
                values, key = self.assets[identity], None
            values = copy.deepcopy(values[(page - 1) * 100:page * 100])
            return {key: values} if key else values
        identity = int(route.rsplit("/", 1)[1])
        collection = self.artifacts if "/actions/" in route else next((items for items in self.assets.values() if any(item["id"] == identity for item in items)), [])
        if method == "GET" and self.before_asset_get:
            callback, self.before_asset_get = self.before_asset_get, None
            callback(collection, identity)
        value = next((item for item in collection if item["id"] == identity), None)
        if method == "GET":
            if value is None:
                assert missing
            return copy.deepcopy(value)
        assert method == "DELETE" and value is not None
        self.deleted.append(identity)
        if not self.keep_deleted:
            collection.remove(value)
        if self.after_delete:
            self.after_delete()
        return None


class RemovalChecks(unittest.TestCase):
    def test_dry_run_never_deletes(self):
        api = FakeAPI([asset(10, "sites.csv"), asset(11, "application.zip")])
        report = removal.remove(api, removal.REPOSITORY)
        self.assertEqual([item["id"] for item in report["targets"]], [10])
        self.assertFalse(api.deleted)
        self.assertFalse(any(method != "GET" for method, path in api.calls))

    def test_only_exact_names_removed_and_repeat_is_safe(self):
        names = sorted(removal.PRIVATE_NAMES) + ["GLPI-Pilot_9.0.0_PROD_Windows-x64.zip", "GLPI-Pilot_9.0.0_DEV_Windows-x64.zip", "SHA256SUMS.txt", "mysites.csv", "SITES.csv", "sites.csv.backup"]
        api = FakeAPI([asset(index + 10, name) for index, name in enumerate(names)])
        before = copy.deepcopy(api.assets[1][3:])
        report = removal.remove(api, removal.REPOSITORY, apply=True)
        self.assertEqual(report["status"], "passed")
        self.assertEqual(api.deleted, [10, 11, 12])
        self.assertEqual(api.assets[1], before)
        self.assertEqual(removal.remove(api, removal.REPOSITORY, apply=True)["deleted"], [])

    def test_actions_artifacts_use_same_allowlist(self):
        api = FakeAPI(artifacts=[asset(20, "sites.csv"), asset(21, "windows-checks-123"), asset(22, "GLPI-Pilot_9.0.0_PROD_Windows-x64.zip")])
        removal.remove(api, removal.REPOSITORY, apply=True)
        self.assertEqual(api.deleted, [20])
        self.assertEqual([item["id"] for item in api.artifacts], [21, 22])

    def test_all_pages_are_frozen_before_deletion(self):
        api = FakeAPI([asset(index + 1, f"unrelated-{index}.zip") for index in range(101)] + [asset(999, "sites.csv")])
        removal.remove(api, removal.REPOSITORY, apply=True)
        self.assertEqual(api.deleted, [999])
        self.assertEqual(len(api.assets[1]), 101)
        self.assertIn(("GET", PREFIX + "/releases/1/assets?per_page=100&page=2"), api.calls)

    def test_changed_asset_name_refused(self):
        api = FakeAPI([asset(10, "sites.csv")])
        api.before_asset_get = lambda items, identity: items[0].update(name="GLPI-Pilot_9.0.0_PROD_Windows-x64.zip")
        with self.assertRaises(removal.RemovalError):
            removal.remove(api, removal.REPOSITORY, apply=True)
        self.assertFalse(api.deleted)

    def test_changed_asset_digest_refused(self):
        api = FakeAPI([asset(10, "sites.csv")])
        api.before_asset_get = lambda items, identity: items[0].update(digest="sha256:" + "b" * 64)
        with self.assertRaises(removal.RemovalError):
            removal.remove(api, removal.REPOSITORY, apply=True)
        self.assertFalse(api.deleted)

    def test_concurrently_removed_asset_is_not_deleted_again(self):
        api = FakeAPI([asset(10, "sites.csv")])
        api.before_asset_get = lambda items, identity: items.clear()
        report = removal.remove(api, removal.REPOSITORY, apply=True)
        self.assertEqual(len(report["already_absent"]), 1)
        self.assertFalse(api.deleted)

    def test_delete_must_be_followed_by_404(self):
        api = FakeAPI([asset(10, "sites.csv")])
        api.keep_deleted = True
        with self.assertRaises(removal.RemovalError):
            removal.remove(api, removal.REPOSITORY, apply=True)

    def test_concurrent_pack_mutation_is_detected(self):
        api = FakeAPI([asset(10, "sites.csv"), asset(11, "application.zip")])
        api.after_delete = lambda: api.assets[1][0].update(digest="sha256:" + "b" * 64)
        with self.assertRaises(removal.RemovalError):
            removal.remove(api, removal.REPOSITORY, apply=True)

    def test_concurrent_release_note_mutation_is_detected(self):
        api = FakeAPI([asset(10, "sites.csv")])
        api.after_delete = lambda: api.releases[0].update(body="Concurrent note")
        with self.assertRaises(removal.RemovalError):
            removal.remove(api, removal.REPOSITORY, apply=True)

    def test_duplicate_names_are_rejected_before_deletion(self):
        api = FakeAPI([asset(10, "sites.csv"), asset(11, "sites.csv")])
        with self.assertRaises(removal.RemovalError):
            removal.remove(api, removal.REPOSITORY, apply=True)
        self.assertFalse(api.deleted)

    def test_other_repository_is_refused(self):
        api = FakeAPI([asset(10, "sites.csv")])
        with self.assertRaises(removal.RemovalError):
            removal.remove(api, "someone/another-repo", apply=True)
        self.assertEqual(api.calls, [])


if __name__ == "__main__":
    unittest.main()
