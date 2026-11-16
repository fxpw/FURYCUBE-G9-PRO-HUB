"""Offline x86 OEM receiver API-boundary audit. No DLL loading or device access.

Uses pinned local evidence/original binaries as Unicorn instruction inputs.
All OS, allocation and CRT transfer calls are emulated. Never compiles, invokes
an updater, enumerates HID, or changes a firmware/package/build artifact.
Only a uniquely named JSON under ignored evidence/rf-host-framing is written.
"""
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import struct
import uuid

import pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_EIP, UC_X86_REG_ESP, UC_X86_REG_EAX, UC_X86_REG_ECX

if not __debug__:
    raise RuntimeError('Assertions are required; do not use -O')

ROOT = Path(__file__).resolve().parents[1]
PINS = {
    'hidapi.dll': 'f4e23fe7182822b57305a8aee056a38e875c047e0785e598c1cfd2c1ab7a92bd',
    'hiddriver_1.dll': 'f9efbf0630218b3e13e894b132d77ceb16113acef1bc841e25af8ce57eac39f4',
    'FURYCUBE.exe': '8ca24d9ed79a27b145a7c52a4d0dcc10e0bf1090c27af2646b6cff0be008cc55',
}
QUERY = bytes.fromhex('a00b080001000000')
ACK = bytes.fromhex('a001000000000000')
STOP = 0x900000


def sha(data):
    return hashlib.sha256(data).hexdigest()


class Fixture:
    def __init__(self, images, library='hidapi.dll', main=False):
        self.u = Uc(UC_ARCH_X86, UC_MODE_32)
        self.calls = []
        self.mode = 'feature'
        self.fallback = False
        self.create_attempts = 0
        self.library = library
        for name in ([library, 'FURYCUBE.exe'] if main else [library]):
            pe = pefile.PE(data=images[name])
            assert pe.FILE_HEADER.Machine == 0x14C
            base = pe.OPTIONAL_HEADER.ImageBase
            self.u.mem_map(base, (pe.OPTIONAL_HEADER.SizeOfImage + 4095) & ~4095)
            self.u.mem_write(base, pe.get_memory_mapped_image())
        self.u.mem_map(0x200000, 0x200000)
        self.u.mem_map(0x800000, 0x10000)
        self.u.mem_map(STOP, 0x1000)
        self.u.hook_add(UC_HOOK_CODE, self.hook)

    def put32(self, address, value):
        self.u.mem_write(address, struct.pack('<I', value))

    def args(self, count):
        sp = self.u.reg_read(UC_X86_REG_ESP)
        return struct.unpack('<' + 'I' * count, self.u.mem_read(sp + 4, count * 4))

    def ret(self, argc, result):
        sp = self.u.reg_read(UC_X86_REG_ESP)
        target = struct.unpack('<I', self.u.mem_read(sp, 4))[0]
        self.u.reg_write(UC_X86_REG_ESP, sp + 4 + argc * 4)
        self.u.reg_write(UC_X86_REG_EAX, result)
        self.u.reg_write(UC_X86_REG_EIP, target)

    def call(self, address, values, ecx=0):
        sp = 0x80FF00
        self.u.mem_write(sp, struct.pack('<' + 'I' * (len(values) + 1), STOP, *values))
        self.u.reg_write(UC_X86_REG_ESP, sp)
        self.u.reg_write(UC_X86_REG_ECX, ecx)
        self.u.emu_start(address, STOP, count=200000)
        assert self.u.reg_read(UC_X86_REG_EIP) == STOP
        return self.u.reg_read(UC_X86_REG_EAX)

    def wire_hidapi(self, device=0x220000):
        self.put32(device, 0x1234)
        self.u.mem_write(device + 0x14, struct.pack('<H', 134))
        self.put32(0x10016314, STOP + 0x100)  # HidD_SetFeature
        self.put32(0x1000E01C, STOP + 0x200)  # DeviceIoControl
        self.put32(0x1000E040, STOP + 0x300)  # GetOverlappedResult

    def hook(self, u, address, size, context):
        if address == STOP + 0x100:
            assert self.mode != 'open', 'Open attempted a feature SET'
            handle, pointer, length = self.args(3)
            raw = bytes(u.mem_read(pointer, length))
            self.calls.append(dict(api='HidD_SetFeature', handle=handle,
                length=length, hex=raw.hex(), zero_tail=raw[8:] == bytes(max(0, length - 8))))
            self.ret(3, 1)
        elif address == STOP + 0x200:
            assert self.mode != 'open', 'Open attempted a feature GET'
            handle, code, ib, il, ob, ol, returned, overlapped = self.args(8)
            assert code == 0xB0192 and il == ol == 8
            assert bytes(u.mem_read(ib, il)) == bytes.fromhex('a000000000000000')
            self.calls.append(dict(api='DeviceIoControl', code=hex(code),
                in_length=il, out_length=ol, input_hex=bytes(u.mem_read(ib, il)).hex(),
                overlapped=bool(overlapped)))
            u.mem_write(ob, ACK)
            self.put32(returned, 8)
            self.ret(8, 1)
        elif address == STOP + 0x300:
            handle, overlapped, returned, wait = self.args(4)
            self.calls.append(dict(api='GetOverlappedResult', wait=wait))
            self.put32(returned, 8)
            self.ret(4, 1)
        elif address == STOP + 0x400:
            self.calls.append(dict(api='Sleep', milliseconds=self.args(1)[0]))
            self.ret(1, 0)
        elif address in (0x10003DF0, 0x100037E0, 0x466D83):
            destination, source_or_value, length = self.args(3)
            data = bytes(u.mem_read(source_or_value, length)) if address == 0x10003DF0 else bytes([source_or_value & 255]) * length
            u.mem_write(destination, data)
            self.ret(0, destination)  # CRT cdecl: caller removes arguments.
        elif address == 0x10004F60:
            self.ret(0, 0x230000)  # malloc boundary; scratch is deliberately dirty.
        elif address in (0x465F3F, 0x100023E4):
            self.ret(0, u.reg_read(UC_X86_REG_EAX))  # Cookie check preserves result.
        elif address == 0x10004A8C:
            count, size = self.args(2)
            u.mem_write(0x220000, bytes(count * size))
            self.ret(0, 0x220000)  # calloc boundary.
        elif address == STOP + 0x500:
            self.calls.append(dict(api='CreateEventW'))
            self.ret(4, 0x5678)
        elif address == STOP + 0x600:
            path, access, share, security, creation, flags, template = self.args(7)
            self.create_attempts += 1
            self.calls.append(dict(api='CreateFileA', access=hex(access), share=share,
                creation=creation, flags=hex(flags)))
            self.ret(7, 0xFFFFFFFF if self.fallback and self.create_attempts == 1 else 0x1234)
        elif address == STOP + 0x700:
            handle, count = self.args(2)
            self.calls.append(dict(api='HidD_SetNumInputBuffers', count=count))
            self.ret(2, 1)
        elif address == STOP + 0x800:
            handle, pointer = self.args(2)
            self.put32(pointer, 0x240000)
            self.calls.append(dict(api='HidD_GetPreparsedData'))
            self.ret(2, 1)
        elif address == STOP + 0x900:
            preparsed, pointer = self.args(2)
            caps = bytearray(64)
            struct.pack_into('<HHHHH', caps, 0, 0, 0xB, 5, 0, 134)
            u.mem_write(pointer, bytes(caps))
            self.calls.append(dict(api='HidP_GetCaps', feature_bytes=134))
            self.ret(2, 0x110000)
        elif address == STOP + 0xA00:
            self.calls.append(dict(api='HidD_FreePreparsedData'))
            self.ret(1, 1)
        elif address == 0x10001690 and self.mode == 'open':
            self.calls.append(dict(boundary='attributes/caps/manufacturer/product/serial metadata helper1690'))
            self.ret(0, 0)  # Inspected separately; no feature writes in its body.


def send_checks(images):
    rows = []
    for library, entry in (('hidapi.dll', 0x100020C0), ('hiddriver_1.dll', 0x10002340)):
        f = Fixture(images, library)
        f.u.mem_write(0x210000, QUERY)
        f.u.mem_write(0x230000, b'\xCC' * 134)
        if library == 'hidapi.dll':
            f.wire_hidapi()
            values = (0x220000, 0x210000, 8)
        else:
            f.put32(0x10014150, 0x1234)
            f.put32(0x1000E018, STOP + 0x100)
            values = (0x210000, 8)
        returned = f.call(entry, values)
        expected = QUERY + bytes(126) if library == 'hidapi.dll' else QUERY
        assert len(f.calls) == 1 and f.calls[0]['hex'] == expected.hex()
        assert returned == (8 if library == 'hidapi.dll' else 1)
        rows.append(dict(library=library, input_length=8, return_value=returned, calls=f.calls))
    return rows


def get_check(images):
    f = Fixture(images)
    f.wire_hidapi()
    f.u.mem_write(0x210000, b'\xA0' + bytes(7))
    returned = f.call(0x10002140, (0x220000, 0x210000, 8))
    assert returned == 8 and bytes(f.u.mem_read(0x210000, 8)) == ACK
    assert [row['api'] for row in f.calls] == ['DeviceIoControl', 'GetOverlappedResult']
    return dict(return_value=returned, calls=f.calls)


def oem_prepare_check(images):
    f = Fixture(images, main=True)
    f.wire_hidapi()
    f.u.mem_write(0x230000, b'\xCC' * 134)
    f.put32(0x200420, 1)  # Normal active radio/HIDAPI branch; no wired branch.
    f.put32(0x335188, 0x220000)
    f.put32(0x46C680, 0x100020C0)
    f.put32(0x46C67C, 0x10002140)
    f.put32(0x46C43C, STOP + 0x400)
    returned = f.call(0x420300, (0x0B, 8, 1), ecx=0x200000)
    sends = [row for row in f.calls if row.get('api') == 'HidD_SetFeature']
    assert returned == 1 and len(sends) == 1
    assert sends[0]['hex'] == (QUERY + bytes(126)).hex()
    assert [row['milliseconds'] for row in f.calls if row.get('api') == 'Sleep'] == [100, 500, 100]
    return dict(return_value=returned, supplied_model_flags=dict(field410=0, field420=1,
        field424=0, field428=0), calls=f.calls)


def open_checks(images):
    rows = []
    for fallback in (False, True):
        f = Fixture(images)
        f.wire_hidapi()
        f.mode, f.fallback = 'open', fallback
        f.u.mem_write(0x10016324, b'\1')  # HID API symbol initialization supplied.
        f.u.mem_write(0x210000, b'OFFLINE-FA60-PATH-NO-OS-ACCESS\0')
        for pointer, target in ((0x1000E024, 0x500), (0x1000E030, 0x600),
            (0x1001630C, 0x700), (0x1001631C, 0x800), (0x10016320, 0x900), (0x10016318, 0xA00)):
            f.put32(pointer, STOP + target)
        returned = f.call(0x10001C20, (0x210000,))
        assert returned == 0x220000
        opens = [row for row in f.calls if row.get('api') == 'CreateFileA']
        assert [row['access'] for row in opens] == (['0xc0000000', '0x0'] if fallback else ['0xc0000000'])
        assert all(row['flags'] == '0x40000000' and row['share'] == row['creation'] == 3 for row in opens)
        assert not any(row.get('api') in ('HidD_SetFeature', 'DeviceIoControl') for row in f.calls)
        assert bytes(f.u.mem_read(returned + 0x14, 2)) == struct.pack('<H', 134)
        rows.append(dict(fallback=fallback, calls=f.calls))
    return rows


def main():
    images = {name: (ROOT / 'evidence/original' / name).read_bytes() for name in PINS}
    for name, data in images.items():
        if sha(data) != PINS[name]:
            raise RuntimeError('Refusing unrecognized OEM binary: ' + name)
    result = dict(status='OFFLINE_OEM_RF_API_BOUNDARY_AUDIT', source_sha256=PINS,
        send_checks=send_checks(images), get_a0_check=get_check(images),
        oem_radio_prepare=oem_prepare_check(images), open_path_checks=open_checks(images),
        device_access=False, compilation=False, host_dll_loaded=False,
        physical_cause_established=False,
        boundaries=['all OS calls supplied', 'CRT allocation/copy/zero and cookie checks supplied',
            'successful ready ACK supplied, not measured', 'OEM radio model flags supplied',
            'open metadata helper1690 separately inspected, symbol initialization supplied'])
    for name, data in images.items():
        assert (ROOT / 'evidence/original' / name).read_bytes() == data
    directory = ROOT / 'evidence/rf-host-framing'
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ') + '-' + uuid.uuid4().hex[:8] + '.json')
    target.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(dict(status=result['status'], evidence=str(target),
        padded_prepare_bytes=134, logical_prepare_bytes=8, get_a0_bytes=8,
        open_has_no_feature_writes=True, device_access=False, compilation=False), indent=2))


if __name__ == '__main__':
    main()
