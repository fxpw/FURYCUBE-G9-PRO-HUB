"""Compile and exercise the real OL3 Thumb A0/status protocol OFFLINE ONLY.

No USB, serial, installed driver, updater execution or device access. Builds
only own object/layout/payload files. ROM byte-switch, memcpy/unaligned pointer
helpers, USB hardware/FIFO and OLED hardware/render primitives are stubbed.
Stock HID SET/A0 data completion, main read FSM, normal HID GET/ready consumption,
early wake and OL3 status construction execute as original/new machine code.
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
    UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC,
)

from emulate_oled_protocol import StockInstructions, crc16
from emulate_oled_arm import elf_text_and_symbols, ArmFixture, page_packet
from emulate_screen_key import thumb_bl


ROOT = Path(__file__).resolve().parents[1]
BASE, APPEND = 0x28790, 0x47400
SOURCE_HASH = 'f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc'
OL2_HASH = 'e7ed5939d60dff19b856092c57d4bef97e0e3ceb01ba8db34ddf0fcf2797320d'
CONTEXT, READY, FSM = 0x400F88, 0x400B15, 0x400B67
RESPONSE, BODY = 0x400F91, 0x400F93
SETUP, USB_CONTEXT = 0x410000, 0x40111A
PROFILE, STAGE, UPDATE_PROFILE, MACRO_SELECTOR = 0x40102C, 0x40104F, 0x400B27, 0x400BA2
PENDING = (0x400B2D, 0x400B2E, 0x400AA0, 0x400AA1, 0x400AA2, 0x400B14)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def build():
    original = (ROOT/'evidence/g9pro-official-firmware-resource.bin').read_bytes()
    assert sha(original) == SOURCE_HASH
    ol2_file = ROOT/'firmware/g9_oled.payload.hex'
    original_ol2 = ol2_file.read_bytes()
    ol2 = bytes.fromhex(original_ol2.decode('ascii'))
    assert len(ol2) == 280 and sha(ol2) == OL2_HASH
    output = ROOT/'bin/g9_ol3.o'
    output.parent.mkdir(exist_ok=True)
    subprocess.run(['clang', '--target=arm-none-eabi', '-march=armv5t', '-mthumb',
                    '-I', str(ROOT/'firmware'), '-c', str(ROOT/'firmware/g9_ol3.S'),
                    '-o', str(output)], check=True)
    code, symbols = elf_text_and_symbols(output.read_bytes())
    assert code[:280] == ol2, 'Existing OL2 uploader must remain byte-identical'
    assert len(code) <= 36*32, 'Existing aligned updater transfer ceiling exceeded'
    image = (ROOT/'evidence/g9pro-firmware-normalized.bin').read_bytes()
    hooks = []
    for address, expected, symbol in (
        (0x33C94, '11704078', 'g9_ol3_version_hook'),
        (0x33CD4, '00f010ff', 'g9_oled_set_hook'),
        (0x342A4, 'c27204d0', 'g9_ol3_prepare_hook'),
        (0x34308, 'fff73cfc', 'g9_ol3_get_hook'),
    ):
        assert image[address-BASE:address-BASE+4] == bytes.fromhex(expected)
        target = APPEND+symbols[symbol]['offset']
        hooks.append(dict(address=address, normalized_offset=address-BASE,
                          old_hex=expected, new_hex=thumb_bl(address, target).hex(),
                          target=target, symbol=symbol))
    blocks = (len(code)+31)//32
    layout = dict(text_bytes=len(code), text_sha256=sha(code), symbols=symbols,
                  hooks=hooks, append_address=APPEND, extension_crc_blocks=blocks,
                  minimum_resource_size=16+(3939+blocks)*34,
                  version_report_marker='OL3', ol2_prefix_sha256=OL2_HASH,
                  source_sha256=SOURCE_HASH, physical_validated=False)
    assert ol2_file.read_bytes() == original_ol2
    return code, layout


def expected_status(status, profile=0, stage=0, dpi=0):
    reply = bytearray(132)
    reply[:6] = bytes([0x22, 132, 0x47, 0x53, 3, status])
    if status == 1:
        reply[6:10] = bytes([profile+1, stage, dpi & 255, dpi >> 8])
    reply[130:] = crc16(reply[:130]).to_bytes(2, 'little')
    return bytes(reply)


class FlowFixture(StockInstructions):
    def __init__(self, code, layout, patched=True, candidate_image=None):
        self.wake_calls = 0
        self.render_calls = 0
        self.display_init_calls = 0
        self.profile_loads = []
        self.flash_reads = []
        self.flash_writes = []
        self.usb_replies = []
        self.mmio_writes = []
        self.application_writes = []
        self.fifo = b''
        self.rom_pointer_slots = {}
        self.allow_profile_load = False
        self.global_flash = None
        self.flash_records = {}
        self.global_persists = []
        self.allow_config_history = False
        self.capture_oled = False
        self.oled_dpi = None
        self.minimum_sp = 0x508000
        super().__init__()
        self.uc.mem_map(0x800000, 0x10000)
        if candidate_image is not None:
            self.uc.mem_write(BASE, candidate_image)
        else:
            self.uc.mem_write(APPEND, code)
            if patched:
                for hook in layout['hooks']:
                    self.uc.mem_write(hook['address'], bytes.fromhex(hook['new_hex']))
        self.uc.hook_add(UC_HOOK_MEM_WRITE, self.write_hook)
        self.set8(0x400B02, 1)
        self.set8(0x400A7C, 1)
        self.set8(0x400B28, 0)

    def set8(self, address, value):
        self.uc.mem_write(address, bytes([value]))

    def get8(self, address):
        return self.uc.mem_read(address, 1)[0]

    def write_hook(self, _uc, _access, address, size, value, _context):
        if 0x401020 <= address < 0x4010AE:
            self.application_writes.append((address, size, value))
        if 0x800000 <= address < 0x810000:
            self.mmio_writes.append((address, size, value))

    def hook(self, uc, address, size, context):
        self.minimum_sp = min(self.minimum_sp, uc.reg_read(UC_ARM_REG_SP))
        if address == 0x42F64 and self.capture_oled:
            self.oled_dpi = uc.reg_read(UC_ARM_REG_R2)
            uc.emu_stop()
        elif address == 0x34AF8:
            self.wake_calls += 1  # Execute the REAL wake body, not a no-op stub.
        elif address == 0x3CDDE:
            self.display_init_calls += 1
            self.ret()
        elif address == 0x3D51E:
            self.render_calls += 1
            self.ret()  # Explicit display-render boundary; no table mutation assumed here.
        elif address == 0x37FDE:
            uc.reg_write(UC_ARM_REG_R0, 1)  # Validated interface request, hardware context stub.
            self.ret()
        elif address == 0x37510:
            uc.reg_write(UC_ARM_REG_R0, 1)  # EP0 OUT data available.
            self.ret()
        elif address == 0x37528:
            target = uc.reg_read(UC_ARM_REG_R1)
            uc.mem_write(target, self.fifo)
            uc.reg_write(UC_ARM_REG_R0, len(self.fifo))
            self.ret()
        elif address == 0x42A02:
            value, target = (uc.reg_read(r) for r in (UC_ARM_REG_R0, UC_ARM_REG_R1))
            uc.mem_write(target, struct.pack('<I', value))
            self.ret()  # ROM unaligned pointer setter.
        elif address == 0x429EE:
            pointer = uc.reg_read(UC_ARM_REG_R0)
            uc.reg_write(UC_ARM_REG_R0, struct.unpack('<I', uc.mem_read(pointer, 4))[0])
            self.ret()  # ROM unaligned pointer getter.
        elif address == 0x37FAA:
            pointer, count = (uc.reg_read(r) for r in (UC_ARM_REG_R2, UC_ARM_REG_R3))
            self.usb_replies.append(bytes(uc.mem_read(pointer, count)))
            self.ret()  # EP0 transfer submission, not physical USB.
        elif address in (0x374AC, 0x37610, 0x37628, 0x375EA, 0x351D8):
            self.ret()  # Delay, endpoint handshakes and peripheral polling setup.
        elif address == 0x24F9C:
            destination, source, count = (uc.reg_read(r) for r in
                                           (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2))
            uc.mem_write(destination, bytes(uc.mem_read(source, count)))
            if destination < 0x4010AE and destination+count > 0x401020:
                self.application_writes.append((destination, count, 'ROM memcpy'))
            self.ret()
        elif address == 0x30B78:
            self.profile_loads.append(uc.reg_read(UC_ARM_REG_R0))
            assert self.allow_profile_load, 'Custom status reached stock profile load'
            # When enabled for history tests, execute the original loader on synthetic flash.
        elif address == 0x30870:
            assert self.allow_config_history, 'Status must never persist profile metadata'
            self.global_persists.append(self.get8(PROFILE))
            self.ret()  # Explicit synthetic-history persistence boundary, no NVM instructions.
        elif address == 0x39BC2:
            source, count, destination = (uc.reg_read(r) for r in
                                         (UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3))
            self.flash_reads.append((source, count))
            if source == 0x7C000 and self.global_flash is not None:
                record = self.global_flash
            else:
                assert source in self.flash_records, (hex(source), count)
                record = self.flash_records[source]
            assert count <= len(record)
            uc.mem_write(destination, record[:count])
            uc.reg_write(UC_ARM_REG_R0, 0)
            self.ret()  # Explicit offline flash input, never a real read.
        elif address in (0x39CB0, 0x39BF0, 0x39C56, 0x39924, 0x39446):
            self.flash_writes.append(address)
            raise AssertionError(f'Forbidden flash/update routine {address:#x}')
        else:
            super().hook(uc, address, size, context)

    def run(self, address):
        canary = bytes([0xA7])*96
        self.uc.mem_write(0x507E00, canary)
        registers = (UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7)
        before = tuple(self.uc.reg_read(reg) for reg in registers)
        super().run(address)
        assert bytes(self.uc.mem_read(0x507E00, len(canary))) == canary, 'Stack canary damaged'
        assert tuple(self.uc.reg_read(reg) for reg in registers) == before, 'Callee-saved register changed'

    def setup_fields(self, profile, stage, values):
        self.uc.mem_write(0x401020, bytes((i*7+3) & 255 for i in range(0x8e)))
        self.set8(PROFILE, profile)
        self.set8(STAGE, stage)
        self.set8(UPDATE_PROFILE, profile)
        self.set8(MACRO_SELECTOR, 23)
        for index, dpi in enumerate(values, 1):
            self.set8(0x40103E+index, dpi & 255)
            self.set8(0x401046+index, dpi >> 8)

    def hid(self, request, report, count):
        setup = bytes([0x21 if request == 9 else 0xA1, request, report, 3, 2, 0, count & 255, count >> 8])
        self.uc.mem_write(SETUP, setup)
        self.uc.reg_write(UC_ARM_REG_R0, SETUP)
        self.uc.reg_write(UC_ARM_REG_R1, USB_CONTEXT)
        before = len(self.usb_replies)
        self.run(0x388A8)
        return self.usb_replies[-1] if len(self.usb_replies) != before else None

    def submit_query(self, report=0x22, length=132, selector=0, query_bytes=None):
        assert self.get8(READY) == 0 and self.get8(FSM) == 0
        assert self.hid(1, 0xA0, 8) == bytes.fromhex('a0 00 00 00 00 00 00 00')
        self.hid(9, 0xA0, 8)
        self.fifo = bytes(query_bytes) if query_bytes is not None else bytes([0xA0, report, length, 0, selector, 0, 0, 0])
        self.run(0x3778E)  # Original EP0 OUT completion parses the real A0 bytes.
        assert self.get8(0x400B14) == 1
        assert self.get8(CONTEXT+2) == report
        assert self.get8(0x400BB0) == length
        assert self.get8(0x400B9F) == selector

    def finish_query(self, report=0x22, get_length=132, worker=False):
        for _ in range(12):
            self.run(0x34216)
            if self.get8(READY):
                break
            if worker:
                self.run(0x3136C)
        assert self.get8(READY) == 1, ('Reply never became ready', self.get8(FSM))
        assert self.hid(1, 0xA0, 8) == bytes.fromhex('a0 01 00 00 00 00 00 00')
        result = self.hid(1, report, get_length)
        assert self.get8(READY) == 0
        self.run(0x34216)
        assert self.get8(FSM) == 0
        assert self.hid(1, report, get_length) is None, 'Response must be consumed only once'
        return result

    def config_snapshot(self):
        return bytes(self.uc.mem_read(0x401020, 0x8e))

    def upload_page(self, data):
        self.commands.clear()
        self.bytes_on_bus.clear()
        assert self.get8(FSM) == self.get8(READY) == 0
        self.hid(9, 0x22, 132)
        self.fifo = bytes(data)
        self.run(0x3778E)
        self.run(0x34216)
        self.run(0x34216)
        assert self.get8(FSM) == self.get8(READY) == 0

    def stock_oled_numeric(self):
        self.capture_oled, self.oled_dpi = True, None
        self.uc.reg_write(UC_ARM_REG_SP, 0x508000)
        self.uc.reg_write(UC_ARM_REG_LR, 0x600001)
        self.uc.emu_start(0x3D329, 0x600000, count=500)
        self.capture_oled = False
        assert self.oled_dpi is not None
        return self.oled_dpi

    def install_synthetic_flash(self, initial_profile):
        """Private fixture, not vendor data and never a physical flash operation."""
        global_data = bytearray([0, 255, 0, 0, 0, 0, 1, 254, 1, 254,
                                 8, 247, initial_profile, initial_profile ^ 255, 4, 251])
        self.global_flash = bytes(global_data)
        self.history_tables = {}
        for profile in range(5):
            sensor = bytearray(47)
            sensor[2], sensor[21] = 255, profile+1
            dpi_values = tuple(800 + profile*400 + index*50 for index in range(8))
            for index, dpi in enumerate(dpi_values):
                sensor[5+index], sensor[13+index] = dpi & 255, dpi >> 8
            light, buttons = bytes(8), bytes(54)
            record = (bytes.fromhex('44bb') + bytes(sensor) + sum(sensor).to_bytes(2, 'big')
                      + light + sum(light).to_bytes(2, 'big')
                      + buttons + sum(buttons).to_bytes(2, 'big'))
            assert len(record) == 117
            address = struct.unpack('<I', self.uc.mem_read(0x4469C+profile*4, 4))[0]
            self.flash_records[address] = record
            self.history_tables[profile] = dpi_values
        self.allow_profile_load = True
        self.run(0x30968)  # Original global-record loader initializes active and Update_Profile.
        assert self.get8(PROFILE) == self.get8(UPDATE_PROFILE) == initial_profile
        self.uc.reg_write(UC_ARM_REG_R0, initial_profile)
        self.run(0x30B78)  # Execute actual checksum/section loader, with synthetic read-only flash.
        assert self.get8(STAGE) == initial_profile+1

    def set_profile_from_main(self, profile):
        self.uc.mem_write(CONTEXT+2, bytes([0x0C, 8]))
        self.uc.mem_write(CONTEXT+4, struct.pack('<I', BODY))
        self.uc.mem_write(BODY, bytes([profile+1, (profile+1)^255, 5, 250]))
        self.run(0x33CD2)  # Real profile-setting dispatcher, no USB command involved.
        assert self.get8(PROFILE) == profile and self.get8(0x400B2E) == 12

    def finish_profile_work(self):
        self.allow_config_history = True
        for _ in range(12):
            self.run(0x3136C)
            if not self.get8(0x400B2D) and not self.get8(0x400B2E):
                break
        assert not self.get8(0x400B2D) and not self.get8(0x400B2E)
        self.allow_config_history = False


def run_regressions(code, layout, candidate_image=None):
    values = (1, 255, 256, 800, 12000, 13000, 32768, 65535)
    checks = 0
    for profile in range(5):
        for stage, dpi in enumerate(values, 1):
            fixture = FlowFixture(code, layout, candidate_image=candidate_image)
            fixture.setup_fields(profile, stage, values)
            before = fixture.config_snapshot()
            fixture.application_writes.clear()
            fixture.submit_query()
            response = fixture.finish_query()
            assert response == expected_status(1, profile, stage, dpi), response.hex()
            assert fixture.config_snapshot() == before and not fixture.application_writes
            assert fixture.get8(UPDATE_PROFILE) == profile and fixture.get8(MACRO_SELECTOR) == 23
            assert fixture.wake_calls == 1 and not fixture.profile_loads and not fixture.flash_reads
            assert not fixture.flash_writes
            assert fixture.stock_oled_numeric() == dpi
            checks += 1

    for address in PENDING:
        fixture = FlowFixture(code, layout, candidate_image=candidate_image)
        fixture.setup_fields(3, 4, values)
        before = fixture.config_snapshot()
        fixture.submit_query()
        fixture.run(0x34216)  # Prepare custom state3, before introducing the concurrent event.
        fixture.set8(address, 1)
        assert fixture.finish_query() == expected_status(2)
        assert fixture.config_snapshot() == before and fixture.get8(address) == 1
        assert not fixture.profile_loads and not fixture.flash_reads and not fixture.flash_writes
        checks += 1

    for profile, stage, dpi_values in ((5, 1, values), (255, 1, values), (0, 0, values),
                                       (0, 9, values), (0, 255, values), (0, 1, (0,)*8)):
        fixture = FlowFixture(code, layout, candidate_image=candidate_image)
        fixture.setup_fields(profile, stage, dpi_values)
        fixture.submit_query()
        assert fixture.finish_query() == expected_status(0)
        assert not fixture.profile_loads and not fixture.flash_reads
        checks += 1

    for length, selector in ((131, 0), (133, 0), (132, 1), (132, 5), (132, 255)):
        fixture = FlowFixture(code, layout, candidate_image=candidate_image)
        fixture.setup_fields(4, 4, values)
        before = fixture.config_snapshot()
        fixture.submit_query(length=length, selector=selector)
        assert fixture.finish_query() == expected_status(0)
        assert fixture.config_snapshot() == before and fixture.get8(UPDATE_PROFILE) == 4
        assert fixture.get8(MACRO_SELECTOR) == 23 and not fixture.profile_loads and not fixture.flash_reads
        checks += 1

    for asleep, gesture in ((0, 0), (1, 0), (1, 1)):
        fixture = FlowFixture(code, layout, candidate_image=candidate_image)
        fixture.setup_fields(2, 3, values)
        fixture.set8(0x400A7A, asleep)
        fixture.set8(0x400AF1, gesture)
        before = fixture.config_snapshot()
        fixture.submit_query()
        assert fixture.finish_query() == expected_status(1, 2, 3, 256)
        assert fixture.wake_calls == 1 and fixture.get8(0x400A7A) == 0
        assert fixture.display_init_calls == int(asleep and not gesture)
        assert fixture.render_calls == int(asleep and not gesture)
        assert fixture.config_snapshot() == before and not fixture.profile_loads and not fixture.flash_reads
        checks += 1

    for address, changed in ((CONTEXT+1, b'\x01'), (CONTEXT+4, struct.pack('<I', 0x410000)),
                              (0x400B28, b'\x01'), (0x400A7C, b'\x00')):
        fixture = FlowFixture(code, layout, candidate_image=candidate_image)
        fixture.setup_fields(2, 3, values)
        before = fixture.config_snapshot()
        fixture.submit_query()
        fixture.run(0x34216)
        fixture.uc.mem_write(address, changed)
        assert fixture.finish_query() == expected_status(0)
        assert fixture.config_snapshot() == before and not fixture.profile_loads and not fixture.flash_reads
        checks += 1

    for offset in (3, 5, 6, 7):
        fixture = FlowFixture(code, layout, candidate_image=candidate_image)
        fixture.setup_fields(2, 3, values)
        before = fixture.config_snapshot()
        query = bytearray.fromhex('a0 22 84 00 00 00 00 00')
        query[offset] = 1
        fixture.submit_query(query_bytes=query)
        assert fixture.finish_query() == expected_status(0)
        assert fixture.config_snapshot() == before and not fixture.profile_loads and not fixture.flash_reads
        checks += 1

    fixture = FlowFixture(code, layout, candidate_image=candidate_image)
    fixture.setup_fields(2, 3, values)
    fixture.set8(UPDATE_PROFILE, 1)
    fixture.submit_query()
    assert fixture.finish_query() == expected_status(2)
    assert fixture.get8(UPDATE_PROFILE) == 1
    checks += 1

    fixture = FlowFixture(code, layout, candidate_image=candidate_image)
    fixture.setup_fields(2, 4, values)
    fixture.submit_query()
    assert fixture.finish_query() == expected_status(1, 2, 4, 800)
    fixture.set8(0x40103E+4, 1600 & 255)
    fixture.set8(0x401046+4, 1600 >> 8)
    fixture.submit_query()
    assert fixture.finish_query() == expected_status(1, 2, 4, 1600), 'Second read reused a cached table or old reply'
    checks += 1

    fixture = FlowFixture(code, layout, candidate_image=candidate_image)
    fixture.setup_fields(2, 4, values)
    before = fixture.config_snapshot()
    fixture.submit_query()
    assert fixture.finish_query() == expected_status(1, 2, 4, 800)
    payload = bytes((i*3) & 255 for i in range(120))
    fixture.upload_page(page_packet(0, payload, flags=1))
    assert fixture.commands == [0xB0, 0x10, 0]
    assert fixture.bytes_on_bus == [0x78, 0x40]+list(payload)
    fixture.submit_query()
    assert fixture.finish_query() == expected_status(1, 2, 4, 800)
    assert fixture.config_snapshot() == before and not fixture.profile_loads and not fixture.flash_reads
    checks += 1

    print(f'PASS {checks} complete A0/HID/MAIN status cases; single consumption; one expected transport wake; no profile/config/NVM mutation')
    history_cases = 0
    for profile in range(5):
        fixture = FlowFixture(code, layout, candidate_image=candidate_image)
        fixture.install_synthetic_flash(profile)
        # A legacy query really selects profile0, then stock GetData reloads active before ready1.
        fixture.submit_query(report=0x0B, length=8, selector=1)
        version = fixture.finish_query(report=0x0B, get_length=8, worker=True)
        assert version == bytes.fromhex('0b080101084f4c33'), version.hex()
        assert fixture.profile_loads == [profile, 0, profile]
        assert fixture.get8(UPDATE_PROFILE) == fixture.get8(PROFILE) == profile
        assert fixture.get8(STAGE) == profile+1
        before = fixture.config_snapshot()
        reads, loads, writes = len(fixture.flash_reads), len(fixture.profile_loads), len(fixture.global_persists)
        fixture.submit_query()
        assert fixture.finish_query() == expected_status(1, profile, profile+1, fixture.history_tables[profile][profile])
        assert before == fixture.config_snapshot()
        assert (len(fixture.flash_reads), len(fixture.profile_loads), len(fixture.global_persists)) == (reads, loads, writes)
        history_cases += 1

        new_profile = (profile+1) % 5
        fixture.set_profile_from_main(new_profile)
        # The real setter changes profile immediately; old stage/table remain until worker completion.
        before = fixture.config_snapshot()
        fixture.submit_query()
        assert fixture.finish_query() == expected_status(2)
        assert fixture.config_snapshot() == before
        fixture.finish_profile_work()
        assert fixture.get8(UPDATE_PROFILE) == fixture.get8(PROFILE) == new_profile
        assert fixture.get8(STAGE) == new_profile+1
        fixture.submit_query()
        assert fixture.finish_query() == expected_status(1, new_profile, new_profile+1,
                                                         fixture.history_tables[new_profile][new_profile])
        history_cases += 1
    print(f'PASS {history_cases} original global-load/legacy-read-restore/profile-SET/main-worker histories; profiles1..5 do not remain BUSY')

    legacy_cases = 0
    for report in (0, 1, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 0x21, 0x2E, 255):
        for selector in (0, 1, 255):
            original = FlowFixture(code, layout, patched=False)
            patched = FlowFixture(code, layout, candidate_image=candidate_image)
            for item in (original, patched):
                item.setup_fields(3, 4, values)
                item.submit_query(report=report, length=132, selector=selector)
                item.run(0x34216)
            assert bytes(original.uc.mem_read(0x400000, 0x20000)) == bytes(patched.uc.mem_read(0x400000, 0x20000)), (report, selector)
            assert original.wake_calls == patched.wake_calls == 1
            legacy_cases += 1
    print(f'PASS {legacy_cases} non22 prepare histories: original and hooked RAM/state/selector behavior agree')

    uploader = ArmFixture(code, layout)
    if candidate_image is not None:
        uploader.uc.mem_write(BASE, candidate_image)
    assert uploader.version() == bytes.fromhex('0b050101084f4c33')
    for page in range(4):
        payload = bytes((index*7+page) & (15 if page == 3 else 255) for index in range(120))
        uploader.set_packet(page_packet(page, payload, flags=int(page == 0)))
        assert uploader.commands == [0xB0+page, 0x10, 0]
        assert uploader.bytes_on_bus == [0x78, 0x40]+list(payload)
    assert uploader.init_calls == 1
    good = page_packet(0, bytes(120))
    invalid_pages = []
    for offset, value in ((0, 0x23), (1, 131), (2, 0), (3, 0), (4, 1),
                          (5, 2), (6, 1), (7, 4), (8, 119), (9, 2)):
        item = bytearray(good)
        item[offset] = value
        item[130:] = struct.pack('<H', crc16(item[:130]))
        invalid_pages.append(bytes(item))
    item = bytearray(good)
    item[50] ^= 1
    invalid_pages.append(bytes(item))
    invalid_pages.append(page_packet(3, bytes([0xF0])*120))
    for page_data in invalid_pages:
        counts = uploader.wake_calls, uploader.init_calls, uploader.draw_calls
        uploader.set_packet(page_data)
        assert counts == (uploader.wake_calls, uploader.init_calls, uploader.draw_calls)
    for length, pointer in ((131, 0x400F93), (133, 0x400F93), (132, 0x410000)):
        counts = uploader.wake_calls, uploader.init_calls, uploader.draw_calls
        uploader.set_packet(good, transport_length=length, payload_pointer=pointer)
        assert counts == (uploader.wake_calls, uploader.init_calls, uploader.draw_calls)
    print('PASS inherited byte-identical GO2 uploader: 4 real draw paths, 15 malformed rejections, OL3 marker')

    return dict(whole_status_cases=checks, real_a0_set_data_fsm_get_executed=True,
                legacy_prepare_cases=legacy_cases, profile_history_cases=history_cases,
                inherited_go2_pages=4, inherited_go2_rejections=15, full_status_page_status_interleave=True,
                stack_canary_and_callee_saved=True,
                expected_transport_wake=True,
                no_custom_profile_load_or_flash=True, candidate_image_executed=candidate_image is not None)


def main():
    code, layout = build()
    result = run_regressions(code, layout)
    (ROOT/'bin/g9_ol3-layout.json').write_text(json.dumps(layout, indent=2), encoding='utf-8')
    print(json.dumps(dict(text_bytes=len(code), text_sha256=sha(code), result=result)))
    print('OFFLINE ONLY: ROM/FIFO/display primitives stubbed; no device/USB/firmware installation')


if __name__ == '__main__':
    main()
