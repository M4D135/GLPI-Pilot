#!/usr/bin/env python3
"""Validate and package the optional site reference, independently of app binaries."""

import argparse
import csv
import hashlib
import io
from pathlib import Path
import sys
import zipfile


SOURCE_FILES = (
    "wad/sites.csv",
    "wad/support/INSTALL-SITES.cmd",
    "wad/support/LIRE-SITES.txt",
)


def package(output_dir: Path) -> None:
    source_dir = Path(__file__).resolve().parent
    payload = {}
    for name in SOURCE_FILES:
        source = source_dir / name
        if not source.is_file() or source.is_symlink():
            raise ValueError(f"Required regular source file is missing: {name}")
        payload[name] = source.read_bytes()

    raw = payload["wad/sites.csv"]
    if not 0 < len(raw) <= 4 * 1024 * 1024:
        raise ValueError("sites.csv must be nonempty and at most 4 MiB.")
    reader = csv.reader(io.StringIO(raw.decode("utf-8-sig"), newline=""), delimiter=";", strict=True)
    rows = [row for row in reader if row]
    if not rows or [cell.strip() for cell in rows[0]] != ["Site", "Termes de détection"]:
        raise ValueError("sites.csv requires exactly the headers Site;Termes de détection.")
    if not 1 <= len(rows) - 1 <= 2000:
        raise ValueError("sites.csv must contain between 1 and 2000 sites.")
    for number, row in enumerate(rows[1:], start=2):
        if len(row) != 2 or not row[0].strip():
            raise ValueError(f"CSV record {number} requires two columns and a nonempty site name.")

    output_dir = output_dir.resolve()
    names = ("sites.csv", "GLPI-Pilot-Sites.zip", "SITES-SHA256SUMS.txt")
    if any((output_dir / name) == (source_dir / source).resolve() for name in names for source in SOURCE_FILES):
        raise ValueError("The output directory must not overwrite a source file.")
    output_dir.mkdir(parents=True, exist_ok=True)
    (output_dir / "sites.csv").write_bytes(raw)
    archive_path = output_dir / "GLPI-Pilot-Sites.zip"
    with zipfile.ZipFile(archive_path, "w") as archive:
        for name in SOURCE_FILES:
            entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.create_system = 3
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, payload[name], compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)

    with zipfile.ZipFile(archive_path) as archive:
        if tuple(archive.namelist()) != SOURCE_FILES or archive.testzip() is not None:
            raise ValueError("The ZIP does not contain exactly the three expected, valid files.")
        if any(archive.read(name) != payload[name] for name in SOURCE_FILES):
            raise ValueError("The ZIP payload differs from the source bytes.")
    if (output_dir / "sites.csv").read_bytes() != raw:
        raise ValueError("The standalone CSV differs from its source bytes.")
    if any((source_dir / name).read_bytes() != payload[name] for name in SOURCE_FILES):
        raise ValueError("A source file changed during packaging.")
    checksums = "".join(
        f"{hashlib.sha256((output_dir / name).read_bytes()).hexdigest()}  {name}\n"
        for name in names[:2]
    )
    (output_dir / names[2]).write_text(checksums, encoding="utf-8", newline="\n")
    print(f"Validated {len(rows) - 1} sites; prepared {', '.join(names)}.")
    print(checksums, end="")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", required=True, type=Path)
    arguments = parser.parse_args()
    try:
        package(arguments.output_dir)
    except (OSError, ValueError, UnicodeError, csv.Error, zipfile.BadZipFile) as error:
        print(f"Site package failed: {error}", file=sys.stderr)
        sys.exit(1)
