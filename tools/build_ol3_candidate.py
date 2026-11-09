"""Build and regress a separate raw OL3 candidate OFFLINE ONLY.

Never runs/repackages an updater or accesses HID/USB. Original resource and OL2
payload stay unchanged. Generated raw BIN/manifest use a unique patch-output
directory and are not physical installation/recovery validation.
"""

from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import uuid

from emulate_ol3_status import build, run_regressions, BASE, APPEND, SOURCE_HASH
from build_oled_candidate import decode_resource, cms, verify_input_paths


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT/'evidence/g9pro-official-firmware-resource.bin'
ROUNDED_SIZE = 0x21000


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main():
    original = SOURCE.read_bytes()
    assert len(original) == 134140 and sha(original) == SOURCE_HASH
    old_ol2_source = (ROOT/'firmware/g9_oled.S').read_bytes()
    old_ol2_payload = (ROOT/'firmware/g9_oled.payload.hex').read_bytes()
    normalized, blocks, tail = decode_resource(original)
    assert len(blocks) == 3939 and tail == bytes([255])*198
    assert BASE+len(normalized) == APPEND
    code, layout = build()
    patched = bytearray(normalized)
    patches = []

    def change(address, before, after, reason):
        offset = address-BASE
        assert len(before) == len(after) and bytes(patched[offset:offset+len(before)]) == before
        patched[offset:offset+len(after)] = after
        patches.append(dict(address=address, normalized_offset=offset, old_hex=before.hex(),
                            new_hex=after.hex(), reason=reason))

    change(0x3201C, bytes.fromhex('0d70'), bytes.fromhex('c046'), 'Disable OLED hold gesture')
    for hook in layout['hooks']:
        change(hook['address'], bytes.fromhex(hook['old_hex']), bytes.fromhex(hook['new_hex']), hook['symbol'])

    framed = bytearray(original[:16])
    changed_blocks = []
    for index, original_block in enumerate(blocks):
        payload = bytes(patched[16+32*index:16+32*(index+1)])
        if payload == original_block[:32]:
            framed.extend(original_block)
        else:
            framed.extend(payload + cms(payload).to_bytes(2, 'big'))
            changed_blocks.append(index)
    padded = code + bytes([255])*((-len(code)) % 32)
    for offset in range(0, len(padded), 32):
        payload = padded[offset:offset+32]
        framed.extend(payload + cms(payload).to_bytes(2, 'big'))
    candidate = bytes(framed)
    expected_offsets = {item['normalized_offset']+i for item in patches
                        for i, (a, b) in enumerate(zip(bytes.fromhex(item['old_hex']), bytes.fromhex(item['new_hex']))) if a != b}
    decoded, candidate_blocks, candidate_tail = decode_resource(candidate)
    assert decoded == bytes(patched)+padded and not candidate_tail
    actual_offsets = {i for i, (a, b) in enumerate(zip(normalized, decoded)) if a != b}
    assert actual_offsets == expected_offsets
    expected_blocks = sorted({(offset-16)//32 for offset in expected_offsets})
    assert expected_blocks == changed_blocks and len(changed_blocks) == 5
    for index, block in enumerate(blocks):
        assert (candidate_blocks[index] == block) == (index not in changed_blocks)
    assert candidate[:16] == original[:16]
    assert len(padded)//32 == layout['extension_crc_blocks']
    assert len(candidate) == layout['minimum_resource_size']
    assert len(candidate) <= ROUNDED_SIZE
    assert (len(candidate)+4095)&~4095 == (len(original)+4095)&~4095 == ROUNDED_SIZE

    # Regress the ACTUAL bytes reconstructed from the newly CRC-framed BIN.
    status_checks = run_regressions(code, layout, candidate_image=decoded)
    zero_checks = verify_input_paths(decoded)
    assert SOURCE.read_bytes() == original
    assert (ROOT/'firmware/g9_oled.S').read_bytes() == old_ol2_source
    assert (ROOT/'firmware/g9_oled.payload.hex').read_bytes() == old_ol2_payload

    stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    destination = ROOT/'patch-output'/(stamp+'-ol3-'+uuid.uuid4().hex[:8])
    destination.mkdir(parents=True, exist_ok=False)
    target = destination/'g9-pro-1.08-zero-oled-status-OL3.CANDIDATE.bin'
    target.write_bytes(candidate)
    manifest = dict(
        status='OFFLINE_CANDIDATE_CHECKS_PASSED_NO_FLASH', physical_mouse_validated=False,
        installed_firmware_backed_up=False, updater_executed=False,
        source_resource_sha256=sha(original), candidate_resource_sha256=sha(candidate),
        source_resource_size=len(original), candidate_resource_size=len(candidate),
        source_original_crc_blocks=len(blocks), candidate_valid_crc_blocks=len(candidate_blocks),
        header_16_bytes_unchanged=True, only_changed_original_crc_blocks=changed_blocks,
        unchanged_original_blocks_preserved_byte_for_byte=True,
        original_data_changed_offsets=sorted(actual_offsets), patches=patches,
        append=dict(address=APPEND, normalized_offset=APPEND-BASE, resource_offset=16+3939*34,
                    machine_code_bytes=len(code), padded_data_bytes=len(padded), crc_blocks=len(padded)//32,
                    code_sha256=sha(code), asm_source_sha256=sha((ROOT/'firmware/g9_ol3.S').read_bytes()),
                    included_ol2_source_sha256=sha(old_ol2_source), byte_identical_ol2_prefix_bytes=280),
        aligned_transfer=dict(source_length=ROUNDED_SIZE, candidate_length=ROUNDED_SIZE,
                              alignment=4096, range_not_expanded=True, physical_flash_mapping_not_validated=True),
        features=dict(version_marker='OL3', page_upload='unchanged GO2, wired-only',
                      status='132-byte GS3 ConfiguredDpiRuntimeOLED; A0 selector0',
                      status_freshness_host_max_ms=1000, status_codes=dict(invalid=0, valid=1, busy=2),
                      no_profile_reload_or_nvm_for_status=True, stock_a0_power_wake_retained=True,
                      new_persistent_global_ram_bytes=0, native_renderer_may_overwrite=True,
                      physical_sensor_applied_dpi=False, ownership=False, flash_picture_persistence=False),
        emulation=dict(**status_checks, **zero_checks),
        limits=['No physical validation or installed full backup',
                'ROM/FIFO and OLED hardware/render boundaries are emulated',
                'Main profile-history persistence uses a synthetic stub, never actual NVM',
                'Qualified ConfiguredDpiRuntimeOLED semantics, not sensor metrology',
                'Recovery gate remains mandatory; capability marker does not prove recovery'],
    )
    (destination/'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    (destination/'README-RU.txt').write_text(
        'OL3: ТОЛЬКО ОФЛАЙН-КАНДИДАТ. НЕ УСТАНОВЛЕН.\n'
        'Проверены CRC, hooks, весь A0/status тракт в эмуляции и прежний размер transfer.\n'
        'Полного backup и независимого recovery мыши нет; прошивку устанавливать нельзя.\n'
        'ConfiguredDpi берётся из runtime RAM как штатный OLED, не измеряется в сенсоре.\n'
        'Статус использует штатный A0 wake, без новой загрузки профиля/записи NVM.\n'
        'Физическая работа, загрузчик и совместимость железа не подтверждены.\n', encoding='utf-8')
    # Own source code only; proprietary firmware stays under ignored patch-output.
    (ROOT/'firmware/g9_ol3.payload.hex').write_text(code.hex()+'\n', encoding='ascii')
    (ROOT/'bin/g9_ol3-layout.json').write_text(json.dumps(layout, indent=2), encoding='utf-8')
    assert target.read_bytes() == candidate
    print(json.dumps(dict(candidate=str(target), manifest=str(destination/'manifest.json'),
                          candidate_sha256=sha(candidate), candidate_bytes=len(candidate),
                          payload_sha256=sha(code), payload_bytes=len(code), new_blocks=len(padded)//32)))
    print('PASS original/OL2 unchanged, 5 changed original CRC blocks, same aligned transfer, NO FLASH')


if __name__ == '__main__':
    main()
