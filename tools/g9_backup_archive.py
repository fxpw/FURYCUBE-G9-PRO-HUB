"""Offline-only Beken HCI read-page codec and captured-data archive verifier.

No serial/USB modules, device opening, subprocesses, loader entry, writes to a
device, automatic probing, protection changes or assumed G9 capacity exist here.
The packet layout is reconstructed from pinned BKFIL instructions in offline
emulation, not physically validated on a G9. A passing archive does NOT clear
the independent-entry/full-restoration gate.
"""
from __future__ import annotations

import argparse
import base64
import binascii
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import struct
import sys
import uuid

PAGE_BYTES = 4096
REPLY_BYTES = 4111
REQUEST_PREFIX = bytes.fromhex("01e0fcfff4050009")
REPLY_PREFIX = bytes.fromhex("040eff01e0fcf4061009")
SOURCE_BKFIL_SHA256 = "79d1d6e30eda7527099c7bdd2b2241488973b68623b4ecfbe87793a176c0d80b"
SCHEMA = "furycube-g9-captured-hci-pages-v1"
# Resource limits for this implementation, NOT an inferred device capacity.
MAX_DECLARED_CAPACITY = 16 * 1024 * 1024
MAX_CAPTURE_BYTES = 32 * 1024 * 1024


class ArchiveError(ValueError):
    pass


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _integer(value, name: str) -> int:
    if type(value) is int:
        return value
    if isinstance(value, str) and re.fullmatch(r"0x[0-9a-fA-F]{1,8}", value):
        return int(value, 16)
    raise ArchiveError(name + " must be an integer or explicit 0x hexadecimal string")


def _address(value) -> int:
    address = _integer(value, "page address")
    if not 0 <= address <= 0xFFFFF000 or address % PAGE_BYTES:
        raise ArchiveError("Page address must be aligned to4096 and fit an entire32-bit page")
    return address


def encode_page_request(address: int) -> bytes:
    """Encode only the reconstructed UART HCI0x09 read-page request.

    Native BKFIL silently aligns downward; this implementation rejects an
    unaligned address instead. These are NOT normal-mouse HID macro reports.
    Returning bytes does not enable any transport or establish compatibility.
    """
    return REQUEST_PREFIX + struct.pack("<I", _address(address))


@dataclass(frozen=True)
class CapturedPage:
    address: int
    data: bytes
    wire_sha256: str
    data_sha256: str


def decode_page_reply(reply: bytes, expected_address: int) -> CapturedPage:
    address = _address(expected_address)
    if not isinstance(reply, bytes) or len(reply) != REPLY_BYTES:
        raise ArchiveError("Reply must contain exactly4111 bytes")
    if reply[:10] != REPLY_PREFIX:
        raise ArchiveError("Wrong read-page event header/extended length/opcode")
    # Native BKFIL does NOT check byte10. Zero is a conservative fail-closed
    # policy here; its exact hardware status semantics remain unqualified.
    if reply[10] != 0:
        raise ArchiveError("Nonzero unqualified status byte10; page rejected")
    if struct.unpack_from("<I", reply, 11)[0] != address:
        raise ArchiveError("Stale or mismatched read-page address")
    payload = reply[15:]
    return CapturedPage(address, payload, sha256(reply), sha256(payload))


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ArchiveError("Duplicate JSON member: " + key)
        result[key] = value
    return result


def read_capture_file(path: Path) -> tuple[dict, bytes]:
    path = Path(path)
    rendered = str(path).replace("/", "\\")
    leaf = path.name.split(".", 1)[0].rstrip(" :").upper()
    if rendered.startswith("\\\\") or re.fullmatch(r"COM[0-9]+|LPT[0-9]+|CON|PRN|AUX|NUL|CONIN\$|CONOUT\$", leaf):
        raise ArchiveError("Only ordinary capture files are accepted; device paths are forbidden")
    if not stat.S_ISREG(path.stat().st_mode):
        raise ArchiveError("Capture input must be an existing regular file")
    # Bound the read itself; a concurrent size change cannot bypass stat limits.
    with path.open("rb") as stream:
        if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
            raise ArchiveError("Capture input must remain a regular file")
        data = stream.read(MAX_CAPTURE_BYTES + 1)
    if not data or len(data) > MAX_CAPTURE_BYTES:
        raise ArchiveError("Capture file is empty or exceeds the implementation size limit")
    try:
        capture = json.loads(data.decode("utf-8-sig"), object_pairs_hook=_unique_object)
    except (UnicodeError, json.JSONDecodeError, RecursionError) as error:
        raise ArchiveError("Invalid capture JSON") from error
    if not isinstance(capture, dict):
        raise ArchiveError("Capture root must be an object")
    return capture, data


def _text(document: dict, name: str, maximum=2000) -> str:
    value = document.get(name)
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise ArchiveError("Missing or invalid capture field: " + name)
    return value


@dataclass(frozen=True)
class VerifiedCapture:
    capture_id: str
    sampled_at: str
    source_kind: str
    declared_capacity: int
    device_identity: str
    capacity_evidence: str
    format_evidence: str
    image: bytes
    pages: tuple[CapturedPage, ...]


def verify_capture(capture: dict, *, allow_fixture=False) -> VerifiedCapture:
    if not isinstance(capture, dict) or capture.get("schema") != SCHEMA:
        raise ArchiveError("Unknown captured-page schema")
    kind = capture.get("sourceKind")
    if kind not in ("wire-capture", "offline-fixture"):
        raise ArchiveError("sourceKind must identify a wire capture or offline fixture")
    if kind == "offline-fixture" and not allow_fixture:
        raise ArchiveError("Synthetic fixture requires explicit --allow-fixture")
    if capture.get("transport") != "UART-HCI09-unqualified":
        raise ArchiveError("Only the reconstructed UART HCI09 representation is supported")
    capture_id = _text(capture, "captureId", 128)
    if not re.fullmatch(r"[a-zA-Z0-9_.-]{8,128}", capture_id):
        raise ArchiveError("Invalid captureId")
    sampled = _text(capture, "capturedAtUtc", 64)
    try:
        instant = datetime.fromisoformat(sampled.replace("Z", "+00:00"))
    except ValueError as error:
        raise ArchiveError("Invalid capture time") from error
    if instant.utcoffset() is None or instant.utcoffset().total_seconds() != 0:
        raise ArchiveError("Capture time must include UTC offset")
    capacity = _integer(capture.get("declaredCapacityBytes"), "declaredCapacityBytes")
    if not PAGE_BYTES <= capacity <= MAX_DECLARED_CAPACITY or capacity % PAGE_BYTES:
        raise ArchiveError("Declared capacity must be a positive whole-page count within implementation limits; no capacity is assumed")
    identity = _text(capture, "deviceIdentityEvidence")
    capacity_evidence = _text(capture, "capacityEvidence")
    format_evidence = _text(capture, "formatEvidence")
    pages = capture.get("pages")
    if not isinstance(pages, list) or len(pages) != capacity // PAGE_BYTES:
        raise ArchiveError("Page count does not cover the complete declared capacity")
    decoded = {}
    for entry in pages:
        if not isinstance(entry, dict):
            raise ArchiveError("Page entry must be an object")
        address = _address(entry.get("address"))
        if address + PAGE_BYTES > capacity:
            raise ArchiveError("Page lies outside the declared capacity")
        if address in decoded:
            raise ArchiveError("Duplicate/overlapping page address")
        encoded = entry.get("replyBase64")
        if not isinstance(encoded, str) or len(encoded) != ((REPLY_BYTES + 2) // 3) * 4:
            raise ArchiveError("Reply Base64 has the wrong exact length")
        try:
            wire = base64.b64decode(encoded, validate=True)
        except (binascii.Error, ValueError) as error:
            raise ArchiveError("Invalid reply Base64") from error
        request_hex = entry.get("requestHex")
        if request_hex is not None:
            if not isinstance(request_hex, str) or request_hex.lower() != encode_page_request(address).hex():
                raise ArchiveError("Captured request does not match its declared page address")
        decoded[address] = decode_page_reply(wire, address)
    required = range(0, capacity, PAGE_BYTES)
    if set(decoded) != set(required):
        raise ArchiveError("Page holes or unexpected regions remain")
    ordered = tuple(decoded[address] for address in required)
    image = b"".join(page.data for page in ordered)
    if len(image) != capacity:
        raise ArchiveError("Assembled image is incomplete")
    return VerifiedCapture(capture_id, instant.isoformat(), kind, capacity, identity, capacity_evidence, format_evidence, image, ordered)


def compare_captures(first: VerifiedCapture, second: VerifiedCapture) -> None:
    if first.capture_id == second.capture_id or first.sampled_at == second.sampled_at:
        raise ArchiveError("Two distinct declared acquisitions/UTC times are required")
    for name in ("source_kind", "declared_capacity", "device_identity", "capacity_evidence", "format_evidence"):
        if getattr(first, name) != getattr(second, name):
            raise ArchiveError("Capture declarations disagree: " + name)
    if first.image != second.image:
        raise ArchiveError("Complete captured images differ; no archive is accepted")
    # Distinct metadata cannot prove that someone really acquired two hardware
    # reads. The output manifest keeps independentAcquisitionVerified=false.


def _write_checked(path: Path, data: bytes) -> str:
    with path.open("xb") as stream:
        count = stream.write(data)
        if count != len(data):
            raise ArchiveError("Short archive-file write")
        stream.flush()
        os.fsync(stream.fileno())
    with path.open("rb") as stream:
        actual = stream.read(len(data) + 1)
    if actual != data:
        raise ArchiveError("Saved archive file failed exact readback")
    return sha256(actual)


def build_archive(capture_a: Path, capture_b: Path, output_parent: Path, *, allow_fixture=False) -> Path:
    doc_a, bytes_a = read_capture_file(capture_a)
    doc_b, bytes_b = read_capture_file(capture_b)
    if bytes_a == bytes_b:
        raise ArchiveError("The exact same capture file is not a second acquisition")
    first = verify_capture(doc_a, allow_fixture=allow_fixture)
    second = verify_capture(doc_b, allow_fixture=allow_fixture)
    compare_captures(first, second)
    parent = Path(output_parent)
    parent.mkdir(parents=True, exist_ok=True)
    output = parent / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ") + "-offline-" + uuid.uuid4().hex[:12])
    output.mkdir(exist_ok=False)
    image_hash = _write_checked(output / "captured-flash.bin", first.image)
    a_hash = _write_checked(output / "capture-a.json", bytes_a)
    b_hash = _write_checked(output / "capture-b.json", bytes_b)
    page_index = [{"address": page.address, "length": PAGE_BYTES, "payloadSha256": page.data_sha256,
                   "replyASha256": page.wire_sha256, "replyBSha256": second.pages[i].wire_sha256}
                  for i, page in enumerate(first.pages)]
    index_hash = _write_checked(output / "pages.json", (json.dumps(page_index, indent=2) + "\n").encode())
    manifest = {
        "schema": "furycube-g9-offline-archive-v1", "archiveFilesVerified": True,
        "coverage": "COMPLETE_FOR_DECLARED_CAPACITY", "declaredCapacityBytes": first.declared_capacity,
        "twoCapturedPassesEqual": True, "sourceKind": first.source_kind,
        "physicalBackupVerified": False, "independentAcquisitionVerified": False,
        "appIndependentEntryVerified": False, "restorationVerified": False,
        "recoveryGate": "BLOCKED", "transport": "UART-HCI09-unqualified",
        "representation": "Returned page bytes; raw/CPU-view/encryption/CRC mapping not established by this validator",
        "statusPolicy": "Byte10 must be zero; hardware semantics unqualified",
        "deviceIdentityEvidence": first.device_identity, "capacityEvidence": first.capacity_evidence,
        "formatEvidence": first.format_evidence, "captureIds": [first.capture_id, second.capture_id],
        "captureTimesUtc": [first.sampled_at, second.sampled_at], "pageCount": len(first.pages),
        "sourceBkfilSha256": SOURCE_BKFIL_SHA256, "codecSha256": sha256(Path(__file__).read_bytes()),
        "files": {"captured-flash.bin": {"length": len(first.image), "sha256": image_hash},
                  "capture-a.json": {"length": len(bytes_a), "sha256": a_hash},
                  "capture-b.json": {"length": len(bytes_b), "sha256": b_hash},
                  "pages.json": {"sha256": index_hash}},
        "limits": ["Declared capacity/identity are input claims, not verified hardware properties.",
                   "Metadata cannot prove independent acquisition.",
                   "No serial connection, loader entry or restoration is performed.",
                   "Keep actual device images and pairing/calibration data outside the public repository."]}
    # Manifest is written LAST. A partial output directory without it is not a
    # successful archive. Existing archives are never replaced or deleted.
    _write_checked(output / "manifest.json", (json.dumps(manifest, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))
    return output


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    encode = commands.add_parser("encode-page", help="Print a pure offline UART read request; no transport")
    encode.add_argument("--address", required=True)
    archive = commands.add_parser("archive", help="Verify two already captured complete page sets and save a local archive")
    archive.add_argument("--capture-a", type=Path, required=True)
    archive.add_argument("--capture-b", type=Path, required=True)
    archive.add_argument("--output-parent", type=Path, required=True)
    archive.add_argument("--allow-fixture", action="store_true", help="Explicitly permit labelled synthetic fixtures; manifest still blocks recovery")
    options = parser.parse_args(argv)
    try:
        if options.command == "encode-page":
            print(encode_page_request(options.address).hex())
        else:
            result = build_archive(options.capture_a, options.capture_b, options.output_parent, allow_fixture=options.allow_fixture)
            print("OFFLINE_ARCHIVE_VERIFIED " + str(result))
            print("RECOVERY_GATE_BLOCKED: physical identity, independent entry and restoration are not validated")
        return 0
    except (ArchiveError, OSError, UnicodeError, RecursionError) as error:
        print("ARCHIVE_REJECTED: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
