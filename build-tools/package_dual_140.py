"""Build and verify the PROD and DEV archives from the validated 1.4.0 publish.

Run with Python 3.9+ from any working directory, after freezing the sources and
the Windows build/CI scripts. This script does not compile or execute the EXE,
contact a server, change application settings, or replace an existing output.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import fnmatch
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import stat
import struct
import sys
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[1]
VERSION = "1.4.0"
ARTIFACTS = ROOT / "artifacts"
PUBLISH = ARTIFACTS / f"publish-{VERSION}-ready"
WORK = ARTIFACTS / f"release-{VERSION}-dual"
RELEASE_MANIFEST = ARTIFACTS / f"release-manifest-{VERSION}-dual.json"
EDITIONS = ("PROD", "DEV")
SOURCE_DIRS = ("src", "tests", "docs", "support", "licenses", ".github")
SOURCE_ROOT_FILES = ("README.md", "build.ps1", ".gitignore")
BLOCK_SIZE = 1024 * 1024

# These are complete, current verification logs, not the earlier failed build
# attempt named build-1.4.0.log. Their original contents are retained verbatim.
CURRENT_LOGS = {
    f"{name}-{VERSION}.log": ARTIFACTS / f"{name}-{VERSION}.log"
    for name in (
        "automation-checks", "startup-diagnostics-checks", "configuration-checks",
        "dashboard-checks", "dashboard-layout-checks", "preview-query-checks",
        "technician-workload-checks", "city-detection-checks", "tour-planning-checks",
        "query-checks", "windows-startup-build", "packaging-script-checks",
    )
}
CURRENT_LOGS[f"build-publish-wpf-{VERSION}.log"] = ARTIFACTS / f"publish-{VERSION}.log"
BASELINE_LOGS = {
    f"{name}-baseline-1.3.0.log": ARTIFACTS / f"{name}-1.3.0.log"
    for name in ("browser-checks", "export-checks", "native-import-checks", "ticket-detail-checks")
}
BASELINE_LOGS.update({
    "native-dom-checks-previous-1.1.1.log": ROOT / "tests/NativeDomChecks/result-20261021-v111.log",
    "dom-checks-previous-1.1.0.log": ROOT / "tests/DomChecks/result-20261021.log",
})

EXCLUDED_DIRS = {
    ".git", ".svn", ".hg", ".vs", ".idea", "__pycache__", ".pytest_cache",
    ".mypy_cache", ".ruff_cache", ".venv", "venv", "bin", "obj", "node_modules",
    "artifacts", "testresults", "publish", "dist", "profiles", "profile", "profils",
    "browser", "browserprofiles", "browser-profiles", "user-data", "local-data",
    "sessions", "logs", "exports", "export", "captures", "screenshots", "upload", "uploads",
}
EXCLUDED_FILES = {
    "settings.json", "technician-preferences.json", "preview-view.json", "tour-order.json",
    "city-settings.json", "credentials.json", "secrets.json", ".ds_store", "thumbs.db",
    "desktop.ini", "id_rsa", "id_ed25519", "cookies", "cookies.sqlite", "login data",
}
EXCLUDED_PATTERNS = (
    ".env", ".env.*", "settings.json.*", "settings-backup*.json", "configuration*.json",
    "credentials*.json", "secrets*.json", "*.user", "*.suo", "*.pdb", "*.pyc", "*.pyo",
    "*.tmp", "*.temp", "*.bak", "*.orig", "*.swp", "*~", "*.part", "*.partial",
    "*.crdownload", "*.pem", "*.pfx", "*.p12", "*.key", ".node.exe.*",
)
SYNTHETIC_SOURCE_LOGS = {
    "tests/DomChecks/result-20261021.log",
    "tests/NativeDomChecks/result-20261021.log",
    "tests/NativeDomChecks/result-20261021-v111.log",
}


class PackagingError(RuntimeError):
    """A packaging invariant was not satisfied; no existing output is removed."""


def require(condition, message):
    if not condition:
        raise PackagingError(message)


def sha256_stream(stream):
    hasher = hashlib.sha256()
    for block in iter(lambda: stream.read(BLOCK_SIZE), b""):
        hasher.update(block)
    return hasher.hexdigest()


def sha256(path):
    with path.open("rb") as stream:
        return sha256_stream(stream)


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n").encode("utf-8")


def regular_file(path):
    require(not path.is_symlink(), f"Symbolic links are not packaged: {path}")
    require(path.is_file() and stat.S_ISREG(path.stat().st_mode), f"Required regular file missing: {path}")


def safe_name(name):
    parts = PurePosixPath(name).parts
    require(bool(parts) and not name.startswith("/") and "\\" not in name,
            f"Unsafe archive path: {name}")
    for part in parts:
        require(part not in (".", "..") and not part.endswith((".", " "))
                and not any(ord(char) < 32 or char in ':*?"<>|' for char in part),
                f"Path cannot be extracted safely on Windows: {name}")
    return name


def transfer_partial(name):
    lower = name.casefold()
    return lower.startswith(".node.exe.") or lower.endswith((".part", ".partial", ".crdownload"))


def excluded_source(path):
    relative = path.relative_to(ROOT).as_posix()
    name = path.name.casefold()
    return (
        name in EXCLUDED_FILES
        or any(fnmatch.fnmatchcase(name, pattern) for pattern in EXCLUDED_PATTERNS)
        or (name.endswith(".log") and relative not in SYNTHETIC_SOURCE_LOGS)
    )


def tree_files(folder, source=False):
    require(folder.is_dir() and not folder.is_symlink(), f"Required directory missing or linked: {folder}")
    paths = []
    for current, directories, files in os.walk(folder, followlinks=False):
        current_path = Path(current)
        kept = []
        for name in sorted(directories):
            if source and name.casefold() in EXCLUDED_DIRS:
                continue
            path = current_path / name
            require(not path.is_symlink() and not getattr(path, "is_junction", lambda: False)(),
                    f"Linked directories are not packaged: {path}")
            kept.append(name)
        directories[:] = kept
        for name in sorted(files):
            path = current_path / name
            if transfer_partial(name) or (source and excluded_source(path)):
                continue
            regular_file(path)
            require(path.resolve().is_relative_to(ROOT), f"File is outside the project: {path}")
            paths.append(path)
    return paths


def mapping(paths, base):
    result = {}
    case_names = set()
    for path in sorted(paths):
        name = safe_name(path.relative_to(base).as_posix())
        require(name.casefold() not in case_names, f"Duplicate Windows path: {name}")
        case_names.add(name.casefold())
        result[name] = {"bytes": path.stat().st_size, "sha256": sha256(path)}
    return result


def map_digest(files):
    return hashlib.sha256(json_bytes(files)).hexdigest()


def check_pe(path, gui=False):
    regular_file(path)
    size = path.stat().st_size
    with path.open("rb") as stream:
        dos = stream.read(64)
        require(len(dos) == 64 and dos[:2] == b"MZ", f"Invalid DOS/PE header: {path}")
        offset = struct.unpack_from("<I", dos, 60)[0]
        require(64 <= offset <= size - 96, f"Invalid PE header offset: {path}")
        stream.seek(offset)
        header = stream.read(96)
    require(header[:4] == b"PE\0\0", f"Invalid PE signature: {path}")
    machine = struct.unpack_from("<H", header, 4)[0]
    optional_size, characteristics = struct.unpack_from("<HH", header, 20)
    require(machine == 0x8664, f"Executable is not Windows AMD64: {path}")
    require(optional_size >= 70 and struct.unpack_from("<H", header, 24)[0] == 0x20B,
            f"Executable is not PE32+: {path}")
    require(characteristics & 0x0002 and not characteristics & 0x2000,
            f"Expected an executable, not a DLL: {path}")
    subsystem = struct.unpack_from("<H", header, 24 + 68)[0]
    require(not gui or subsystem == 2, f"Application is not a Windows GUI executable: {path}")
    return {"machine": "AMD64", "pe_format": "PE32+", "subsystem": subsystem, "bytes": size}


def copy_verified(source, destination, expected):
    regular_file(source)
    destination.parent.mkdir(parents=True, exist_ok=True)
    with source.open("rb") as incoming, destination.open("xb") as outgoing:
        shutil.copyfileobj(incoming, outgoing, BLOCK_SIZE)
    require(destination.stat().st_size == expected["bytes"] and sha256(destination) == expected["sha256"],
            f"Input changed while packaging, or copy failed: {source}")
    shutil.copystat(source, destination, follow_symlinks=False)


def write_new(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as stream:
        stream.write(data)


def select_sources():
    paths = []
    for name in SOURCE_DIRS:
        paths.extend(tree_files(ROOT / name, source=True))
    for name in SOURCE_ROOT_FILES:
        regular_file(ROOT / name)
        paths.append(ROOT / name)
    optional = [ROOT / name for name in ("global.json", "NuGet.Config", "nuget.config", "Directory.Packages.props")]
    optional.extend(ROOT.glob("Directory.Build.*"))
    for path in sorted(optional):
        if os.path.lexists(path):
            regular_file(path)
            paths.append(path)
    # Only top-level build helpers belong here: downloaded SDK/cache contents do
    # not become sources merely because they contain Python or PowerShell files.
    for path in sorted((ROOT / "build-tools").iterdir()):
        if path.is_file() and path.suffix.casefold() in (".py", ".ps1"):
            regular_file(path)
            paths.append(path)
    files = mapping(paths, ROOT)
    required = {
        "src/GlpiPilot/GlpiPilot.csproj", "src/GlpiPilot/MainWindow.Cities.cs",
        "src/GlpiPilot/MainWindow.Queries.cs", "src/GlpiPilot/Core/QueryService.cs",
        "src/GlpiPilot/Core/CityDetectionService.cs", "src/GlpiPilot/Core/TourPlanningService.cs",
        "src/GlpiPilot/UI/QueryWorkshopView.cs", ".github/workflows/windows.yml",
        "build-tools/Invoke-WindowsCi.ps1", "build-tools/package_dual_140.py",
        "tests/QueryChecks/Program.cs", "tests/WindowsStartupChecks/Program.cs",
        *SOURCE_ROOT_FILES,
    }
    require(required <= files.keys(), "Missing source components: " + ", ".join(sorted(required - files.keys())))
    return files


def select_validation(extra_logs):
    selected = {}
    index = []
    for category, entries in (("current-1.4.0", CURRENT_LOGS), ("historical-baselines", BASELINE_LOGS)):
        for filename, path in entries.items():
            regular_file(path)
            require(path.stat().st_size > 0, f"Validation log is empty: {path}")
            selected[filename] = path
            index.append({"file": filename, "scope": category, "source": path.relative_to(ROOT).as_posix()})
    for supplied in extra_logs:
        path = (ROOT / supplied).resolve()
        require(path.is_relative_to(ARTIFACTS) and path.suffix.casefold() == ".log",
                "Additional validation logs must be .log files under artifacts/.")
        regular_file(path)
        require(path.stat().st_size > 0, f"Validation log is empty: {path}")
        require(path.name.casefold() not in {name.casefold() for name in selected},
                f"Duplicate validation log name: {path.name}")
        safe_name(path.name)
        selected[path.name] = path
        index.append({"file": path.name, "scope": "additional-validation",
                      "source": path.relative_to(ROOT).as_posix()})
    for record in index:
        path = selected[record["file"]]
        record.update({"bytes": path.stat().st_size, "sha256": sha256(path)})
    return selected, index


def validate_archive(path, expected, edition, sources, driver):
    with zipfile.ZipFile(path, "r") as archive:
        infos = archive.infolist()
        names = [info.filename for info in infos]
        require(len(names) == len(set(name.casefold() for name in names)), "Duplicate ZIP members")
        require(set(names) == set(expected), f"Archive member list differs from stage: {edition}")
        require({name.split("/", 1)[0] for name in names} == {"GLPI-Pilot.exe", "wad"},
                "Archive root must contain only GLPI-Pilot.exe and wad/")
        for info in infos:
            safe_name(info.filename)
            require(not info.is_dir(), "Unexpected directory entry in archive manifest")
            require(not transfer_partial(PurePosixPath(info.filename).name), "Transfer partial in archive")
            require(not stat.S_ISLNK(info.external_attr >> 16), "Symbolic link in archive")
        require(archive.testzip() is None, f"ZIP CRC verification failed: {path}")
        actual = {}
        for info in infos:
            with archive.open(info) as stream:
                actual[info.filename] = {"bytes": info.file_size, "sha256": sha256_stream(stream)}
        require(actual == expected, f"ZIP SHA-256 verification failed: {edition}")
        checksums = archive.read("wad/SHA256SUMS.txt").decode("utf-8").splitlines()
        required_checksums = [f"{entry['sha256']}  {name}" for name, entry in sorted(actual.items())
                              if name != "wad/SHA256SUMS.txt"]
        require(checksums == required_checksums, "SHA256SUMS does not cover the exact archive contents")
        embedded = json.loads(archive.read("wad/manifest.json"))
        payload = {name: entry for name, entry in actual.items()
                   if name not in ("wad/manifest.json", "wad/SHA256SUMS.txt")}
        require(embedded["files"] == payload and embedded["edition"] == edition,
                "Embedded payload manifest does not match the archive")
        version = json.loads(archive.read("wad/version.json"))
        require(version["version"] == VERSION and version["edition"] == edition,
                "Embedded version metadata does not match the archive")
        archived_driver = {name.removeprefix("wad/.playwright/"): entry for name, entry in actual.items()
                           if name.startswith("wad/.playwright/")}
        archived_sources = {name.removeprefix("wad/sources/"): entry for name, entry in actual.items()
                            if name.startswith("wad/sources/")}
        require(archived_driver == driver, "Packaged Playwright differs from the complete published driver")
        require(archived_sources == (sources if edition == "DEV" else {}),
                "DEV sources are incomplete/modified, or sources leaked into PROD")
        require(not any(name.startswith("wad/validation/") for name in actual) if edition == "PROD" else True,
                "Validation logs must be confined to DEV")
        return actual


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--validation-log", action="append", default=[], metavar="ARTIFACT_LOG",
                        help="Also include this explicitly selected artifacts/*.log file in DEV (repeatable).")
    args = parser.parse_args(argv)
    destinations = {edition: ARTIFACTS / f"GLPI-Pilot_{VERSION}_{edition}_Windows-x64.zip" for edition in EDITIONS}
    for path in (WORK, RELEASE_MANIFEST, *destinations.values()):
        require(not os.path.lexists(path), f"Refusing to replace existing output: {path}")

    executable = PUBLISH / "GLPI-Pilot.exe"
    exe_pe = check_pe(executable, gui=True)
    executable_bytes = executable.read_bytes()
    require(b"asInvoker" in executable_bytes, "EXE does not contain the expected asInvoker manifest")
    require(VERSION.encode("utf-16-le") in executable_bytes, "EXE version resource does not contain 1.4.0")
    del executable_bytes
    exe_info = {"bytes": executable.stat().st_size, "sha256": sha256(executable)}
    driver = mapping(tree_files(PUBLISH / ".playwright"), PUBLISH / ".playwright")
    required_driver = {"node/win32_x64/node.exe", "node/LICENSE", "package/package.json", "package/cli.js",
                       "package/index.js", "package/browsers.json", "package/lib/bootstrap.js",
                       "package/lib/coreBundle.js", "package/LICENSE", "package/NOTICE"}
    require(required_driver <= driver.keys(), "Published Playwright driver is incomplete")
    node_pe = check_pe(PUBLISH / ".playwright/node/win32_x64/node.exe")
    package = json.loads((PUBLISH / ".playwright/package/package.json").read_text(encoding="utf-8"))
    project = ET.parse(ROOT / "src/GlpiPilot/GlpiPilot.csproj").getroot()
    package_ref = project.find(".//PackageReference[@Include='Microsoft.Playwright']")
    require(project.findtext(".//Version") == VERSION, "Source project version is not 1.4.0")
    require(package_ref is not None and package_ref.get("Version") == package.get("version"),
            "Playwright package and source project versions do not match")

    sources = select_sources()
    common = {}
    for folder in ("docs", "support", "licenses"):
        common.update(mapping(tree_files(ROOT / folder, source=True), ROOT))
    for required in ("support/DIAGNOSTIC_DEMARRAGE.cmd", "support/Diagnostic-Demarrage.ps1",
                     "support/LIRE-LE-DIAGNOSTIC.txt", "docs/Rapport-verification.txt"):
        require(required in common, f"Required support/documentation file missing: {required}")
    validation_files, validation_index = select_validation(args.validation_log)
    captured_at = datetime.now(timezone.utc).isoformat(timespec="seconds")
    WORK.mkdir(parents=True, exist_ok=False)
    release = {"version": VERSION, "created_utc": captured_at, "layout": "GLPI-Pilot.exe + wad/",
               "source_files": len(sources), "source_snapshot_sha256": map_digest(sources),
               "driver_files": len(driver), "driver_snapshot_sha256": map_digest(driver),
               "executable": exe_info, "pe": {"application": exe_pe, "node": node_pe},
               "verification": "Static PE checks, source/payload SHA-256 equality, ZIP CRC; no Windows execution.",
               "files": []}
    archive_manifests = {}
    for edition in EDITIONS:
        print(f"[{edition}] Copying verified payload...", flush=True)
        stage = WORK / edition
        stage.mkdir()
        wad = stage / "wad"
        copy_verified(executable, stage / "GLPI-Pilot.exe", exe_info)
        for name, entry in driver.items():
            copy_verified(PUBLISH / ".playwright" / name, wad / ".playwright" / name, entry)
        for name, entry in common.items():
            copy_verified(ROOT / name, wad / name, entry)
        copy_verified(ROOT / "README.md", wad / "docs/LISEZ-MOI.md", sources["README.md"])
        if edition == "DEV":
            for name, entry in sources.items():
                copy_verified(ROOT / name, wad / "sources" / name, entry)
            for record in validation_index:
                copy_verified(validation_files[record["file"]], wad / "validation" / record["file"], record)
            write_new(wad / "validation/INDEX.json", json_bytes({
                "scope": "Current logs and explicitly named historical baselines; no Windows execution is claimed.",
                "logs": validation_index,
            }))
        write_new(wad / "docs/PACK-PROD-DEV.txt", (
            f"GLPI Pilot {VERSION} — pack {edition}\n\n"
            "Extraire entièrement l’archive dans un nouveau dossier, puis ouvrir GLPI-Pilot.exe.\n"
            "Conserver tout le dossier wad à côté de l’EXE. Aucun ancien pack n’est nécessaire.\n"
            "Le runtime et le pilote sont inclus. Edge ou Chrome doit être installé et autorisé.\n"
            "Les réglages et le profil SSO restent dans %LOCALAPPDATA%\\GLPI-Pilot.\n\n"
            "PROD contient l’application, le pilote, les guides, les licences et les diagnostics.\n"
            "DEV contient le même EXE et le même pilote, plus les sources dépliées dans\n"
            "wad\\sources et les journaux de validation dans wad\\validation. Les journaux\n"
            "historiques sont nommés baseline/previous ; ils ne valent pas une nouvelle exécution.\n\n"
            "En cas de non-démarrage, lancer wad\\support\\DIAGNOSTIC_DEMARRAGE.cmd\n"
            "sans mode administrateur. Lire le guide et le rapport de vérification dans wad\\docs.\n"
            "Le conditionnement vérifie les fichiers ; il ne démontre pas leur exécution Windows.\n"
            "wad\\SHA256SUMS.txt couvre chaque fichier du pack, sauf ce fichier d’empreintes lui-même.\n"
        ).encode("utf-8"))
        write_new(wad / "version.json", json_bytes({
            "application": "GLPI Pilot", "version": VERSION, "target": "win-x64", "edition": edition,
            "distribution": edition, "source_commit": None,
            "sdk": "10.0.402", "runtime": "10.0.13", "playwright": package["version"],
            "created_utc": captured_at, "self_contained": True, "sources_included": edition == "DEV",
            "validation_logs_included": edition == "DEV", "executable_sha256": exe_info["sha256"],
            "driver_snapshot_sha256": map_digest(driver),
        }))
        payload = mapping(tree_files(stage), stage)
        write_new(wad / "manifest.json", json_bytes({
            "schema_version": 1, "application": "GLPI Pilot", "version": VERSION, "edition": edition,
            "created_utc": captured_at, "files": payload, "driver_files": len(driver),
            "source_files": len(sources) if edition == "DEV" else 0,
            "source_snapshot_sha256": map_digest(sources) if edition == "DEV" else None,
            "coverage": "All payload files; manifest.json and SHA256SUMS.txt are excluded to avoid self-reference.",
        }))
        with_manifest = mapping(tree_files(stage), stage)
        write_new(wad / "SHA256SUMS.txt", "".join(
            f"{entry['sha256']}  {name}\n" for name, entry in sorted(with_manifest.items())
        ).encode("utf-8"))
        expected = mapping(tree_files(stage), stage)
        staged_zip = WORK / destinations[edition].name
        print(f"[{edition}] Writing archive and checking CRC/SHA-256...", flush=True)
        with zipfile.ZipFile(staged_zip, "x", zipfile.ZIP_DEFLATED, compresslevel=6, strict_timestamps=False) as archive:
            for name in sorted(expected):
                archive.write(stage / name, name)
        archive_manifests[edition] = validate_archive(staged_zip, expected, edition, sources, driver)
        release["files"].append({"edition": edition, "name": destinations[edition].name,
                                 "bytes": staged_zip.stat().st_size, "sha256": sha256(staged_zip),
                                 "members": len(expected), "crc_verified": True, "sha256_verified": True})

    same_runtime = lambda files: {name: value for name, value in files.items()
                                 if name == "GLPI-Pilot.exe" or name.startswith("wad/.playwright/")}
    require(same_runtime(archive_manifests["PROD"]) == same_runtime(archive_manifests["DEV"]),
            "PROD and DEV do not contain identical application and driver bytes")
    release["application_and_driver_identical_between_editions"] = True
    release["dev_sources_identical_to_captured_project"] = True
    # Publish only archives that have both passed validation. Exclusive creation
    # also closes the race between the initial output check and this final copy.
    for record in release["files"]:
        copy_verified(WORK / record["name"], destinations[record["edition"]], record)
    write_new(RELEASE_MANIFEST, json_bytes(release))
    print(json.dumps(release, ensure_ascii=False, indent=2), flush=True)


if __name__ == "__main__":
    try:
        main()
    except (PackagingError, OSError, zipfile.BadZipFile, ValueError, ET.ParseError) as error:
        print(f"Packaging stopped: {error}\nExisting outputs were not replaced. Inspect any new partial work under {WORK}.",
              file=sys.stderr, flush=True)
        sys.exit(1)
