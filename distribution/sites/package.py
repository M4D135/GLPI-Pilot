#!/usr/bin/env python3
"""Validate and package the optional site reference, independently of app binaries."""

import argparse
import csv
import hashlib
import io
import math
from pathlib import Path
import re
import sys
import unicodedata
import zipfile


SOURCE_FILES = (
    "wad/sites.csv",
    "wad/support/INSTALL-SITES.cmd",
    "wad/support/LIRE-SITES.txt",
    "wad/support/SOURCES-SITES.md",
)
REQUIRED_HEADERS = ("Site", "Termes de détection")
GEOGRAPHIC_HEADERS = (
    "Ville", "Code postal", "Adresse", "Latitude ville", "Longitude ville", "Latitude site", "Longitude site",
)


def validate_reference(raw: bytes) -> int:
    if not 0 < len(raw) <= 4 * 1024 * 1024:
        raise ValueError("sites.csv must be nonempty and at most 4 MiB.")
    reader = csv.reader(io.StringIO(raw.decode("utf-8-sig"), newline=""), delimiter=";", strict=True)
    rows = [row for row in reader if row]
    headers = [cell.strip() for cell in rows[0]] if rows else []
    if (not set(REQUIRED_HEADERS).issubset(headers) or len(set(headers)) != len(headers)
            or any(header not in REQUIRED_HEADERS + GEOGRAPHIC_HEADERS for header in headers)):
        raise ValueError("sites.csv requires Site and Termes de détection, with only the documented optional geographic columns.")
    if not 1 <= len(rows) - 1 <= 2000:
        raise ValueError("sites.csv must contain between 1 and 2000 sites.")

    def key(value: str) -> str:
        text = "".join(char for char in unicodedata.normalize("NFD", value) if unicodedata.category(char) != "Mn")
        return " ".join("".join(char.upper() if char.isalnum() else " " for char in text).split())

    def valid_text(value: str) -> bool:
        return len(value) <= 120 and not any(unicodedata.category(char) in ("Cc", "Cs") for char in value)

    def coordinates(values: dict[str, str], kind: str, number: int) -> tuple[float, float] | None:
        pair = [values.get(f"{axis} {kind}", "") for axis in ("Latitude", "Longitude")]
        if not any(pair):
            return None
        if not all(pair):
            raise ValueError(f"CSV record {number} requires both coordinates for {kind}.")
        parsed = []
        for text in pair:
            text = text.removeprefix("'")
            if not re.fullmatch(r"[+-]?(?:\d+(?:[.,]\d*)?|[.,]\d+)", text, re.ASCII):
                raise ValueError(f"CSV record {number} has an invalid coordinate.")
            parsed.append(float(text.replace(",", ".")))
        if not all(math.isfinite(value) for value in parsed) or not -90 <= parsed[0] <= 90 or not -180 <= parsed[1] <= 180:
            raise ValueError(f"CSV record {number} has out-of-range coordinates.")
        return parsed[0], parsed[1]

    names = set()
    cities = {}
    for number, row in enumerate(rows[1:], start=2):
        if len(row) != len(headers):
            raise ValueError(f"CSV record {number} must have the declared number of columns.")
        values = dict(zip(headers, (cell.strip() for cell in row)))
        name = values["Site"]
        if not valid_text(name) or not key(name) or key(name) in names:
            raise ValueError(f"CSV record {number} has an empty, duplicate or invalid site name.")
        names.add(key(name))
        terms = [term.strip() for term in re.split(r"[;|\r\n]+", values["Termes de détection"]) if term.strip()]
        if any(not valid_text(term) or not key(term) for term in terms) or len({key(term) for term in terms} - {key(name)}) > 12:
            raise ValueError(f"CSV record {number} has invalid or too many detection terms.")
        city, postal, address = (values.get(header, "") for header in ("Ville", "Code postal", "Adresse"))
        if any(not valid_text(value) for value in (city, postal, address)) or city and not key(city):
            raise ValueError(f"CSV record {number} has invalid geographic text.")
        city_point = coordinates(values, "ville", number)
        coordinates(values, "site", number)
        if not city and (postal or city_point):
            raise ValueError(f"CSV record {number} requires a city for its postal code or coordinates.")
        if city:
            previous_postal, previous_point = cities.get(key(city), ("", None))
            if postal and previous_postal and postal != previous_postal or city_point and previous_point and city_point != previous_point:
                raise ValueError(f"CSV record {number} conflicts with another record for the same city.")
            cities[key(city)] = postal or previous_postal, city_point or previous_point
    if len(cities) > 1000:
        raise ValueError("sites.csv must not exceed 1000 cities.")
    return len(rows) - 1


def package(output_dir: Path) -> None:
    source_dir = Path(__file__).resolve().parent
    payload = {}
    for name in SOURCE_FILES:
        source = source_dir / name
        if not source.is_file() or source.is_symlink():
            raise ValueError(f"Required regular source file is missing: {name}")
        payload[name] = source.read_bytes()

    raw = payload["wad/sites.csv"]
    site_count = validate_reference(raw)

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
            raise ValueError("The ZIP does not contain exactly the expected, valid files.")
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
    print(f"Validated {site_count} sites; prepared {', '.join(names)}.")
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
