"""OL4 offline regression: real EP0 64-byte OUT completion and GS4 FSM.
FIFO/ROM/display primitives remain emulated; no hardware or updater access.
The pre-fix132B failure is exercised as64+64+4, not a synthetic132B FIFO.
"""
from pathlib import Path
import hashlib,json,struct,subprocess
from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_WRITE
from unicorn.arm_const import (
    UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3,
    UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7,
    UC_ARM_REG_R8, UC_ARM_REG_R9, UC_ARM_REG_R10, UC_ARM_REG_R11,
    UC_ARM_REG_R12, UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC,
    UC_ARM_REG_CPSR,
)
from emulate_ol3_status import (FlowFixture as LegacyFlowFixture, CONTEXT,READY,FSM,PROFILE,STAGE,UPDATE_PROFILE,MACRO_SELECTOR,PENDING)
from emulate_oled_arm import elf_text_and_symbols
from emulate_oled_protocol import crc16
from emulate_screen_key import thumb_bl
ROOT=Path(__file__).resolve().parents[1]
BASE,APPEND=0x28790,0x47400
SOURCE_HASH='f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc'
def sha(data):return hashlib.sha256(data).hexdigest()
def build():
    original=(ROOT/'evidence/g9pro-official-firmware-resource.bin').read_bytes()
    assert sha(original)==SOURCE_HASH
    output=ROOT/'bin/g9_ol4.o';output.parent.mkdir(exist_ok=True)
    subprocess.run(['clang','--target=arm-none-eabi','-march=armv5t','-mthumb','-c',str(ROOT/'firmware/g9_ol4.S'),'-o',str(output)],check=True)
    code,symbols=elf_text_and_symbols(output.read_bytes())
    assert len(code)<=36*32
    image=(ROOT/'evidence/g9pro-firmware-normalized.bin').read_bytes()
    hooks=[]
    for address,expected,symbol in (
        (0x33C94,'11704078','g9_ol4_version_hook'),
        (0x33CD4,'00f010ff','g9_ol4_set_hook'),
        (0x342A4,'c27204d0','g9_ol4_prepare_hook'),
        (0x34308,'fff73cfc','g9_ol4_get_hook'),
        (0x37896,'06702a78','g9_ol4_ep0_mouse_ready_hook')):
        assert image[address-BASE:address-BASE+4]==bytes.fromhex(expected)
        target=APPEND+symbols[symbol]['offset']
        hooks.append(dict(address=address,normalized_offset=address-BASE,old_hex=expected,new_hex=thumb_bl(address,target).hex(),target=target,symbol=symbol))
    descriptor=image[0x44890-BASE:0x4497C-BASE]
    needle=bytes.fromhex('85 22 95 83 b1 01')
    assert descriptor.count(needle)==1
    address=0x44890+descriptor.index(needle)+3
    assert image[address-BASE]==131
    descriptor_patch=dict(address=address,normalized_offset=address-BASE,old_hex='83',new_hex='3f',reason='Report22 feature payload131 to63 bytes; total64')
    blocks=(len(code)+31)//32
    layout=dict(text_bytes=len(code),text_sha256=sha(code),symbols=symbols,hooks=hooks,descriptor_patch=descriptor_patch,append_address=APPEND,extension_crc_blocks=blocks,minimum_resource_size=16+(3939+blocks)*34,version_report_marker='OL4',source_sha256=SOURCE_HASH,physical_validated=False)
    return code,layout

def expected_status(status,profile=0,stage=0,dpi=0):
    reply=bytearray(64);reply[:6]=bytes([0x22,64,0x47,0x53,4,status])
    if status==1:reply[6:10]=bytes([profile+1,stage,dpi&255,dpi>>8])
    reply[62:]=crc16(reply[:62]).to_bytes(2,'little')
    return bytes(reply)

def chunk_packet(page,x,payload,flags=0):
    assert len(payload)==40
    data=bytearray(64);data[:12]=bytes([0x22,64,0x47,0x4f,4,1,0,page,x,40,flags,0]);data[12:52]=payload
    data[62:]=crc16(data[:62]).to_bytes(2,'little')
    return bytes(data)

class FlowFixture(LegacyFlowFixture):
    def __init__(self,code,layout,patched=True,candidate_image=None):
        super().__init__(code,layout,patched,candidate_image)
        if patched and candidate_image is None:
            p=layout['descriptor_patch'];self.uc.mem_write(p['address'],bytes.fromhex(p['new_hex']))
    def submit_query(self,report=0x22,length=64,selector=0,query_bytes=None):
        return super().submit_query(report,length,selector,query_bytes)
    def finish_query(self,report=0x22,get_length=64,worker=False):
        return super().finish_query(report,get_length,worker)
    def upload_chunk(self,data,run_main=True):
        assert len(data)<=64,'One physical EP0 OUT packet maximum'
        self.commands.clear();self.bytes_on_bus.clear()
        assert self.get8(FSM)==self.get8(READY)==0
        self.hid(9,0x22,len(data));self.fifo=bytes(data);self.run(0x3778E)
        if run_main:
            self.run(0x34216);self.run(0x34216)
            assert self.get8(FSM)==self.get8(READY)==0
    def direct_chunk(self,data,transport_length=64,payload_pointer=0x400F93):
        self.commands.clear();self.bytes_on_bus.clear()
        self.uc.mem_write(CONTEXT+2,bytes([0x22,transport_length]));self.uc.mem_write(CONTEXT+4,struct.pack('<I',payload_pointer));self.uc.mem_write(0x400F91,bytes(data))
        self.run(0x33CD2)

    def submit_query_with_idle_irq(self, report, length, selector, pause_at):
        """One serialized A0 OUT completion interrupts the idle main FSM.

        Execute the original USB callback, preserving the interrupted CPU and
        its main stack as an IRQ would. No second query, duplicate completion,
        concurrent owner or synthetic ready/header write is introduced.
        """
        assert self.get8(READY) == self.get8(FSM) == 0
        assert self.hid(1, 0xA0, 8) == bytes.fromhex('a000000000000000')
        self.hid(9, 0xA0, 8)  # SETUP, before this request's sole OUT completion.
        interrupted = [False]
        def pause(uc, address, _size, _context):
            if address == pause_at and not interrupted[0]:
                interrupted[0] = True
                uc.emu_stop()
        hook = self.uc.hook_add(UC_HOOK_CODE, pause)
        self.uc.reg_write(UC_ARM_REG_SP, 0x508000)
        self.uc.reg_write(UC_ARM_REG_LR, 0x600001)
        try:
            self.uc.emu_start(0x34217, 0x600000, count=20000)
        finally:
            self.uc.hook_del(hook)
        assert interrupted[0], ('Unreached idle FSM instruction', hex(pause_at))
        registers = (
            UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3,
            UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7,
            UC_ARM_REG_R8, UC_ARM_REG_R9, UC_ARM_REG_R10, UC_ARM_REG_R11,
            UC_ARM_REG_R12, UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC,
            UC_ARM_REG_CPSR,
        )
        cpu = tuple(self.uc.reg_read(reg) for reg in registers)
        # The fixture's normal run() uses a single synthetic stack region.
        # Preserve it here instead of pretending an IRQ shares the main stack.
        stack = bytes(self.uc.mem_read(0x507000, 0x1100))
        self.fifo = bytes([0xA0, report, length, 0, selector, 0, 0, 0])
        self.run(0x3778E)
        self.uc.mem_write(0x507000, stack)
        for reg, value in zip(registers, cpu):
            self.uc.reg_write(reg, value)
        self.uc.emu_start(pause_at | 1, 0x600000, count=20000)
        assert self.uc.reg_read(UC_ARM_REG_PC) == 0x600000
        assert self.uc.reg_read(UC_ARM_REG_SP) == 0x508000


# Every executed Thumb instruction boundary in the idle FSM from entry up to
# the queued-request test. The 0x3423A..40 bytes are the ROM switch table, not
# executed instructions. At 0x34246 the old zero pending flag has already been
# loaded: the IRQ queues work for the following tick, which must also succeed.
IDLE_IRQ_BOUNDARIES = (
    0x34216, 0x34218, 0x3421A, 0x3421C, 0x3421E, 0x34220,
    0x34222, 0x34224, 0x34226, 0x34228, 0x3422A, 0x3422C,
    0x3422E, 0x34230, 0x34232, 0x34234, 0x34236,
    0x34242, 0x34244, 0x34246,
)


def run_claim_irq_regressions(code, layout, candidate_image=None):
    def prepared(previous, initial_profile=0):
        f = FlowFixture(code, layout, candidate_image=candidate_image)
        f.install_synthetic_flash(initial_profile)
        # Start from a real legacy response, then the requested prior response.
        f.submit_query(report=0x0B, length=8, selector=1)
        assert f.finish_query(report=0x0B, get_length=8, worker=True) == bytes.fromhex('0b080101084f4c34')
        if previous != (0x0B, 8, 1):
            f.submit_query(report=previous[0], length=previous[1], selector=previous[2])
            f.finish_query(report=previous[0], get_length=previous[1], worker=True)
        return f

    version = (0x0B, 8, 1)
    status = (0x22, 64, 0)
    transitions = []
    # Profile selectors 1..5 must select the intended record, then restore the
    # active runtime profile, even when the idle entry sampled the previous GET.
    for report, length in ((0x0B, 8), (4, 52), (6, 9), (8, 59), (0x0A, 120), (0x0C, 8)):
        for selector in range(1, 6):
            transitions.append((status, (report, length, selector), 0))
    for active in range(5):
        transitions.append((version, status, active))
    for report, length in ((4, 52), (6, 9), (8, 59), (0x0A, 120), (0x0C, 8)):
        transitions.append(((report, length, 1), status, 0))

    checks = 0
    for previous, request, active in transitions:
        baseline = prepared(previous, active)
        baseline.submit_query(report=request[0], length=request[1], selector=request[2])
        expected = baseline.finish_query(report=request[0], get_length=request[1], worker=True)
        expected_runtime = baseline.config_snapshot()
        expected_loads = baseline.profile_loads[:]
        for boundary in IDLE_IRQ_BOUNDARIES:
            fixture = prepared(previous, active)
            fixture.submit_query_with_idle_irq(*request, pause_at=boundary)
            actual = fixture.finish_query(report=request[0], get_length=request[1], worker=True)
            assert actual == expected, (previous, request, active, hex(boundary), actual.hex(), expected.hex())
            assert fixture.config_snapshot() == expected_runtime
            assert fixture.profile_loads == expected_loads
            assert not fixture.flash_writes
            checks += 1

    # Prove the exact observed 22 08 02 01 08 OL4 failure before the claim fix.
    # Revert only the metadata-reload prefix in emulator memory. Replay the old
    # STRB selector / PUSH r1 / LDRB report, then branch to the existing CMP.
    # The candidate and every file remain untouched; no old artifact is needed.
    broken = prepared(status)
    target = APPEND + layout['symbols']['g9_ol4_prepare_hook']['offset']
    assert bytes(broken.uc.mem_read(target + 20, 2)) == bytes.fromhex('2229')
    broken.uc.mem_write(target, bytes.fromhex('c27202b4817805e0') + bytes.fromhex('c046') * 6)
    broken.uc.ctl_remove_cache(target, target + 20)
    broken.submit_query_with_idle_irq(*version, pause_at=0x34230)
    observed = broken.finish_query(report=0x0B, get_length=8, worker=True)
    assert observed == bytes.fromhex('22080201084f4c34'), observed.hex()
    print(f'PASS {checks} serialized A0 OUT IRQ boundary/transition cases; exact pre-fix 22080201084f4c34 reproduced; claimed metadata reload removes stale headers/selectors')
    return checks


class MouseEndpointFixture(FlowFixture):
    """Execute real mouse-report construction, FIFO writes and TX callback.

    The supplied controller-busy condition describes a previous IN transfer;
    there is no simulated host consumption or claim about a real USB stall.
    The GO4 EP0 completion and mouse path themselves execute native code.
    """
    def __init__(self, code, layout, candidate_image=None, guard=True):
        self.submissions = []
        self.endpoint_fifo_writes = []
        self.controller_busy = {1: False, 2: True}
        super().__init__(code, layout, candidate_image=candidate_image)
        if not guard:
            self.uc.mem_write(0x37896, bytes.fromhex('06702a78'))
            self.uc.ctl_remove_cache(0x37896, 0x3789A)
        self.uc.hook_add(UC_HOOK_MEM_WRITE, self.fifo_write)

    def hook(self, uc, address, size, context):
        if address == 0x376AA:
            endpoint, pointer, count = (uc.reg_read(reg) for reg in
                                       (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2))
            self.submissions.append((endpoint, bytes(uc.mem_read(pointer, count))))
            # Do not return/stub: run 376AA and the actual 3759C FIFO writer.
        super().hook(uc, address, size, context)

    def fifo_write(self, _uc, _access, address, size, value, _context):
        for endpoint in (1, 2):
            if address == 0x804020 + 4 * endpoint:
                self.endpoint_fifo_writes.append((endpoint, size, value, self.controller_busy[endpoint]))

    def pending_mouse(self, buttons, dx=3, dy=-2):
        self.set8(0x400B23, 0)     # Actual EP2 IN completion has not occurred.
        self.set8(0x400AAE, 10)    # Existing retry counter has not expired.
        self.set8(0x400BA8, 1)     # Preserve already sampled motion below.
        self.set8(0x400A79, 0)     # Sensor hardware sampling is outside this test.
        self.set8(0x400B11, 0)     # Existing boot-protocol mouse format:3 bytes.
        self.set8(0x400AF3, 1)     # Buttons dirty; include both L/R and releases.
        self.set8(0x400AF4, int(bool(dx or dy)))
        self.set8(0x400B96, buttons)
        self.uc.mem_write(0x400B8A, struct.pack('<hh', dx, dy))

    def poll_input(self):
        self.set8(0x400B1A, 1)     # One existing input-poll timer event.
        self.run(0x38BF2)

    def complete_mouse_transfer(self):
        self.controller_busy[2] = False
        self.run(0x387A0)         # The real registered EP2 IN callback.
        assert self.get8(0x400B23) == 1

    def interrupt_mouse_poll_with_chunk(self, boundary):
        self.hid(9, 0x22, 64)
        self.set8(0x400B1A, 1)
        paused = [False]
        def pause(uc, address, _size, _context):
            if address == boundary and not paused[0]:
                paused[0] = True
                uc.emu_stop()
        handle = self.uc.hook_add(UC_HOOK_CODE, pause)
        self.uc.reg_write(UC_ARM_REG_SP, 0x508000)
        self.uc.reg_write(UC_ARM_REG_LR, 0x600001)
        try:
            self.uc.emu_start(0x38BF3, 0x600000, count=20000)
        finally:
            self.uc.hook_del(handle)
        assert paused[0], ('Unreached mouse readiness instruction', hex(boundary))
        registers = (
            UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3,
            UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7,
            UC_ARM_REG_R8, UC_ARM_REG_R9, UC_ARM_REG_R10, UC_ARM_REG_R11,
            UC_ARM_REG_R12, UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC,
            UC_ARM_REG_CPSR,
        )
        cpu = tuple(self.uc.reg_read(reg) for reg in registers)
        stack = bytes(self.uc.mem_read(0x507000, 0x1100))
        self.fifo = chunk_packet(0, 0, bytes(40))
        self.run(0x3778E)  # Only IRQ completion; do not execute main work yet.
        self.uc.mem_write(0x507000, stack)
        for reg, value in zip(registers, cpu):
            self.uc.reg_write(reg, value)
        self.uc.emu_start(boundary | 1, 0x600000, count=20000)
        assert self.uc.reg_read(UC_ARM_REG_PC) == 0x600000
        assert self.uc.reg_read(UC_ARM_REG_SP) == 0x508000
        # The actual main loop can now process the queued page, then poll again.
        self.run(0x34216)
        self.run(0x34216)
        self.poll_input()


def run_mouse_ready_regressions(code, layout, candidate_image=None, protected_reports=(0x22,)):
    checks = 0
    for buttons, dx, dy, protocol in ((b, x, y, p) for b, x, y in
                                     ((1, 3, -2), (2, -4, 5), (3, 0, 0), (0, 0, 0))
                                     for p in (0, 1)):
        expected = (bytes([buttons]) + struct.pack('<hh', dx, dy) + bytes(2)
                    if protocol else bytes([buttons, dx & 255, dy & 255]))
        before = MouseEndpointFixture(code, layout, candidate_image, guard=False)
        before.pending_mouse(buttons, dx, dy)
        before.set8(0x400B11, protocol)
        before.poll_input()
        assert not before.submissions and not before.endpoint_fifo_writes
        before.upload_chunk(chunk_packet(0, 0, bytes(40)))
        assert before.get8(0x400B23) == 1 and before.get8(0x400AAE) == 10
        before.poll_input()
        assert before.submissions == [(2, expected)]
        assert before.endpoint_fifo_writes == [(2, 1, value, True) for value in expected]

        after = MouseEndpointFixture(code, layout, candidate_image)
        after.pending_mouse(buttons, dx, dy)
        after.set8(0x400B11, protocol)
        # Multiple display chunks cannot manufacture an IN completion.
        for x in (0, 40, 80):
            after.upload_chunk(chunk_packet(0, x, bytes(40)))
            assert after.get8(0x400B23) == 0
            after.poll_input()
            assert not after.submissions and not after.endpoint_fifo_writes
        assert after.get8(0x400AAE) == 10 and after.get8(0x400AF3) == 1
        after.complete_mouse_transfer()
        after.poll_input()
        assert after.submissions == [(2, expected)]
        assert after.endpoint_fifo_writes == [(2, 1, value, False) for value in expected]
        assert after.get8(0x400B23) == 0
        checks += 1

    # EP1 keyboard output remains independent while EP2 is legitimately busy.
    keyboard = MouseEndpointFixture(code, layout, candidate_image)
    keyboard.pending_mouse(1)
    keyboard.upload_chunk(chunk_packet(0, 0, bytes(40)))
    keyboard.set8(0x400B22, 1)
    keyboard.set8(0x400AF6, 1)
    keyboard.uc.mem_write(0x400B42, bytes.fromhex('00270000000000'))
    keyboard.poll_input()
    assert keyboard.submissions == [(1, bytes.fromhex('0000270000000000'))]
    assert all(endpoint == 1 and not busy for endpoint, _size, _value, busy in keyboard.endpoint_fifo_writes)
    assert keyboard.get8(0x400B23) == 0
    keyboard.poll_input()
    assert len(keyboard.submissions) == 1
    keyboard.complete_mouse_transfer()
    keyboard.poll_input()
    assert keyboard.submissions[-1] == (2, bytes.fromhex('0103fe'))
    checks += 1

    # Actual GO4 OUT IRQ interruption before/around every instruction of the
    # EP2 readiness/backoff gate. Before the fix a busy FIFO is written either
    # on return from that IRQ or on the following mouse poll; after it, only a
    # genuine endpoint2 completion can release the same pending input report.
    for boundary in (0x38BF2, 0x38D92, 0x38D94, 0x38D96,
                     0x38D98, 0x38D9A, 0x38D9C, 0x38D9E):
        for guard in (False, True):
            mouse = MouseEndpointFixture(code, layout, candidate_image, guard=guard)
            mouse.pending_mouse(1)
            mouse.interrupt_mouse_poll_with_chunk(boundary)
            if guard:
                assert not mouse.submissions and not mouse.endpoint_fifo_writes
                mouse.complete_mouse_transfer()
                mouse.poll_input()
                assert mouse.submissions == [(2, bytes.fromhex('0103fe'))]
                assert not any(busy for _ep, _size, _value, busy in mouse.endpoint_fifo_writes)
            else:
                assert mouse.submissions == [(2, bytes.fromhex('0103fe'))]
                assert mouse.endpoint_fifo_writes == [(2, 1, value, True) for value in (1, 3, 254)]
            checks += 1

    # Replay exactly the two original instructions for every other report ID;
    # preserve r0/r1/r3/r5/r6 and SP, including original r2 length reload.
    target = APPEND + layout['symbols']['g9_ol4_ep0_mouse_ready_hook']['offset']
    fixture = FlowFixture(code, layout, candidate_image=candidate_image)
    for report in (0, 4, 5, 6, 8, 9, 0x0C, 0x10, 0x21, 0x22, 0x23, 0x2E, 0xA0, 255):
        for ready in (0, 1):
            fixture.uc.mem_write(0x410000, bytes([report]) + bytes(63))
            fixture.set8(0x400B23, ready)
            fixture.set8(0x400BB0, 37)
            values = {UC_ARM_REG_R0: 0x400B23, UC_ARM_REG_R1: 0x410000,
                      UC_ARM_REG_R3: 0x3456789, UC_ARM_REG_R5: 0x400BB0,
                      UC_ARM_REG_R6: 1}
            for reg, value in values.items():
                fixture.uc.reg_write(reg, value)
            fixture.run(target)
            assert fixture.get8(0x400B23) == (ready if report in protected_reports else 1)
            assert fixture.uc.reg_read(UC_ARM_REG_R2) == 37
            assert all(fixture.uc.reg_read(reg) == value for reg, value in values.items())
            checks += 1
    assert not fixture.flash_reads and not fixture.flash_writes and not fixture.profile_loads
    print(f'PASS {checks} EP2 readiness/FIFO/legacy cases: pre-fix GO4 writes busy mouse FIFO; guard waits for real TX callback; L/R/motion/release and independent EP1 keyboard preserved')
    return checks

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

    for length, selector in ((63, 0), (65, 0), (64, 1), (64, 5), (64, 255)):
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
        query = bytearray.fromhex('a0 22 40 00 00 00 00 00')
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
    for x in (0,40,80):
        fixture.upload_chunk(chunk_packet(0,x,payload[x:x+40],flags=int(x==0)))
        assert fixture.commands == [0xB0, 0x10 | (x >> 4), x & 15]
        assert fixture.bytes_on_bus == [0x78, 0x40]+list(payload[x:x+40])
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
        assert version == bytes.fromhex('0b080101084f4c34'), version.hex()
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
                item.submit_query(report=report, length=64, selector=selector)
                item.run(0x34216)
            assert bytes(original.uc.mem_read(0x400000, 0x20000)) == bytes(patched.uc.mem_read(0x400000, 0x20000)), (report, selector)
            assert original.wake_calls == patched.wake_calls == 1
            legacy_cases += 1
    print(f'PASS {legacy_cases} non22 prepare histories: original and hooked RAM/state/selector behavior agree')

    upload_checks=0
    uploader=FlowFixture(code,layout,candidate_image=candidate_image)
    for page in range(4):
        for x in (0,40,80):
            payload=bytes((i*7+page+x)&(15 if page==3 else 255) for i in range(40))
            uploader.upload_chunk(chunk_packet(page,x,payload,flags=int(page==0 and x==0)))
            assert uploader.commands==[0xB0+page,0x10|(x>>4),x&15],(page,x,uploader.commands)
            assert uploader.bytes_on_bus==[0x78,0x40]+list(payload)
            assert not uploader.flash_reads and not uploader.flash_writes and not uploader.profile_loads
            upload_checks+=1
    assert uploader.display_init_calls==1
    good=chunk_packet(0,0,bytes(40))
    bad=[]
    for offset,value in ((0,0x23),(1,63),(2,0),(3,0),(4,3),(5,2),(6,1),(7,4),(8,1),(8,39),(8,120),(9,39),(10,2),(11,1),(52,1),(61,1)):
        item=bytearray(good);item[offset]=value;item[62:]=crc16(item[:62]).to_bytes(2,'little');bad.append(bytes(item))
    item=bytearray(good);item[25]^=1;bad.append(bytes(item))
    bad += [chunk_packet(3,0,bytes([0xF0])*40),chunk_packet(0,40,bytes(40),1),chunk_packet(1,0,bytes(40),1)]
    for data in bad:
        counts=(uploader.wake_calls,uploader.display_init_calls,len(uploader.commands),len(uploader.bytes_on_bus))
        uploader.direct_chunk(data)
        assert not uploader.commands and not uploader.bytes_on_bus
        assert counts[:2]==(uploader.wake_calls,uploader.display_init_calls)
    for length,pointer in ((63,0x400F93),(65,0x400F93),(64,0x410000)):
        counts=(uploader.wake_calls,uploader.display_init_calls)
        uploader.direct_chunk(good,length,pointer)
        assert not uploader.commands and not uploader.bytes_on_bus and counts==(uploader.wake_calls,uploader.display_init_calls)
    # All malformed64-byte GO4 packets with report22 also traverse real EP0 once.
    for data in bad:
        if data[0]!=0x22:continue
        f=FlowFixture(code,layout,candidate_image=candidate_image)
        f.upload_chunk(data)
        assert not f.commands and not f.bytes_on_bus
    # Failure beforefix:132B transfer is physically64+64+4 FIFO chunks.
    broken=LegacyFlowFixture(code,layout,patched=False)
    payload=bytes([0x22,132,0x47,0x4F,2,1,0,0,120,1])+bytes(range(120))+bytes.fromhex('3344')
    broken.hid(9,0x22,132)
    for at in range(0,132,64):
        broken.fifo=payload[at:at+64];broken.run(0x3778E)
    actual=bytes(broken.uc.mem_read(0x400F91,10))
    assert actual==bytes.fromhex('76773344000000000000') and actual!=payload[:10],actual.hex()
    assert broken.get8(CONTEXT+2)==0x76
    print('PASS prefixed132B regression: real64+64+4 OUT corrupts old logicalheader; OL4 uses12 bounded64B chunks')
    print(f'PASS {upload_checks} real USB chunk->MAIN->native draw paths, {len(bad)+3} malformed gate cases, GS4status, callee-saved/stack')
    irq_checks = run_claim_irq_regressions(code, layout, candidate_image)
    mouse_checks = run_mouse_ready_regressions(code, layout, candidate_image)
    return dict(whole_status_cases=checks,legacy_prepare_cases=legacy_cases,profile_history_cases=history_cases,real_usb_out_chunks=upload_checks,malformed_rejections=len(bad)+3,prefixed132_failure_proved=True,claimed_metadata_irq_cases=irq_checks,mouse_ready_cases=mouse_checks,stock_usb_callbacks_unchanged=False,ep0_mouse_ready_guard_only=True,stack_canary_and_callee_saved=True,candidate_image_executed=candidate_image is not None)

def main():
    code,layout=build();result=run_regressions(code,layout)
    (ROOT/'bin/g9_ol4-layout.json').write_text(json.dumps(layout,indent=2),encoding='utf-8')
    print(json.dumps(dict(text_bytes=len(code),text_sha256=sha(code),descriptor_patch=layout['descriptor_patch'],result=result)))
if __name__=='__main__':main()
