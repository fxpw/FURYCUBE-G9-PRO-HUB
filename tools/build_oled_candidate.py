"""Build a new raw OL2 firmware resource candidate, OFFLINE ONLY.

Never overwrites the official resource/updater. No USB or executable updater is
invoked. Header, original CRC blocks, exact hooks, extension, transfer round-size
and the actual resulting ARM bytes are checked before emitting PASS metadata.
Physical update acceptance/recovery have not been validated.
"""

from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import uuid
from emulate_oled_arm import build, run_regressions, BASE, APPEND, SOURCE_HASH
from emulate_screen_key import Fixture, OLED_OFF, GESTURE
from check_firmware_header import seal_header, validate_header, EDITABLE_HEADER_OFFSETS

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT/'evidence/g9pro-official-firmware-resource.bin'
EXPECTED_BLOCKS = 3939
ALIGNED_TRANSFER = 0x21000


def sha(data):
    return hashlib.sha256(data).hexdigest()


def cms(data):
    value = 0xFFFF
    for byte in data:
        value ^= byte << 8
        for _ in range(8):
            value = ((value << 1) ^ (0x8005 if value & 0x8000 else 0)) & 0xFFFF
    return value


def decode_resource(raw):
    assert len(raw) >= 16
    blocks, normalized = [], bytearray(raw[:16])
    offset = 16
    while offset < len(raw):
        item = raw[offset:offset+34]
        if item == b'\xFF'*len(item):
            assert raw[offset:] == b'\xFF'*(len(raw)-offset), 'Non-FF bytes after padding begins'
            break
        assert len(item) == 34, 'Partial non-padding CRC block'
        assert cms(item[:32]) == int.from_bytes(item[32:], 'big'), f'Bad CRC block at {offset:#x}'
        blocks.append(item)
        normalized.extend(item[:32])
        offset += 34
    return bytes(normalized), blocks, raw[offset:]


def verify_input_paths(candidate_image):
    empty, down = (1, bytes(8)), (1, bytes.fromhex('0000270000000000'))
    for index, off, chord in ((3, 0, False), (3, 1, False), (2, 0, False), (3, 0, True)):
        fixture = Fixture(index)
        fixture.uc.mem_write(BASE, candidate_image)
        fixture.set8(OLED_OFF, off)
        held = 2 if chord else 0
        if held:
            fixture.stable_edge(held, 'right held')
        fixture.stable_edge(held | (1 << index), 'target pressed')
        assert fixture.get8(GESTURE) == 0
        fixture.cycle('target held')
        fixture.release_starts()
        fixture.stable_edge(held, 'target release debounced')
        assert fixture.reports == [empty, down, empty], (index, off, chord, fixture.reports)
    print('PASS: actual candidate bytes retain ordinary 0 on screen on/off, middle and right chord')
    return {'ordinary_zero_screen_on_off_middle_right_chord': True}


def main():
    raw = SOURCE.read_bytes()
    assert sha(raw) == SOURCE_HASH, 'Source resource hash mismatch'
    validate_header(raw)
    normalized, blocks, tail = decode_resource(raw)
    assert len(raw) == 134140 and len(blocks) == EXPECTED_BLOCKS and tail == b'\xFF'*198
    assert BASE+len(normalized) == APPEND
    code, layout = build()
    assert sha(code) == layout['text_sha256']
    padded_code = code + b'\xFF'*((-len(code)) % 32)
    assert len(padded_code)//32 == layout['extension_crc_blocks']
    assert len(padded_code) <= 36*32
    patched = bytearray(normalized)
    patch_records = []

    def patch(offset, old, new, reason):
        assert len(old) == len(new) and patched[offset:offset+len(old)] == old
        patched[offset:offset+len(new)] = new
        patch_records.append({'normalized_offset': offset, 'address': BASE+offset,
                              'old_hex': old.hex(), 'new_hex': new.hex(), 'reason': reason})

    patch(0x988C, bytes.fromhex('0d70'), bytes.fromhex('c046'), 'Disable OLED hold gesture for ordinary button behavior')
    for hook in layout['hooks']:
        patch(hook['normalized_offset'], bytes.fromhex(hook['old_hex']), bytes.fromhex(hook['new_hex']), hook['symbol'])
    result = bytearray(raw[:16])
    changed_blocks = []
    for index, item in enumerate(blocks):
        offset = 16+index*32
        payload = bytes(patched[offset:offset+32])
        if payload == item[:32]:
            result.extend(item)  # Preserve unchanged blocks INCLUDING their original CRC bytes.
        else:
            result.extend(payload + cms(payload).to_bytes(2, 'big'))
            changed_blocks.append(index)
    for offset in range(0, len(padded_code), 32):
        payload = padded_code[offset:offset+32]
        result.extend(payload + cms(payload).to_bytes(2, 'big'))
    candidate = seal_header(result)
    header = validate_header(candidate)
    header_changes = [i for i in range(16) if candidate[i] != raw[i]]
    assert set(header_changes) <= EDITABLE_HEADER_OFFSETS
    resource_padding = (-len(result)) % 4
    assert len(candidate) == layout['minimum_resource_size']+resource_padding
    assert len(candidate) <= ALIGNED_TRANSFER
    assert (len(raw)+4095)&~4095 == (len(candidate)+4095)&~4095 == ALIGNED_TRANSFER
    decoded, candidate_blocks, candidate_tail = decode_resource(candidate)
    assert candidate_tail == bytes([255])*resource_padding
    assert len(candidate_blocks) == EXPECTED_BLOCKS+len(padded_code)//32
    expected_image = bytearray(patched)+padded_code
    expected_image[:16] = candidate[:16]
    assert decoded == bytes(expected_image)
    expected_offsets = {record['normalized_offset']+i for record in patch_records
                        for i, (a, b) in enumerate(zip(bytes.fromhex(record['old_hex']), bytes.fromhex(record['new_hex']))) if a != b}
    actual_offsets = {i for i, (a, b) in enumerate(zip(normalized, decoded)) if i >= 16 and a != b}
    assert actual_offsets == expected_offsets, 'Unexpected original image modifications'
    expected_blocks = sorted({(offset-16)//32 for offset in expected_offsets})
    assert changed_blocks == expected_blocks and len(changed_blocks) == 3
    for index in range(EXPECTED_BLOCKS):
        assert (candidate_blocks[index] == blocks[index]) == (index not in changed_blocks)

    # Run against the bytes decoded from the ACTUAL newly framed candidate,
    # rather than silently patching an unrelated in-memory fixture.
    arm_checks = run_regressions(code, layout, candidate_image=decoded)
    input_checks = verify_input_paths(decoded)
    assert SOURCE.read_bytes() == raw, 'Original source was changed externally'
    token = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')+'-oled-arm-'+uuid.uuid4().hex[:8]
    destination = ROOT/'patch-output'/token
    destination.mkdir(parents=True, exist_ok=False)
    target = destination/'g9-pro-1.08-zero-oled-OL2.CANDIDATE.bin'
    target.write_bytes(candidate)
    manifest = {
        'status': 'OFFLINE_CANDIDATE_CHECKS_PASSED_NO_FLASH',
        'physical_mouse_validated': False,
        'installed_firmware_backed_up': False,
        'original_updater_executed': False,
        'source_resource_sha256': sha(raw), 'candidate_resource_sha256': sha(candidate),
        'source_resource_size': len(raw), 'candidate_resource_size': len(candidate),
        'header_16_bytes_unchanged': False,
        'header_changed_offsets': header_changes,
        'header_crc32_length': header,
        'header_other_10_bytes_preserved': True,
        'container_ff_word_padding_bytes': resource_padding,
        'source_original_crc_blocks': EXPECTED_BLOCKS,
        'candidate_valid_crc_blocks': len(candidate_blocks),
        'only_changed_original_crc_blocks': changed_blocks,
        'unchanged_original_blocks_preserved_byte_for_byte': True,
        'original_data_changed_offsets': sorted(actual_offsets),
        'patches': patch_records,
        'append': {'address': APPEND, 'normalized_offset': APPEND-BASE,
                   'resource_offset': 16+EXPECTED_BLOCKS*34,
                   'machine_code_bytes': len(code), 'padded_data_bytes': len(padded_code),
                   'crc_blocks': len(padded_code)//32, 'code_sha256': sha(code),
                   'asm_source_sha256': sha((ROOT/'firmware/g9_oled.S').read_bytes())},
        'updater_aligned_transfer_bounds': {'source_offset': 0, 'candidate_offset': 0,
                                           'source_length': ALIGNED_TRANSFER, 'candidate_length': ALIGNED_TRANSFER,
                                           'alignment': 4096, 'range_not_expanded': True,
                                           'physical_flash_mapping_not_validated': True},
        'firmware_features': {'ordinary_zero_hold_gate_removed': True, 'version_marker': 'OL2',
                              'oled_protocol': 'wired-only GO2 WRITE_PAGE',
                              'persistent_global_ram_bytes': 0, 'ownership': False,
                              'flash_picture_persistence': False, 'native_renderer_may_overwrite': True},
        'emulation': {**arm_checks, **input_checks},
        'warning': 'Not a validated device update. No flash or updater execution performed. Obtain installed firmware backup/recovery and review before any physical update.',
    }
    (destination/'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    assert target.read_bytes() == candidate
    print('PASS: original unchanged; header CRC32/length sealed, other10 header bytes preserved; 3 modified/9 appended CRC blocks; same bounds')
    print(json.dumps({'candidate': str(target), 'manifest': str(destination/'manifest.json'),
                      'candidate_sha256': sha(candidate), 'status': manifest['status']}))


if __name__ == '__main__':
    main()
