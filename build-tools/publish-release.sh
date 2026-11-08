#!/usr/bin/env bash
set -euo pipefail
# Only the exact PROD-LIGHT ZIP retained by the successful Windows build is promoted. No build here.
: "${GH_TOKEN:?GitHub token required}"
: "${GITHUB_REPOSITORY:?Repository required}"
: "${GITHUB_SHA:?Tested commit required}"
: "${QUALIFIED_RUN_ID:?Inspected complete Windows run required}"
: "${QUALIFIED_RUN_ATTEMPT:?Inspected run attempt required}"
: "${EXPECTED_LIGHT_SHA256:?Inspected PROD-LIGHT digest required}"
version=$(python3 -c 'import xml.etree.ElementTree as E; print(E.parse("src/GlpiPilot/GlpiPilot.csproj").findtext("PropertyGroup/Version"))')
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid release version'; exit 1; }
tag="v$version"
notes="docs/RELEASE-$version.md"
[[ -f "$notes" ]] || { echo 'Release notes are missing'; exit 1; }
release_dir=$(mktemp -d)
published_dir=$(mktemp -d)
evidence_dir="$PWD/artifacts/published-verification"
mkdir -p "$evidence_dir"
trap 'rm -rf "$release_dir" "$published_dir"' EXIT
python3 tests/ReleasePackChecks/test_verify.py
# Re-check run receipts even when invoked outside the Actions step. The worker must possess the same bytes.
python3 build-tools/select-verified-release.py --stored "$evidence_dir" "$GITHUB_SHA" "$QUALIFIED_RUN_ID" \
  "$QUALIFIED_RUN_ATTEMPT" "$version" "$EXPECTED_LIGHT_SHA256"
store="${GLPI_CI_EVIDENCE_ROOT:-$HOME/GLPI-Pilot-Evidence}/$QUALIFIED_RUN_ID/$QUALIFIED_RUN_ATTEMPT/$GITHUB_SHA"
name="GLPI-Pilot_${version}_PROD-LIGHT_Windows-x64.zip"
[[ -f "$store/$name" && ! -L "$store/$name" ]] || {
  echo "Missing retained original: $name (expected in $store)."
  echo 'The publication workflow places it there from the qualified run artifact; run it through'
  echo '.github/workflows/publish-verified-release.yml, or copy the same bytes there after checking their SHA-256.'
  exit 1; }
cp -- "$store/$name" "$release_dir/$name"
python3 - "$release_dir" "$version" "$GITHUB_SHA" <<'PY'
import hashlib, json, os, pathlib, sys, zipfile
root, version, commit = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3]
# PROD-LIGHT uses the WebView2 runtime installed on Windows (verify-release-packs.py checks it).
edition = 'PROD-LIGHT'
path = root / f'GLPI-Pilot_{version}_{edition}_Windows-x64.zip'
with zipfile.ZipFile(path) as archive:
    roots = {name.split('/')[0] for name in archive.namelist()}
    if roots != {'GLPI-Pilot.exe', 'GLPI-Pilot-Admin.exe', 'wad'}:
        raise SystemExit(f'{edition}: incorrect ZIP root or nested artifact wrapper')
    info = json.loads(archive.read('wad/version.json'))
    if info['version'] != version or info['source_commit'] != commit or info['distribution'] != edition:
        raise SystemExit(f'{edition}: artifact belongs to a different build')
    exe = hashlib.sha256(archive.read('GLPI-Pilot.exe')).hexdigest()
    if exe != info['executable_sha256']: raise SystemExit(f'{edition}: executable digest mismatch')
    admin = hashlib.sha256(archive.read('GLPI-Pilot-Admin.exe')).hexdigest()
    if admin != info['admin_executable_sha256']: raise SystemExit(f'{edition}: Admin executable digest mismatch')
    if admin == exe: raise SystemExit(f'{edition}: Standard and Admin must be distinct compiled binaries')
with path.open("rb") as stream: digest = hashlib.file_digest(stream, "sha256").hexdigest()
if digest != os.environ['EXPECTED_LIGHT_SHA256']:
    raise SystemExit(f'{edition}: downloaded bytes differ from the independently inspected archive')
(root / 'SHA256SUMS.txt').write_text(digest + '  ' + path.name + '\n', encoding='utf-8')
PY
python3 build-tools/verify-release-packs.py "$release_dir" "$version" "$GITHUB_SHA" --output "$evidence_dir/candidate-archives.json"
check_refs() {
  local main_sha tag_refs
  main_sha=$(gh api "repos/$GITHUB_REPOSITORY/git/ref/heads/main" --jq '.object.sha')
  [[ "$main_sha" == "$GITHUB_SHA" ]] || { echo 'main changed; refusing stale publication.'; return 1; }
  tag_refs=$(gh api "repos/$GITHUB_REPOSITORY/git/matching-refs/tags/$tag")
  if jq -e --arg ref "refs/tags/$tag" 'any(.[]; .ref==$ref)' <<< "$tag_refs" >/dev/null; then
    [[ "$(gh api "repos/$GITHUB_REPOSITORY/commits/$tag" --jq '.sha')" == "$GITHUB_SHA" ]] || { echo 'Existing tag belongs to another commit'; return 1; }
  fi
}
download_and_compare() {
  local output="$1" original
  gh release download "$tag" --repo "$GITHUB_REPOSITORY" --dir "$output" \
    --pattern 'SHA256SUMS.txt' --pattern "GLPI-Pilot_${version}_PROD-LIGHT_Windows-x64.zip"
  for original in "$release_dir"/*; do cmp "$original" "$output/${original##*/}"; done
}
check_inventory() {
  local draft="$1" metadata
  metadata=$(gh release view "$tag" --repo "$GITHUB_REPOSITORY" --json isDraft,targetCommitish,assets)
  python3 - "$version" "$draft" "$GITHUB_SHA" "$metadata" <<'PYINV'
import json, sys
version, state, commit, metadata = sys.argv[1:]
release = json.loads(metadata)
expected = sorted(['SHA256SUMS.txt', f'GLPI-Pilot_{version}_PROD-LIGHT_Windows-x64.zip'])
if (release['isDraft'] != (state == 'draft') or sorted(a['name'] for a in release['assets']) != expected
        or (release['isDraft'] and release['targetCommitish'] != commit)):
    raise SystemExit('Release state, target or asset inventory changed')
PYINV
}
check_refs
new_publication=true
existing=$(gh release view "$tag" --repo "$GITHUB_REPOSITORY" --json isDraft,targetCommitish,assets 2>/dev/null || true)
if [[ -n "$existing" ]]; then
  # An old draft must not silently reintroduce a withdrawn edition.
  python3 - "$version" "$existing" <<'PYASSETS'
import json, sys
version, release = sys.argv[1], json.loads(sys.argv[2])
allowed = {'SHA256SUMS.txt', f'GLPI-Pilot_{version}_PROD-LIGHT_Windows-x64.zip'}
names = {a['name'] for a in release['assets']}
if names - allowed or (not release['isDraft'] and names != allowed):
    raise SystemExit('Unexpected assets in the existing release. Check its inventory before promotion.')
PYASSETS
  draft=$(python3 -c 'import json,sys; print(str(json.load(sys.stdin)["isDraft"]).lower())' <<< "$existing")
  if [[ "$draft" != true ]]; then
    published_sha=$(gh api "repos/$GITHUB_REPOSITORY/commits/$tag" --jq '.sha')
    [[ "$published_sha" == "$GITHUB_SHA" ]] || { echo 'This version is already published for another commit. Bump the version.'; exit 1; }
    echo "Release $tag already published for this tested commit; its assets remain unchanged."
    new_publication=false
  else
    target=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["targetCommitish"])' <<< "$existing")
    [[ "$target" == "$GITHUB_SHA" ]] || { echo 'An existing draft targets another commit; refusing to replace it.'; exit 1; }
    for file in "$release_dir"/*; do
      name=${file##*/}
      if jq -e --arg name "$name" 'any(.assets[]; .name==$name)' <<< "$existing" >/dev/null; then
        gh release download "$tag" --repo "$GITHUB_REPOSITORY" --pattern "$name" --dir "$published_dir"
        cmp "$file" "$published_dir/$name" || { echo "Existing draft asset differs: $name"; exit 1; }
        rm -- "$published_dir/$name"
      else
        gh release upload "$tag" "$file" --repo "$GITHUB_REPOSITORY"
      fi
    done
  fi
else
  gh release create "$tag" "$release_dir"/* --repo "$GITHUB_REPOSITORY" --target "$GITHUB_SHA" --title "GLPI Pilot $version · Windows PROD-LIGHT" --notes-file "$notes" --draft
fi
if [[ "$new_publication" == true ]]; then
  mkdir "$published_dir/draft"
  download_and_compare "$published_dir/draft"
  check_inventory draft
  check_refs
  gh release edit "$tag" --repo "$GITHUB_REPOSITORY" --notes-file "$notes" --draft=false --latest
fi
# Download the distributed assets using release access, including for a private repository.
download_and_compare "$published_dir"
check_inventory published
python3 build-tools/verify-release-packs.py "$published_dir" "$version" "$GITHUB_SHA" --output "$evidence_dir/published-archives.json"
[[ "$(gh api "repos/$GITHUB_REPOSITORY/commits/$tag" --jq '.sha')" == "$GITHUB_SHA" ]]
cp "$published_dir/SHA256SUMS.txt" "$evidence_dir/SHA256SUMS.txt"
gh release view "$tag" --repo "$GITHUB_REPOSITORY" --json url,tagName,isDraft,isPrerelease,targetCommitish > "$evidence_dir/release.json"
printf 'Verified source commit: %s\nQualified Windows run: %s / %s\nPublication run: %s / %s\nOriginal ZIP SHA-256: %s\nNew publication: %s\nPROD-LIGHT: OK\n' "$GITHUB_SHA" "$QUALIFIED_RUN_ID" "$QUALIFIED_RUN_ATTEMPT" "${GITHUB_RUN_ID:-unknown}" "${GITHUB_RUN_ATTEMPT:-unknown}" "$EXPECTED_LIGHT_SHA256" "$new_publication" > "$evidence_dir/verification.log"
printf '\n## Pack publié\n[GLPI Pilot %s](https://github.com/%s/releases/tag/%s)\n' "$version" "$GITHUB_REPOSITORY" "$tag" >> "$GITHUB_STEP_SUMMARY"
