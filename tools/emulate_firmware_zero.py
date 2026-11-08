"""Offline check of the official G9 PRO firmware's ordinary-key path.

This runs selected Thumb instructions, not a complete mouse simulation. It has
no USB/device access and never writes firmware or settings to physical hardware.

Explicit assumptions/stubs:
* RAM starts cleared; button-table entry 3 is set to 11 00 27.
* The ROM byte-switch helper and ROM memcpy are replaced with Python equivalents.
* USB endpoint 1 is assumed ready before each output attempt. The hardware-send
  routine is intercepted to record its arguments, and execution stops there.
* GPIO scanning, interrupts, timers, boot and the USB host are not emulated.

Passing proves that the selected firmware instructions interpret this triplet
as a normal USB keyboard 0 under these assumptions. It does not prove that the
physical mouse runs this image, enters this path, or emits a real USB report.
"""

from pathlib import Path
from unicorn import Uc, UC_ARCH_ARM, UC_MODE_THUMB, UC_HOOK_CODE
from unicorn.arm_const import (
    UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3,
    UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7,
    UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC,
)


ROOT = Path(__file__).resolve().parents[1]
IMAGE_PATH = ROOT / 'evidence/g9pro-firmware-normalized.bin'
IMAGE_BASE = 0x28790
KEY_DISPATCH = 0x3236C
COMBO_STATE_MACHINE = 0x32B30
USB_KEYBOARD_FRAGMENT = 0x38C4E
ROM_BYTE_SWITCH = 0x25078
ROM_MEMCPY = 0x24F9C
HARDWARE_SEND = 0x376AA
STOP = 0x600000
BUTTON_INDEX = 3
KEY_TABLE = 0x401076
COMBO_STATE = 0x400B2B
COMBO_PARAMETERS = 0x400B9A
ENDPOINT_READY = 0x400B22


def main():
    data = IMAGE_PATH.read_bytes()
    # Check the address-base derivation against the device descriptor and the
    # descriptor-table pointer. This catches accidentally using the older base.
    assert data[0x449C2 - IMAGE_BASE:0x449C2 - IMAGE_BASE + 2] == b'\x12\x01'
    assert int.from_bytes(data[0x44A41 - IMAGE_BASE:0x44A41 - IMAGE_BASE + 4], 'little') == 0x449C2

    emulator = Uc(UC_ARCH_ARM, UC_MODE_THUMB)
    emulator.mem_map(0x20000, 0x30000)  # Image plus addresses of stubbed ROM calls.
    emulator.mem_write(IMAGE_BASE, data)
    emulator.mem_map(0x400000, 0x20000)  # Cleared RAM.
    emulator.mem_map(0x500000, 0x10000)  # Stack.
    emulator.mem_map(STOP, 0x1000)
    emulator.mem_write(KEY_TABLE + 3 * BUTTON_INDEX, bytes.fromhex('110027'))
    reports = []
    halted = []

    def return_from_stub():
        emulator.reg_write(UC_ARM_REG_PC, emulator.reg_read(UC_ARM_REG_LR))

    def instruction_hook(uc, address, _size, _context):
        if address == ROM_BYTE_SWITCH:
            table_address = uc.reg_read(UC_ARM_REG_LR) & ~1
            index = uc.reg_read(UC_ARM_REG_R3)
            table = bytes(uc.mem_read(table_address, 100))
            assert 0 <= index < table[0], 'Fixture must select a real switch case'
            target = table_address + 2 * table[index + 1]
            uc.reg_write(UC_ARM_REG_PC, target | 1)
        elif address == ROM_MEMCPY:
            destination, source, count = [
                uc.reg_read(register)
                for register in (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2)
            ]
            uc.mem_write(destination, bytes(uc.mem_read(source, count)))
            return_from_stub()
        elif address == HARDWARE_SEND:
            endpoint, pointer, count = [
                uc.reg_read(register)
                for register in (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2)
            ]
            reports.append((endpoint, bytes(uc.mem_read(pointer, count))))
            halted.append('hardware send intercepted')
            uc.emu_stop()
        elif address == STOP:
            halted.append('function returned')
            uc.emu_stop()

    emulator.hook_add(UC_HOOK_CODE, instruction_hook)

    def run(address):
        halted.clear()
        emulator.reg_write(UC_ARM_REG_SP, 0x508000)
        emulator.reg_write(UC_ARM_REG_LR, STOP | 1)
        emulator.emu_start(address | 1, STOP, count=10000)
        # Unicorn can stop at the explicit end address before invoking its hook.
        assert halted or emulator.reg_read(UC_ARM_REG_PC) == STOP, 'Instruction budget exhausted'

    def attempt_keyboard_output():
        # These are the values the surrounding USB handler provides. Entering
        # this fragment skips USB state/interrupt checks, explicitly assuming
        # the configured endpoint can accept an IN report.
        emulator.mem_write(ENDPOINT_READY, b'\x01')
        emulator.reg_write(UC_ARM_REG_R7, ENDPOINT_READY)
        emulator.reg_write(UC_ARM_REG_R6, 0x400B23)
        emulator.reg_write(UC_ARM_REG_R5, 0)
        run(USB_KEYBOARD_FRAGMENT)

    emulator.reg_write(UC_ARM_REG_R0, BUTTON_INDEX)
    run(KEY_DISPATCH)
    assert bytes(emulator.mem_read(COMBO_STATE, 1)) == b'\x01'
    assert bytes(emulator.mem_read(COMBO_PARAMETERS, 2)) == b'\x00\x27'
    run(COMBO_STATE_MACHINE)
    attempt_keyboard_output()
    run(COMBO_STATE_MACHINE)
    attempt_keyboard_output()

    expected = [
        (1, bytes.fromhex('0000000000000000')),
        (1, bytes.fromhex('0000270000000000')),
    ]
    for endpoint, report in reports:
        print(f'EP{endpoint} report={report.hex()}')
    assert reports == expected, f'Unexpected generated reports: {reports!r}'
    print('PASS: firmware triplet 11 00 27 at index 3 generates ordinary keyboard 0')
    print('OFFLINE ONLY: ROM/hardware stubs; endpoint ready assumed; no physical USB validation')


if __name__ == '__main__':
    main()
