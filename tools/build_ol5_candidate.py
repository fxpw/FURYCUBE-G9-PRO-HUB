"""Prepare the pinned OL5 raw BIN offline; never open a device or run an updater.

OL5 needs a separately reviewed 34-page transfer. OL4's 33-page limits stay
unchanged. A passing artifact is NOT authorization/qualification to install it:
the OEM host also erases one additional, currently unbacked sector.
"""
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import uuid

from build_oled_candidate import cms, decode_resource
from check_firmware_header import seal_header, validate_header, EDITABLE_HEADER_OFFSETS
from emulate_ol5_radio import build, run_regressions as radio_checks, BASE, APPEND, SOURCE_HASH
from emulate_ol5_retention import run_regressions as retention_checks

ROOT = Path(__file__).resolve().parents[1]
if not __debug__:
    raise RuntimeError('Firmware verification requires assertions; do not use -O')
PAYLOAD_HASH = 'c36395c7e5fffd8f088816b73225df0906229a8381a3fa71eea68a18bdee4d68'
RESOURCE_HASH = 'f5488618c511e1fab2cd1253fc69546b8d8b6aeaff76d58982e3cf8497d2f97b'
PAYLOAD_BYTES, ORIGINAL_BLOCKS, APPENDED_BLOCKS = 1924, 3939, 61
RESOURCE_BYTES, TRANSFER_BYTES = 136016, 0x22000


def sha(data):
    return hashlib.sha256(data).hexdigest()


def build_resource(original, code, layout):
    assert len(original) == 134140 and sha(original) == SOURCE_HASH
    validate_header(original)
    assert len(code) == PAYLOAD_BYTES and sha(code) == PAYLOAD_HASH
    normalized, blocks, tail = decode_resource(original)
    assert len(blocks) == ORIGINAL_BLOCKS and tail == b'\xff' * 198
    assert BASE + len(normalized) == APPEND
    patched, patches = bytearray(normalized), []
    records = layout['hooks'] + layout['data_patches'] + [layout['descriptor_patch']]
    assert len(layout['hooks']) == 25 and len(layout['data_patches']) == 2
    changed_bytes = set()
    for record in records:
        offset = record['normalized_offset']
        before, after = bytes.fromhex(record['old_hex']), bytes.fromhex(record['new_hex'])
        assert offset == record['address'] - BASE and offset >= 16
        assert len(before) == len(after) and offset + len(before) <= len(normalized)
        assert bytes(patched[offset:offset + len(before)]) == before, record
        # Disjoint patch sites; no preimage may silently depend on another patch.
        span = set(range(offset, offset + len(before)))
        assert not span.intersection(changed_bytes)
        changed_bytes.update(span)
        patched[offset:offset + len(before)] = after
        patches.append(record)

    result, changed_blocks = bytearray(original[:16]), []
    for index, block in enumerate(blocks):
        payload = bytes(patched[16 + index * 32:16 + (index + 1) * 32])
        if payload == block[:32]:
            result.extend(block)
        else:
            result.extend(payload + cms(payload).to_bytes(2, 'big'))
            changed_blocks.append(index)
    padded = code + b'\xff' * ((-len(code)) % 32)
    assert len(padded) == APPENDED_BLOCKS * 32
    for offset in range(0, len(padded), 32):
        payload = padded[offset:offset + 32]
        result.extend(payload + cms(payload).to_bytes(2, 'big'))
    candidate = seal_header(result)
    assert sha(candidate) == RESOURCE_HASH, 'Reviewed OL5 resource differs'
    header = validate_header(candidate)
    assert len(candidate) == RESOURCE_BYTES
    assert ((len(candidate) + 4095) & ~4095) == TRANSFER_BYTES
    assert ((len(original) + 4095) & ~4095) == 0x21000
    assert {i for i in range(16) if original[i] != candidate[i]} <= EDITABLE_HEADER_OFFSETS
    decoded, framed, padding = decode_resource(candidate)
    expected = bytearray(patched) + padded
    expected[:16] = candidate[:16]
    assert decoded == bytes(expected) and not padding
    assert len(framed) == ORIGINAL_BLOCKS + APPENDED_BLOCKS
    actual = {i for i, (a, b) in enumerate(zip(normalized, decoded)) if i >= 16 and a != b}
    wanted = {p['normalized_offset'] + i for p in patches
              for i, (a, b) in enumerate(zip(bytes.fromhex(p['old_hex']), bytes.fromhex(p['new_hex']))) if a != b}
    assert actual == wanted
    assert changed_blocks == sorted({(i - 16) // 32 for i in wanted})
    assert len(changed_blocks) == 28
    for index, block in enumerate(blocks):
        assert (block == framed[index]) == (index not in changed_blocks)
    return candidate, decoded, dict(header=header, patches=patches, changed_blocks=changed_blocks)


def main():
    source = ROOT / 'evidence/g9pro-official-firmware-resource.bin'
    original = source.read_bytes()
    # Keep previous physically tested variants intact, including their bounds.
    preserved = {p: p.read_bytes() for p in (
        ROOT / 'firmware/g9_ol4.S', ROOT / 'firmware/g9_ol4.payload.hex',
        ROOT / 'src/G9FirmwareOl4Patch.cs', ROOT / 'tools/build_ol4_candidate.py')}
    code, layout = build()
    candidate, decoded, framing = build_resource(original, code, layout)
    # Execute bytes decoded from this exact framed BIN, not another fixture image.
    checks = dict(radio=radio_checks(code, layout, candidate_image=decoded),
                  retention=retention_checks(code, layout, candidate_image=decoded))
    assert source.read_bytes() == original
    assert all(path.read_bytes() == value for path, value in preserved.items())
    stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    out = ROOT / 'patch-output' / (stamp + '-OL5-' + uuid.uuid4().hex[:8])
    out.mkdir(parents=True, exist_ok=False)
    target = out / 'g9-pro-1.08-oled-radio-retained-OL5.CANDIDATE.bin'
    target.write_bytes(candidate)
    manifest = dict(status='OFFLINE_RAW_CANDIDATE_PASSED_NOT_INSTALL_QUALIFIED',
        source_resource_sha256=sha(original), candidate_resource_sha256=sha(candidate),
        resource_bytes=len(candidate), payload_bytes=len(code), payload_sha256=sha(code),
        asm_sha256=sha((ROOT / 'firmware/g9_ol5.S').read_bytes()),
        original_crc_blocks=ORIGINAL_BLOCKS, appended_crc_blocks=APPENDED_BLOCKS,
        framing=framing, layout=layout, emulation=checks,
        transfer=dict(original_bytes=0x21000, candidate_bytes=TRANSFER_BYTES,
            original_pages=33, candidate_pages=34, range_expanded=True,
            original_erase='[0x2B000,0x51000)', candidate_erase='[0x2B000,0x52000)',
            candidate_program='[0x2B00A,0x4D00A)', additional_erase='[0x51000,0x52000)',
            additional_sector_backed_up=False, resource_load_path_qualified=False),
        device_access=False, updater_executed=False, physical_validated=False,
        installed_full_backup=False, independent_rollback_verified=False,
        limits=['Additional erase sector not backed up; no installation qualification',
                'Receiver relay and SRAM retention require physical checks',
                'Retention is SRAM/warm-reset only; no hard-power-loss promise',
                'No per-frame nonvolatile writes; live PC widgets require HUB'])
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    assert target.read_bytes() == candidate
    print(json.dumps(dict(candidate=str(target), manifest=str(out / 'manifest.json'),
                         sha256=sha(candidate), bytes=len(candidate), install_qualified=False)))


if __name__ == '__main__':
    main()
