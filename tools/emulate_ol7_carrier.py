"""Offline prototype of a reserved SET04 carrier; NEVER send to a device.

Only the modified mouse-side ARM path executes. Actual USB-receiver framing
is still a physical qualification gap. Unmodified firmware must never receive
these packets: they are not sensor settings and are not admitted by that API.
No updater or installable candidate is produced.
"""
from pathlib import Path
import hashlib
import json
import subprocess
import inspect
from emulate_ol5_radio import build as baseline, BASE, APPEND, ROOT, RFSTATE, REMAIN, SEQ
from emulate_ol5_retention import NativeRadioFixture, image
from emulate_oled_arm import elf_text_and_symbols
from emulate_screen_key import thumb_bl
from emulate_oled_protocol import crc16
from emulate_ol4_status import run_mouse_ready_regressions, run_claim_irq_regressions
from build_oled_candidate import verify_input_paths
from unicorn.arm_const import (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2,
    UC_ARM_REG_R3, UC_ARM_REG_R4, UC_ARM_REG_SP, UC_ARM_REG_LR)


def build():
    _, layout = baseline()
    output = ROOT / 'bin/g9_ol7.o'
    subprocess.run(['clang', '--target=arm-none-eabi', '-march=armv5t', '-mthumb',
                    '-c', str(ROOT / 'firmware/g9_ol7.S'), '-o', str(output)],
                   check=True, cwd=ROOT)
    code, symbols = elf_text_and_symbols(output.read_bytes())
    for hook in layout['hooks']:
        hook['symbol'] = hook['symbol'].replace('g9_ol5', 'g9_ol7')
        hook['target'] = APPEND + symbols[hook['symbol']]['offset']
        hook['new_hex'] = thumb_bl(hook['address'], hook['target']).hex()
    layout.update(symbols=symbols, text_bytes=len(code), text_sha256=hashlib.sha256(code).hexdigest(),
                  extension_crc_blocks=(len(code)+31)//32, version_report_marker='OL7')
    layout['minimum_resource_size'] = 16 + (3939+layout['extension_crc_blocks'])*34
    assert layout['minimum_resource_size'] <= 0x22000
    return code, layout


def carrier(page, x, pixels, initialize=False):
    assert page in range(4) and x in (0, 40, 80) and len(pixels) == 40
    packet = bytearray(56)
    packet[:12] = bytes([4, 56, 1, 0, 1, 0x3F, 0xA5, 0x5A, page, x, int(initialize), 40])
    packet[12:24], packet[25:53] = pixels[:12], pixels[12:]
    packet[24] = 0  # Invalid native DPI stage; every valid sensor SET has1..8.
    packet[53:55] = crc16(packet[:53]).to_bytes(2, 'little')
    packet[55] = 6
    return bytes(packet)


class Fixture(NativeRadioFixture):
    def __init__(self, code, layout, candidate_image=None):
        self.legacy_dispatches = 0
        self.legacy_registers = None
        super().__init__(code, layout, candidate_image=candidate_image)

    def hook(self, uc, address, size, context):
        if address == 0x33CD2:
            # Normal sensor writes are deliberately a boundary in this proof;
            # only prove dispatch unchanged, never model them as safe no-ops.
            self.legacy_dispatches += 1
            self.legacy_registers = tuple(uc.reg_read(reg) for reg in
                (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3,
                 UC_ARM_REG_R4, UC_ARM_REG_SP, UC_ARM_REG_LR))
            self.ret()
        else:
            super().hook(uc, address, size, context)

    def packet(self, packet, kind=0, selector=0):
        self.prefix(report=4, length=56, kind=kind, selector=selector)
        self.fragment(1, packet[2:32])
        self.fragment(2, packet[32:])

    def tick(self):
        before = self.total_instructions
        self.run(APPEND+self.layout['symbols']['g9_ol7_restore_tick']['offset'])
        return self.total_instructions-before

    def upload(self, frame, initialize=True):
        for index in range(12):
            page, part=divmod(index,3)
            self.packet(carrier(page,part*40,frame[index*40:(index+1)*40],initialize and index==0))


def run_checks(code, layout, candidate_image=None):
    def fixture():return Fixture(code,layout,candidate_image)
    original = (ROOT/'evidence/g9pro-firmware-normalized.bin').read_bytes()
    # The old normalized evidence includes an FF-only resource tail; code starts
    # at the reviewed append address, not after that serialization padding.
    assert original[APPEND-BASE:] == bytes([255])*(len(original)-(APPEND-BASE))
    combined = bytearray(original[:APPEND-BASE])
    for patch in layout['hooks']+layout['data_patches']+[layout['descriptor_patch']]:
        offset = patch['address']-BASE
        before, after = bytes.fromhex(patch['old_hex']), bytes.fromhex(patch['new_hex'])
        assert combined[offset:offset+len(before)] == before
        combined[offset:offset+len(after)] = after
    assert BASE+len(combined) == APPEND
    combined.extend(code)
    if candidate_image is not None:
        # Resource sealing changes the reviewed header fields, not ARM code.
        from check_firmware_header import EDITABLE_HEADER_OFFSETS
        assert {i for i in range(16) if combined[i]!=candidate_image[i]}<=EDITABLE_HEADER_OFFSETS
        combined[:16]=candidate_image[:16]
        assert bytes(combined)==candidate_image[:len(combined)]
    input_checks = verify_input_paths(bytes(combined))
    # The shared regression's historical symbol name is an emulator alias only.
    guard_layout = dict(layout, symbols=dict(layout['symbols'],
        g9_ol4_ep0_mouse_ready_hook=layout['symbols']['g9_ol7_ep0_mouse_ready_hook']))
    readiness_checks = run_mouse_ready_regressions(code, guard_layout,candidate_image,protected_reports=(4,5,6,8,12,0x22))
    # Adapt only expected Python marker/symbol names; candidate ARM stays exact.
    scope=dict(run_claim_irq_regressions.__globals__)
    source=inspect.getsource(run_claim_irq_regressions).replace('4f4c34','4f4c37').replace('g9_ol4_','g9_ol7_')
    exec(source,scope)
    irq_checks=scope['run_claim_irq_regressions'](code,layout,candidate_image)
    f = fixture()
    original_config = bytes(f.uc.mem_read(0x401020, 0x8E))
    frame = image(31)
    for index in range(12):
        page, part = divmod(index, 3)
        f.packet(carrier(page, part*40, frame[index*40:(index+1)*40], index == 0))
        assert f.store_next() == index+1
        assert f.get8(RFSTATE) == 10 and f.get8(REMAIN) == 0 and f.get8(SEQ) == 2
        assert not f.legacy_dispatches and not f.flash_writes and not f.profile_loads
        assert bytes(f.uc.mem_read(0x401020, 0x8E)) == original_config
    assert f.active_frame() == frame
    f.clear_trace()
    for index in range(12):
        f.tick()
        assert f.transactions[-1] == bytes([0x78, 0x40]) + frame[index*40:(index+1)*40]
    newer = image(37)
    for index in range(11):
        page, part = divmod(index, 3)
        f.packet(carrier(page, part*40, newer[index*40:(index+1)*40], index == 0))
        assert f.active_frame() == frame  # Partial replacement cannot publish.
    f.packet(carrier(3, 80, newer[440:480]))
    assert f.active_frame() == newer
    good = carrier(0, 0, bytes(40), True)
    rejected = 0
    for offset in (2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 24, 25, 51, 52, 53, 54, 55):
        bad = bytearray(good)
        bad[offset] ^= 0x80
        g = fixture()
        before = bytes(g.uc.mem_read(0x401020, 0x8E))
        g.packet(bytes(bad))
        assert g.active_frame() is None and not g.legacy_dispatches
        assert not g.flash_writes and not g.profile_loads
        assert bytes(g.uc.mem_read(0x401020, 0x8E)) == before
        rejected += 1
    for kind in (1, 255):
        g = fixture(); g.packet(good, kind=kind)
        assert g.active_frame() is None and not g.flash_writes and not g.legacy_dispatches
        rejected += 1
    # Valid CRC must not bypass structural validation. Pixel changes with a
    # recomputed CRC are valid data, so are deliberately not counted as rejects.
    semantic = []
    for offset, value in ((2, 2), (3, 1), (4, 2), (5, 0), (6, 0xA4),
                          (7, 0x5B), (8, 4), (9, 1), (10, 2), (11, 39),
                          (24, 1), (55, 1)):
        bad = bytearray(good); bad[offset] = value
        bad[53:55] = crc16(bad[:53]).to_bytes(2, 'little')
        semantic.append(bytes(bad))
    semantic.extend((carrier(3, 0, bytes([0xF0])*40),
                     carrier(0, 40, bytes(40), True)))
    for bad in semantic:
        g = fixture()
        before = bytes(g.uc.mem_read(0x401020, 0x8E))
        g.packet(bad)
        assert g.active_frame() is None and not g.legacy_dispatches
        assert not g.flash_writes and not g.profile_loads
        assert bytes(g.uc.mem_read(0x401020, 0x8E)) == before
        rejected += 1
    # Real successful RF SET04 from dpi1600-applied.log: byte6 is NONZERO.
    # These are normal sensor fields, never assume both signature slots zero.
    # Native flash semantics are NOT emulated by this dispatch boundary.
    g = fixture()
    ordinary = bytes.fromhex('04380100013f0500204080c040e0646403060c121f2e000002ff000000ff000000ffffff0000ffffff00ffff4000ffffff01117700000000')
    g.packet(bytes(ordinary))
    assert g.legacy_dispatches == 1
    native = fixture()
    native.uc.mem_write(0x3012A, thumb_bl(0x3012A, 0x33CD2))
    native.packet(bytes(ordinary))
    assert native.legacy_registers == g.legacy_registers
    result = dict(status='OFFLINE_MOUSE_SIDE_PROTOTYPE_ONLY', payload_bytes=len(code),
                  payload_sha256=hashlib.sha256(code).hexdigest(),
                  resource_minimum_bytes=layout['minimum_resource_size'],
                  complete_frame_chunks=12, render_ticks=12, malformed_rejected=rejected,
                  normal_set_dispatch_preserved=True, legacy_arguments_preserved=True,
                  config_unchanged=True,
                  custom_flash_writes=0, physical_receiver_qualified=False,
                  input_checks=input_checks, mouse_readiness_cases=readiness_checks,wired_irq_cases=irq_checks,
                  partial_frame_keeps_previous=True,
                  device_access=False, installable_candidate=False)
    # All native wired rendering, sleep, pixel gates and retained reset checks
    # also run, replacing only the RF fixture with the carrier and omitting
    # report22 RF status, which this variant deliberately does not implement.
    from emulate_ol5_retention import run_regressions
    native_layout=dict(layout,symbols=dict(layout['symbols']))
    for name,value in layout['symbols'].items():
        if name.startswith('g9_ol7_'):native_layout['symbols'][name.replace('g9_ol7_','g9_ol5_')]=value
    result['native_checks']=run_regressions(code,native_layout,candidate_image,
        radio_fixture_factory=Fixture,radio_status_supported=False)
    from emulate_usb_settings_input import run_regressions as settings_input_checks
    result['settings_input_checks']=settings_input_checks(code,layout,candidate_image)
    return result


def main():
    code,layout=build()
    result=run_checks(code,layout)
    (ROOT/'bin/g9_ol7-carrier-proof.json').write_text(json.dumps(result, indent=2)+'\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
