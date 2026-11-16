"""Original x86 resource preparation in Unicorn; all OS/CRT calls are stubs.

Proves the old rounded memcpy overread and the exact OL5 bounded-copy patch.
No Windows PE entrypoint, updater UI, HID or physical device is executed.
"""
from pathlib import Path
import argparse
import json
import struct
import pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_ESP, UC_X86_REG_EIP, UC_X86_REG_ECX, UC_X86_REG_EAX
from ol5_updater_package import patch_host, sha, SOURCE_HASH, RESOURCE_HASH, RESOURCE_BYTES, resources
from emulate_updater_transfer import binary, ROOT, u32, put, args, ret

BASE, CTX, DEST, SOURCE, STACK, STOP, STUB = 0x400000, 0x20000000, 0x30000000, 0x31000000, 0x70000000, 0x71000000, 0x71001000


def run_prepare(executable, resource):
    pe = pefile.PE(data=executable)
    u = Uc(UC_ARCH_X86, UC_MODE_32)
    u.mem_map(BASE, (pe.OPTIONAL_HEADER.SizeOfImage + 4095) & ~4095)
    u.mem_write(BASE, pe.get_memory_mapped_image())
    u.mem_map(CTX, 0x10000)
    u.mem_map(DEST, 0x402000)
    rounded = (len(resource) + 4095) & ~4095
    u.mem_map(SOURCE, rounded + 4096)
    u.mem_write(SOURCE, resource + b'\xa6' * (rounded + 4096 - len(resource)))
    u.mem_map(STACK, 0x20000)
    u.mem_map(STOP, 0x10000)
    imports = {}
    for dll in pe.DIRECTORY_ENTRY_IMPORT:
        for imp in dll.imports:
            address = STUB + len(imports) * 16
            imports[address] = (imp.name or b'').decode()
            put(u, imp.address, address)
    top = STACK + 0x1F000
    put(u, top, STOP)
    u.reg_write(UC_X86_REG_ESP, top)
    u.reg_write(UC_X86_REG_ECX, CTX)
    copies, allocations = [], []

    def hook(u, address, size, _):
        if address == STOP:
            u.emu_stop(); return
        if address == 0x405B3E:
            count = args(u, 1)[0]
            assert count == 0x400000
            allocations.append(count); ret(u, DEST); return
        if address == 0x54FDA0:
            dest, value, count = args(u, 3)
            assert dest == DEST and count == 0x400000 and value == 255
            u.mem_write(dest, bytes([value]) * count); ret(u, dest); return
        if address == 0x54F820:
            dest, src, count = args(u, 3)
            assert dest == DEST and src == SOURCE and count in (len(resource), rounded)
            copies.append(count)
            u.mem_write(dest, bytes(u.mem_read(src, count))); ret(u, dest); return
        if address in imports:
            name = imports[address]
            if name in ('GetModuleHandleW', 'GetModuleHandleA'):
                assert args(u, 1) == [0]; ret(u, BASE, 4); return
            if name in ('FindResourceW', 'FindResourceA'):
                module, identifier, kind = args(u, 3)
                assert module == BASE and identifier == 129
                ret(u, CTX + 0x8000, 12); return
            if name == 'LoadResource':
                assert args(u, 2) == [BASE, CTX + 0x8000]
                ret(u, CTX + 0x9000, 8); return
            if name == 'SizeofResource':
                assert args(u, 2) == [BASE, CTX + 0x8000]
                ret(u, len(resource), 8); return
            if name == 'LockResource':
                assert args(u, 1) == [CTX + 0x9000]
                ret(u, SOURCE, 4); return
            raise AssertionError('Unexpected API: ' + name)
        assert 0x404C90 <= address <= 0x404E58, hex(address)

    u.hook_add(UC_HOOK_CODE, hook)
    u.emu_start(0x404C90, STOP + 1, count=2000000)
    assert u.reg_read(UC_X86_REG_EIP) == STOP
    assert u.reg_read(UC_X86_REG_EAX) == 1 and u.reg_read(UC_X86_REG_ESP) == top + 4
    assert allocations == [0x400000] and len(copies) == 1
    assert u32(u, CTX + 0xD8) == 0x2B00A and u32(u, CTX + 0xDC) == rounded
    prepared = bytes(u.mem_read(DEST, rounded))
    assert prepared[:len(resource)] == resource
    return prepared, dict(resource_bytes=len(resource), transfer_bytes=rounded,
                          memcpy_bytes=copies[0], overread_bytes=copies[0] - len(resource),
                          padding_ff=prepared[len(resource):] == b'\xff' * (rounded - len(resource)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('resource', type=Path)
    parser.add_argument('--package', type=Path)
    options = parser.parse_args()
    resource = options.resource.read_bytes()
    assert len(resource) == RESOURCE_BYTES and sha(resource) == RESOURCE_HASH
    original_buffer, original = run_prepare(binary, resource)
    assert original['overread_bytes'] == 3248 and not original['padding_ff']
    image = options.package.read_bytes() if options.package else patch_host(binary)
    if options.package:
        from ol5_updater_package import verify
        verify(binary, image, resource)
        assert resources(image)[('BIN', 129, 2052)] == resource
    prepared, corrected = run_prepare(image, resource)
    assert corrected['overread_bytes'] == 0 and corrected['padding_ff']
    # Full transfer replay uses this actual prepared buffer and the same image.
    from emulate_updater_transfer import run, transfer_bounds
    result = run('normal', len(prepared), image_bytes=image, payload_data=prepared)
    bounds = transfer_bounds(result, len(prepared), bounded_erases=True)
    print(json.dumps(dict(offline_only=True, original=original, corrected=corrected,
                          transfer=bounds, prepared_sha256=sha(prepared),
                          actual_package=bool(options.package), device_access=False), indent=2))


if __name__ == '__main__':
    main()
