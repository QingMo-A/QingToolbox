#!/usr/bin/env python3
"""Package every Android module source folder into a distributable .qmod archive.

A .qmod is a plain zip holding `manifest.json` plus the module's `web/` payload. The
manifest is signed with a payload digest over every other entry, so the shell can prove
that the contents it is about to install are the contents the manifest describes.

The digest algorithm here must stay byte-for-byte identical to
`MobileModuleManifest.payloadDigest` in the shell:

    for each entry except manifest.json, in code-point order of the entry name:
        feed  name + "\\n" + sha256hex(content) + "\\n"
    digest = sha256hex(of everything fed so far)

Archives are written deterministically (fixed timestamps, sorted entry names) so the same
sources always produce the same bytes and the catalog hashes stay stable.

Usage:  python tools/pack_android_modules.py [--check]
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import sys
import zipfile
from pathlib import Path

MODULE_ROOT = Path(__file__).resolve().parent.parent
SOURCE_ROOT = MODULE_ROOT / "src"
MANIFEST_ENTRY = "manifest.json"
ALLOWED_PREFIXES = ("web/",)
FIXED_TIMESTAMP = (1980, 1, 1, 0, 0, 0)
MAX_ARCHIVE_BYTES = 8 * 1024 * 1024
MAX_ENTRY_BYTES = 4 * 1024 * 1024
MAX_TOTAL_BYTES = 16 * 1024 * 1024
MAX_ENTRIES = 512
SCHEMA_VERSION = 1
API_VERSION = 1
RUNTIME_WEB = "web"


class PackageError(Exception):
    """A module source tree or produced archive that the shell would refuse."""


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def payload_digest(entries: dict[str, bytes]) -> str:
    digest = hashlib.sha256()
    for name in sorted(entries):
        if name == MANIFEST_ENTRY:
            continue
        digest.update(name.encode("utf-8"))
        digest.update(b"\n")
        digest.update(sha256_hex(entries[name]).encode("ascii"))
        digest.update(b"\n")
    return digest.hexdigest()


def is_safe_relative_path(path: str) -> bool:
    if not path or path.startswith("/") or "\\" in path or "\x00" in path:
        return False
    segments = path.split("/")
    return all(segment not in ("", ".", "..") for segment in segments)


def collect_payload(module_dir: Path) -> dict[str, bytes]:
    entries: dict[str, bytes] = {}
    web_root = module_dir / "web"
    if not web_root.is_dir():
        raise PackageError(f"{module_dir.name}: no web/ directory")
    for path in sorted(web_root.rglob("*")):
        if not path.is_file():
            continue
        name = path.relative_to(module_dir).as_posix()
        if not is_safe_relative_path(name):
            raise PackageError(f"{module_dir.name}: unsafe path {name}")
        entries[name] = path.read_bytes()
    if not entries:
        raise PackageError(f"{module_dir.name}: web/ is empty")
    return entries


def validate_manifest(module_dir: Path, manifest: dict, entries: dict[str, bytes]) -> None:
    name = module_dir.name
    if manifest.get("schemaVersion") != SCHEMA_VERSION:
        raise PackageError(f"{name}: schemaVersion must be {SCHEMA_VERSION}")
    if manifest.get("apiVersion") != API_VERSION:
        raise PackageError(f"{name}: apiVersion must be {API_VERSION}")
    if manifest.get("id") != name:
        raise PackageError(f"{name}: id must match the folder name")
    if manifest.get("runtimeType") != RUNTIME_WEB:
        raise PackageError(f"{name}: runtimeType must be {RUNTIME_WEB}")
    entry = manifest.get("entry")
    if not isinstance(entry, str) or not entry.endswith(".html"):
        raise PackageError(f"{name}: entry must be an .html path")
    if entry not in entries:
        raise PackageError(f"{name}: entry {entry} is not part of web/")
    capabilities = manifest.get("capabilities", [])
    if not isinstance(capabilities, list) or any(not isinstance(item, str) for item in capabilities):
        raise PackageError(f"{name}: capabilities must be a list of strings")


def build_archive(module_dir: Path) -> tuple[bytes, dict, dict[str, bytes]]:
    entries = collect_payload(module_dir)

    manifest_path = module_dir / MANIFEST_ENTRY
    if not manifest_path.is_file():
        raise PackageError(f"{module_dir.name}: no {MANIFEST_ENTRY}")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    # `_`-prefixed keys are authoring notes and never travel inside a package.
    manifest = {key: value for key, value in manifest.items() if not key.startswith("_")}
    validate_manifest(module_dir, manifest, entries)

    manifest["payloadHash"] = payload_digest(entries)
    manifest_bytes = json.dumps(manifest, ensure_ascii=False, indent=2).encode("utf-8") + b"\n"

    total = sum(len(content) for content in entries.values()) + len(manifest_bytes)
    if len(entries) + 1 > MAX_ENTRIES:
        raise PackageError(f"{module_dir.name}: too many entries")
    if total > MAX_TOTAL_BYTES:
        raise PackageError(f"{module_dir.name}: payload larger than {MAX_TOTAL_BYTES} bytes")
    for name, content in entries.items():
        if len(content) > MAX_ENTRY_BYTES:
            raise PackageError(f"{module_dir.name}: {name} is larger than {MAX_ENTRY_BYTES} bytes")

    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name in [MANIFEST_ENTRY, *sorted(entries)]:
            content = manifest_bytes if name == MANIFEST_ENTRY else entries[name]
            info = zipfile.ZipInfo(name, date_time=FIXED_TIMESTAMP)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            archive.writestr(info, content)
    data = buffer.getvalue()

    if len(data) > MAX_ARCHIVE_BYTES:
        raise PackageError(f"{module_dir.name}: archive larger than {MAX_ARCHIVE_BYTES} bytes")

    return data, manifest, entries


def verify_archive(data: bytes) -> dict:
    """Mirrors the rules the Android shell enforces before it installs anything."""
    entries: dict[str, bytes] = {}
    total = 0
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        if len(archive.infolist()) > MAX_ENTRIES:
            raise PackageError("archive has too many entries")
        for info in archive.infolist():
            if info.is_dir():
                continue
            name = info.filename
            if not is_safe_relative_path(name):
                raise PackageError(f"archive holds an unsafe path: {name}")
            if name != MANIFEST_ENTRY and not name.startswith(ALLOWED_PREFIXES):
                raise PackageError(f"archive holds a file modules may not ship: {name}")
            if name in entries:
                raise PackageError(f"archive holds {name} twice")
            content = archive.read(info)
            if len(content) > MAX_ENTRY_BYTES:
                raise PackageError(f"archive entry {name} is too large")
            total += len(content)
            entries[name] = content

    if total > MAX_TOTAL_BYTES:
        raise PackageError("archive payload is too large")
    if MANIFEST_ENTRY not in entries:
        raise PackageError("archive has no manifest")

    manifest = json.loads(entries[MANIFEST_ENTRY].decode("utf-8"))
    entry = manifest.get("entry")
    if entry not in entries:
        raise PackageError("archive is missing the page named by its manifest")
    if payload_digest(entries) != manifest.get("payloadHash"):
        raise PackageError("archive payload does not match the digest in its manifest")
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check",
        action="store_true",
        help="verify the existing archives instead of rewriting them",
    )
    arguments = parser.parse_args()

    if not SOURCE_ROOT.is_dir():
        print(f"no module sources at {SOURCE_ROOT}", file=sys.stderr)
        return 1

    module_dirs = sorted(path for path in SOURCE_ROOT.iterdir() if path.is_dir())
    if not module_dirs:
        print(f"no module folders in {SOURCE_ROOT}", file=sys.stderr)
        return 1

    catalog = []
    failures = []

    for module_dir in module_dirs:
        try:
            data, manifest, entries = build_archive(module_dir)
        except PackageError as error:
            failures.append(str(error))
            continue

        file_name = f"{manifest['id']}-{manifest['version']}.qmod"
        target = MODULE_ROOT / file_name
        digest = sha256_hex(data)

        if arguments.check:
            if not target.is_file() or target.read_bytes() != data:
                failures.append(f"{file_name} is out of date")
                continue
        else:
            target.write_bytes(data)

        verify_archive(data)
        payload_bytes = sum(len(content) for content in entries.values())
        catalog.append(
            {
                "id": manifest["id"],
                "version": manifest["version"],
                "apiVersion": manifest["apiVersion"],
                "runtimeType": manifest["runtimeType"],
                "displayName": manifest["displayName"],
                "description": manifest.get("description", {}),
                "capabilities": manifest.get("capabilities", []),
                "file": file_name,
                "sizeBytes": len(data),
                "payloadBytes": payload_bytes,
                "sha256": digest,
                "payloadHash": manifest["payloadHash"],
            }
        )
        print(f"{file_name}  {len(data):>7} bytes  sha256 {digest[:16]}…")

    if failures:
        for failure in failures:
            print(f"FAILED: {failure}", file=sys.stderr)
        return 1

    catalog_path = MODULE_ROOT / "index.json"
    catalog_document = {
        "schemaVersion": SCHEMA_VERSION,
        "apiVersion": API_VERSION,
        "generatedBy": "tools/pack_android_modules.py",
        "modules": catalog,
    }
    serialized = json.dumps(catalog_document, ensure_ascii=False, indent=2) + "\n"
    if arguments.check:
        if not catalog_path.is_file() or catalog_path.read_text(encoding="utf-8") != serialized:
            print("FAILED: index.json is out of date", file=sys.stderr)
            return 1
    else:
        catalog_path.write_text(serialized, encoding="utf-8")

    print(f"{len(catalog)} module(s) packaged")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
