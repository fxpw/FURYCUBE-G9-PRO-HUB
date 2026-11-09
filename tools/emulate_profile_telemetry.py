"""Run stock Thumb profile/DPI formatters and USB report assembly offline.

No USB, serial, process or physical mouse access. The unmodified local firmware
fixture is required; it is not distributed. ROM memcpy is stubbed. RAM starts
cleared, RF mode is explicitly wired, and USB endpoint readiness is supplied.
Hardware send is intercepted. GPIO, task scheduling, interrupts and transport
delivery are not emulated. These checks prove byte construction and shared-slot
overwrite, not physical notification delivery or complete change coverage.
"""

from pathlib import Path

from unicorn import Uc, UC_ARCH_ARM, UC_MODE_THUMB, UC_HOOK_CODE
from unicorn.arm_const import (
    UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R4,
    UC_ARM_REG_R5, UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC,
)


ROOT = Path(__file__).resolve().parents[1]
BASE = 0x28790
STOP = 0x600000
PROFILE_FORMATTER = 0x31A5A
DPI_FORMATTER = 0x31A0C
USB_FRAGMENT = 0x38CB6
PROFILE = 0x40102C
DPI_STAGE = 0x40104F
PENDING_BUFFER = 0x400B4F
PENDING_FLAG = 0x400AFA
ENDPOINT_READY = 0x400B24
USB_BUFFER = 0x400F0F


class Fixture:
    def __init__(self):
        image = (ROOT / 'evidence/g9pro-firmware-normalized.bin').read_bytes()
        assert image[PROFILE_FORMATTER-BASE:PROFILE_FORMATTER-BASE+4] == bytes.fromhex('294810b5')
        assert image[0x38D24-BASE:0x38D24-BASE+4] == bytes.fromhex('11780320')
        self.uc = Uc(UC_ARCH_ARM, UC_MODE_THUMB)
        self.uc.mem_map(0x20000, 0x30000)
        self.uc.mem_write(BASE, image)
        self.uc.mem_map(0x400000, 0x20000)
        self.uc.mem_map(0x500000, 0x10000)
        self.uc.mem_map(STOP, 0x1000)
        self.reports = []
        self.uc.hook_add(UC_HOOK_CODE, self.hook)

    def hook(self, uc, address, _size, _context):
        if address == 0x24F9C:
            destination, source, count = [uc.reg_read(reg) for reg in
                                           (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2)]
            uc.mem_write(destination, bytes(uc.mem_read(source, count)))
            uc.reg_write(UC_ARM_REG_PC, uc.reg_read(UC_ARM_REG_LR))
        elif address == 0x376AA:
            endpoint, pointer, count = [uc.reg_read(reg) for reg in
                                       (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2)]
            self.reports.append((endpoint, bytes(uc.mem_read(pointer, count))))
            uc.emu_stop()
        elif address == STOP:
            uc.emu_stop()

    def run(self, address):
        self.uc.reg_write(UC_ARM_REG_SP, 0x508000)
        self.uc.reg_write(UC_ARM_REG_LR, STOP | 1)
        self.uc.emu_start(address | 1, STOP, count=1000)
        assert self.uc.reg_read(UC_ARM_REG_PC) in (STOP, 0x376AA), 'Instruction budget exhausted'

    def profile(self, index):
        self.uc.mem_write(PROFILE, bytes([index]))
        self.run(PROFILE_FORMATTER)
        assert bytes(self.uc.mem_read(PENDING_BUFFER, 4)) == bytes([0xC9, 0x80, index, 0])
        assert bytes(self.uc.mem_read(PENDING_FLAG, 1)) == b'\x01'

    def stage(self, stage):
        self.uc.mem_write(DPI_STAGE, bytes([stage]))
        self.run(DPI_FORMATTER)
        assert bytes(self.uc.mem_read(PENDING_BUFFER, 4)) == bytes([0xC9, 0x10, stage, 0])

    def drain(self):
        self.uc.mem_write(ENDPOINT_READY, b'\x01')
        self.uc.reg_write(UC_ARM_REG_R4, USB_BUFFER)
        self.uc.reg_write(UC_ARM_REG_R5, 0)
        self.run(USB_FRAGMENT)
        assert bytes(self.uc.mem_read(PENDING_FLAG, 1)) == b'\x00'
        return self.reports[-1]


def main():
    for index in range(5):
        fixture = Fixture()
        fixture.profile(index)
        endpoint, report = fixture.drain()
        assert (endpoint, report) == (3, bytes([3, 0xC9, 0x80, index, 0]))
        print(f'profile index {index}, display {index+1}: EP{endpoint} {report.hex(" ")}')

    fixture = Fixture()
    fixture.profile(2)
    fixture.stage(6)
    assert fixture.drain() == (3, bytes.fromhex('03 c9 10 06 00'))
    assert len(fixture.reports) == 1, 'No queued profile report is preserved'
    print('overwrite: profile2 -> stage6 before drain yields only 03 c9 10 06 00')

    fixture = Fixture()
    fixture.stage(4)
    fixture.profile(0)
    assert fixture.drain() == (3, bytes.fromhex('03 c9 80 00 00'))
    assert len(fixture.reports) == 1
    print('overwrite: stage4 -> profile0 before drain yields only 03 c9 80 00 00')

    fixture = Fixture()
    fixture.profile(1)
    fixture.profile(4)
    assert fixture.drain() == (3, bytes.fromhex('03 c9 80 04 00'))
    assert len(fixture.reports) == 1
    print('overwrite: profile1 -> profile4 before drain yields only last profile')
    print('PASS: 5 exact profile reports and 3 shared-slot overwrite regressions')
    print('OFFLINE ONLY: ROM memcpy/send stubs, wired/endpoint-ready assumptions; no physical validation')


if __name__ == '__main__':
    main()
