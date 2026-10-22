#!/usr/bin/env bash
set -euo pipefail
# Only exact, successful Windows build artifacts are promoted. No build is performed here.
: "${GH_TOKEN:?GitHub token required}"
: "${GITHUB_REPOSITORY:?Repository required}"
: "${GITHUB_SHA:?Tested commit required}"
: "${PROD_ARTIFACT_ID:?PROD artifact required}"
: "${DEV_ARTIFACT_ID:?DEV artifact required}"
version=$(python3 -c 'import xml.etree.ElementTree as E; print(E.parse("src/GlpiPilot/GlpiPilot.csproj").findtext("PropertyGroup/Version"))')
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid release version'; exit 1; }
tag="v$version"
notes="docs/RELEASE-$version.md"
[[ -f "$notes" ]] || { echo 'Release notes are missing'; exit 1; }
release_dir=$(mktemp -d)
trap 'rm -rf "$release_dir"' EXIT
for edition in PROD DEV; do
  if [[ "$edition" == PROD ]]; then artifact_id="$PROD_ARTIFACT_ID"; else artifact_id="$DEV_ARTIFACT_ID"; fi
  [[ "$artifact_id" =~ ^[0-9]+$ ]] || { echo 'Invalid artifact identity'; exit 1; }
  name="GLPI-Pilot_${version}_${edition}_Windows-x64.zip"
  # gh follows the temporary artifact redirect; preserving stdout as bytes avoids another ZIP wrapper.
  gh api "repos/$GITHUB_REPOSITORY/actions/artifacts/$artifact_id/zip" > "$release_dir/$name"
done
python3 - "$release_dir" "$version" "$GITHUB_SHA" <<'PY'
import hashlib, json, pathlib, sys, zipfile
root, version, commit = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3]
lines, executables = [], []
for edition in ('PROD', 'DEV'):
    path = root / f'GLPI-Pilot_{version}_{edition}_Windows-x64.zip'
    with zipfile.ZipFile(path) as archive:
        roots = {name.split('/')[0] for name in archive.namelist()}
        if roots != {'GLPI-Pilot.exe', 'wad'}:
            raise SystemExit(f'{edition}: incorrect ZIP root or nested artifact wrapper')
        info = json.loads(archive.read('wad/version.json'))
        if info['version'] != version or info['source_commit'] != commit or info['distribution'] != edition:
            raise SystemExit(f'{edition}: artifact belongs to a different build')
        exe = hashlib.sha256(archive.read('GLPI-Pilot.exe')).hexdigest()
        if exe != info['executable_sha256']: raise SystemExit(f'{edition}: executable digest mismatch')
        executables.append(exe)
    lines.append(hashlib.sha256(path.read_bytes()).hexdigest() + '  ' + path.name)
if len(set(executables)) != 1: raise SystemExit('PROD and DEV executables differ')
(root / 'SHA256SUMS.txt').write_text('\n'.join(lines) + '\n', encoding='utf-8')
PY
existing=$(gh release view "$tag" --repo "$GITHUB_REPOSITORY" --json isDraft,targetCommitish 2>/dev/null || true)
if [[ -n "$existing" ]]; then
  draft=$(python3 -c 'import json,sys; print(str(json.load(sys.stdin)["isDraft"]).lower())' <<< "$existing")
  if [[ "$draft" != true ]]; then
    published_sha=$(gh api "repos/$GITHUB_REPOSITORY/commits/$tag" --jq '.sha')
    [[ "$published_sha" == "$GITHUB_SHA" ]] || { echo 'This version is already published for another commit. Bump the version.'; exit 1; }
    echo "Release $tag already published for this tested commit; its assets remain unchanged."
    exit 0
  fi
  target=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["targetCommitish"])' <<< "$existing")
  [[ "$target" == "$GITHUB_SHA" ]] || { echo 'An existing draft targets another commit; refusing to replace it.'; exit 1; }
  gh release upload "$tag" "$release_dir"/* --repo "$GITHUB_REPOSITORY" --clobber
  gh release edit "$tag" --repo "$GITHUB_REPOSITORY" --notes-file "$notes" --draft=false --latest
else
  gh release create "$tag" "$release_dir"/* --repo "$GITHUB_REPOSITORY" --target "$GITHUB_SHA" --title "GLPI Pilot $version · Windows PROD + DEV" --notes-file "$notes" --latest
fi
printf '\n## Packs publiés\n[GLPI Pilot %s](https://github.com/%s/releases/tag/%s)\n' "$version" "$GITHUB_REPOSITORY" "$tag" >> "$GITHUB_STEP_SUMMARY"
