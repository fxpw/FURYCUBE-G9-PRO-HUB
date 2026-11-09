"""Pure offline checks/sealing of G9's 16-byte image-container header.

CRC32 covers ALL framed bytes after the header, including per-block CRC16 and
FF tail: reflected0xEDB88320, initFFFFFFFF, no final XOR (inverted zlib.crc32).
Length@6 is the full container size in four-byte words, including the header.
The other ten header bytes are preserved verbatim; identity/security meaning
and installed-loader enforcement are NOT established by this file-format check.
No USB/serial, vendor executable, compiler, network or output-file operation.
"""
from pathlib import Path
import argparse
import hashlib
import json
import struct
import zlib


HEADER_BYTES = 16
MAX_WORDS = 0xFFFE  # 0xFFFF is the SDK erased/unset sentinel, not a valid length.
EDITABLE_HEADER_OFFSETS = frozenset((0, 1, 2, 3, 6, 7))


class FirmwareHeaderError(ValueError):
    pass


def _bytes(data):
    if not isinstance(data, (bytes, bytearray, memoryview)):
        raise TypeError('A bytes-like image container is required')
    data = bytes(data)
    if len(data) < HEADER_BYTES:
        raise FirmwareHeaderError('Image is shorter than its 16-byte header')
    return data


def body_crc32(data):
    """Return the no-final-XOR CRC of the complete framed container body."""
    data = _bytes(data)
    return zlib.crc32(data[HEADER_BYTES:]) ^ 0xFFFFFFFF


def inspect_header(data):
    data = _bytes(data)
    crc, version, words, uid, crc_status, section_status, rom_version = struct.unpack('<IHHIBBH', data[:HEADER_BYTES])
    calculated = body_crc32(data)
    errors = []
    if len(data) % 4:
        errors.append('Container length is not divisible by four')
    if not HEADER_BYTES//4 <= words <= MAX_WORDS:
        errors.append('Header word length is zero, too short or the erased sentinel')
    if words*4 != len(data):
        errors.append('Header length does not match the full container length')
    if crc != calculated:
        errors.append('Header CRC32 does not match the complete framed body')
    return dict(package_header_consistent=not errors, errors=errors,
                container_bytes=len(data), header_hex=data[:HEADER_BYTES].hex(),
                stored_crc32=crc, calculated_crc32=calculated,
                length_words=words, declared_container_bytes=words*4,
                image_version=version, uid=uid, crc_status=crc_status,
                section_status=section_status, rom_version=rom_version,
                installed_loader_enforces_header='unknown', physical_validated=False)


def validate_header(data):
    result = inspect_header(data)
    if result['errors']:
        raise FirmwareHeaderError('; '.join(result['errors']))
    return result


def seal_header(data):
    """Return a NEW container: FF pad to four bytes, then fix CRC and length.

    Does not mutate the caller's buffer or a file. This makes the observed
    package convention consistent; it is not a firmware/boot compatibility seal.
    """
    original = _bytes(data)
    padding = (-len(original)) % 4
    if (len(original)+padding)//4 > MAX_WORDS:
        raise FirmwareHeaderError('Container exceeds the non-sentinel 16-bit word length')
    result = bytearray(original+bytes([255])*padding)
    struct.pack_into('<H', result, 6, len(result)//4)
    struct.pack_into('<I', result, 0, body_crc32(result))
    assert all(result[i] == original[i] for i in range(HEADER_BYTES) if i not in EDITABLE_HEADER_OFFSETS)
    validate_header(result)
    return bytes(result)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('images', type=Path, nargs='+', help='Existing raw BIN containers; never executable inputs')
    args = parser.parse_args()
    bad = False
    for path in args.images:
        if not path.is_file():
            raise FirmwareHeaderError('An existing regular raw BIN file is required')
        data = path.read_bytes()
        result = inspect_header(data)
        result.update(path=str(path), sha256=hashlib.sha256(data).hexdigest(), offline_only=True)
        print(json.dumps(result))
        bad |= not result['package_header_consistent']
    if bad:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
