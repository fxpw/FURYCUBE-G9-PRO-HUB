"""Narrow NEW lifecycle audit, offline only; not the old aggregate regression.

Executes stock handlers with/without the two-byte OLED no-wait change. Uses the
existing synthetic flash/global/profile history fixture, never physical NVM.
ROM24FEC is a TWO-argument zero-fill; main cycles and USB readiness are supplied.
Endpoint busy also sets stock400AAF to1 to disable the alternate send path.
No GPIO timing, RF receiver, OS key-repeat or real USB completion is emulated.
Known failures are reported as limitations; --strict returns nonzero for them.
Existing firmware/backend/GUI files are never modified by this script.
"""

from pathlib import Path
import argparse
import json
import struct

from unicorn.arm_const import (
    UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3,
    UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7,
    UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC,
)

from emulate_ol3_status import FlowFixture, PROFILE, UPDATE_PROFILE
from emulate_screen_key import Fixture as EarlierButtonFixture


ROOT = Path(__file__).resolve().parents[1]
STOP = 0x600000
EMPTY = bytes(8)
ZERO_DOWN = bytes.fromhex('00 00 27 00 00 00 00 00')


class ZeroFillCoverage(EarlierButtonFixture):
    def __init__(self):
        self.zero_fill_coverage = []
        super().__init__(3, 'disable-oled-hold')

    def hook(self, uc, address, size, context):
        if address == 0x24FEC:
            target, count = (uc.reg_read(r) for r in (UC_ARM_REG_R0, UC_ARM_REG_R1))
            self.zero_fill_coverage.append((target, count))
            uc.mem_write(target, bytes(count))
            uc.reg_write(UC_ARM_REG_PC, uc.reg_read(UC_ARM_REG_LR))
        else:
            super().hook(uc, address, size, context)


class LifecycleFixture(FlowFixture):
    def __init__(self, index, no_wait):
        self.keyboard_reports = []
        self.zero_fill_calls = []
        self.endpoint_ready = True
        super().__init__(b'', {'hooks': []}, patched=False)
        if no_wait:
            assert bytes(self.uc.mem_read(0x3201C, 2)) == bytes.fromhex('0d70')
            self.uc.mem_write(0x3201C, bytes.fromhex('c046'))
        self.index = index
        self.install_synthetic_flash(0)
        for profile in range(5):
            self.binding(profile, index, bytes.fromhex('11 00 27'))
        self.uc.reg_write(UC_ARM_REG_R0, 0)
        self.run(0x30B78)
        self.set8(0x400A79, 1)  # Active device / raw-edge acceptance.
        self.set8(0x400AAF, 1)  # Honor endpoint-ready rather than fallback send.

    def hook(self, uc, address, size, context):
        if address == 0x24FEC:
            target, count = (uc.reg_read(r) for r in (UC_ARM_REG_R0, UC_ARM_REG_R1))
            assert count <= 4096
            self.zero_fill_calls.append((target, count))
            uc.mem_write(target, bytes(count))
            self.ret()
        elif address == 0x376AA:
            endpoint, pointer, count = (uc.reg_read(r) for r in
                                       (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2))
            if endpoint == 1:
                self.keyboard_reports.append(bytes(uc.mem_read(pointer, count)))
            self.ret()
        else:
            super().hook(uc, address, size, context)

    def binding(self, profile, index, triplet):
        address = struct.unpack('<I', self.uc.mem_read(0x4469C+4*profile, 4))[0]
        record = bytearray(self.flash_records[address])
        record[61+3*index:64+3*index] = triplet
        record[115:117] = (sum(record[61:115]) & 65535).to_bytes(2, 'big')
        self.flash_records[address] = bytes(record)

    def usb_keyboard_attempt(self):
        self.set8(0x400B22, int(self.endpoint_ready))
        # Supply the actual surrounding handler's epilogue frame, allowing the
        # no-send path to return too. Earlier fixtures stopped at every send.
        self.uc.reg_write(UC_ARM_REG_R7, 0x400B22)
        self.uc.reg_write(UC_ARM_REG_R6, 0x400B23)
        self.uc.reg_write(UC_ARM_REG_R5, 0)
        saved = tuple(self.uc.reg_read(r) for r in
                      (UC_ARM_REG_R3, UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7))
        self.uc.mem_write(0x508000, struct.pack('<6I', *saved, STOP | 1))
        self.uc.reg_write(UC_ARM_REG_SP, 0x508000)
        self.uc.reg_write(UC_ARM_REG_LR, STOP | 1)
        self.uc.emu_start(0x38C4F, STOP, count=10000)
        assert self.uc.reg_read(UC_ARM_REG_PC) == STOP

    def cycle(self):
        self.run(0x31EB6)
        self.run(0x32AA4)
        self.run(0x32B30)
        if self.get8(0x400AF6):
            self.usb_keyboard_attempt()

    def edge(self, down):
        self.set8(0x400AFC, 1)
        self.set8(0x400AFF, 1)
        self.uc.mem_write(0x400AE0, struct.pack('<H', 1 << self.index if down else 0))
        self.cycle()

    def release(self):
        self.set8(0x400AFC, 0)
        self.cycle()
        self.edge(False)

    def tap(self, held_cycles=2):
        self.edge(True)
        for _ in range(held_cycles):
            self.cycle()
        self.release()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--strict', action='store_true')
    args = parser.parse_args()
    observations = []
    earlier = ZeroFillCoverage()
    earlier.stable_edge(8, 'contract coverage press')
    earlier.cycle('contract coverage held')
    earlier.release_starts()
    earlier.stable_edge(0, 'contract coverage release')
    assert [report for endpoint, report in earlier.reports if endpoint == 1] == [EMPTY, ZERO_DOWN, EMPTY]
    assert not earlier.zero_fill_coverage, 'Earlier tap coverage has changed: review zero-fill model'
    for index, patched, label in ((2, False, 'stock middle'), (3, True, 'no-wait screen')):
        fixture = LifecycleFixture(index, patched)
        for _ in range(5):
            fixture.tap()
        assert fixture.keyboard_reports == [EMPTY, ZERO_DOWN, EMPTY]*5
        assert fixture.get8(0x400B2B) == 0
        observations.append(dict(case=label+' repeated taps', complete_edges=True, reports=len(fixture.keyboard_reports)))

        fixture = LifecycleFixture(index, patched)
        fixture.tap(held_cycles=30)
        assert fixture.keyboard_reports == [EMPTY, ZERO_DOWN, EMPTY]
        observations.append(dict(case=label+' long held ordinary key', complete_edges=True, repeat_scope='OS repeat not modeled'))

        fixture = LifecycleFixture(index, patched)
        fixture.endpoint_ready = False
        fixture.tap()
        assert not fixture.keyboard_reports
        fixture.endpoint_ready = True
        fixture.cycle()
        missing_down = ZERO_DOWN not in fixture.keyboard_reports
        observations.append(dict(case=label+' press and release while endpoint busy', safety_gap=missing_down,
                                 missing_down=missing_down, reports=[r.hex() for r in fixture.keyboard_reports]))

        fixture = LifecycleFixture(index, patched)
        fixture.edge(True)
        fixture.cycle()
        assert fixture.keyboard_reports == [EMPTY, ZERO_DOWN]
        fixture.endpoint_ready = False
        fixture.release()
        for _ in range(3):
            fixture.cycle()
        assert fixture.get8(0x400AF6) == 1
        fixture.endpoint_ready = True
        fixture.cycle()
        assert fixture.keyboard_reports == [EMPTY, ZERO_DOWN, EMPTY]
        observations.append(dict(case=label+' release delayed by endpoint busy', complete_edges=True))

        for triplet, target in ((bytes.fromhex('11 00 27'), 'same 0'),
                                (bytes.fromhex('11 00 28'), 'different keyboard key'),
                                (bytes.fromhex('0d 00 00'), 'DPI function')):
            fixture = LifecycleFixture(index, patched)
            fixture.binding(1, index, triplet)
            fixture.edge(True)
            fixture.cycle()
            assert fixture.keyboard_reports == [EMPTY, ZERO_DOWN]
            fixture.set_profile_from_main(1)
            fixture.finish_profile_work()
            assert fixture.get8(PROFILE) == fixture.get8(UPDATE_PROFILE) == 1
            fixture.release()
            for _ in range(2):
                fixture.cycle()
            release_complete = bool(fixture.keyboard_reports) and fixture.keyboard_reports[-1] == EMPTY
            keyboard_buffer = bytes(fixture.uc.mem_read(0x400B42, 7))
            observations.append(dict(case=label+' profile switch while held -> '+target,
                                     safety_gap=not release_complete, complete_release=release_complete,
                                     reports=[r.hex() for r in fixture.keyboard_reports],
                                     keyboard_buffer=keyboard_buffer.hex()))

    result = dict(offline_only=True, physical_validated=False, existing_firmware_files_modified=False,
                  zero_fill_contract='ROM24FEC(dst,count) -> zeros',
                  earlier_successful_tap_zero_fill_calls=earlier.zero_fill_coverage,
                  observations=observations, known_limitations=sum(bool(x.get('safety_gap')) for x in observations))
    (ROOT/'evidence/button-lifecycle-gaps.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    for item in observations:
        print(('GAP ' if item.get('safety_gap') else 'PASS ')+item['case'])
    print(json.dumps(dict(cases=len(observations), known_limitations=result['known_limitations'],
                          evidence='evidence/button-lifecycle-gaps.json', hardware_stubs=True)))
    if args.strict and result['known_limitations']:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
