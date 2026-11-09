"""Independent offline checks for the exact G9 PRO OLED-gesture patch.

Does not load executable code or access USB. Physical compatibility, bootloader
acceptance and recovery of a real mouse are outside these checks.
"""
from pathlib import Path
import argparse
import hashlib
import json
import pefile
from check_firmware_header import validate_header, EDITABLE_HEADER_OFFSETS

ROOT = Path(__file__).resolve().parents[1]
SOURCE_EXE_HASH = '558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4'
SOURCE_BIN_HASH = 'f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc'
PATCH_BIN_HASH = '01908b65a972ae04282392088e2581640365a0d0712c9f281d6f54691ea14865'

def digest(data):
    return hashlib.sha256(data).hexdigest()

def resource(pe):
    found = []
    for kind in pe.DIRECTORY_ENTRY_RESOURCE.entries:
        if str(kind.name) != 'BIN':
            continue
        for number in kind.directory.entries:
            if number.id != 129:
                continue
            for lang in number.directory.entries:
                if lang.id == 2052:
                    entry = lang.data.struct
                    found.append(pe.get_data(entry.OffsetToData, entry.Size))
    assert len(found) == 1, 'Expected exactly one BIN/129/2052 resource'
    return found[0]

def crc16_cms(data):
    value = 0xffff
    for byte in data:
        value ^= byte << 8
        for _ in range(8):
            value = ((value << 1) ^ (0x8005 if value & 0x8000 else 0)) & 0xffff
    return value

def verify_crc_blocks(data):
    count = 0
    for offset in range(16, len(data), 34):
        block = data[offset:offset+34]
        if block == bytes([255]) * len(block):
            assert data[offset:] == bytes([255]) * (len(data)-offset), 'Non-padding after FF tail'
            break
        assert len(block) == 34, 'Truncated non-padding checksum block'
        assert crc16_cms(block[:32]) == int.from_bytes(block[32:], 'big'), hex(offset)
        count += 1
    assert count == 3939, 'Unexpected non-padding block count'
    return count

def sections(pe):
    return {
        section.Name.rstrip(b'\0').decode('ascii'): section.get_data()
        for section in pe.sections
        if section.Name.rstrip(b'\0') != b'.rsrc'
    }

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('candidate', type=Path)
    args = parser.parse_args()
    sources = list((ROOT/'evidence/firmware-readonly').rglob('Update.exe'))
    assert len(sources) == 1, 'Ambiguous original updater'
    source_path = sources[0]
    assert digest(source_path.read_bytes()) == SOURCE_EXE_HASH, 'Original updater hash mismatch'
    source_pe = pefile.PE(str(source_path))
    candidate_pe = pefile.PE(str(args.candidate))
    before, after = resource(source_pe), resource(candidate_pe)
    assert digest(before) == SOURCE_BIN_HASH
    validate_header(before)
    header = validate_header(after)
    assert digest(after) == PATCH_BIN_HASH
    assert len(before) == len(after) == 134140
    changes = [i for i, (a, b) in enumerate(zip(before, after)) if a != b]
    assert changes == [0, 1, 2, 3, 0xa212, 0xa213, 0xa216, 0xa217], changes
    assert all(before[i] == after[i] for i in range(16) if i not in EDITABLE_HEADER_OFFSETS)
    assert before[6:8] == after[6:8]  # The no-wait candidate has no size change.
    assert before[0xa212:0xa214] == bytes.fromhex('0d70')
    assert after[0xa212:0xa214] == bytes.fromhex('c046')
    assert before[0xa216:0xa218] == bytes.fromhex('53aa')
    assert after[0xa216:0xa218] == bytes.fromhex('3433')
    assert verify_crc_blocks(before) == verify_crc_blocks(after) == 3939
    before_sections, after_sections = sections(source_pe), sections(candidate_pe)
    assert before_sections == after_sections, 'Updater sections outside .rsrc changed'
    report = {
        'result': 'OFFLINE_CHECKS_PASSED',
        'physical_mouse_validated': False,
        'installed_firmware_backed_up': False,
        'candidate': str(args.candidate),
        'source_updater_sha256': SOURCE_EXE_HASH,
        'source_resource_sha256': SOURCE_BIN_HASH,
        'patched_resource_sha256': PATCH_BIN_HASH,
        'resource_changes': [hex(i) for i in changes],
        'header_crc32_length': header,
        'header_other_10_bytes_preserved': True,
        'valid_crc16_cms_blocks': 3939,
        'non_resource_sections_unchanged': list(before_sections),
        'behavior': 'Disable OLED hold gesture; leave global debounce unchanged',
    }
    path = args.candidate.parent/'independent-verification.json'
    path.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding='utf-8')
    print(json.dumps(report, ensure_ascii=False))

if __name__ == '__main__':
    main()
