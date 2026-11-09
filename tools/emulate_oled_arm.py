"""Compile and exercise the REAL Thumb-1 OLED extension, offline only.

Writes only .o/.text/layout build artifacts under bin/. Does NOT generate or
modify any firmware resource/updater, access USB, or run physical hardware.
ROM zero-fill/memcpy/switch, wake, flash-profile restore and I2C hardware are
intercepted. CRC, validation, local packet snapshot and native OLED draw-block
instructions execute as ARM/Thumb code. Appended code uses no new globals.
"""

from pathlib import Path
import hashlib
import json
import struct
import subprocess
from unicorn.arm_const import UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_SP
from emulate_oled_protocol import StockInstructions, packet, crc16, REPORT, LENGTH
from emulate_screen_key import thumb_bl

ROOT = Path(__file__).resolve().parents[1]
APPEND = 0x47400
BASE = 0x28790
SOURCE_HASH = 'f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc'


def elf_text_and_symbols(data):
    assert data[:6] == b'\x7fELF\x01\x01', 'Expected little-endian ELF32'
    assert struct.unpack_from('<H', data, 18)[0] == 40, 'Expected EM_ARM'
    section_offset = struct.unpack_from('<I', data, 32)[0]
    section_size, count, names_index = struct.unpack_from('<HHH', data, 46)
    sections = [struct.unpack_from('<10I', data, section_offset+i*section_size) for i in range(count)]
    names_section = sections[names_index]
    names = data[names_section[4]:names_section[4]+names_section[5]]

    def string(table, offset):
        return table[offset:table.index(b'\0', offset)].decode('ascii')

    named = {string(names, item[0]): item for item in sections}
    assert not any(item[1] in (4, 9) and item[5] for item in sections), 'Unresolved ELF relocations'
    text = named['.text']
    symbols = {}
    symtab = named['.symtab']
    strtab = sections[symtab[6]]
    strings = data[strtab[4]:strtab[4]+strtab[5]]
    for offset in range(symtab[4], symtab[4]+symtab[5], symtab[9]):
        name, value, size, info, _other, index = struct.unpack_from('<IIIBBH', data, offset)
        if name and (info & 15) == 2 and index not in (0, 0xFFF1):
            symbols[string(strings, name)] = {'offset': value & ~1, 'size': size}
    return data[text[4]:text[4]+text[5]], symbols


def build():
    (ROOT/'bin').mkdir(exist_ok=True)
    output = ROOT/'bin/g9_oled.o'
    subprocess.run(['clang', '--target=arm-none-eabi', '-march=armv5t', '-mthumb',
                    '-c', str(ROOT/'firmware/g9_oled.S'), '-o', str(output)], check=True)
    code, symbols = elf_text_and_symbols(output.read_bytes())
    assert len(code) <= 36*32, 'Extension must fit existing updater erase/write round size'
    original = (ROOT/'evidence/g9pro-official-firmware-resource.bin').read_bytes()
    assert hashlib.sha256(original).hexdigest() == SOURCE_HASH
    image = (ROOT/'evidence/g9pro-firmware-normalized.bin').read_bytes()
    expected = {0x33C94: bytes.fromhex('11704078'),
                0x33CD4: thumb_bl(0x33CD4, 0x34AF8)}
    hooks = []
    for address, symbol in ((0x33C94, 'g9_oled_version_hook'), (0x33CD4, 'g9_oled_set_hook')):
        before = image[address-BASE:address-BASE+4]
        assert before == expected[address], (hex(address), before.hex(), expected[address].hex())
        target = APPEND + symbols[symbol]['offset']
        hooks.append({'address': address, 'normalized_offset': address-BASE,
                      'old_hex': before.hex(), 'new_hex': thumb_bl(address, target).hex(),
                      'target': target, 'symbol': symbol})
    blocks = (len(code)+31)//32
    layout = {'status': 'OFFLINE_BUILD_ONLY_NO_FIRMWARE_RESOURCE_GENERATED',
              'base': BASE, 'append_address': APPEND, 'append_normalized_offset': APPEND-BASE,
              'original_crc_blocks': 3939, 'extension_size': len(code),
              'extension_crc_blocks': blocks, 'total_crc_blocks': 3939+blocks,
              'minimum_resource_size': 16+(3939+blocks)*34,
              'original_updater_rounded_write_size': 0x21000,
              'text_sha256': hashlib.sha256(code).hexdigest(), 'hooks': hooks, 'symbols': symbols,
              'version_report_marker': 'OL2', 'requires_wired_pid': '2148',
              'protocol': 'GO,version2,WRITE_PAGE-only,no ownership,no sequence state'}
    assert (layout['minimum_resource_size']+4095)&~4095 == 0x21000
    (ROOT/'bin/g9_oled.text.bin').write_bytes(code)
    (ROOT/'bin/g9_oled-layout.json').write_text(json.dumps(layout, indent=2), encoding='utf-8')
    return code, layout


def page_packet(page, payload, flags=0):
    data = bytearray(packet(1, 0, page, payload))
    data[4] = 2
    data[9] = flags
    data[130:] = struct.pack('<H', crc16(data[:130]))
    return bytes(data)


class ArmFixture(StockInstructions):
    def __init__(self, code, layout, patched=True):
        self.wake_calls = 0
        self.init_calls = 0
        self.draw_calls = 0
        self.extension_steps = 0
        self.minimum_sp = 0x508000
        super().__init__()
        self.uc.mem_write(APPEND, code)
        if patched:
            for hook in layout['hooks']:
                self.uc.mem_write(hook['address'], bytes.fromhex(hook['new_hex']))

    def hook(self, uc, address, size, context):
        if APPEND <= address < APPEND+1152:
            self.extension_steps += 1
            self.minimum_sp = min(self.minimum_sp, uc.reg_read(UC_ARM_REG_SP))
        if address == 0x24F9C:
            destination, source, count = [uc.reg_read(r) for r in (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2)]
            uc.mem_write(destination, bytes(uc.mem_read(source, count)))
            self.ret()
        elif address == 0x34AF8:
            self.wake_calls += 1
            self.ret()
        elif address == 0x3CDDE:
            self.init_calls += 1
            self.ret()
        elif address == 0x30B78:
            self.ret()  # Restore of the stock active profile would access flash.
        else:
            if address == 0x3CEE6:
                self.draw_calls += 1
            super().hook(uc, address, size, context)

    def version(self):
        self.uc.mem_write(0x400F8A, b'\x0b')
        self.uc.mem_write(0x400F8C, struct.pack('<I', 0x400F94))
        self.uc.mem_write(0x400F91, bytes.fromhex('0b0501')+bytes(132))
        self.run(0x33B84)
        return bytes(self.uc.mem_read(0x400F91, 8))

    def set_packet(self, data, report=REPORT, transport_length=None, payload_pointer=0x400F93):
        self.commands.clear()
        self.bytes_on_bus.clear()
        self.uc.mem_write(0x400F8A, bytes([report, transport_length if transport_length is not None else len(data)]))
        self.uc.mem_write(0x400F8C, struct.pack('<I', payload_pointer))
        self.uc.mem_write(0x400F91, bytes(data).ljust(132, b'\0'))
        self.uc.mem_write(0x507E80, bytes([0xC3])*64)
        self.run(0x33CD2)
        assert bytes(self.uc.mem_read(0x507E80, 64)) == bytes([0xC3])*64, 'Stack canary damaged'


def run_regressions(code, layout, candidate_image=None):
    original = ArmFixture(code, layout, patched=False)
    assert original.version() == bytes.fromhex('0b05010108000000')
    fixture = ArmFixture(code, layout)
    if candidate_image is not None:
        fixture.uc.mem_write(BASE, candidate_image)
    assert fixture.version() == bytes.fromhex('0b050101084f4c32')
    print('PASS: real original GET 0B -> stock zero padding; hooked GET 0B -> OL2 marker')
    for page in range(4):
        payload = bytes((x * 7 + page) & (0xF if page == 3 else 0xFF) for x in range(120))
        before = fixture.draw_calls
        fixture.set_packet(page_packet(page, payload, flags=int(page == 0)))
        assert fixture.draw_calls == before+1, (page, fixture.draw_calls, before)
        assert fixture.commands == [0xB0+page, 0x10, 0]
        assert fixture.bytes_on_bus == [0x78, 0x40]+list(payload)
    assert fixture.init_calls == 1
    print('PASS: compiled Thumb-1 handler validates/snapshots 4 pages and executes native I2C draw code')
    good = page_packet(0, bytes(120))
    invalid = []
    for offset, value in ((0, 0x23), (1, 131), (2, 0), (3, 0), (4, 1), (5, 2),
                          (6, 1), (7, 4), (8, 119), (9, 2)):
        item = bytearray(good)
        item[offset] = value
        item[130:] = struct.pack('<H', crc16(item[:130]))
        invalid.append(bytes(item))
    item = bytearray(good)
    item[50] ^= 1
    invalid.append(bytes(item))
    invalid.append(page_packet(3, bytes([0xF0])*120))
    for data in invalid:
        counts = fixture.draw_calls, fixture.init_calls, fixture.wake_calls
        fixture.set_packet(data)
        assert counts == (fixture.draw_calls, fixture.init_calls, fixture.wake_calls), data[:10].hex()
        assert not fixture.commands and not fixture.bytes_on_bus
    for length, pointer in ((131, 0x400F93), (133, 0x400F93), (132, 0x410000)):
        counts = fixture.draw_calls, fixture.init_calls, fixture.wake_calls
        fixture.set_packet(good, transport_length=length, payload_pointer=pointer)
        assert counts == (fixture.draw_calls, fixture.init_calls, fixture.wake_calls)
    before = fixture.wake_calls
    fixture.set_packet(bytes(132), report=0x2E)
    assert fixture.wake_calls == before+1 and not fixture.commands
    assert fixture.extension_steps > 1000
    print('PASS: real ARM rejects header/version/opcode/reserved/page/count/flags/CRC/pixels/transport/pointer errors')
    print('PASS: non-22 SET tail-calls stock wake; no new global state; stack canary intact')
    print(f'ARM extension {len(code)} bytes, {layout["extension_crc_blocks"]} framed blocks; same 0x21000 erase/write round size')
    print(f'Minimum extension SP observed: {fixture.minimum_sp:#x}; entry SP=0x508000')
    print('OFFLINE ONLY: no USB or physical display validation')
    return {'real_arm_tests_passed': True, 'rejected_cases': len(invalid)+3,
            'minimum_extension_sp': fixture.minimum_sp, 'extension_steps': fixture.extension_steps,
            'candidate_image_executed': candidate_image is not None}


def main():
    code, layout = build()
    run_regressions(code, layout)


if __name__ == '__main__':
    main()
