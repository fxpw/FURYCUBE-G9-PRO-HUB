"""DG2 minimal OEM diagnostic regression; offline only, never device access.

Only DG2's own object is compiled. No OL/store hooks or other builds are used.
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
from unicorn.arm_const import (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2,
    UC_ARM_REG_R3, UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_SP)

ROOT=Path(__file__).resolve().parents[1]
BASE,APPEND=0x28790,0x47400
SOURCE_HASH='f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc'
PROTECTED=(4,5,6,8,12,0x22)
RFBUF,RFSTATE,REMAIN,SEQ=0x400EEF,0x400B5C,0x400BA0,0x400BA1

def sha(data):return hashlib.sha256(data).hexdigest()

def build():
    original=(ROOT/'evidence/g9pro-official-firmware-resource.bin').read_bytes()
    assert sha(original)==SOURCE_HASH
    output=ROOT/'bin/g9_dg2.o';output.parent.mkdir(exist_ok=True)
    subprocess.run(['clang','--target=arm-none-eabi','-march=armv5t','-mthumb',
        '-c',str(ROOT/'firmware/g9_dg2.S'),'-o',str(output)],check=True,cwd=ROOT)
    code,symbols=elf_text_and_symbols(output.read_bytes())
    # This leaf has no PC-relative operands/relocations; exact OL7 instructions
    # can be checked directly against its immutable installed-build object.
    reference,rs=elf_text_and_symbols((ROOT/'bin/g9_ol7.o').read_bytes())
    old=rs['g9_ol7_ep0_mouse_ready_hook'];new=symbols['g9_dg2_ep0_mouse_ready_hook']
    assert old['size']==new['size']
    assert code[new['offset']:new['offset']+new['size']]==reference[old['offset']:old['offset']+old['size']]
    normalized,blocks,tail=decode_resource(original)
    assert len(blocks)==3939 and tail==b'\xff'*198 and BASE+len(normalized)==APPEND
    hook=dict(address=0x37896,normalized_offset=0x37896-BASE,old_hex='06702a78',
        new_hex=thumb_bl(0x37896,APPEND+new['offset']).hex(),symbol='g9_dg2_ep0_mouse_ready_hook')
    prepare=dict(address=0x342A4,normalized_offset=0x342A4-BASE,old_hex='c27204d0',
        new_hex=thumb_bl(0x342A4,APPEND+symbols['g9_dg2_prepare_hook']['offset']).hex(),symbol='g9_dg2_prepare_hook')
    nop=dict(address=0x3201C,normalized_offset=0x3201C-BASE,old_hex='0d70',new_hex='c046',
        reason='Preserve existing NoWait ordinary-screen-key fix')
    for patch in (hook,prepare,nop):
        pos=patch['normalized_offset'];assert normalized[pos:pos+len(bytes.fromhex(patch['old_hex']))]==bytes.fromhex(patch['old_hex'])
    layout=dict(text_bytes=len(code),text_sha256=sha(code),symbols=symbols,hooks=[hook,prepare],
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
    def prefix(self,report,length,selector=0):
        self.rf_fifo=bytes([0x37,report,length,0,selector,0,0,0])
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
    # version, descriptor, loop, allocator and OLED writer outside three patches.
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
            leaf.run(APPEND+layout['symbols']['g9_dg2_ep0_mouse_ready_hook']['offset'])
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
    return dict(status='OFFLINE_DG2_MINIMAL_OEM_DIAGNOSTIC_ONLY',payload_bytes=len(code),payload_sha256=sha(code),
        exact_three_instruction_patches=True,original_version_descriptor_rf_loops_allocator_oled_unchanged=True,
        leaf_byte_identical_to_ol7=True,leaf_report_readiness_cases=512,ep0_ep2_cases=endpoint,
        independent_keyboard_zero_cases=keyboard,unguarded_busy_fifo_reproduced=True,
        input_checks=verify_input_paths(candidate_image),stock_rf_comparison_cases=len(radio),radio_responses=radio,
        normal_read_irq=run_normal_irq_checks(code,layout,candidate_image),
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
        dg1_wrong_header_reproduced_hex=observed.hex(),dg2_same_interruption_hex=repaired.hex(),
        hardware_timing_emulated=False,concurrent_owners_supported=False)

if __name__=='__main__':
    from build_dg2_candidate import build_resource
    code,layout=build();resource,decoded,changed=build_resource((ROOT/'evidence/g9pro-official-firmware-resource.bin').read_bytes(),code,layout)
    print(json.dumps(run_checks(code,layout,decoded),indent=2))
