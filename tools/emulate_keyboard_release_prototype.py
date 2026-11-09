"""Real Thumb keyboard-release policy prototype; OFFLINE ONLY.

Compiles only our .S under bin/, patches emulated memory, runs real profile
SET/worker/load, SET08, button/FSM and USB assembly instructions. Synthetic
flash, ROM memcpy/zero-fill, display and transport boundaries remain explicit
stubs from the lifecycle fixture. No USB, loader, NVM write or firmware resource
generation. Published OL3 source/payload/hash are never changed.

This intentionally does NOT fix a complete tap lost before first EP acceptance,
running macro cancellation, consumer/mouse lifecycles, sleep or RF delivery.
"""

from pathlib import Path
import hashlib
import json
import struct
import subprocess

from unicorn import UC_HOOK_MEM_WRITE
from unicorn.arm_const import (
    UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3,
    UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7,
    UC_ARM_REG_PC, UC_ARM_REG_SP, UC_ARM_REG_LR,
)

from emulate_button_lifecycle_gaps import LifecycleFixture, EMPTY, ZERO_DOWN
from emulate_oled_arm import elf_text_and_symbols, page_packet
from emulate_ol3_status import PROFILE, UPDATE_PROFILE, expected_status
from emulate_screen_key import thumb_bl


ROOT = Path(__file__).resolve().parents[1]
BASE, PLACEMENT, OL3_PLACEMENT, APPEND_LIMIT = 0x28790, 0x47680, 0x47400, 0x47880
IMAGE_SHA = 'e0cc9bcb8930ea430d8bc14fefb746f189f0f5f743b81c0b32a5b161f7fba5db'
OL3_SHA = 'f9938ccff010dd680f1df37c0b0a97a3a6af6c8aeb12859475ca4dc19eba85ed'
KEYBOARD, ORDINARY, SHORTCUT, DIRTY, RF_GATE, USB_GATE = (
    0x400B42, 0x400B2B, 0x400B2C, 0x400AF6, 0x400B19, 0x400B1A)
REGISTERS = (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3,
             UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7)
OL3_HOOKS = {
    0x33C94: '13f040fc', 0x33CD4: '13f09dfb',
    0x342A4: '13f041f9', 0x34308: '13f01ff9',
}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def build():
    image = (ROOT/'evidence/g9pro-firmware-normalized.bin').read_bytes()
    assert sha(image) == IMAGE_SHA
    assert image[0x374A8-BASE:0x374AA-BASE] == bytes.fromhex('7047'), 'Pinned logger must remain BX LR'
    ol3 = bytes.fromhex((ROOT/'firmware/g9_ol3.payload.hex').read_text('ascii'))
    assert len(ol3) == 636 and sha(ol3) == OL3_SHA
    assert OL3_PLACEMENT+((len(ol3)+31)//32)*32 == PLACEMENT
    output = ROOT/'bin/g9_keyboard_release_prototype.o'
    output.parent.mkdir(exist_ok=True)
    subprocess.run(['clang', '--target=arm-none-eabi', '-march=armv5t', '-mthumb',
                    '-c', str(ROOT/'firmware/g9_keyboard_release_prototype.S'),
                    '-o', str(output)], check=True)
    code, symbols = elf_text_and_symbols(output.read_bytes())
    assert PLACEMENT+((len(code)+31)//32)*32 <= APPEND_LIMIT
    hooks = []
    for site, original, symbol in (
        (0x30D80, 0x374A8, 'g9_release_profile_load_hook'),
        (0x33F4E, 0x24F9C, 'g9_release_button_copy_hook'),
    ):
        old = image[site-BASE:site-BASE+4]
        assert old == thumb_bl(site, original)
        target = PLACEMENT+symbols[symbol]['offset']
        hooks.append(dict(address=site, old_hex=old.hex(),
                          new_hex=thumb_bl(site, target).hex(), target=target,
                          symbol=symbol))
    layout = dict(status='OFFLINE_PROTOTYPE_NOT_INSTALLABLE', placement=PLACEMENT,
                  bytes=len(code), sha256=sha(code), hooks=hooks, symbols=symbols,
                  with_ol3_padded_bytes=PLACEMENT-OL3_PLACEMENT+((len(code)+31)//32)*32,
                  append_limit=APPEND_LIMIT, published_ol3_sha256=OL3_SHA)
    (ROOT/'bin/g9_keyboard_release_prototype.layout.json').write_text(
        json.dumps(layout, indent=2), encoding='utf-8')
    return code, layout, ol3


class ReleaseFixture(LifecycleFixture):
    def __init__(self, code, layout, ol3, index=3, no_wait=True, release_patch=True):
        self.prototype_writes = []
        self.replayed_calls = []
        self.prototype_steps = 0
        self.prototype_minimum_sp = 0x508000
        self.prototype_end = PLACEMENT+len(code)
        super().__init__(index, no_wait)
        self.uc.mem_write(OL3_PLACEMENT, ol3)
        for address, value in OL3_HOOKS.items():
            self.uc.mem_write(address, bytes.fromhex(value))
        self.uc.mem_write(PLACEMENT, code)
        if release_patch:
            for hook in layout['hooks']:
                assert bytes(self.uc.mem_read(hook['address'], 4)).hex() == hook['old_hex']
                self.uc.mem_write(hook['address'], bytes.fromhex(hook['new_hex']))
        # Synthetic-profile initialization already executed the stock loader.
        # Invalidate its translated block after replacing the emulated BL;
        # mem_write alone does not invalidate Unicorn's instruction cache.
        self.uc.ctl_remove_cache(0x20000, 0x50000)
        self.uc.hook_add(UC_HOOK_MEM_WRITE, self.prototype_write)

    def prototype_write(self, uc, access, address, size, value, context):
        pc = uc.reg_read(UC_ARM_REG_PC)
        if PLACEMENT <= pc < self.prototype_end:
            self.prototype_writes.append((address, size, value))
            if address < 0x500000:  # Stack is checked separately by inherited canary.
                allowed = set(range(KEYBOARD, KEYBOARD+8)) | {ORDINARY, SHORTCUT, DIRTY, RF_GATE, USB_GATE}
                assert set(range(address, address+size)) <= allowed, (hex(pc), hex(address), size)

    def hook(self, uc, address, size, context):
        if PLACEMENT <= address < self.prototype_end:
            self.prototype_steps += 1
            self.prototype_minimum_sp = min(self.prototype_minimum_sp, uc.reg_read(UC_ARM_REG_SP))
        if address in (0x374A8, 0x24F9C):
            # Record only calls originating from the new wrappers, not all stock copies.
            lr = uc.reg_read(UC_ARM_REG_LR) & ~1
            if PLACEMENT <= lr < self.prototype_end:
                self.replayed_calls.append((address, tuple(uc.reg_read(r) for r in REGISTERS[:3])))
        super().hook(uc, address, size, context)

    def set_binding_in_ram(self, triplet, index=None):
        index = self.index if index is None else index
        self.uc.mem_write(0x401076+3*index, triplet)

    def edge_mask(self, mask):
        self.set8(0x400AFC, 1)
        self.set8(0x400AFF, 1)
        self.uc.mem_write(0x400AE0, struct.pack('<H', mask))
        self.cycle()

    def set_keys_from_main(self, index, triplet, valid_checksum=True):
        keys = bytearray(self.uc.mem_read(0x401076, 54))
        keys[3*index:3*index+3] = triplet
        checksum = sum(keys) & 65535
        if not valid_checksum:
            checksum ^= 1
        frame = bytes([8, 59, self.get8(PROFILE)+1])+keys+checksum.to_bytes(2, 'big')
        assert len(frame) == 59
        self.uc.mem_write(0x400F8A, frame[:2])
        self.uc.mem_write(0x400F8C, struct.pack('<I', 0x400F93))
        self.uc.mem_write(0x400F91, frame)
        self.run(0x33CD2)
        return bytes(keys)

    def held_down(self, triplet=bytes.fromhex('110027')):
        self.set_binding_in_ram(triplet)
        self.edge(True)
        self.cycle()
        expected = bytes([triplet[1], 0, triplet[2]])+bytes(5)
        assert self.keyboard_reports == [bytes([triplet[1]])+bytes(7), expected]
        return expected

    def assert_released(self):
        assert bytes(self.uc.mem_read(KEYBOARD, 8)) == EMPTY
        assert self.get8(ORDINARY) == self.get8(SHORTCUT) == 0
        assert self.keyboard_reports[-1] == EMPTY, [r.hex() for r in self.keyboard_reports]
        assert not self.flash_writes


def run_tests(code, layout, ol3):
    observations = []

    def result(case, fixture, **extra):
        observations.append(dict(case=case, reports=[r.hex() for r in fixture.keyboard_reports],
                                 prototype_steps=fixture.prototype_steps,
                                 prototype_stack_bytes=0x508000-fixture.prototype_minimum_sp,
                                 **extra))

    for index, no_wait, label in ((2, False, 'middle control'), (3, True, 'no-wait screen')):
        for triplet, target in ((bytes.fromhex('110027'), 'same0'),
                                (bytes.fromhex('110028'), 'different key'),
                                (bytes.fromhex('0d0000'), 'DPI action')):
            for patched in (False, True):
                f = ReleaseFixture(code, layout, ol3, index, no_wait, patched)
                f.binding(1, index, triplet)
                f.held_down()
                f.set_profile_from_main(1)
                f.finish_profile_work()
                assert f.get8(PROFILE) == f.get8(UPDATE_PROFILE) == 1
                if patched:
                    assert f.get8(DIRTY) == f.get8(RF_GATE) == f.get8(USB_GATE) == 1
                    assert any(call[0] == 0x374A8 for call in f.replayed_calls)
                f.release()
                f.cycle()
                if patched or target == 'same0':
                    f.assert_released()
                else:
                    assert ZERO_DOWN in f.keyboard_reports and f.keyboard_reports[-1] != EMPTY
                    assert f.get8(KEYBOARD+1) == 0x27
                result(f'{label}: profile -> {target}, release patch={patched}', f,
                       known_gap=not patched and target != 'same0',
                       gap_kind='baseline_reproduction' if not patched and target != 'same0' else None)

        f = ReleaseFixture(code, layout, ol3, index, no_wait)
        f.held_down(bytes.fromhex('110127'))
        f.binding(1, index, bytes.fromhex('0d0000'))
        f.set_profile_from_main(1)
        f.finish_profile_work()
        f.release()
        f.assert_released()
        result(label+': modifier and0 released on profile change', f)

        f = ReleaseFixture(code, layout, ol3, index, no_wait)
        f.held_down()
        before = len(f.keyboard_reports)
        f.uc.reg_write(UC_ARM_REG_R0, 0)
        f.run(0x30B78)  # Same-profile reload uses the same explicit release policy.
        f.cycle()
        f.cycle()
        assert f.keyboard_reports[before:] == [EMPTY]
        f.release()
        f.assert_released()
        f.tap()
        assert f.keyboard_reports[-3:] == [EMPTY, ZERO_DOWN, EMPTY]
        result(label+': same-profile reload releases once; new physical tap works', f)

        for patched in (False, True):
            f = ReleaseFixture(code, layout, ol3, index, no_wait, patched)
            f.held_down()
            keys = f.set_keys_from_main(index, bytes.fromhex('110028'))
            assert bytes(f.uc.mem_read(0x401076, 54)) == keys
            f.release()
            f.cycle()
            if patched:
                assert any(call[0] == 0x24F9C and call[1] == (0x401076, 0x400DF0, 54)
                           for call in f.replayed_calls)
                f.assert_released()
                f.tap()
                assert f.keyboard_reports[-3:] == [EMPTY, bytes.fromhex('0000280000000000'), EMPTY]
            else:
                assert f.keyboard_reports[-1] != EMPTY and f.get8(KEYBOARD+1) == 0x27
            result(f'{label}: SET08 rebind while held, release patch={patched}', f,
                   known_gap=not patched, gap_kind='baseline_reproduction' if not patched else None)

        f = ReleaseFixture(code, layout, ol3, index, no_wait)
        f.held_down()
        f.set_binding_in_ram(bytes.fromhex('110128'), 1)
        f.edge_mask((1 << index) | 2)
        f.cycle()
        assert bytes(f.uc.mem_read(KEYBOARD, 3)) == bytes.fromhex('01 27 28')
        f.binding(1, index, bytes.fromhex('0d0000'))
        f.set_profile_from_main(1)
        f.finish_profile_work()
        f.set8(0x400AFC, 0)
        f.cycle()
        f.edge_mask(0)
        f.cycle()
        f.assert_released()
        result(label+': two mapped keys and modifier are released by real profile load', f)

        f = ReleaseFixture(code, layout, ol3, index, no_wait)
        f.held_down()
        f.endpoint_ready = False
        f.binding(1, index, bytes.fromhex('0d0000'))
        f.set_profile_from_main(1)
        f.finish_profile_work()
        f.release()
        for _ in range(4):
            f.cycle()
        assert f.keyboard_reports == [EMPTY, ZERO_DOWN] and f.get8(DIRTY) == 1
        f.endpoint_ready = True
        f.cycle()
        f.assert_released()
        result(label+': mutation key-up survives endpoint backpressure', f)

        f = ReleaseFixture(code, layout, ol3, index, no_wait)
        for phase in (1, 2):
            f.set8(ORDINARY, phase)
            f.set8(SHORTCUT, phase)
            f.uc.mem_write(0x400B9A, bytes.fromhex('0127'))
            f.uc.reg_write(UC_ARM_REG_R0, 0)
            f.run(0x30B78)
            assert f.get8(ORDINARY) == f.get8(SHORTCUT) == 0
            f.cycle()
        assert f.keyboard_reports == [EMPTY, EMPTY]
        result(label+': queued ordinary and shortcut state cannot emit after reload', f)

        f = ReleaseFixture(code, layout, ol3, index, no_wait)
        for _ in range(5):
            f.tap()
        assert f.keyboard_reports == [EMPTY, ZERO_DOWN, EMPTY]*5
        result(label+': repeated ready-endpoint taps preserved', f)

        f = ReleaseFixture(code, layout, ol3, index, no_wait)
        for profile in range(5):
            f.uc.reg_write(UC_ARM_REG_R0, profile)
            f.run(0x30B78)
            f.cycle()
        assert not f.keyboard_reports and f.get8(DIRTY) == 0
        result(label+': idle loads of five profiles do not invent keyboard reports', f)

        f = ReleaseFixture(code, layout, ol3, index, no_wait)
        f.held_down()
        before = bytes(f.uc.mem_read(KEYBOARD, 8))
        f.set_keys_from_main(index, bytes.fromhex('110028'), valid_checksum=False)
        assert bytes(f.uc.mem_read(KEYBOARD, 8)) == before
        assert bytes(f.uc.mem_read(0x401076+3*index, 3)) == bytes.fromhex('110027')
        f.release()
        f.assert_released()
        result(label+': rejected SET08 leaves original held-key release intact', f)

        f = ReleaseFixture(code, layout, ol3, index, no_wait)
        f.endpoint_ready = False
        f.tap()
        f.endpoint_ready = True
        f.cycle()
        assert f.keyboard_reports == [EMPTY]  # Deliberately still not solved.
        result(label+': complete short tap before EP acceptance remains a known gap', f,
               known_gap=True, gap_kind='remaining_in_prototype')

    f = ReleaseFixture(code, layout, ol3)
    helper = PLACEMENT+layout['symbols']['g9_release_keyboard']['offset']
    # Distinct canaries immediately around every audited target range.
    f.uc.mem_write(0x400B40, bytes.fromhex('a1b2')+bytes.fromhex('0127282930313200')+bytes.fromhex('c3d4'))
    f.uc.mem_write(0x400B2A, bytes.fromhex('e5 02 04 f6'))
    f.set8(DIRTY-1, 0x87)
    f.set8(DIRTY+1, 0x98)
    f.set8(0x400B18, 0x69)
    f.set8(0x400B1B, 0x78)
    before = bytes(f.uc.mem_read(0x400000, 0x2000))
    register_values = [0x10203040+i*0x01010101 for i in range(8)]
    for register, value in zip(REGISTERS, register_values):
        f.uc.reg_write(register, value)
    f.run(helper)
    assert [f.uc.reg_read(r) for r in REGISTERS] == register_values
    after = bytes(f.uc.mem_read(0x400000, 0x2000))
    allowed = set(range(KEYBOARD, KEYBOARD+8)) | {ORDINARY, SHORTCUT, DIRTY, RF_GATE, USB_GATE}
    changed = [0x400000+i for i, (a, b) in enumerate(zip(before, after)) if a != b]
    assert set(changed) <= allowed
    assert f.get8(DIRTY) == f.get8(RF_GATE) == f.get8(USB_GATE) == 1
    assert bytes(f.uc.mem_read(KEYBOARD, 8)) == EMPTY
    assert f.get8(ORDINARY) == f.get8(SHORTCUT) == 0
    assert f.prototype_minimum_sp >= 0x507F00 and not f.mmio_writes and not f.flash_writes
    result('direct helper: multiple usages/modifier, exact RAM bounds, r0-r7 preserved', f,
           ram_addresses_changed=[hex(a) for a in changed])

    # Coexistence with the published payload: status must not cause a load/release.
    f = ReleaseFixture(code, layout, ol3)
    f.held_down()
    config = f.config_snapshot()
    keyboard = bytes(f.uc.mem_read(KEYBOARD, 8))
    loads = len(f.profile_loads)
    f.submit_query()
    assert f.finish_query() == expected_status(1, 0, 1, 800)
    assert len(f.profile_loads) == loads and f.config_snapshot() == config
    assert bytes(f.uc.mem_read(KEYBOARD, 8)) == keyboard
    f.upload_page(page_packet(0, bytes(120), flags=1))
    assert len(f.profile_loads) == loads and bytes(f.uc.mem_read(KEYBOARD, 8)) == keyboard
    f.release()
    f.assert_released()
    result('published OL3 status and page upload do not invoke release policy', f)

    for item in observations:
        print(('KNOWN GAP ' if item.get('known_gap') else 'PASS ')+item['case'])
    return observations


def main():
    frozen = {name: (ROOT/name).read_bytes() for name in (
        'firmware/g9_ol3.S', 'firmware/g9_ol3.payload.hex', 'firmware/g9_oled.S',
        'firmware/g9_oled.payload.hex', 'evidence/g9pro-official-firmware-resource.bin')}
    code, layout, ol3 = build()
    observations = run_tests(code, layout, ol3)
    assert all((ROOT/name).read_bytes() == before for name, before in frozen.items())
    result = dict(offline_only=True, physical_validated=False,
                  published_firmware_unchanged=True, firmware_resource_generated=False,
                  layout=layout, observations=observations,
                  known_gap_cases=sum(bool(x.get('known_gap')) for x in observations),
                  baseline_gap_reproductions=sum(x.get('gap_kind') == 'baseline_reproduction' for x in observations),
                  remaining_prototype_gap_cases=sum(x.get('gap_kind') == 'remaining_in_prototype' for x in observations))
    (ROOT/'evidence/keyboard-release-prototype.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(dict(cases=len(observations), known_gap_cases=result['known_gap_cases'],
                          baseline_gap_reproductions=result['baseline_gap_reproductions'],
                          remaining_prototype_gap_cases=result['remaining_prototype_gap_cases'],
                          prototype_bytes=len(code), sha256=sha(code),
                          evidence='evidence/keyboard-release-prototype.json')))


if __name__ == '__main__':
    main()
