"""DG3 minimal OEM diagnostic regression; offline only, never device access.

Only DG3's own object is compiled. No OL/store hooks or other builds are used.
ROM helpers, USB/RF FIFOs, sensor/display hardware and synthetic flash are
explicit emulator boundaries; native parsers/dispatchers/input code execute.
"""
from pathlib import Path
import hashlib
import json
import struct
import subprocess
from emulate_oled_arm import elf_text_and_symbols
from emulate_screen_key import thumb_bl
from emulate_ol3_status import FlowFixture as StockFlowFixture
from emulate_ol4_status import MouseEndpointFixture, FlowFixture as IRQFixture, IDLE_IRQ_BOUNDARIES
from build_oled_candidate import decode_resource, verify_input_paths
from unicorn import UC_HOOK_CODE
from unicorn.arm_const import (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2,
    UC_ARM_REG_R3, UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7,
    UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_CPSR)

ROOT=Path(__file__).resolve().parents[1]
BASE,APPEND=0x28790,0x47400
SOURCE_HASH='f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc'
PROTECTED=(4,5,6,8,12,0x22)
RFBUF,RFSTATE,REMAIN,SEQ=0x400EEF,0x400B5C,0x400BA0,0x400BA1

def sha(data):return hashlib.sha256(data).hexdigest()

def build():
    original=(ROOT/'evidence/g9pro-official-firmware-resource.bin').read_bytes()
    assert sha(original)==SOURCE_HASH
    output=ROOT/'bin/g9_dg3.o';output.parent.mkdir(exist_ok=True)
    subprocess.run(['clang','--target=arm-none-eabi','-march=armv5t','-mthumb',
        '-c',str(ROOT/'firmware/g9_dg3.S'),'-o',str(output)],check=True,cwd=ROOT)
    code,symbols=elf_text_and_symbols(output.read_bytes())
    # This leaf has no PC-relative operands/relocations; exact OL7 instructions
    # can be checked directly against its immutable installed-build object.
    reference,rs=elf_text_and_symbols((ROOT/'bin/g9_ol7.o').read_bytes())
    old=rs['g9_ol7_ep0_mouse_ready_hook'];new=symbols['g9_dg3_ep0_mouse_ready_hook']
    assert old['size']==new['size']
    assert code[new['offset']:new['offset']+new['size']]==reference[old['offset']:old['offset']+old['size']]
    normalized,blocks,tail=decode_resource(original)
    assert len(blocks)==3939 and tail==b'\xff'*198 and BASE+len(normalized)==APPEND
    hook=dict(address=0x37896,normalized_offset=0x37896-BASE,old_hex='06702a78',
        new_hex=thumb_bl(0x37896,APPEND+new['offset']).hex(),symbol='g9_dg3_ep0_mouse_ready_hook')
    prepare=dict(address=0x342A4,normalized_offset=0x342A4-BASE,old_hex='c27204d0',
        new_hex=thumb_bl(0x342A4,APPEND+symbols['g9_dg3_prepare_hook']['offset']).hex(),symbol='g9_dg3_prepare_hook')
    scalar=dict(address=0x3D33A,normalized_offset=0x3D33A-BASE,old_hex='02020a43',
        new_hex=thumb_bl(0x3D33A,APPEND+symbols['g9_dg3_display_scalar']['offset']).hex(),symbol='g9_dg3_display_scalar')
    wake=dict(address=0x305D2,normalized_offset=0x305D2-BASE,old_hex=thumb_bl(0x305D2,0x34AF8).hex(),
        new_hex=thumb_bl(0x305D2,APPEND+symbols['g9_dg3_radio_wake_hook']['offset']).hex(),symbol='g9_dg3_radio_wake_hook')
    nop=dict(address=0x3201C,normalized_offset=0x3201C-BASE,old_hex='0d70',new_hex='c046',
        reason='Preserve existing NoWait ordinary-screen-key fix')
    for patch in (hook,prepare,scalar,wake,nop):
        pos=patch['normalized_offset'];assert normalized[pos:pos+len(bytes.fromhex(patch['old_hex']))]==bytes.fromhex(patch['old_hex'])
    layout=dict(text_bytes=len(code),text_sha256=sha(code),symbols=symbols,hooks=[hook,prepare,scalar,wake],
        data_patches=[nop],append_address=APPEND,extension_crc_blocks=(len(code)+31)//32,
        source_sha256=SOURCE_HASH,version_marker='OEM_UNCHANGED',custom_oled_supported=False)
    assert 16+(3939+layout['extension_crc_blocks'])*34<=0x21000
    return code,layout

class RadioFixture(StockFlowFixture):
    """Original RF instructions; no OL5 inheritance or implicit store boot."""
    def __init__(self,code,layout,candidate_image):
        self.rf_fifo=b'';self.rf_sent=[]
        super().__init__(code,layout,candidate_image=candidate_image)
        self.set8(0x400A7C,0)
    def hook(self,uc,address,size,context):
        if address==0x373AC:
            uc.mem_write(RFBUF,self.rf_fifo.ljust(32,b'\0'));self.ret()
        elif address==0x3725E:
            self.rf_sent.append(bytes(uc.mem_read(RFBUF,uc.reg_read(UC_ARM_REG_R0))));self.ret()
        elif address in (0x371CE,0x371B2,0x36EDC,0x31C78,0x374A8,0x3BC66):self.ret()
        elif address==0x34AF8:
            self.wake_calls+=1;self.ret()
        else:super().hook(uc,address,size,context)
    def irq(self,value):self.uc.mem_write(0x805100,struct.pack('<I',value))
    def prefix(self,report,length,selector=0,kind=0):
        self.rf_fifo=bytes([0x37,report,length,kind,selector,0,0,0])
        self.irq(0x40);self.run(0x30534);assert self.get8(RFSTATE)==7
        self.run(0x3022E)
        assert self.rf_sent[-1]==bytes([0x47,report,length,0,selector,0,0,0])
        self.irq(0x20);self.run(0x30146)
        assert self.get8(REMAIN)==length-2 and self.get8(SEQ)==0
        assert self.get8(RFSTATE)==(11 if selector else 12)
    def read(self,report,length,selector=0):
        self.prefix(report,length,selector)
        if selector:
            for _ in range(12):
                if self.get8(RFSTATE)==11:self.run(0x30032)
                self.run(0x3136C)
                if self.get8(RFSTATE)==12 and not self.get8(0x400B2D) and not self.get8(0x400B2E):break
        self.run(0x2FFF8);assert self.get8(RFSTATE)==13,('RF reply not ready',self.get8(RFSTATE))
        fragments=[]
        while self.get8(REMAIN):
            self.set8(0x400B04,1)
            old=self.get8(REMAIN);self.run(0x2FF3E)
            frame=self.rf_sent[-1];assert frame[:2]==bytes([0x39,len(fragments)+1])
            fragments.append(frame[2:2+min(old,30)]);self.set8(RFSTATE,13)
        return bytes([report,length])+b''.join(fragments)

SENSOR=bytes.fromhex('04380100013f0500204080c040e0646403060c121f2e000002ff000000ff000000ffffff0000ffffff00ffff4000ffffff01117700000000')
KEYS=bytes.fromhex('083b010200000300000400001100270d00003c00000500000600000100000100000100000100000100000100000100000100000a000009000000b0')
PACKETS=(SENSOR,bytes.fromhex('050f01')+bytes(12),bytes.fromhex('06090100ff00000000'),
    KEYS,bytes.fromhex('0c0a01fe05fa00000000'),bytes([0x22,64])+bytes(62))

def run_checks(code,layout,candidate_image):
    assert candidate_image is not None,'Check the exact decoded sealed candidate'
    normalized,_,_=decode_resource((ROOT/'evidence/g9pro-official-firmware-resource.bin').read_bytes())
    expected=bytearray(normalized)
    for patch in layout['hooks']+layout['data_patches']:
        pos=patch['normalized_offset'];new=bytes.fromhex(patch['new_hex']);expected[pos:pos+len(new)]=new
    expected.extend(code+b'\xff'*((-len(code))%32))
    from check_firmware_header import EDITABLE_HEADER_OFFSETS
    assert {i for i in range(16) if expected[i]!=candidate_image[i]}<=EDITABLE_HEADER_OFFSETS
    expected[:16]=candidate_image[:16];assert bytes(expected)==candidate_image
    actual={i for i in range(16,len(normalized)) if normalized[i]!=candidate_image[i]}
    declared={p['normalized_offset']+i for p in layout['hooks']+layout['data_patches']
        for i,(a,b) in enumerate(zip(bytes.fromhex(p['old_hex']),bytes.fromhex(p['new_hex']))) if a!=b}
    assert actual==declared
    # Equality across the entire original application proves every RF/parser,
    # version, descriptor, loop, allocator and OLED writer outside five patches.
    endpoint=keyboard=0
    for packet in PACKETS:
        for buttons in (0,1,2,3):
            for protocol in (0,1):
                f=MouseEndpointFixture(code,layout,candidate_image)
                f.pending_mouse(buttons);f.set8(0x400B11,protocol)
                before=f.config_snapshot();f.hid(9,packet[0],len(packet));f.fifo=packet;f.run(0x3778E)
                assert f.get8(0x400B23)==0 and not f.flash_writes and f.config_snapshot()==before
                f.poll_input();assert not f.submissions and not f.endpoint_fifo_writes
                f.complete_mouse_transfer();f.poll_input()
                assert len(f.submissions)==1 and f.submissions[0][0]==2
                assert not any(row[3] for row in f.endpoint_fifo_writes)
                endpoint+=1
        f=MouseEndpointFixture(code,layout,candidate_image);f.pending_mouse(1)
        f.hid(9,packet[0],len(packet));f.fifo=packet;f.run(0x3778E)
        f.set8(0x400B22,1);f.set8(0x400AF6,1)
        f.uc.mem_write(0x400B42,bytes.fromhex('00270000000000'));f.poll_input()
        assert f.submissions==[(1,bytes.fromhex('0000270000000000'))] and f.get8(0x400B23)==0
        keyboard+=1
    # Unguarded OEM control reproduces the busy-EP2 overwrite under same inputs.
    broken=MouseEndpointFixture(code,layout,candidate_image,guard=False);broken.pending_mouse(1)
    broken.hid(9,8,len(KEYS));broken.fifo=KEYS;broken.run(0x3778E);broken.poll_input()
    assert broken.endpoint_fifo_writes and all(row[3] for row in broken.endpoint_fifo_writes)
    leaf=StockFlowFixture(code,layout,candidate_image=candidate_image)
    for report in range(256):
        for ready in (0,1):
            leaf.uc.mem_write(0x410000,bytes([report])+bytes(63));leaf.set8(0x400B23,ready);leaf.set8(0x400BB0,37)
            values={UC_ARM_REG_R0:0x400B23,UC_ARM_REG_R1:0x410000,UC_ARM_REG_R3:0x123456,
                UC_ARM_REG_R5:0x400BB0,UC_ARM_REG_R6:1}
            for reg,value in values.items():leaf.uc.reg_write(reg,value)
            leaf.run(APPEND+layout['symbols']['g9_dg3_ep0_mouse_ready_hook']['offset'])
            assert leaf.get8(0x400B23)==(ready if report in PROTECTED else 1)
            assert leaf.uc.reg_read(UC_ARM_REG_R2)==37
            assert all(leaf.uc.reg_read(reg)==value for reg,value in values.items())
    radio=[]
    for active in range(5):
        for report,length in ((0x0B,8),(0x0C,8),(4,52)):
            for selector in (0,1):
                patched=RadioFixture(code,layout,candidate_image)
                stock=RadioFixture(b'',dict(hooks=[]),normalized)
                for f in (patched,stock):f.install_synthetic_flash(active)
                got=patched.read(report,length,selector);baseline=stock.read(report,length,selector)
                assert got==baseline,(report,active,selector,got.hex(),baseline.hex())
                assert patched.config_snapshot()==stock.config_snapshot()
                assert patched.profile_loads==stock.profile_loads and not patched.flash_writes
                if report==0x0B:assert b'OL' not in got
                radio.append(dict(report=report,active_profile=active+1,selector=selector,response_hex=got.hex()))
    return dict(status='OFFLINE_DG3_MINIMAL_OEM_DIAGNOSTIC_ONLY',payload_bytes=len(code),payload_sha256=sha(code),
        exact_five_instruction_patches=True,original_version_descriptor_rf_getter_loops_allocator_unchanged=True,
        leaf_byte_identical_to_ol7=True,leaf_report_readiness_cases=512,ep0_ep2_cases=endpoint,
        independent_keyboard_zero_cases=keyboard,unguarded_busy_fifo_reproduced=True,
        input_checks=verify_input_paths(candidate_image),stock_rf_comparison_cases=len(radio),radio_responses=radio,
        normal_read_irq=run_normal_irq_checks(code,layout,candidate_image),
        telemetry=run_telemetry_checks(code,layout,candidate_image),
        custom_oled_supported=False,physical_rf_control_confirmed=False,device_access=False,updater_executed=False)

def run_normal_irq_checks(code,layout,candidate_image):
    """Real A0 completion interrupts every idle-FSM instruction boundary.

    One request owns the transport. Synthetic read-only valid flash supplies
    native preload/checksum/restore logic; no request or ready flag is forged.
    """
    reports=((0x0B,8),(0x0C,8),(4,52),(6,9),(8,59),(0x0A,120))
    def prepared(previous,active=0,broken=False):
        f=IRQFixture(code,layout,candidate_image=candidate_image)
        if broken:
            # Restore ONLY the DG1/original prepare instructions in emulator RAM.
            f.uc.mem_write(0x342A4,bytes.fromhex('c27204d0'))
            f.uc.ctl_remove_cache(0x342A4,0x342A8)
        f.install_synthetic_flash(active)
        f.submit_query(report=previous[0],length=previous[1],selector=previous[2])
        f.finish_query(report=previous[0],get_length=previous[1],worker=True)
        return f
    transitions=[]
    for index,(report,length) in enumerate(reports):
        previous=reports[(index-1)%len(reports)]
        for selector in range(6):
            # Alternate active profiles so selector1..5 actually exercise load
            # of other profiles and restoration of the current runtime table.
            active=(selector+2)%5
            transitions.append(((*previous,1),(report,length,selector),active))
    cases=loads=0
    for previous,request,active in transitions:
        ref=prepared(previous,active)
        ref.submit_query(report=request[0],length=request[1],selector=request[2])
        expected=ref.finish_query(report=request[0],get_length=request[1],worker=True)
        assert expected[0:2]==bytes(request[0:2]),(request,expected.hex())
        expected_config=ref.config_snapshot();expected_loads=ref.profile_loads[:]
        for boundary in IDLE_IRQ_BOUNDARIES:
            f=prepared(previous,active)
            loads_before=len(f.profile_loads)
            f.submit_query_with_idle_irq(*request,pause_at=boundary)
            got=f.finish_query(report=request[0],get_length=request[1],worker=True)
            assert got==expected,(previous,request,active,hex(boundary),got.hex(),expected.hex())
            assert f.config_snapshot()==expected_config and f.profile_loads==expected_loads
            assert not f.flash_writes
            query_loaded=len(f.profile_loads)>loads_before
            if request[0] in (4,6,8,0x0A) and request[2]:assert query_loaded
            loads+=int(query_loaded);cases+=1
    # Concrete DG1 failure: request06 inherits previous04 report metadata
    # when A0 OUT IRQ lands after the idle FSM sampled that previous request.
    bad=prepared((4,52,2),active=1,broken=True)
    bad.submit_query_with_idle_irq(6,9,2,pause_at=0x34230)
    observed=bad.finish_query(report=6,get_length=9,worker=True)
    assert observed[0]==4 and observed[1]==9,observed.hex()
    fixed=prepared((4,52,2),active=1)
    fixed.submit_query_with_idle_irq(6,9,2,pause_at=0x34230)
    repaired=fixed.finish_query(report=6,get_length=9,worker=True)
    assert repaired[0:2]==bytes([6,9]) and repaired!=observed
    return dict(normal_reports=[r for r,_ in reports],idle_boundaries=len(IDLE_IRQ_BOUNDARIES),
        serialized_irq_comparison_cases=cases,selector_query_profile_load_cases=loads,
        dg1_wrong_header_reproduced_hex=observed.hex(),dg3_same_interruption_hex=repaired.hex(),
        hardware_timing_emulated=False,concurrent_owners_supported=False)

class TelemetryFixture(RadioFixture):
    """Real native wake/composite/font/pixel routines, with hardware boundaries.

    sprintf42F64 is an explicit formatting boundary using its real format
    string/scalar. Font3D24C and native draw/pixel code execute; I2C is stubbed.
    """
    def __init__(self,code,layout,candidate_image):
        self.events=[];self.formatted=[];self.draw_text=[];self.stop_at=None
        self.route_test=False;self.reached_prefix=False
        self.wake_return=APPEND+layout['symbols']['g9_dg3_radio_wake_hook']['offset']+6
        super().__init__(code,layout,candidate_image)
    def hook(self,uc,address,size,context):
        if address==self.stop_at:uc.emu_stop();return
        if address in (0x305FC,0x305D2,0x305D6,0x3D51E,0x3D54E,self.wake_return):
            self.events.append((address,self.get8(RFSTATE),self.get8(0x400A83),self.get8(0x400A7A)))
        if address==0x30534:self.reached_prefix=True
        if self.route_test and address in (0x38BF2,0x34216,0x3858E,0x38E4C,0x39192,0x31EB6,0x32AA4,0x35D72):self.ret();return
        if address==0x34AF8:
            StockFlowFixture.hook(self,uc,address,size,context);return
        if address==0x3D51E:
            self.render_calls+=1;return # Execute, including native dirty clear.
        if address==0x42F64:
            pointer,fmt,value=(uc.reg_read(r) for r in (UC_ARM_REG_R0,UC_ARM_REG_R1,UC_ARM_REG_R2))
            fmt=bytes(uc.mem_read(fmt,32)).split(b'\0')[0].decode('ascii')
            result=(fmt%value).encode('ascii');assert len(result)<12
            self.formatted.append((value,fmt,result.decode('ascii')))
            uc.mem_write(pointer,result+b'\0');uc.reg_write(UC_ARM_REG_R0,len(result));self.ret();return
        if address==0x3D24C:
            self.draw_text.append(bytes(uc.mem_read(uc.reg_read(UC_ARM_REG_R3),12)).split(b'\0')[0].decode('ascii'))
        super().hook(uc,address,size,context)
    def run(self,address):
        canary=bytes([0xA7])*96;self.uc.mem_write(0x507E00,canary)
        regs=(UC_ARM_REG_R4,UC_ARM_REG_R5,UC_ARM_REG_R6,UC_ARM_REG_R7)
        before=tuple(self.uc.reg_read(r) for r in regs)
        self.uc.reg_write(UC_ARM_REG_SP,0x508000);self.uc.reg_write(UC_ARM_REG_LR,0x600001)
        self.uc.emu_start(address|1,0x600000,count=2000000)
        assert self.uc.reg_read(UC_ARM_REG_PC)==0x600000,hex(self.uc.reg_read(UC_ARM_REG_PC))
        assert bytes(self.uc.mem_read(0x507E00,96))==canary
        assert tuple(self.uc.reg_read(r) for r in regs)==before
    def receive(self,report=11,length=8,kind=0,get=True):
        self.rf_fifo=bytes([0x37 if get else 0x36,report,length,kind,1,0,0,0])
        self.irq(0x40);self.run(0x30534)
    def timer_step(self):
        # Native35636 entry through actual countdown/dispatcher. Stop before
        # unrelated subsequent timer services, rather than pretending time.
        self.set8(0x400B41,1)
        self.uc.reg_write(UC_ARM_REG_SP,0x508000);self.uc.reg_write(UC_ARM_REG_LR,0x600001)
        self.stop_at=0x35824
        try:self.uc.emu_start(0x35637,0x600000,count=500000)
        finally:self.stop_at=None
        assert self.uc.reg_read(UC_ARM_REG_PC)==0x35824
    def owner_move_stop(self):
        # Sampled sensor deltas are the boundary; execute native motion tail
        # after its hardware read, with the actual saved-register stack frame.
        # Pinned native motion pointers347E8/347EC, from actual LDR operands.
        original=(ROOT/'evidence/g9pro-firmware-normalized.bin').read_bytes()
        dx=struct.unpack_from('<I',original,0x347E8-BASE)[0]
        dy=struct.unpack_from('<I',original,0x347EC-BASE)[0]
        self.uc.mem_write(dx,struct.pack('<h',3));self.uc.mem_write(dy,struct.pack('<h',-2))
        self.uc.reg_write(UC_ARM_REG_R6,0x400B8A);self.uc.reg_write(UC_ARM_REG_R7,1)
        self.uc.reg_write(UC_ARM_REG_SP,0x507FE8)
        self.uc.mem_write(0x507FE8,struct.pack('<6I',0,0,0,0,0,0x600001))
        self.uc.emu_start(0x346B9,0x600000,count=500000)
        assert self.uc.reg_read(UC_ARM_REG_PC)==0x600000 and self.uc.reg_read(UC_ARM_REG_SP)==0x508000
        assert self.get8(0x400AB6)==20 and self.get8(0x400B2F)==240

def run_telemetry_checks(code,layout,candidate_image):
    def fixture():
        f=TelemetryFixture(code,layout,candidate_image);f.install_synthetic_flash(0)
        f.set8(0x400B0E,1);return f
    # Native request flow, all stock-accepted kinds, awake and actual sleep wake.
    prefix_cases=0
    for asleep in (0,1):
        for kind in (0,1,2,255):
            f=fixture();f.set8(0x400A7A,asleep);f.set8(0x400A83,asleep)
            before=f.config_snapshot();f.receive(kind=kind)
            assert f.get8(RFSTATE)==7 and f.get8(0x400A83)==1 and f.get8(0x400A7A)==0
            assert f.config_snapshot()==before and not f.flash_writes
            entered=[e[0] for e in f.events]
            assert entered.index(0x305FC)<entered.index(0x305D2)<entered.index(0x305D6)
            if asleep:
                assert 0x3D51E in entered and 0x3D54E in entered
                clear=next(e for e in f.events if e[0]==f.wake_return)
                assert clear[2]==0 # Stock composite cleared old dirty before hook requeues.
            assert all(value==9007 for value,_,_ in f.formatted if value>=9000)
            prefix_cases+=1
    rejects=0
    for report,length,get in ((12,8,True),(11,9,True),(11,8,False)):
        f=fixture();f.set8(0x400A83,0);f.receive(report,length,get=get)
        assert f.get8(0x400A83)==0;rejects+=1
    for flag in (0x400AF1,0x400AF2):
        for prior in (0,1):
            f=fixture();f.set8(flag,1);f.set8(0x400A83,prior)
            f.receive();assert f.get8(0x400A83)==prior;rejects+=1
    # Native mode-selection branches never invoke RF parser in wired/BT paths.
    for wired,bt in ((1,0),(0,1),(0,2)):
        f=fixture();f.route_test=True;f.set8(0x400A7C,wired);f.set8(0x400B28,bt)
        f.set8(0x400B0A,1);f.set8(0x400ACA,0);f.set8(RFSTATE,2);f.set8(0x400B62,250)
        f.uc.reg_write(UC_ARM_REG_R4,0x400A7C);f.uc.reg_write(UC_ARM_REG_R5,0x400B28)
        f.uc.reg_write(UC_ARM_REG_SP,0x508000);f.uc.reg_write(UC_ARM_REG_LR,0x600001)
        f.uc.emu_start(0x36475,0x364CA,count=500000)
        assert f.uc.reg_read(UC_ARM_REG_PC)==0x364CA and not f.reached_prefix
        rejects+=1
    # Actual native scalar -> chosen sprintf format -> native font/pixel stream.
    numeric=[]
    for state in (0,7,8,11,12,13,14,24,255):
        f=fixture();f.set8(RFSTATE,state);before=f.config_snapshot()
        f.run(0x3D328)
        assert f.formatted==[(9000+state,'%d dpi ',str(9000+state)+' dpi ')]
        assert f.draw_text==[str(9000+state)+' dpi '] and f.bytes_on_bus
        assert f.config_snapshot()==before and not f.flash_writes
        numeric.append(dict(state=state,text=f.draw_text[0],pixel_primitive_bytes=len(f.bytes_on_bus)))
    # Queue through real prefix + wake, owner motion's native AB6/B2F writes,
    # native20-count service -> real dirty dispatcher -> numeric font renderer.
    f=fixture();f.set8(0x400A7A,1);f.receive();assert f.get8(0x400A83)==1
    f.set8(RFSTATE,12);f.owner_move_stop();f.formatted.clear();f.draw_text.clear();f.bytes_on_bus.clear()
    for step in range(20):
        f.timer_step();assert f.get8(0x400AB6)==19-step
        if step<19:assert not f.formatted
    assert f.get8(0x400A83)==0 and any(v==9012 for v,_,_ in f.formatted)
    # Differential wake hook outputs plus exact live flags at native main-loop
    # continuation CMP. The old instruction stream is reverted only in RAM.
    regs=(UC_ARM_REG_R0,UC_ARM_REG_R1,UC_ARM_REG_R2,UC_ARM_REG_R3,UC_ARM_REG_R4,
        UC_ARM_REG_R5,UC_ARM_REG_R6,UC_ARM_REG_R7,UC_ARM_REG_SP)
    differential=0
    for asleep in (0,1):
        for off in (0,1):
            left,right=fixture(),fixture()
            right.uc.mem_write(0x305D2,thumb_bl(0x305D2,0x34AF8))
            right.uc.mem_write(0x3D33A,bytes.fromhex('02020a43'))
            for item in (left,right):
                item.set8(0x400A7A,asleep);item.set8(0x400AF2,off);item.set8(0x400A83,0)
                item.receive()
            assert tuple(left.uc.reg_read(r) for r in regs)==tuple(right.uc.reg_read(r) for r in regs)
            for item in (left,right):
                item.set8(0x400B0A,1);item.set8(0x400ACA,0)
                item.uc.emu_start(0x364B7,0x364C6,count=100)
            assert tuple(left.uc.reg_read(r) for r in regs)==tuple(right.uc.reg_read(r) for r in regs)
            assert left.uc.reg_read(UC_ARM_REG_CPSR)==right.uc.reg_read(UC_ARM_REG_CPSR)
            differential+=1
    # Equal numeric strings must produce identical native continuation state
    # and font/pixel stream. Stock oracle uses genuine configured DPI9007;
    # candidate obtains9007 from RF state7, without modifying that configuration.
    left,right=fixture(),fixture()
    right.uc.mem_write(0x3D33A,bytes.fromhex('02020a43'))
    for item in (left,right):
        item.set8(RFSTATE,7);stage=item.get8(0x40104F)
        item.set8(0x40103E+stage,9007&255);item.set8(0x401046+stage,9007>>8)
        before=item.config_snapshot();item.run(0x3D328);assert item.config_snapshot()==before
    assert left.formatted==right.formatted and left.bytes_on_bus==right.bytes_on_bus
    assert tuple(left.uc.reg_read(r) for r in regs)==tuple(right.uc.reg_read(r) for r in regs)
    assert left.uc.reg_read(UC_ARM_REG_CPSR)==right.uc.reg_read(UC_ARM_REG_CPSR)
    return dict(real_prefix_cases=prefix_cases,negative_or_mode_cases=rejects,
        prefix_assignment_before_wake_proven=True,post_wake_dirty_survives_native_clear=True,
        native_scalar_formats=numeric,native_move_stop_countdown20_redraw=True,
        native_wake_continuation_register_and_live_flag_pairs=differential,
        native_equal_string_scalar_continuation_and_pixel_oracle=True,
        no_extra_timer_or_AB6_B2F_writes=True,new_ram=False,config_unchanged=True,
        sprintf_boundary='Python applies exact native format to actual ARM scalar; ROM/library formatter not executed',
        tick_wall_time_qualified=False,plain9000_is_ambiguous=True,
        power_policy='No new draw/wake call; AF1/AF2 block only extra dirty queue; native draw keeps stock policy',
        scalar_active_on_all_native_draws_including_USB=True,physical_rf_control_confirmed=False)

if __name__=='__main__':
    from build_dg3_candidate import build_resource
    code,layout=build();resource,decoded,changed=build_resource((ROOT/'evidence/g9pro-official-firmware-resource.bin').read_bytes(),code,layout)
    print(json.dumps(run_checks(code,layout,decoded),indent=2))
