#!/usr/bin/env python3
"""Apply the six reviewed release-note replacements without changing assets.

Only the body of the six pinned release IDs may be patched. Every original
body must still have its reviewed SHA-256, or already equal its replacement.
The plan and replacement documents contain no original private text. Without
--apply, this script only validates the plan and the current GitHub records.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import sys
from urllib.error import HTTPError
from urllib.request import HTTPRedirectHandler, Request, build_opener


REPOSITORY = "M4D135/GLPI-Pilot"
APPROVED_RELEASES = {
    422682913: "v3.3.3",
    423057808: "v3.5.0",
    423395654: "v3.8.1",
    423557601: "v3.9.0",
    423704969: "v3.10.0",
    423991766: "v3.10.1",
}
PLAN_FIELDS = {"release_id", "tag_name", "original_body_sha256", "redacted_body_sha256", "body_file"}
RELEASE_FIELDS = ("id", "tag_name", "target_commitish", "name", "draft", "prerelease", "published_at", "created_at",
                  "html_url", "url", "assets_url", "upload_url", "tarball_url", "zipball_url", "node_id", "author",
                  "immutable", "discussion_url")
ASSET_FIELDS = ("id", "name", "state", "size", "digest", "created_at", "updated_at", "browser_download_url",
                "content_type", "label", "node_id", "url", "uploader")


class RedactionError(RuntimeError):
    pass


def require(value, message):
    if not value:
        raise RedactionError(message)


def sha256(value):
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def json_object(raw):
    def unique(items):
        result = {}
        for key, value in items:
            require(key not in result, "Duplicate JSON field")
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=unique)


def load_plan(path, repository_root):
    plan = json_object(path.read_text(encoding="utf-8"))
    require(isinstance(plan, dict) and set(plan) == {"repository", "releases"}, "Unexpected plan structure")
    require(plan["repository"] == REPOSITORY, "Plan repository is not authorized")
    require(isinstance(plan["releases"], list) and len(plan["releases"]) == len(APPROVED_RELEASES), "The plan must contain every approved release exactly once")
    root = repository_root.resolve(strict=True)
    notes, seen = [], set()
    for entry in plan["releases"]:
        require(isinstance(entry, dict) and set(entry) == PLAN_FIELDS, "Unexpected plan entry fields")
        identity, tag = entry["release_id"], entry["tag_name"]
        require(type(identity) is int and identity in APPROVED_RELEASES and identity not in seen, "Unapproved or duplicate release ID")
        require(tag == APPROVED_RELEASES[identity], f"Release {identity}: tag does not match the approved identity")
        seen.add(identity)
        for field in ("original_body_sha256", "redacted_body_sha256"):
            require(isinstance(entry[field], str) and re.fullmatch(r"[a-f0-9]{64}", entry[field]), "Invalid body SHA-256")
        require(entry["original_body_sha256"] != entry["redacted_body_sha256"], "Original and replacement body hashes must differ")
        require(entry["body_file"] == f"docs/RELEASE-{tag[1:]}.md", "Replacement must come from the exact approved release document")
        body_path = root / entry["body_file"]
        require(not (root / "docs").is_symlink() and not body_path.is_symlink(), "Replacement documents cannot be symbolic links")
        require(body_path.resolve(strict=True).is_relative_to(root) and body_path.is_file(), "Replacement document escapes the checkout")
        raw = body_path.read_bytes()
        require(0 < len(raw) <= 1024 * 1024, "Invalid replacement body size")
        body = raw.decode("utf-8")
        require(sha256(body) == entry["redacted_body_sha256"], f"Release {identity}: replacement document does not match its reviewed hash")
        notes.append({**entry, "body": body})
    require(seen == APPROVED_RELEASES.keys(), "Incomplete approved release inventory")
    return sorted(notes, key=lambda note: note["release_id"])


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise RedactionError("Unexpected GitHub API redirect; no credential forwarded")


class GitHub:
    def __init__(self, repository, token):
        require(repository == REPOSITORY, "This maintenance task is restricted to its original repository")
        require(bool(token), "GH_TOKEN is required")
        self.prefix = f"/repos/{repository}/releases/"
        self.token = token
        self.opener = build_opener(NoRedirect())

    def request(self, method, path, *, data=None):
        require(method in {"GET", "PATCH"}, "Only inventory and release-body PATCH are supported")
        match = re.fullmatch(re.escape(self.prefix) + r"([1-9][0-9]*)(/assets\?per_page=100&page=[1-9][0-9]*)?", path)
        require(match is not None and int(match[1]) in APPROVED_RELEASES, "API path is not an approved release")
        if method == "PATCH":
            require(match[2] is None and isinstance(data, dict) and set(data) == {"body"}
                    and isinstance(data["body"], str), "Only the release body may be patched")
        else:
            require(data is None, "GET requests cannot have a mutation payload")
        payload = json.dumps(data, ensure_ascii=False).encode("utf-8") if data is not None else None
        request = Request("https://api.github.com" + path, method=method, data=payload, headers={
            "Authorization": "Bearer " + self.token,
            "Accept": "application/vnd.github+json",
            "Content-Type": "application/json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "GLPI-Pilot-reviewed-release-note-maintenance",
        })
        try:
            with self.opener.open(request, timeout=45) as response:
                require(response.status == 200, f"Unexpected HTTP {response.status} for {method} {path}")
                raw = response.read(16 * 1024 * 1024 + 1)
                require(len(raw) <= 16 * 1024 * 1024, "Oversized API response")
                return json_object(raw)
        except HTTPError as error:
            raise RedactionError(f"GitHub HTTP {error.code} for {method} {path}") from None


def signature(value, fields):
    return {key: value.get(key) for key in fields}


def snapshot(api, note):
    identity, tag = note["release_id"], note["tag_name"]
    path = f"/repos/{REPOSITORY}/releases/{identity}"
    release = api.request("GET", path)
    require(isinstance(release, dict) and release.get("id") == identity and release.get("tag_name") == tag,
            f"Release {identity}: API identity does not match the approved plan")
    require(release.get("draft") is False and release.get("prerelease") is False and release.get("published_at"),
            f"Release {identity}: expected an existing published stable release")
    require(release.get("html_url") == f"https://github.com/{REPOSITORY}/releases/tag/{tag}" and isinstance(release.get("body"), str),
            f"Release {identity}: unexpected release URL or body type")
    assets, names = {}, set()
    for page in range(1, 1001):
        values = api.request("GET", f"{path}/assets?per_page=100&page={page}")
        require(isinstance(values, list) and len(values) <= 100, "Unexpected paginated assets response")
        for asset in values:
            require(isinstance(asset, dict) and type(asset.get("id")) is int and asset["id"] > 0,
                    "Invalid asset identity")
            require(asset["id"] not in assets and isinstance(asset.get("name"), str) and asset["name"] not in names,
                    "Duplicate asset during pagination")
            assets[asset["id"]] = signature(asset, ASSET_FIELDS)
            names.add(asset["name"])
        if len(values) < 100:
            return {"metadata": signature(release, RELEASE_FIELDS), "assets": assets, "body": release["body"]}
    raise RedactionError("Asset pagination exceeded its safety bound")


def same_untouched(original, current, identity):
    # GitHub updates the release's updated_at when its body changes; user
    # downloads may also increase download_count. Neither is written here.
    require(original["metadata"] == current["metadata"], f"Release {identity}: other metadata changed")
    require(original["assets"] == current["assets"], f"Release {identity}: assets changed")


def redact(api, notes, *, apply=False):
    require({note["release_id"]: note["tag_name"] for note in notes} == APPROVED_RELEASES
            and len(notes) == len(APPROVED_RELEASES), "Unapproved release inventory")
    before = {}
    # Read all approved releases before any PATCH. One stale body rejects the
    # whole preflight rather than partially overwriting an outdated review.
    for note in notes:
        identity = note["release_id"]
        require(sha256(note["body"]) == note["redacted_body_sha256"], "Replacement body changed after plan validation")
        current = snapshot(api, note)
        require(sha256(current["body"]) in {note["original_body_sha256"], note["redacted_body_sha256"]},
                f"Release {identity}: body no longer matches the reviewed original or replacement")
        before[identity] = current
    report = {"repository": REPOSITORY, "captured_at_utc": datetime.now(timezone.utc).isoformat(),
              "mode": "apply" if apply else "inventory", "status": "inventory",
              "planned_releases": [{key: note[key] for key in ("release_id", "tag_name", "original_body_sha256", "redacted_body_sha256", "body_file")} for note in notes],
              "updated": [], "already_redacted": []}
    if not apply:
        return report
    for note in notes:
        identity, original = note["release_id"], before[note["release_id"]]
        current = snapshot(api, note)
        same_untouched(original, current, identity)
        digest = sha256(current["body"])
        item = {"release_id": identity, "tag_name": note["tag_name"], "body_sha256": note["redacted_body_sha256"]}
        if digest == note["redacted_body_sha256"]:
            require(current["body"] == note["body"], "Replacement body does not match exactly")
            report["already_redacted"].append(item)
        else:
            require(digest == note["original_body_sha256"] and current["body"] == original["body"],
                    f"Release {identity}: concurrent body change; PATCH refused")
            api.request("PATCH", f"/repos/{REPOSITORY}/releases/{identity}", data={"body": note["body"]})
            current = snapshot(api, note)
            require(current["body"] == note["body"], f"Release {identity}: replacement was not stored exactly")
            same_untouched(original, current, identity)
            report["updated"].append(item)
            print(f"Redacted release {identity} ({note['tag_name']}); body hash and unchanged metadata/assets verified", flush=True)
    # Recheck early replacements after all six have been considered.
    for note in notes:
        current = snapshot(api, note)
        require(current["body"] == note["body"], f"Release {note['release_id']}: final body verification failed")
        same_untouched(before[note["release_id"]], current, note["release_id"])
    report.update(status="passed", exact_reviewed_bodies_verified=True, other_release_metadata_unchanged=True,
                  all_release_assets_unchanged=True, patch_payload_body_only=True)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", required=True, type=Path)
    parser.add_argument("--repository-root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--repository", default=REPOSITORY)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    try:
        notes = load_plan(args.plan, args.repository_root)
        report = redact(GitHub(args.repository, os.environ.get("GH_TOKEN", "")), notes, apply=args.apply)
        if args.report:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 0
    except (RedactionError, OSError, ValueError, KeyError, TypeError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
