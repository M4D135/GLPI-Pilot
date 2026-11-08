"""Offline checks for release-note maintenance; every release below is fictitious."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location("notes", Path(__file__).with_name("redact_private_site_release_notes.py"))
notes = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(notes)
FICTIONAL_RELEASES = {101: "v9.0.1", 102: "v9.0.2"}
PREFIX = "/repos/" + notes.REPOSITORY + "/releases/"


def original(identity):
    return f"Fictional original note {identity}.\n"


def replacement(identity):
    return f"Fictional replacement note {identity}.\n"


def asset(identity):
    return {"id": identity, "name": f"application-{identity}.zip", "size": 100, "state": "uploaded",
            "digest": "sha256:" + "a" * 64, "created_at": "2026-01-01T00:00:00Z",
            "updated_at": "2026-01-01T00:00:00Z", "download_count": 0}


class FakeAPI:
    def __init__(self):
        self.releases = {identity: {"id": identity, "tag_name": tag, "target_commitish": "a" * 40,
                                   "name": "Fictional release", "body": original(identity), "draft": False,
                                   "prerelease": False, "published_at": "2026-01-01T00:00:00Z",
                                   "updated_at": "2026-01-01T00:00:00Z",
                                   "html_url": f"https://github.com/{notes.REPOSITORY}/releases/tag/{tag}"}
                         for identity, tag in FICTIONAL_RELEASES.items()}
        self.assets = {identity: [asset(identity * 10)] for identity in FICTIONAL_RELEASES}
        self.calls, self.patches, self.get_counts = [], [], {}
        self.before_release_get = None
        self.after_patch = None

    def request(self, method, path, *, data=None):
        self.calls.append((method, path, copy.deepcopy(data)))
        relative = path.removeprefix(PREFIX)
        identity = int(relative.split("/")[0])
        if "/assets?" in relative:
            assert method == "GET"
            page = int(relative.split("page=")[-1])
            return copy.deepcopy(self.assets[identity][(page - 1) * 100:page * 100])
        if method == "GET":
            self.get_counts[identity] = self.get_counts.get(identity, 0) + 1
            if self.before_release_get:
                self.before_release_get(self, identity, self.get_counts[identity])
            return copy.deepcopy(self.releases[identity])
        assert method == "PATCH" and set(data) == {"body"}
        self.patches.append((identity, copy.deepcopy(data)))
        self.releases[identity]["body"] = data["body"]
        self.releases[identity]["updated_at"] = "2026-01-02T00:00:00Z"
        if self.after_patch:
            self.after_patch(self, identity)
        return copy.deepcopy(self.releases[identity])


class NoteRedactionChecks(unittest.TestCase):
    def setUp(self):
        self.allowlist = patch.object(notes, "APPROVED_RELEASES", FICTIONAL_RELEASES)
        self.allowlist.start()
        self.addCleanup(self.allowlist.stop)
        self.temporary = tempfile.TemporaryDirectory(prefix="glpi-fictional-note-checks-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / "docs").mkdir()
        self.plan_path = self.root / "plan.json"
        self.plan = {"repository": notes.REPOSITORY, "releases": []}
        for identity, tag in FICTIONAL_RELEASES.items():
            path = f"docs/RELEASE-{tag[1:]}.md"
            (self.root / path).write_text(replacement(identity), encoding="utf-8")
            self.plan["releases"].append({"release_id": identity, "tag_name": tag,
                                          "original_body_sha256": notes.sha256(original(identity)),
                                          "redacted_body_sha256": notes.sha256(replacement(identity)), "body_file": path})

    def load(self):
        self.plan_path.write_text(json.dumps(self.plan), encoding="utf-8")
        return notes.load_plan(self.plan_path, self.root)

    def test_minimal_plan_reads_only_approved_documents(self):
        loaded = self.load()
        self.assertEqual([value["body"] for value in loaded], [replacement(101), replacement(102)])
        self.assertFalse(any("body" in entry or "original_body" in entry for entry in self.plan["releases"]))

    def test_other_repository_rejected(self):
        self.plan["repository"] = "someone/other-repository"
        with self.assertRaises(notes.RedactionError):
            self.load()

    def test_unknown_id_or_tag_rejected(self):
        for field, value in (("release_id", 999), ("tag_name", "v8.0.0")):
            with self.subTest(field=field):
                prior = self.plan["releases"][0][field]
                self.plan["releases"][0][field] = value
                with self.assertRaises(notes.RedactionError):
                    self.load()
                self.plan["releases"][0][field] = prior

    def test_duplicate_or_missing_target_rejected(self):
        self.plan["releases"][1] = copy.deepcopy(self.plan["releases"][0])
        with self.assertRaises(notes.RedactionError):
            self.load()
        self.plan["releases"].pop()
        with self.assertRaises(notes.RedactionError):
            self.load()

    def test_plan_cannot_embed_original_text(self):
        self.plan["releases"][0]["original_body"] = original(101)
        with self.assertRaises(notes.RedactionError):
            self.load()

    def test_unreviewed_document_bytes_rejected(self):
        (self.root / self.plan["releases"][0]["body_file"]).write_text("Unreviewed document.\n")
        with self.assertRaises(notes.RedactionError):
            self.load()

    def test_unexpected_path_and_symbolic_link_rejected(self):
        entry = self.plan["releases"][0]
        expected = entry["body_file"]
        entry["body_file"] = "../outside.md"
        with self.assertRaises(notes.RedactionError):
            self.load()
        entry["body_file"] = expected
        path = self.root / expected
        path.unlink()
        (self.root / "other.md").write_text(replacement(101))
        path.symlink_to(self.root / "other.md")
        with self.assertRaises(notes.RedactionError):
            self.load()

    def test_dry_run_uses_get_only_and_reports_no_body(self):
        api = FakeAPI()
        report = notes.redact(api, self.load())
        self.assertEqual(report["status"], "inventory")
        self.assertFalse(api.patches)
        self.assertTrue(all(method == "GET" for method, path, data in api.calls))
        self.assertNotIn(original(101).strip(), json.dumps(report))
        self.assertNotIn(replacement(101).strip(), json.dumps(report))

    def test_apply_sends_only_body_and_preserves_every_asset(self):
        api = FakeAPI()
        before_assets = copy.deepcopy(api.assets)
        report = notes.redact(api, self.load(), apply=True)
        self.assertEqual(api.patches, [(identity, {"body": replacement(identity)}) for identity in FICTIONAL_RELEASES])
        self.assertEqual(api.assets, before_assets)
        self.assertEqual(report["status"], "passed")
        self.assertTrue(report["patch_payload_body_only"])
        self.assertTrue(report["all_release_assets_unchanged"])

    def test_repeat_accepts_exact_replacements_without_patch(self):
        api = FakeAPI()
        loaded = self.load()
        notes.redact(api, loaded, apply=True)
        api.patches.clear()
        report = notes.redact(api, loaded, apply=True)
        self.assertFalse(api.patches)
        self.assertEqual(len(report["already_redacted"]), 2)

    def test_mixed_already_redacted_and_original(self):
        api = FakeAPI()
        api.releases[101]["body"] = replacement(101)
        report = notes.redact(api, self.load(), apply=True)
        self.assertEqual([identity for identity, payload in api.patches], [102])
        self.assertEqual([item["release_id"] for item in report["already_redacted"]], [101])

    def test_second_stale_body_rejects_before_first_patch(self):
        api = FakeAPI()
        api.releases[102]["body"] = "A newer independent edit.\n"
        with self.assertRaises(notes.RedactionError):
            notes.redact(api, self.load(), apply=True)
        self.assertFalse(api.patches)
        self.assertEqual(api.releases[101]["body"], original(101))

    def test_wrong_remote_identity_or_unpublished_release_rejected(self):
        for field, value in (("id", 999), ("tag_name", "v8.0.0"), ("draft", True), ("prerelease", True)):
            with self.subTest(field=field):
                api = FakeAPI()
                api.releases[101][field] = value
                with self.assertRaises(notes.RedactionError):
                    notes.redact(api, self.load(), apply=True)
                self.assertFalse(api.patches)

    def test_concurrent_body_edit_is_not_overwritten(self):
        api = FakeAPI()
        def edit(current, identity, count):
            if identity == 101 and count == 2:
                current.releases[identity]["body"] = "Concurrent independent edit.\n"
        api.before_release_get = edit
        with self.assertRaises(notes.RedactionError):
            notes.redact(api, self.load(), apply=True)
        self.assertFalse(api.patches)

    def test_concurrent_asset_edit_before_patch_is_rejected(self):
        api = FakeAPI()
        def edit(current, identity, count):
            if identity == 101 and count == 2:
                current.assets[identity][0]["digest"] = "sha256:" + "b" * 64
        api.before_release_get = edit
        with self.assertRaises(notes.RedactionError):
            notes.redact(api, self.load(), apply=True)
        self.assertFalse(api.patches)

    def test_server_must_store_exact_body(self):
        api = FakeAPI()
        api.after_patch = lambda current, identity: current.releases[identity].update(body="Wrong body")
        with self.assertRaises(notes.RedactionError):
            notes.redact(api, self.load(), apply=True)
        self.assertEqual(len(api.patches), 1)

    def test_metadata_or_asset_changes_after_patch_are_detected(self):
        for kind in ("metadata", "assets"):
            with self.subTest(kind=kind):
                api = FakeAPI()
                if kind == "metadata":
                    api.after_patch = lambda current, identity: current.releases[identity].update(target_commitish="b" * 40)
                else:
                    api.after_patch = lambda current, identity: current.assets[identity][0].update(size=999)
                with self.assertRaises(notes.RedactionError):
                    notes.redact(api, self.load(), apply=True)
                self.assertEqual(len(api.patches), 1)

    def test_pagination_preserves_assets_beyond_first_page(self):
        api = FakeAPI()
        api.assets[101] = [asset(1000 + index) for index in range(101)]
        notes.redact(api, self.load(), apply=True)
        self.assertEqual(len(api.assets[101]), 101)
        self.assertTrue(any(path.endswith('/101/assets?per_page=100&page=2') for method, path, data in api.calls))

    def test_late_change_to_earlier_note_is_detected(self):
        api = FakeAPI()
        def edit(current, identity):
            if identity == 102:
                current.releases[101]["body"] = "Changed after its earlier verification.\n"
        api.after_patch = edit
        with self.assertRaises(notes.RedactionError):
            notes.redact(api, self.load(), apply=True)

    def test_download_counts_may_change_without_changing_asset_identity(self):
        api = FakeAPI()
        api.after_patch = lambda current, identity: current.assets[identity][0].update(download_count=1)
        self.assertEqual(notes.redact(api, self.load(), apply=True)["status"], "passed")

    def test_transport_only_accepts_approved_get_and_body_patch(self):
        client = notes.GitHub(notes.REPOSITORY, "fictitious-test-token")
        class Response:
            status = 200
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def read(self, limit): return b'{"ok":true}'
        class Opener:
            requests = []
            def open(self, request, timeout):
                self.requests.append(request)
                return Response()
        client.opener = Opener()
        client.request("PATCH", PREFIX + '101', data={"body": replacement(101)})
        request = client.opener.requests[0]
        self.assertEqual(request.full_url, 'https://api.github.com' + PREFIX + '101')
        self.assertEqual(request.get_method(), 'PATCH')
        self.assertEqual(json.loads(request.data), {"body": replacement(101)})
        for method, path, payload in (("DELETE", PREFIX + '101', None), ("PATCH", PREFIX + '999', {"body": "x"}),
                                      ("PATCH", PREFIX + '101', {"body": "x", "name": "x"}),
                                      ("PATCH", PREFIX + '101/assets?per_page=100&page=1', {"body": "x"}),
                                      ("GET", 'https://other.invalid/path', None)):
            with self.subTest(method=method, path=path):
                with self.assertRaises(notes.RedactionError):
                    client.request(method, path, data=payload)
        self.assertEqual(len(client.opener.requests), 1)

    def test_redirect_never_forwards_credential(self):
        with self.assertRaises(notes.RedactionError):
            notes.NoRedirect().redirect_request(None, None, 302, "", {}, "https://other.invalid/")


if __name__ == "__main__":
    unittest.main()
