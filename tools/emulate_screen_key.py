"""Offline reproduction of the OLED button's ordinary-key cancellation.

Runs original Thumb handlers for gesture recognition, button dispatch, ordinary
key state progression and USB report construction. GPIO/debounce transitions
and main-loop calls are supplied explicitly, not measured or emulated as real
time. ROM switch/memcpy/memset and OLED/USB hardware calls are stubbed. An IN
endpoint is assumed ready before every output attempt. This is not a complete
mouse emulator or physical USB validation.

The optional candidates change CODE IN EMULATED MEMORY ONLY. The first skips
the OLED gesture for opcode 0x11 using an appended wrapper. The second replaces
the single instruction that arms the OLED hold gesture with a Thumb-1 NOP. The
second candidate disables that gesture for all assignments and needs no added
code space. No modified firmware file is produced.
"""

from pathlib import Path
import struct
from unicorn import Uc, UC_ARCH_ARM, UC_MODE_THUMB, UC_HOOK_CODE
from unicorn.arm_const import (
    UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3,
    UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7,
    UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC,
)

ROOT = Path(__file__).resolve().parents[1]
IMAGE_BASE = 0x28790
STOP = 0x600000
KEY_TABLE = 0x401076
GESTURE = 0x400AF1
OLED_OFF = 0x400AF2
DEBOUNCE_READY = 0x400AFC
NEW_GPIO_EVENT = 0x400AFF
RAW_KEY_MASK = 0x400AE0
COMBO_STATE = 0x400B2B
KEYBOARD_DIRTY = 0x400AF6


def thumb_bl(source, target):
    delta = target - (source + 4)
    assert delta % 2 == 0 and -(1 << 22) <= delta < (1 << 22)
    return struct.pack('<HH', 0xF000 | ((delta >> 12) & 0x7FF),
                       0xF800 | ((delta >> 1) & 0x7FF))


class Fixture:
    def __init__(self, index, patched=False):
        data = (ROOT / 'evidence/g9pro-firmware-normalized.bin').read_bytes()
        self.uc = Uc(UC_ARCH_ARM, UC_MODE_THUMB)
        self.uc.mem_map(0x20000, 0x30000)
        self.uc.mem_write(IMAGE_BASE, data)
        self.uc.mem_map(0x400000, 0x20000)
        self.uc.mem_map(0x500000, 0x10000)
        self.uc.mem_map(STOP, 0x1000)
        self.reports = []
        self.oled_calls = []
        self.sensor_calls = []
        self.trace = []
        self.label = ''
        self.set8(0x400A79, 1)  # Active mouse, required for gesture recognition.
        # Right button, middle button and the OLED/DPI button's defaults.
        self.uc.mem_write(KEY_TABLE + 3, bytes.fromhex('030000'))
        self.uc.mem_write(KEY_TABLE + 6, bytes.fromhex('040000'))
        self.uc.mem_write(KEY_TABLE + 9, bytes.fromhex('0d0000'))
        if index is not None:
            self.uc.mem_write(KEY_TABLE + 3 * index, bytes.fromhex('110027'))
        if patched == 'disable-oled-hold':
            self.patch_disable_oled_hold_in_memory()
        elif patched:
            self.patch_gesture_policy_in_memory()
        self.uc.hook_add(UC_HOOK_CODE, self.hook)

    def patch_gesture_policy_in_memory(self):
        # Replace CMP r0,#8 / BEQ original_arm with a Thumb BL wrapper. The
        # surrounding function saved LR in its prologue. For other raw masks
        # the wrapper returns to 0x32006, preserving the original fallback.
        site, extension = 0x32002, 0x48000
        assert bytes(self.uc.mem_read(site, 4)) == bytes.fromhex('082806d0')
        wrapper = bytes.fromhex(
            '0828'  # cmp r0,#8
            '07d1'  # bne return_to_original_fallback
            '044b'  # ldr r3,=button3_opcode
            '1b78'  # ldrb r3,[r3]
            '112b'  # cmp r3,#0x11
            '01d0'  # beq skip_gesture_arm
            '044b'  # ldr r3,=original_arm|1
            '1847'  # bx r3
            '024b'  # skip: ldr r3,=post_gesture_arm|1
            '1847'  # bx r3
            '7047'  # return: bx lr
            'c046'  # alignment padding
        ) + struct.pack('<III', KEY_TABLE + 9, 0x3204B, 0x32015)
        self.uc.mem_write(extension, wrapper)
        self.uc.mem_write(site, thumb_bl(site, extension))

    def patch_disable_oled_hold_in_memory(self):
        # Original: STRB r5,[r1], where r1=&gesture and r5=1. MOV r8,r8 is
        # the Thumb-1 NOP encoding, appropriate to this ARM968/Thumb image.
        # Keep the original instruction length and every branch/literal address.
        assert bytes(self.uc.mem_read(0x3201C, 2)) == bytes.fromhex('0d70')
        self.uc.mem_write(0x3201C, bytes.fromhex('c046'))

    def set8(self, address, value):
        self.uc.mem_write(address, bytes([value]))

    def get8(self, address):
        return bytes(self.uc.mem_read(address, 1))[0]

    def hook(self, uc, address, _size, _context):
        if address == 0x25078:  # ROM byte-switch table helper.
            table_address = uc.reg_read(UC_ARM_REG_LR) & ~1
            index = uc.reg_read(UC_ARM_REG_R3)
            table = bytes(uc.mem_read(table_address, 100))
            assert 0 <= index < table[0]
            uc.reg_write(UC_ARM_REG_PC, table_address + 2 * table[index + 1] | 1)
        elif address in (0x24F9C, 0x24FEC):  # ROM memcpy / memset.
            destination, source_or_fill, count = [
                uc.reg_read(r) for r in (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2)
            ]
            payload = (bytes(uc.mem_read(source_or_fill, count))
                       if address == 0x24F9C else bytes([source_or_fill & 255]) * count)
            uc.mem_write(destination, payload)
            uc.reg_write(UC_ARM_REG_PC, uc.reg_read(UC_ARM_REG_LR))
        elif address == 0x31A0C:
            # Applying the selected DPI stage to sensor/SPI hardware. The
            # firmware's preceding stage-selection instructions still execute.
            self.sensor_calls.append(address)
            uc.reg_write(UC_ARM_REG_PC, uc.reg_read(UC_ARM_REG_LR))
        elif address in (0x3DABC, 0x3DAFA, 0x3CDDE, 0x3D51E, 0x3D328, 0x3BC66):
            # OLED hardware/rendering and diagnostic printing only.
            self.oled_calls.append(address)
            uc.reg_write(UC_ARM_REG_PC, uc.reg_read(UC_ARM_REG_LR))
        elif address == 0x376AA:  # Actual USB transport intercepted before MMIO.
            endpoint, pointer, count = [
                uc.reg_read(r) for r in (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2)
            ]
            report = bytes(uc.mem_read(pointer, count))
            self.reports.append((endpoint, report))
            self.trace.append(f'{self.label}: EP{endpoint} {report.hex()}')
            uc.emu_stop()

    def run(self, address):
        self.uc.reg_write(UC_ARM_REG_SP, 0x508000)
        self.uc.reg_write(UC_ARM_REG_LR, STOP | 1)
        previous = len(self.reports)
        self.uc.emu_start(address | 1, STOP, count=20000)
        assert self.uc.reg_read(UC_ARM_REG_PC) == STOP or len(self.reports) > previous

    def cycle(self, label):
        self.label = label
        self.run(0x31EB6)  # OLED/function gesture plus debounced edge translation.
        self.run(0x32AA4)  # Process button-table press/release events.
        self.run(0x32B30)  # Ordinary-key state machine.
        self.trace.append(f'{label}: gesture={self.get8(GESTURE)} combo={self.get8(COMBO_STATE)}')
        if self.get8(KEYBOARD_DIRTY):
            self.set8(0x400B22, 1)  # Endpoint ready: explicit fixture assumption.
            self.uc.reg_write(UC_ARM_REG_R7, 0x400B22)
            self.uc.reg_write(UC_ARM_REG_R6, 0x400B23)
            self.uc.reg_write(UC_ARM_REG_R5, 0)
            self.run(0x38C4E)

    def stable_edge(self, mask, label):
        self.set8(DEBOUNCE_READY, 1)
        self.set8(NEW_GPIO_EVENT, 1)
        self.uc.mem_write(RAW_KEY_MASK, struct.pack('<H', mask))
        self.cycle(label)

    def release_starts(self):
        # The sampler has seen a physical change and cleared its stability flag,
        # but its new debounced mask/event has not been published yet.
        self.set8(DEBOUNCE_READY, 0)
        self.cycle('physical release before debounce')


def tap(index, patched=False, chord=False, extra_release_cycles=0, oled_off=0):
    fixture = Fixture(index, patched)
    fixture.set8(OLED_OFF, oled_off)
    held_mask = 2 if chord else 0
    if chord:
        fixture.stable_edge(held_mask, 'right held')
    fixture.stable_edge(held_mask | (1 << index), 'target pressed')
    fixture.cycle('target held')
    fixture.release_starts()
    for _ in range(extra_release_cycles):
        fixture.cycle('additional cycle before release debounce completes')
    fixture.stable_edge(held_mask, 'target release debounced')
    return fixture


def dpi_click_without_oled_hold(oled_off):
    fixture = Fixture(None, 'disable-oled-hold')
    fixture.set8(OLED_OFF, oled_off)
    fixture.set8(0x400A8F, 1)  # Sensor connected/active.
    fixture.set8(0x40103C, 0x3F)  # Enabled DPI stages 1..6.
    fixture.set8(0x40104F, 1)  # Current DPI stage.
    fixture.stable_edge(8, 'DPI pressed with gesture disabled')
    assert fixture.get8(GESTURE) == 0
    assert fixture.get8(0x40104F) == 2, 'DPI click must still select the next stage'
    fixture.cycle('DPI held')
    fixture.set8(0x400B35, 0)
    fixture.cycle('DPI hold timer expired with gesture disabled')
    assert fixture.get8(GESTURE) == 0
    assert fixture.get8(0x40104F) == 2, 'Holding must not cause a second DPI click'
    assert 0x3DAFA not in fixture.oled_calls, 'Disabled hold must not turn OLED off'
    assert fixture.sensor_calls == [0x31A0C]
    fixture.release_starts()
    fixture.stable_edge(0, 'DPI release debounced with gesture disabled')
    assert fixture.get8(0x40104F) == 2
    assert not fixture.reports
    return fixture


def long_dpi_hold(patched):
    fixture = Fixture(None, patched)
    fixture.stable_edge(8, 'DPI pressed')
    assert fixture.get8(GESTURE) == 1
    fixture.set8(0x400B35, 0)  # Advance the external hold timer to expiration.
    fixture.cycle('DPI hold timer expired')
    assert fixture.get8(OLED_OFF) == 1
    assert 0x3DABC in fixture.oled_calls and 0x3DAFA in fixture.oled_calls
    fixture.release_starts()
    fixture.stable_edge(0, 'DPI release debounced')
    assert not fixture.reports
    return fixture


def main():
    empty = (1, bytes(8))
    zero_down = (1, bytes.fromhex('0000270000000000'))
    expected_key = [empty, zero_down, empty]
    cases = [
        ('original middle ordinary 0', tap(2), expected_key),
        ('original screen ordinary 0', tap(3), [empty, empty]),
        ('original screen ordinary 0 with right held', tap(3, chord=True), expected_key),
        # Shows why extending the release debounce window MAY avoid cancellation;
        # the relation between milliseconds and this extra task cycle requires
        # a physical test and is not established by this fixture.
        ('original screen with one additional release-window cycle',
         tap(3, extra_release_cycles=1), expected_key),
        ('candidate screen ordinary 0', tap(3, patched=True), expected_key),
        ('candidate middle ordinary 0', tap(2, patched=True), expected_key),
        ('candidate screen ordinary 0 with right held', tap(3, patched=True, chord=True), expected_key),
        ('two-byte NOP screen ordinary 0 / OLED on',
         tap(3, patched='disable-oled-hold', oled_off=0), expected_key),
        ('two-byte NOP screen ordinary 0 / OLED off',
         tap(3, patched='disable-oled-hold', oled_off=1), expected_key),
        ('two-byte NOP middle ordinary 0',
         tap(2, patched='disable-oled-hold'), expected_key),
        ('two-byte NOP screen ordinary 0 with right held',
         tap(3, patched='disable-oled-hold', chord=True), expected_key),
    ]
    for label, fixture, expected in cases:
        print(label)
        for line in fixture.trace:
            print(' ', line)
        assert fixture.reports == expected, (label, fixture.reports)
        print(' PASS')
    original_hold = long_dpi_hold(False)
    candidate_hold = long_dpi_hold(True)
    assert original_hold.oled_calls == candidate_hold.oled_calls
    print('PASS: original DPI long-hold OLED toggle preserved by in-memory candidate')
    dpi_click_without_oled_hold(0)
    dpi_click_without_oled_hold(1)
    print('PASS: two-byte NOP preserves DPI click with OLED on/off; disables OLED long-hold toggle')
    print('OFFLINE ONLY: GPIO timing supplied; ROM/OLED/USB stubbed; endpoint ready assumed')
    print('No firmware/configuration files or physical devices modified')


if __name__ == '__main__':
    main()
