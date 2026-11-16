"""Offline OL9 packaging and exact x86 patches; never execute/open a device.

Windows resource APIs edit a .disabled copy. pefile independently verifies every
resource, original PE layout and all bytes outside the two explicit code spans.
The original updater and prior OL4 builders are never modified.
"""
from pathlib import Path
import argparse
import ctypes
import hashlib
import json
import os
import struct
import pefile

if not __debug__:
    raise RuntimeError('Packaging requires assertions; do not use -O')
ROOT = Path(__file__).resolve().parents[1]
SOURCE_HASH = '558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4'
RESOURCE_HASH = '385a653c071cf509d376f8efe2c5d96333cfc68ee7a4d13f7f2b35b8a18dab0e'
PACKAGE_HASH = '1f7a34bf8ce51cbc9c0be9072b0b49e5e78849260965734539e3b03cb972980c'
RESOURCE_BYTES = 136152
COPY_ADDRESS = 0x404E3D
COPY_BEFORE = bytes.fromhex('5356ffb7ec000000e8d6a9140083c418')
# memset immediately before this span returns destination in EAX. Keep the
# rounded transfer length in EBX, but copy exactly the pinned resource bytes.
COPY_AFTER = (b'\x68' + struct.pack('<I', RESOURCE_BYTES) + b'\x56\x50\xe8' +
              struct.pack('<i', 0x54F820 - (COPY_ADDRESS + 12)) + b'\x83\xc4\x18\x90')
assert len(COPY_BEFORE) == len(COPY_AFTER) == 16
PATCHES = ((0x4033B9, bytes.fromhex('8b87dc000000'), bytes.fromhex('b80010020090')),
           (COPY_ADDRESS, COPY_BEFORE, COPY_AFTER))


def sha(data):
    return hashlib.sha256(data).hexdigest()


def patch_host(data):
    pe = pefile.PE(data=data)
    result = bytearray(data)
    for address, before, after in PATCHES:
        offset = pe.get_offset_from_rva(address - pe.OPTIONAL_HEADER.ImageBase)
        assert bytes(result[offset:offset + len(before)]) == before, hex(address)
        result[offset:offset + len(before)] = after
    return bytes(result)


def resources(data):
    pe = pefile.PE(data=data)
    result = {}
    for kind in pe.DIRECTORY_ENTRY_RESOURCE.entries:
        for name in kind.directory.entries:
            for lang in name.directory.entries:
                key = (str(kind.name) if kind.name else kind.id,
                       str(name.name) if name.name else name.id, lang.id)
                entry = lang.data.struct
                assert key not in result
                result[key] = pe.get_data(entry.OffsetToData, entry.Size)
    return result


def resource_section(pe):
    matches = [s for s in pe.sections if s.Name.rstrip(b'\0') == b'.rsrc']
    assert len(matches) == 1
    return matches[0]


def verify(original, packed, resource):
    assert sha(original) == SOURCE_HASH
    assert len(resource) == RESOURCE_BYTES and sha(resource) == RESOURCE_HASH
    assert sha(packed) == PACKAGE_HASH, 'Reviewed complete updater hash differs'
    before, after = pefile.PE(data=original), pefile.PE(data=packed)
    assert len(original) == len(packed)
    section = resource_section(before)
    new_section = resource_section(after)
    assert new_section.Misc_VirtualSize <= section.SizeOfRawData
    assert ((new_section.Misc_VirtualSize + 4095) & ~4095) == ((section.Misc_VirtualSize + 4095) & ~4095)
    start, end = section.PointerToRawData, section.PointerToRawData + section.SizeOfRawData
    expected = bytearray(patch_host(original))
    # New packing fits original raw capacity/mapped pages. Only these two
    # resource extent fields must grow; do not keep UpdateResource's unrelated
    # SizeOfInitializedData canonicalization.
    for offset, value in ((section.get_file_offset() + 8, new_section.Misc_VirtualSize),
                          (before.OPTIONAL_HEADER.DATA_DIRECTORY[2].get_file_offset() + 4,
                           after.OPTIONAL_HEADER.DATA_DIRECTORY[2].Size)):
        struct.pack_into('<I', expected, offset, value)
    assert after.OPTIONAL_HEADER.DATA_DIRECTORY[2].Size == new_section.Misc_VirtualSize
    assert before.header != after.header and bytes(expected[:before.OPTIONAL_HEADER.SizeOfHeaders]) == after.header
    assert packed[:start] == expected[:start] and packed[end:] == expected[end:]
    a, b = resources(original), resources(packed)
    assert a.keys() == b.keys() and a[('BIN', 129, 2052)] != resource
    assert b.pop(('BIN', 129, 2052)) == resource
    a.pop(('BIN', 129, 2052))
    assert a == b, 'Another resource changed'
    for address, _, replacement in PATCHES:
        assert after.get_data(address - after.OPTIONAL_HEADER.ImageBase, len(replacement)) == replacement
    return dict(updater_sha256=sha(packed), resource_sha256=sha(resource),
                file_bytes=len(packed), other_resources_preserved=len(a),
                header_changes=['rsrc.VirtualSize', 'ResourceDirectory.Size'],
                original_raw_offsets_and_mapped_pages_preserved=True,
                code_patch_spans=[dict(address=hex(a), old=b.hex(), new=c.hex()) for a,b,c in PATCHES],
                erase='[0x2B000,0x51000)', program='[0x2B00A,0x4D00A)',
                device_access=False, updater_executed=False)


def package(resource_path):
    assert os.name == 'nt', 'Resource packaging uses Windows only'
    matches = list((ROOT / 'evidence/firmware-readonly').rglob('Update.exe'))
    assert len(matches) == 1
    original = matches[0].read_bytes()
    resource = resource_path.read_bytes()
    assert sha(original) == SOURCE_HASH
    assert len(resource) == RESOURCE_BYTES and sha(resource) == RESOURCE_HASH
    target = resource_path.parent / 'FURYCUBE-G9-PRO-OL9-bounded.exe.disabled'
    assert not target.exists()
    target.write_bytes(original)
    api = ctypes.WinDLL('kernel32', use_last_error=True)
    api.BeginUpdateResourceW.argtypes = [ctypes.c_wchar_p, ctypes.c_int]
    api.BeginUpdateResourceW.restype = ctypes.c_void_p
    api.UpdateResourceW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_void_p,
                                   ctypes.c_ushort, ctypes.c_void_p, ctypes.c_uint]
    api.UpdateResourceW.restype = ctypes.c_int
    api.EndUpdateResourceW.argtypes = [ctypes.c_void_p, ctypes.c_int]
    api.EndUpdateResourceW.restype = ctypes.c_int
    handle = api.BeginUpdateResourceW(str(target), False)
    assert handle, ctypes.get_last_error()
    data = ctypes.create_string_buffer(resource)
    ok = api.UpdateResourceW(handle, 'BIN', 129, 2052, data, len(resource))
    if not ok:
        api.EndUpdateResourceW(handle, True)
        raise RuntimeError('UpdateResource failed: ' + str(ctypes.get_last_error()))
    assert api.EndUpdateResourceW(handle, False), ctypes.get_last_error()
    repacked = target.read_bytes()
    a, b = pefile.PE(data=original), pefile.PE(data=repacked)
    src, dst = resource_section(b), resource_section(a)
    assert src.VirtualAddress == dst.VirtualAddress
    assert src.SizeOfRawData <= dst.SizeOfRawData and src.Misc_VirtualSize <= dst.SizeOfRawData
    assert ((src.Misc_VirtualSize + 4095) & ~4095) == ((dst.Misc_VirtualSize + 4095) & ~4095)
    result = bytearray(original)
    result[dst.PointerToRawData:dst.PointerToRawData + dst.SizeOfRawData] = b'\0' * dst.SizeOfRawData
    result[dst.PointerToRawData:dst.PointerToRawData + src.SizeOfRawData] = repacked[src.PointerToRawData:src.PointerToRawData + src.SizeOfRawData]
    struct.pack_into('<I', result, dst.get_file_offset() + 8, src.Misc_VirtualSize)
    struct.pack_into('<I', result, a.OPTIONAL_HEADER.DATA_DIRECTORY[2].get_file_offset() + 4,
                     b.OPTIONAL_HEADER.DATA_DIRECTORY[2].Size)
    packed = patch_host(bytes(result))
    report = verify(original, packed, resource)
    target.write_bytes(packed)
    assert matches[0].read_bytes() == original
    (target.parent / 'updater-package.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(dict(target=str(target), **report)))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('resource', type=Path)
    package(parser.parse_args().resource.resolve())
