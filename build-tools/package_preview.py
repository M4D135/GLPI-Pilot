"""Package a locally compiled Windows preview without claiming Windows execution.

The stable release path remains build.ps1 followed by the Windows CI gates.
This helper requires a clean, committed source tree and never replaces an output.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

from package_dual_140 import (
    PackagingError, check_pe, copy_verified, json_bytes, mapping,
    regular_file, require, safe_name, sha256, sha256_stream, tree_files, write_new,
)

ROOT = Path(__file__).resolve().parents[1]
SUITES = (
    "StartupDiagnosticsChecks", "StartupResilienceChecks", "FoundationChecks",
    "WorkplaceChecks", "ConfigurationChecks", "DashboardChecks",
    "DashboardLayoutChecks", "PreviewQueryChecks", "TechnicianWorkloadChecks",
    "CityDetectionChecks", "TourPlanningChecks", "QueryChecks",
    "NativeImportChecks", "ExportChecks", "BrowserChecks", "AutomationChecks",
    "NativeDomChecks", "TicketDetailChecks",
)


def git(*arguments):
    return subprocess.check_output(["git", *arguments], cwd=ROOT).decode("utf-8").strip()


def verify_zip(path, expected, edition, commit, version, exe_hash, sources):
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        require(len(names) == len(set(name.casefold() for name in names)), "Duplicate archive path")
        require(set(names) == set(expected), "Archive differs from its staged files")
        require({name.split("/")[0] for name in names} == {"GLPI-Pilot.exe", "wad"}, "Incorrect pack root")
        require(archive.testzip() is None, "Archive CRC verification failed")
        for name in names:
            safe_name(name)
            with archive.open(name) as stream:
                require(sha256_stream(stream) == expected[name]["sha256"], "Archive checksum mismatch: " + name)
        metadata = json.loads(archive.read("wad/version.json"))
        require(metadata["source_commit"] == commit and metadata["version"] == version,
                "Pack belongs to a different source version")
        require(metadata["distribution"] == edition and metadata["channel"] == "local-preview",
                "Pack preview status is missing")
        require(metadata["windows_execution_verified"] is False, "Preview cannot claim Windows verification")
        require(metadata["executable_sha256"] == exe_hash, "Executable metadata mismatch")
        declared = archive.read("wad/SHA256SUMS.txt").decode("utf-8").splitlines()
        actual = [f"{entry['sha256']}  {name}" for name, entry in sorted(expected.items())
                  if name != "wad/SHA256SUMS.txt"]
        require(declared == actual, "Checksum manifest does not cover every file")
        archived_sources = {
            name.removeprefix("wad/sources/"): value for name, value in expected.items()
            if name.startswith("wad/sources/")
        }
        require(archived_sources == (sources if edition == "DEV" else {}), "Source archive coverage mismatch")
        return {name: entry for name, entry in expected.items()
                if name == "GLPI-Pilot.exe" or name.startswith("wad/.playwright/")}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--validation", type=Path, required=True)
    parser.add_argument("--sdk-version", required=True)
    args = parser.parse_args()
    publish, output, validation = (path.resolve() for path in (args.publish, args.output, args.validation))
    require(all(path.is_relative_to(ROOT) for path in (publish, output, validation)),
            "Build inputs and outputs must remain inside this repository")
    require(not output.exists(), "Output already exists; use a new destination")
    require(not git("status", "--porcelain", "--untracked-files=normal"),
            "Commit the exact source files before producing the preview")
    commit = git("rev-parse", "HEAD")
    branch = git("symbolic-ref", "--short", "HEAD")
    source_tree = git("rev-parse", "HEAD^{tree}")
    project = ET.parse(ROOT / "src/GlpiPilot/GlpiPilot.csproj").getroot()
    version = project.findtext(".//Version")
    require(version and all(part.isdigit() for part in version.split(".")), "Invalid project version")
    executable = publish / "GLPI-Pilot.exe"
    pe = check_pe(executable, gui=True)
    executable_data = executable.read_bytes()
    require(version.encode("utf-16-le") in executable_data, "Executable version resource differs from the project")
    require(b"asInvoker" in executable_data, "Expected Windows application manifest is missing")
    del executable_data
    exe_info = {"bytes": executable.stat().st_size, "sha256": sha256(executable)}
    driver = mapping(tree_files(publish / ".playwright"), publish / ".playwright")
    require({"node/win32_x64/node.exe", "package/cli.js", "package/package.json"} <= driver.keys(),
            "The complete Windows Playwright driver is required")
    check_pe(publish / ".playwright/node/win32_x64/node.exe")
    driver_package = json.loads((publish / ".playwright/package/package.json").read_text())
    playwright = project.find(".//PackageReference[@Include='Microsoft.Playwright']")
    require(playwright is not None and playwright.get("Version") == driver_package["version"],
            "Source and driver versions differ")
    tracked = [ROOT / name for name in git("ls-tree", "-r", "--name-only", "HEAD").splitlines()]
    sources = mapping(tracked, ROOT)
    common = {name: entry for name, entry in sources.items()
              if name.startswith(("docs/", "support/", "licenses/"))}
    for name in ("support/DEBLOQUER_TOUT.cmd", "support/DIAGNOSTIC_DEMARRAGE.cmd",
                 "docs/Guide-GLPI-Pilot.html", "docs/Suivi-200-ameliorations.md"):
        require(name in common, "Required support file is missing: " + name)
    logs, test_count = {}, 0
    for suite in SUITES:
        path = validation / (suite + ".log")
        regular_file(path)
        require(path.stat().st_size > 0, "Empty validation log: " + suite)
        last = path.read_text(encoding="utf-8").strip().splitlines()[-1]
        ratio = re.match(r"^(\d+)/(\d+)\b", last)
        counted = re.match(r"^(\d+) .+checks passed\.$", last)
        diagnostics = re.match(r"^Startup diagnostics: (\d+) passed, 0 failed\.$", last)
        if ratio:
            require(ratio[1] == ratio[2] and int(ratio[1]) > 0, "A suite is not fully successful: " + suite)
            test_count += int(ratio[1])
        else:
            summary = counted or diagnostics
            require(summary is not None and int(summary[1]) > 0, "Successful final summary missing: " + suite)
            test_count += int(summary[1])
        logs[path.name] = {"bytes": path.stat().st_size, "sha256": sha256(path)}
    cross_build = validation / "Windows-cross-build.log"
    regular_file(cross_build)
    build_text = cross_build.read_text(encoding="utf-8")
    require("Build succeeded." in build_text and "0 Error(s)" in build_text, "Cross-build did not succeed")
    logs[cross_build.name] = {"bytes": cross_build.stat().st_size, "sha256": sha256(cross_build)}
    created = datetime.now(timezone.utc).isoformat(timespec="seconds")
    output.mkdir(parents=True)
    bundle = output / "GLPI-Pilot-source.bundle"
    subprocess.run(["git", "bundle", "create", str(bundle), "HEAD", "refs/heads/" + branch], cwd=ROOT, check=True)
    subprocess.run(["git", "bundle", "verify", str(bundle)], cwd=ROOT, check=True,
                   stdout=subprocess.DEVNULL)
    bundle_info = {"bytes": bundle.stat().st_size, "sha256": sha256(bundle)}
    results, runtime_files = [], {}
    for edition in ("PROD", "DEV"):
        stage = output / edition
        stage.mkdir()
        wad = stage / "wad"
        copy_verified(executable, stage / "GLPI-Pilot.exe", exe_info)
        for name, info in driver.items():
            copy_verified(publish / ".playwright" / name, wad / ".playwright" / name, info)
        for name, info in common.items():
            copy_verified(ROOT / name, wad / name, info)
        copy_verified(ROOT / "README.md", wad / "docs/LISEZ-MOI.md", sources["README.md"])
        if edition == "DEV":
            for name, info in sources.items():
                copy_verified(ROOT / name, wad / "sources" / name, info)
            for name, info in logs.items():
                copy_verified(validation / name, wad / "validation" / name, info)
            copy_verified(bundle, wad / "recovery/GLPI-Pilot-source.bundle", bundle_info)
            write_new(wad / "recovery/REPRISE.txt", (
                f"GLPI Pilot {version} — sources exactes de cette préversion\nCommit : {commit}\n\n"
                "Les sources dépliées sont dans wad/sources. Le bundle conserve le commit\n"
                "et ses ancêtres. Pour reprendre dans un nouveau dossier avec Git :\n\n"
                f'git clone --branch "{branch}" "GLPI-Pilot-source.bundle" GLPI-Pilot-reprise\n'
                "cd GLPI-Pilot-reprise\n"
                "git switch -c reprise-2.0.0\n"
                "git remote set-url origin https://github.com/M4D135/GLPI-Pilot.git\n\n"
                "Le clone seul ne publie rien. Après restauration de l’accès GitHub,\n"
                "vérifier la branche distante, puis publier et exécuter la chaîne Windows.\n"
                "Les contrôles locaux ne remplacent pas le lancement réel de l’EXE.\n"
            ).encode("utf-8"))
        write_new(wad / "docs/ETAT-PREVERSION.txt", (
            f"GLPI Pilot {version} — PRÉVERSION {edition}\nCommit local : {commit}\n\n"
            "L’application et son pilote Windows ont été compilés et vérifiés localement.\n"
            "Les suites métier et les scénarios navigateur synthétiques ont réussi.\n"
            "Le connecteur GitHub a refusé la publication (HTTP 403). Le rendu WPF,\n"
            "le démarrage réel de l’EXE et les contrôles de zoom Windows n’ont donc\n"
            "pas été exécutés pour ce commit. Le portail privé et son SSO restent à essayer.\n\n"
            "Extraire l’archive complète dans un nouveau dossier. Conserver wad à côté\n"
            "de GLPI-Pilot.exe. Edge ou Chrome doit être installé et autorisé.\n"
            "Le pack DEV contient aussi les sources, les journaux et un bundle Git.\n"
        ).encode("utf-8"))
        write_new(wad / "version.json", json_bytes({
            "application": "GLPI Pilot", "version": version, "target": "win-x64",
            "channel": "local-preview", "distribution": edition, "self_contained": True,
            "created_utc": created, "sdk": args.sdk_version, "playwright": driver_package["version"],
            "source_commit": commit, "source_tree": source_tree,
            "remote_publication": "pending", "windows_execution_verified": False,
            "local_checks_passed": test_count,
            "executable_sha256": exe_info["sha256"],
        }))
        payload = mapping(tree_files(stage), stage)
        write_new(wad / "SHA256SUMS.txt", "".join(
            f"{info['sha256']}  {name}\n" for name, info in sorted(payload.items())
        ).encode("utf-8"))
        expected = mapping(tree_files(stage), stage)
        archive_path = output / f"GLPI-Pilot_{version}_PREVERSION_{edition}_Windows-x64.zip"
        print(f"{edition}: archive et vérification de {len(expected)} fichiers…", flush=True)
        with zipfile.ZipFile(archive_path, "x", zipfile.ZIP_DEFLATED, compresslevel=6, strict_timestamps=False) as archive:
            for name in sorted(expected):
                archive.write(stage / name, name)
        runtime_files[edition] = verify_zip(archive_path, expected, edition, commit, version, exe_info["sha256"], sources)
        results.append({"edition": edition, "path": str(archive_path), "bytes": archive_path.stat().st_size,
                        "sha256": sha256(archive_path), "files": len(expected)})
    require(runtime_files["PROD"] == runtime_files["DEV"], "PROD and DEV runtimes differ")
    write_new(output / "SHA256SUMS.txt", "".join(
        f"{item['sha256']}  {Path(item['path']).name}\n" for item in results
    ).encode("utf-8"))
    write_new(output / "verification.json", json_bytes({
        "version": version, "source_commit": commit, "source_tree": source_tree, "pe": pe,
        "sources": len(sources), "validation_logs": len(logs), "windows_execution_verified": False,
        "local_checks_passed": test_count,
        "identical_executable_and_driver": True, "archives": results,
    }))
    print(json.dumps(results, ensure_ascii=False, indent=2), flush=True)


if __name__ == "__main__":
    try:
        main()
    except (PackagingError, OSError, ValueError, subprocess.CalledProcessError, zipfile.BadZipFile) as error:
        print("Préparation interrompue : " + str(error), file=sys.stderr)
        sys.exit(1)
