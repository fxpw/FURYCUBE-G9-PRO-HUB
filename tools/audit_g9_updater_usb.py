"""Read-only static inventory of the pinned G9 updater's HID imports/calls.

Parses bytes with pefile. Never loads/runs the vendor PE or opens USB/serial.
No output file or modified binary is created. Optional argument is an existing
official Update.exe; otherwise use the local ignored official-download folder.
This classifies host operations, not unknown device-loader firmware opcodes.
"""
from pathlib import Path
import argparse
import hashlib
import json
import re
import struct

import pefile


ROOT = Path(__file__).resolve().parents[1]
SOURCE_SHA = '558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4'
EXPECTED = {
    'hid_write': (0x4032C9, 0x40349E, 0x4035DF, 0x40381C, 0x4039DB, 0x403B05),
    'hid_send_feature_report': (0x403097, 0x403C08),
    'hid_read': (0x4032FA, 0x4034BB, 0x4035FC, 0x403855, 0x4039F8),
    'hid_open_path': (0x402FE0, 0x40303B, 0x4030D9, 0x403BAF, 0x40537E, 0x40541D, 0x405498),
    'hid_enumerate': (0x402FB6, 0x40300D, 0x4030C4, 0x403B3D, 0x40533A, 0x4053C3, 0x405486),
    'hid_init': (0x4044F2,),
    'hid_free_enumeration': (0x402FF0, 0x40304B, 0x4030FE, 0x4031D3, 0x403BC3,
                             0x4053AE, 0x4053CA, 0x405409, 0x405457, 0x4054B6, 0x4054D1),
    'hid_version': (0x4044D1,),
}


def audit(path):
    if not path.is_file():
        raise ValueError('An existing regular official PE file is required')
    data = path.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest != SOURCE_SHA:
        raise ValueError('Unrecognized updater SHA256; this audit is image-specific')
    pe = pefile.PE(data=data)
    base = pe.OPTIONAL_HEADER.ImageBase
    text = next(section for section in pe.sections if section.Name.rstrip(b'\0') == b'.text')
    start, end = base+text.VirtualAddress, base+text.VirtualAddress+text.Misc_VirtualSize
    imports = []
    for dll in pe.DIRECTORY_ENTRY_IMPORT:
        if dll.dll.lower() != b'hidapi.dll':
            continue
        for symbol in dll.imports:
            name = symbol.name.decode('ascii')
            pattern = struct.pack('<I', symbol.address)
            references = []
            for match in re.finditer(re.escape(pattern), data):
                try:
                    address = base+pe.get_rva_from_offset(match.start())
                except pefile.PEFormatError:
                    continue
                if start <= address < end:
                    references.append(address)
            assert tuple(references) == EXPECTED[name], (name, references)
            direct_calls = []
            for address in references:
                if pe.get_data(address-base-2, 2) == b'\xff\x15':
                    direct_calls.append(address-2)
            imports.append(dict(name=name, iat=f'{symbol.address:#x}',
                                text_operand_references=[f'{x:#x}' for x in references],
                                direct_call_sites=[f'{x:#x}' for x in direct_calls]))
    assert {item['name'] for item in imports} == set(EXPECTED)
    assert not any(item['name'] == 'hid_get_feature_report' for item in imports)
    return dict(offline_only=True, vendor_executed=False, device_access=False,
                updater_sha256=digest, updater_bytes=len(data), hid_imports=imports,
                loader_write_sites=6, loader_reply_read_sites=5, application_feature_send_sites=2,
                limitation='Host HID call inventory only; does not enumerate firmware-side loader capabilities.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('binary', type=Path, nargs='?')
    args = parser.parse_args()
    if args.binary is None:
        matches = list((ROOT/'evidence/firmware-readonly').rglob('Update.exe'))
        if len(matches) != 1:
            raise ValueError('Supply one exact official Update.exe path')
        args.binary = matches[0]
    print(json.dumps(audit(args.binary), indent=2))


if __name__ == '__main__':
    main()
