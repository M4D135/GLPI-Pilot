#!/usr/bin/env python3
"""Remove only the retired site's three exact public asset names.

Default is an inventory-only dry run. --apply deletes matching release assets
and Actions artifacts, verifies HTTP 404, and checks every other release asset
and the release metadata remain unchanged. No tag, release, source, executable,
note or generic workflow log can be deleted by this script.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import sys
from urllib.error import HTTPError
from urllib.request import HTTPRedirectHandler, Request, build_opener


REPOSITORY = "M4D135/GLPI-Pilot"
PRIVATE_NAMES = frozenset({"sites.csv", "GLPI-Pilot-Sites.zip", "SITES-SHA256SUMS.txt"})
RELEASE_FIELDS = ("id", "tag_name", "target_commitish", "name", "body", "draft", "prerelease", "published_at", "created_at")
ASSET_FIELDS = ("id", "name", "state", "size", "digest", "created_at", "updated_at", "browser_download_url")
ARTIFACT_FIELDS = ("id", "name", "size_in_bytes", "digest", "created_at", "updated_at", "expires_at", "expired")


class RemovalError(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise RemovalError(message)


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise RemovalError("Unexpected GitHub API redirect; no credential forwarded")


class GitHub:
    def __init__(self, repository, token):
        require(repository == REPOSITORY, "This maintenance task is restricted to its original repository")
        require(bool(token), "GH_TOKEN is required")
        self.prefix = f"/repos/{repository}/"
        self.token = token
        self.opener = build_opener(NoRedirect())

    def request(self, method, path, *, missing=False):
        require(method in {"GET", "DELETE"}, "Only inventory and asset deletion are supported")
        require(path.startswith(self.prefix) and ".." not in path and "#" not in path,
                "Unexpected API path")
        if method == "DELETE":
            import re
            require(re.fullmatch(self.prefix + r"(?:releases/assets|actions/artifacts)/[1-9][0-9]*", path),
                    "Only a release asset or Actions artifact ID may be deleted")
        request = Request("https://api.github.com" + path, method=method, headers={
            "Authorization": "Bearer " + self.token,
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "GLPI-Pilot-retired-sites-maintenance",
        })
        try:
            with self.opener.open(request, timeout=45) as response:
                require(response.status == (204 if method == "DELETE" else 200),
                        f"Unexpected HTTP {response.status} for {method} {path}")
                raw = response.read(16 * 1024 * 1024 + 1)
                require(len(raw) <= 16 * 1024 * 1024, "Oversized API response")
                return json.loads(raw) if raw else None
        except HTTPError as error:
            if missing and error.code == 404:
                return None
            raise RemovalError(f"GitHub HTTP {error.code} for {method} {path}") from None


def paginated(api, path, key=None):
    items, seen = [], set()
    for page in range(1, 1001):
        document = api.request("GET", f"{path}?per_page=100&page={page}")
        values = document if key is None else document[key]
        require(isinstance(values, list) and len(values) <= 100, "Unexpected paginated API response")
        for value in values:
            require(isinstance(value, dict) and type(value.get("id")) is int and value["id"] > 0,
                    "Invalid API resource identity")
            require(value["id"] not in seen, "Repeated identity during pagination; retry from a fresh snapshot")
            seen.add(value["id"])
            items.append(value)
        if len(values) < 100:
            return items
    raise RemovalError("API pagination exceeded its safety bound")


def signature(value, fields):
    return {key: value.get(key) for key in fields}


def release_snapshot(api, repository):
    prefix = f"/repos/{repository}"
    releases = {}
    for release in paginated(api, prefix + "/releases"):
        assets = paginated(api, f"{prefix}/releases/{release['id']}/assets")
        require(len({item["name"] for item in assets}) == len(assets), "Duplicate release asset names")
        releases[release["id"]] = {"metadata": signature(release, RELEASE_FIELDS), "assets": assets}
    return releases


def remove(api, repository, *, apply=False):
    require(repository == REPOSITORY, "This maintenance task is restricted to its original repository")
    prefix = f"/repos/{repository}"
    before = release_snapshot(api, repository)
    artifacts = paginated(api, prefix + "/actions/artifacts", "artifacts")
    targets = []
    for release in before.values():
        for asset in release["assets"]:
            if asset["name"] in PRIVATE_NAMES:
                targets.append(("release_asset", asset, ASSET_FIELDS, prefix + f"/releases/assets/{asset['id']}", release["metadata"]["tag_name"]))
    for artifact in artifacts:
        if artifact["name"] in PRIVATE_NAMES:
            targets.append(("actions_artifact", artifact, ARTIFACT_FIELDS, prefix + f"/actions/artifacts/{artifact['id']}", None))
    report = {"repository": repository, "captured_at_utc": datetime.now(timezone.utc).isoformat(),
              "mode": "apply" if apply else "inventory", "status": "inventory",
              "release_count": len(before), "actions_artifact_count": len(artifacts),
              "targets": [{"kind": kind, "id": item["id"], "name": item["name"], "tag": tag}
                          for kind, item, fields, path, tag in targets], "deleted": [], "already_absent": []}
    if not apply:
        return report
    # Freeze the complete inventory before the first mutation: deleting from a
    # live paginated collection would otherwise skip assets on later pages.
    for kind, original, fields, path, tag in targets:
        current = api.request("GET", path, missing=True)
        item = {"kind": kind, "id": original["id"], "name": original["name"], "tag": tag}
        if current is None:
            report["already_absent"].append(item)
            continue
        require(current.get("name") in PRIVATE_NAMES and signature(current, fields) == signature(original, fields),
                f"Asset {original['id']} changed since inventory; deletion refused")
        api.request("DELETE", path)
        require(api.request("GET", path, missing=True) is None, f"Deleted asset {original['id']} remains accessible through the API")
        report["deleted"].append(item)
        print(f"Removed {kind} {original['id']} ({original['name']}); API 404 verified", flush=True)
    after = release_snapshot(api, repository)
    for identity, original in before.items():
        require(identity in after, f"Release {identity} disappeared during maintenance")
        current = after[identity]
        require(original["metadata"] == current["metadata"], f"Release {identity} metadata changed during maintenance")
        preserved = lambda release: {item["id"]: signature(item, ASSET_FIELDS) for item in release["assets"] if item["name"] not in PRIVATE_NAMES}
        require(preserved(original) == preserved(current), f"Other release assets changed for release {identity}")
    require(not any(asset["name"] in PRIVATE_NAMES for release in after.values() for asset in release["assets"]),
            "A retired site asset is still published; a concurrent publication may have occurred")
    remaining = paginated(api, prefix + "/actions/artifacts", "artifacts")
    require(not any(item["name"] in PRIVATE_NAMES for item in remaining), "A retired site Actions artifact is still present")
    report.update(status="passed", release_metadata_unchanged=True, other_release_assets_unchanged=True,
                  retired_names_absent=True, api_404_verified_for_each_deleted_asset=True)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", default=REPOSITORY)
    parser.add_argument("--apply", action="store_true", help="Delete only the three exact retired asset names")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    try:
        report = remove(GitHub(args.repository, os.environ.get("GH_TOKEN", "")), args.repository, apply=args.apply)
        if args.report:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 0
    except (RemovalError, OSError, ValueError, KeyError, TypeError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
