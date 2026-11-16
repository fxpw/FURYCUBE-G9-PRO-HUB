"""Offline stock/OL7 RF selector audit using cached objects; never compile/access devices.

Run from WSL with tools/pyenv/bin/python tools/audit_rf_get_selector.py.
RF FIFO, transmit completion, periodic scheduling and unrelated hardware services
are supplied boundaries. Prefix/parser, complete worker, getter, profile loader,
RF dispatcher and fragmentation execute their original ARM instructions.
"""
from datetime import datetime, timezone
from pathlib import Path
import copy
import hashlib
import json
import struct
import uuid

from emulate_oled_arm import elf_text_and_symbols
from emulate_screen_key import thumb_bl
from emulate_ol5_radio import ROOT, BASE, APPEND, SOURCE_HASH, RadioFixture, RFSTATE, REMAIN, SEQ
from unicorn.arm_const import (UC_ARM_REG_R0, UC_ARM_REG_R4, UC_ARM_REG_R5,
    UC_ARM_REG_R6, UC_ARM_REG_R7, UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC)

if not __debug__:
    raise RuntimeError('Assertions are required; do not use -O')

OL7_TEXT_HASH = '1c7da04f67a9d40346d2fcf9dc34b2490ef6a41cea47c8837485ce8b98b58290'
REPORT_LENGTHS = {0x0B: 8, 0x0C: 8, 4: 52}
SENSOR_RAM, SENSOR_CRC = 0x40103A, 0x40106A
# These do not replace the RF dispatcher, worker, getter, loader or355B8 body.
UNRELATED_SERVICES = (0x31EB6, 0x32AA4, 0x35D72, 0x32B30, 0x32B72,
    0x2ABF0, 0x333DE, 0x33124, 0x34838, 0x35284, 0x3535E, 0x35360,
    0x2B42A, 0x2B7E2, 0x2A964, 0x2AB12)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def cached_inputs():
    paths = [ROOT / name for name in ('bin/g9_ol7.o', 'bin/g9_ol5-layout.json',
        'evidence/g9pro-official-firmware-resource.bin', 'evidence/g9pro-firmware-normalized.bin')]
    contents = {path: path.read_bytes() for path in paths}
    assert sha(contents[paths[2]]) == SOURCE_HASH
    code, symbols = elf_text_and_symbols(contents[paths[0]])
    assert len(code) == 2032 and sha(code) == OL7_TEXT_HASH
    layout = copy.deepcopy(json.loads(contents[paths[1]]))
    for hook in layout['hooks']:
        hook['symbol'] = hook['symbol'].replace('g9_ol5', 'g9_ol7')
        hook['target'] = APPEND + symbols[hook['symbol']]['offset']
        hook['new_hex'] = thumb_bl(hook['address'], hook['target']).hex()
    layout['symbols'] = symbols
    assert len(layout['hooks']) == 25 and len(layout['data_patches']) == 2
    for patch in layout['hooks'] + layout['data_patches'] + [layout['descriptor_patch']]:
        offset = patch['address'] - BASE
        old = bytes.fromhex(patch['old_hex'])
        assert contents[paths[3]][offset:offset + len(old)] == old
    return code, layout, contents


class SelectorFixture(RadioFixture):
    def __init__(self, code, layout, stock, active):
        self.stop_at = None
        self.phase = 'initialization'
        self.in_getter = False
        self.events = []
        super().__init__(code, layout)
        if stock:
            for patch in layout['hooks'] + layout['data_patches'] + [layout['descriptor_patch']]:
                self.uc.mem_write(patch['address'], bytes.fromhex(patch['old_hex']))
        self.install_synthetic_flash(active)
        self.set8(0x400B41, 1)  # Stable next peripheral branch, not a query flag.
        self.set8(0x400B5E, 250)
        self.set8(0x400B62, 1)  # RF dispatcher enabled; timers are supplied.
        self.profile_loads.clear()
        self.flash_reads.clear()
        self.application_writes.clear()
        self.events.clear()

    def hook(self, uc, address, size, context):
        if address == self.stop_at:
            uc.emu_stop()
            return
        if address == 0x33B84:
            self.in_getter = True
            self.events.append(dict(event='getter', phase=self.phase))
        elif address == 0x3002A:
            self.in_getter = False
        elif address == 0x30032:
            self.events.append(dict(event='selector_preparation', phase=self.phase))
        elif address == 0x3136C:
            self.events.append(dict(event='worker', phase=self.phase,
                pending=self.pending()))
        elif address == 0x30B78:
            self.events.append(dict(event='profile_load', phase=self.phase,
                in_getter=self.in_getter, profile_zero_based=uc.reg_read(UC_ARM_REG_R0)))
        elif address == 0x30870:
            raise AssertionError('Read reached global-profile persistence')
        if address in UNRELATED_SERVICES:
            self.ret()
            return
        super().hook(uc, address, size, context)

    def pending(self):
        return [self.get8(0x400B2D), self.get8(0x400B2E)]

    def iteration(self):
        # Supply a periodic dispatch opportunity, then execute the real branches
        # through RF dispatcher306CC, worker3136C and original355B8.
        self.set8(0x400B0A, 1)
        self.set8(0x400ACA, 0)
        self.set8(0x400ACB, 0)
        for reg, value in ((UC_ARM_REG_R4, 0x400A7C), (UC_ARM_REG_R5, 0x400B28),
                          (UC_ARM_REG_R6, 0x400B2A), (UC_ARM_REG_R7, 0x400B07)):
            self.uc.reg_write(reg, value)
        self.uc.reg_write(UC_ARM_REG_SP, 0x508000)
        self.uc.reg_write(UC_ARM_REG_LR, 0x600001)
        self.stop_at = 0x36508
        try:
            self.uc.emu_start(0x3643B, 0x600000, count=500000)
        finally:
            self.stop_at = None
        assert self.uc.reg_read(UC_ARM_REG_PC) == 0x36508
        assert self.uc.reg_read(UC_ARM_REG_SP) == 0x508000

    def query(self, report, selector):
        length = REPORT_LENGTHS[report]
        self.phase = 'prefix'
        self.prefix(report, length, get=True, selector=selector)
        prefix_state = self.get8(RFSTATE)
        assert prefix_state == (12 if selector == 0 else 11)
        steps = []
        for index in range(8):
            self.phase = 'prepare_iteration_' + str(index + 1)
            before = dict(state=self.get8(RFSTATE), pending=self.pending())
            self.iteration()
            steps.append(dict(before=before, after=dict(state=self.get8(RFSTATE),
                pending=self.pending())))
            if self.get8(RFSTATE) == 13:
                break
        assert self.get8(RFSTATE) == 13, steps
        assert self.pending() == [0, 0]
        prepared = bytes(self.uc.mem_read(0x400F91, length - 2))
        assert self.get8(REMAIN) == length - 2 and self.get8(SEQ) == 0
        self.phase = 'fragmentation'
        fragments = []
        while self.get8(REMAIN):
            count = min(self.get8(REMAIN), 30)
            sent_before = len(self.rf_sent)
            self.run(0x306CC)  # Actual state13 dispatch and fragment producer.
            assert len(self.rf_sent) == sent_before + 1 and self.get8(RFSTATE) == 14
            frame = self.rf_sent[-1]
            assert frame[:2] == bytes([0x39, len(fragments) + 1])
            fragments.append(frame[2:2 + count])
            self.irq(0x20)     # Supplied successful RF TX-completion interrupt.
            self.run(0x306CC)  # Actual state14 completion returns to state13.
            assert self.get8(RFSTATE) == 13
        self.run(0x306CC)
        assert self.get8(RFSTATE) == 0
        body = b''.join(fragments)
        assert body == prepared
        return bytes([report, length]) + body, prefix_state, steps


def expected_reply(fixture, report, selector, stock, active, mutated):
    length = REPORT_LENGTHS[report]
    header = bytes([report, length]) + (bytes([selector]) if selector else b'')
    if report == 0x0B:
        body = bytes([1, 8]) + (b'' if stock else b'OL7')
    elif report == 0x0C:
        body = bytes([active + 1, (active + 1) ^ 255, 5, 250])
    else:
        profile = selector - 1 if selector else active
        address = struct.unpack('<I', fixture.uc.mem_read(0x4469C + profile * 4, 4))[0]
        sensor = bytearray(fixture.flash_records[address][2:49])
        if mutated and not selector:
            sensor[0] = 0x6D
        body = sensor + sum(sensor).to_bytes(2, 'big')
    assert len(header + body) <= length
    return (header + body).ljust(length, b'\0')


def run_case(code, layout, stock, active, report, selector, mutated=False):
    f = SelectorFixture(code, layout, stock, active)
    if mutated:
        assert report == 4
        f.set8(SENSOR_RAM, 0x6D)
        checksum = sum(bytes(f.uc.mem_read(SENSOR_RAM, 47)))
        f.uc.mem_write(SENSOR_CRC, checksum.to_bytes(2, 'little'))
    before = f.config_snapshot()
    expected = expected_reply(f, report, selector, stock, active, mutated)
    reply, prefix_state, steps = f.query(report, selector)
    assert reply == expected, (report, selector, stock, active, reply.hex(), expected.hex())
    loads = [event for event in f.events if event['event'] == 'profile_load']
    preloads = [event for event in loads if not event['in_getter']]
    restores = [event for event in loads if event['in_getter']]
    assert len(restores) == 1 and restores[0]['profile_zero_based'] == active
    assert [event['profile_zero_based'] for event in preloads] == ([0] if report == 4 and selector == 1 else [])
    assert len(steps) == (1 if selector == 0 else 3 if report == 4 else 2)
    assert not f.flash_writes and not f.global_persists
    if not mutated:
        assert f.config_snapshot() == before
    return dict(firmware='stock' if stock else 'OL7', active_profile=active + 1,
        report=report, selector=selector, runtime_sensor_mutated=mutated,
        raw_logical_reply_hex=reply.hex(), logical_bytes=len(reply), prefix_state=prefix_state,
        prepare_iterations=steps, events=f.events, flash_reads=f.flash_reads,
        profile_loads=f.profile_loads, flash_writes=f.flash_writes,
        config_changed_offsets=[i for i, (a, b) in enumerate(zip(before, f.config_snapshot())) if a != b],
        final_active_profile=f.get8(0x40102C) + 1, final_update_profile=f.get8(0x400B27) + 1)


def main():
    code, layout, cached = cached_inputs()
    cases = []
    for stock in (True, False):
        for active in (0, 2):
            for report in REPORT_LENGTHS:
                for selector in (0, 1):
                    cases.append(run_case(code, layout, stock, active, report, selector))
        for selector in (0, 1):
            cases.append(run_case(code, layout, stock, 2, 4, selector, mutated=True))
    assert len(cases) == 28
    for path, content in cached.items():
        assert path.read_bytes() == content, 'Cached/source artifact changed: ' + str(path)
    result = dict(status='OFFLINE_STOCK_OL7_SELECTOR_AUDIT', cases=cases,
        cached_source_hashes={str(path.relative_to(ROOT)): sha(data) for path, data in cached.items()},
        ol7_text_sha256=sha(code), device_access=False, compilation=False,
        receiver_usb_reassembly_modeled=False, physical_timeout_cause_established=False,
        boundaries=['synthetic valid flash inputs', 'RF FIFO/transmit and IRQ delivery',
            'periodic scheduling gates', 'wake hardware', 'unrelated main-loop services',
            'ROM memcpy/zero/switch and hardware-ID helper351D8'],
        key_findings=['selector0 bypasses state11 preparation, not getter active-profile reload',
            'GET0B/0C selector1 queues no preload; GET04 selector1 queues task22',
            'selector0 replies have2-byte header; selector1 replies have3-byte header',
            'all valid-record cases call active-profile loader after response copy',
            'no flash/global-profile writes reached in tested cases'])
    out = ROOT / 'evidence/rf-selector-audit'
    out.mkdir(parents=True, exist_ok=True)
    target = out / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ') + '-' + uuid.uuid4().hex[:8] + '.json')
    target.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(dict(status=result['status'], cases=len(cases), evidence=str(target),
        stock_ol7_get0b_selector0=[row['raw_logical_reply_hex'] for row in cases
            if row['report'] == 0x0B and row['selector'] == 0 and row['active_profile'] == 1],
        device_access=False, compilation=False), indent=2))


if __name__ == '__main__':
    main()
