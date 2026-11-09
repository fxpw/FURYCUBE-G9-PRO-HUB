"""Offline erase-scope comparison for pinned G9 application candidates.

Does not create a flash payload or open USB/serial. An official app resource is
a reference, not an installed-flash preimage. Unknown sector bytes stay unknown.
The proposed small-sector scheme is NOT qualified on the installed loader.
"""
import argparse
import hashlib
import json
from pathlib import Path

from check_firmware_header import validate_header


FLASH_BASE = 0x2B00A  # OEM resource destination, not a normalized CPU address.
SECTOR_BYTES = 0x1000
OEM_ERASE_START, OEM_ERASE_END = 0x2B000, 0x51000
ORIGINAL_SHA256 = 'f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc'
CANDIDATES = {
    '01908b65a972ae04282392088e2581640365a0d0712c9f281d6f54691ea14865': 'NoWait',
    '08b277e7b2a6941ac1abe05a53bd36a9836310915461efca610fc98211d22ad0': 'OL2',
    '62ad25dadbc5b3d0fe6ca4383c81ce6259f488c70c20793b568be6e93a8561fb': 'OL3',
}


def _sha(data):
    return hashlib.sha256(data).hexdigest()


def _sector_changes(original, candidate):
    """Arithmetic only; inputs are reference bytes, never asserted flash data."""
    if type(original) is not bytes or type(candidate) is not bytes:
        raise ValueError('Byte strings are required')
    if not original or len(candidate) < len(original):
        raise ValueError('Empty inputs and shrinking candidates are unsupported')
    changed = [index for index, value in enumerate(candidate)
               if index >= len(original) or value != original[index]]
    # New FF bytes also count: the existing device contents are UNKNOWN there.
    sectors = sorted({(FLASH_BASE+index)//SECTOR_BYTES*SECTOR_BYTES for index in changed})
    if not sectors or any(start < OEM_ERASE_START or start+SECTOR_BYTES > OEM_ERASE_END
                          for start in sectors):
        raise ValueError('Empty update or sector outside the audited OEM interval')
    result = []
    reference_end = FLASH_BASE+len(original)
    for start in sectors:
        end = start+SECTOR_BYTES
        lo, hi = max(start, FLASH_BASE), min(end, reference_end)
        unknown = []
        if start < lo:
            unknown.append([start, lo])
        if hi < end:
            unknown.append([max(start, hi), end])
        # Intersecting sectors can be wholly outside the reference for a new
        # extension. Do not report overlapping or negative coverage spans.
        if hi <= lo:
            unknown = [[start, end]]
            known = 0
        else:
            known = hi-lo
        offsets = [offset for offset in changed if start <= FLASH_BASE+offset < end]
        result.append(dict(start=start, end_exclusive=end, bytes=SECTOR_BYTES,
                           proposed_changed_or_new_bytes=len(offsets),
                           official_reference_bytes=known,
                           absent_from_official_reference=unknown,
                           installed_preimage_acquired=False,
                           must_capture_complete_sector_before_erase=True))
    return changed, result


def plan_resources(original, candidate):
    original_sha, candidate_sha = _sha(original), _sha(candidate)
    if original_sha != ORIGINAL_SHA256 or candidate_sha not in CANDIDATES:
        raise ValueError('Only the exact official image and corrected pinned candidates are accepted')
    validate_header(original)
    validate_header(candidate)
    changed, sectors = _sector_changes(original, candidate)
    return dict(schema_version=1, offline_only=True, candidate=CANDIDATES[candidate_sha],
                original_sha256=original_sha, candidate_sha256=candidate_sha,
                resource_destination=FLASH_BASE, proposed_sector_bytes=SECTOR_BYTES,
                resource_changed_or_new_bytes=len(changed), sectors=sectors,
                proposed_erase_bytes=len(sectors)*SECTOR_BYTES,
                oem_erase_interval=[OEM_ERASE_START, OEM_ERASE_END],
                oem_erase_bytes=OEM_ERASE_END-OEM_ERASE_START,
                is_flash_payload=False, flashable=False, restore_proven=False,
                installed_loader_compatible=False, transport_implemented=False,
                required_evidence=[
                    'Main-application-independent entry for the installed G9 loader',
                    'Preservation or backup of every entry/verification side effect, including macro storage',
                    'Installed-image identity and qualified raw flash read/write representation',
                    'Two matching complete preimages of every proposed sector, retained locally',
                    'Qualified sector erase/program/readback and original-sector restoration',
                ],
                limitation='Arithmetic scope only. No captured flash data, erase/program payload, packet stream or device operation.')


def _read_resource(path):
    text = str(path).replace('/', '\\')
    if text.startswith(('\\\\.\\', '\\\\?\\')) or not path.is_file():
        raise ValueError('An existing regular local resource file is required')
    size = path.stat().st_size
    if not 16 <= size <= 0xFFFE*4:
        raise ValueError('Invalid resource size')
    return path.read_bytes()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('original', type=Path)
    parser.add_argument('candidate', type=Path)
    args = parser.parse_args()
    print(json.dumps(plan_resources(_read_resource(args.original), _read_resource(args.candidate)), indent=2))


if __name__ == '__main__':
    main()
