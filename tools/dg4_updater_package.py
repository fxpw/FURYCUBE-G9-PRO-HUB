"""Pinned DG4 offline packaging in the original resource slot; never launch an updater."""
from pathlib import Path
import argparse
import hashlib
import json
import struct
import pefile

if not __debug__:
    raise RuntimeError('Assertions are required; do not use -O')
ROOT = Path(__file__).resolve().parents[1]
SOURCE_HASH = '558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4'
SOURCE_RESOURCE_HASH = 'f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc'
RESOURCE_BYTES = 134112
RESOURCE_HASH = '5b5bad4b0c15a60af976f6b96a5d1d912d1a0d6af6c26587ace0aed0f40b0074'
PACKAGE_HASH = 'e2c6f5135ac8b72b40e737088517173501122629b658307fb59043d74439f24c'
ORIGINAL_SLOT_BYTES = 134140
TRANSFER_BYTES = 0x21000
KEY = ('BIN', 129, 2052)
COPY_ADDRESS = 0x404E3D
COPY_BEFORE = bytes.fromhex('5356ffb7ec000000e8d6a9140083c418')
COPY_AFTER = (b'\x68' + struct.pack('<I', RESOURCE_BYTES) + b'\x56\x50\xe8' +
              struct.pack('<i', 0x54F820 - (COPY_ADDRESS + 12)) + b'\x83\xc4\x18\x90')
assert len(COPY_AFTER) == len(COPY_BEFORE) == 16


def sha(data):
    return hashlib.sha256(data).hexdigest()


def entries(pe):
    result = {}
    for kind in pe.DIRECTORY_ENTRY_RESOURCE.entries:
        for name in kind.directory.entries:
            for language in name.directory.entries:
                key = (str(kind.name) if kind.name else kind.id,
                       str(name.name) if name.name else name.id, language.id)
                assert key not in result
                result[key] = language.data.struct
    return result


def resources(data):
    pe = pefile.PE(data=data)
    return {key: pe.get_data(entry.OffsetToData, entry.Size)
            for key, entry in entries(pe).items()}


def render(original, resource):
    assert RESOURCE_HASH is not None, 'DG4 resource pins are required before packaging'
    assert 0 < RESOURCE_BYTES <= ORIGINAL_SLOT_BYTES, 'DG4 exceeds the immutable original resource slot'
    assert (RESOURCE_BYTES + 4095) & ~4095 == TRANSFER_BYTES, 'DG4 must retain the original33-page transfer'
    assert sha(original) == SOURCE_HASH
    assert len(resource) == RESOURCE_BYTES and sha(resource) == RESOURCE_HASH
    pe = pefile.PE(data=original)
    entry = entries(pe)[KEY]
    assert entry.Size == ORIGINAL_SLOT_BYTES
    assert sha(pe.get_data(entry.OffsetToData, entry.Size)) == SOURCE_RESOURCE_HASH
    start = pe.get_offset_from_rva(entry.OffsetToData)
    code = pe.get_offset_from_rva(COPY_ADDRESS - pe.OPTIONAL_HEADER.ImageBase)
    assert original[code:code + 16] == COPY_BEFORE
    result = bytearray(original)
    result[start:start + entry.Size] = resource + b'\xff' * (entry.Size - len(resource))
    struct.pack_into('<I', result, entry.get_file_offset() + 4, len(resource))
    result[code:code + 16] = COPY_AFTER
    return bytes(result)


def verify(original, packed, resource, inspect_only=False):
    assert RESOURCE_HASH is not None and 0 < RESOURCE_BYTES <= ORIGINAL_SLOT_BYTES
    assert (RESOURCE_BYTES + 4095) & ~4095 == TRANSFER_BYTES
    assert sha(original) == SOURCE_HASH
    assert len(resource) == RESOURCE_BYTES and sha(resource) == RESOURCE_HASH
    if not inspect_only:
        assert PACKAGE_HASH is not None and sha(packed) == PACKAGE_HASH, 'Unpinned DG4 package'
    assert len(packed) == len(original)
    before, after = pefile.PE(data=original), pefile.PE(data=packed)
    assert before.header == after.header, 'Every PE header must remain byte-identical'
    a, b = entries(before)[KEY], entries(after)[KEY]
    assert a.Size == ORIGINAL_SLOT_BYTES
    assert a.OffsetToData == b.OffsetToData and b.Size == RESOURCE_BYTES
    assert a.get_file_offset() == b.get_file_offset()
    start = before.get_offset_from_rva(a.OffsetToData)
    code = before.get_offset_from_rva(COPY_ADDRESS - before.OPTIONAL_HEADER.ImageBase)
    ranges = sorted([(start, start + a.Size), (a.get_file_offset() + 4, a.get_file_offset() + 8), (code, code + 16)])
    cursor = 0
    for low, high in ranges:
        assert cursor <= low < high <= len(original)
        assert original[cursor:low] == packed[cursor:low], 'Unexpected bytes outside reviewed spans'
        cursor = high
    assert original[cursor:] == packed[cursor:]
    assert packed[code:code + 16] == COPY_AFTER
    assert packed[start:start + RESOURCE_BYTES] == resource
    assert packed[start + RESOURCE_BYTES:start + a.Size] == b'\xff' * (a.Size - RESOURCE_BYTES)
    original_resources, packed_resources = resources(original), resources(packed)
    assert original_resources.keys() == packed_resources.keys()
    assert packed_resources.pop(KEY) == resource
    assert sha(original_resources.pop(KEY)) == SOURCE_RESOURCE_HASH
    assert original_resources == packed_resources and len(original_resources) == 748
    # Original erase-size reload remains exact; 33-page resource needs no expansion.
    assert after.get_data(0x4033B9 - after.OPTIONAL_HEADER.ImageBase, 6) == bytes.fromhex('8b87dc000000')
    return dict(updater_sha256=sha(packed), resource_sha256=sha(resource),
        file_bytes=len(packed), other_resources_preserved=len(original_resources),
        pe_headers_unchanged=True, resource_slot_unchanged=True,
        changed_spans=[dict(start=low, end=high) for low, high in ranges],
        copy_patch=dict(address=hex(COPY_ADDRESS), before=COPY_BEFORE.hex(), after=COPY_AFTER.hex()),
        program_pages=33, erase='[0x2B000,0x51000)', program='[0x2B00A,0x4C00A)',
        device_access=False, updater_executed=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('resource', type=Path)
    parser.add_argument('--inspect', action='store_true', help='Verify structure and print hash without writing any executable')
    options = parser.parse_args()
    source = list((ROOT / 'evidence/firmware-readonly').rglob('Update.exe'))
    assert len(source) == 1
    original, resource = source[0].read_bytes(), options.resource.read_bytes()
    packed = render(original, resource)
    result = verify(original, packed, resource, inspect_only=options.inspect)
    if options.inspect:
        print(json.dumps(dict(status='INSPECTED_NO_EXECUTABLE_WRITTEN', **result), indent=2))
        return
    target = options.resource.parent / 'FURYCUBE-G9-PRO-DG4-original-slot.exe.disabled'
    with target.open('xb') as stream:
        stream.write(packed)
    assert source[0].read_bytes() == original
    (target.parent / 'updater-package.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(dict(target=str(target), **result), indent=2))


if __name__ == '__main__':
    main()
