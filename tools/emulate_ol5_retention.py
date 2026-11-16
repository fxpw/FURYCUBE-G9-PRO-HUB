"""Combined OL5 retention/gating proof, offline only; never build an updater.

Real native byte/GPIO/start/stop/init/draw/composite instructions execute. ROM
decimal formatting and unrelated main-loop hardware service are boundaries.
RF timing/receiver hardware and physical SRAM retention remain unqualified.
"""
from pathlib import Path
import hashlib
import json
import struct

from unicorn import UC_HOOK_CODE
from unicorn.arm_const import (
    UC_ARM_REG_R0,UC_ARM_REG_R1,UC_ARM_REG_R2,UC_ARM_REG_R3,
    UC_ARM_REG_R4,UC_ARM_REG_R5,UC_ARM_REG_R6,UC_ARM_REG_R7,
    UC_ARM_REG_R8,UC_ARM_REG_R9,UC_ARM_REG_R10,UC_ARM_REG_R11,
    UC_ARM_REG_R12,UC_ARM_REG_SP,UC_ARM_REG_LR,UC_ARM_REG_PC,UC_ARM_REG_CPSR,
)
from emulate_ol5_radio import build,FlowFixture,RadioFixture,APPEND,BASE,ROOT,chunk_packet,expected_status
from emulate_screen_key import thumb_bl
from emulate_oled_protocol import crc16
from build_oled_candidate import verify_input_paths

CPU=(UC_ARM_REG_R0,UC_ARM_REG_R1,UC_ARM_REG_R2,UC_ARM_REG_R3,
     UC_ARM_REG_R4,UC_ARM_REG_R5,UC_ARM_REG_R6,UC_ARM_REG_R7,
     UC_ARM_REG_R8,UC_ARM_REG_R9,UC_ARM_REG_R10,UC_ARM_REG_R11,
     UC_ARM_REG_R12,UC_ARM_REG_SP,UC_ARM_REG_LR,UC_ARM_REG_CPSR)


class NativeFixture(FlowFixture):
    def __init__(self,code,layout,candidate_image=None):
        self.transactions=[];self.current=None;self.total_instructions=0
        self.stop_at=None;self.original_main_calls=0;self.layout=layout
        super().__init__(code,layout,candidate_image=candidate_image)

    def hook(self,uc,address,size,context):
        self.total_instructions+=1
        if address==self.stop_at:
            uc.emu_stop();return
        if address==0x3CDBE:
            self.commands.append(uc.reg_read(UC_ARM_REG_R0)&255)
        elif address==0x3CD7A:
            value=uc.reg_read(UC_ARM_REG_R0)&255
            self.bytes_on_bus.append(value)
            if self.current is not None:self.current.append(value)
        elif address==0x3CDAE:
            assert self.current is None
            self.current=[]
        elif address==0x3CD6A:
            if self.current is not None:
                self.transactions.append(bytes(self.current));self.current=None
        elif address==0x3CDDE:
            self.display_init_calls+=1
        elif address==0x3D51E:
            self.render_calls+=1
        elif address==0x42F64:
            target=uc.reg_read(UC_ARM_REG_R0)
            text=str(uc.reg_read(UC_ARM_REG_R2)).encode('ascii')
            uc.mem_write(target,text+b'\0');uc.reg_write(UC_ARM_REG_R0,len(text));self.ret()
        elif address==0x355B8:
            self.original_main_calls+=1;self.ret()
        elif address in (0x3CD40,0x31CB6,0x3BA36):
            pass  # Run native reset GPIO, finite delay and watchdog body.
        else:
            super().hook(uc,address,size,context)

    def pointer(self):return struct.unpack('<I',self.uc.mem_read(0x406F20,4))[0]
    def meta(self):return self.pointer()+964
    def clear_trace(self):
        self.transactions.clear();self.commands.clear();self.bytes_on_bus.clear()
        self.mmio_writes.clear();assert self.current is None
    def tick(self):
        before=self.total_instructions
        self.run(APPEND+self.layout['symbols']['g9_ol5_restore_tick']['offset'])
        return self.total_instructions-before
    def active_frame(self):
        self.run(APPEND+self.layout['symbols']['g9_store_active']['offset'])
        pointer=self.uc.reg_read(UC_ARM_REG_R0)
        return bytes(self.uc.mem_read(pointer,480)) if pointer else None
    def upload(self,frame,initialize=True,wire=True):
        for index in range(12):
            page,x=divmod(index,3);data=chunk_packet(page,x*40,frame[index*40:(index+1)*40],int(initialize and index==0))
            if wire:self.upload_chunk(data)
            else:self.direct_chunk(data)
    def boundary(self,site,value,cpsr=0xA00000F3):
        for index,reg in enumerate(CPU[:-3]):self.uc.reg_write(reg,0x11110000+index)
        self.uc.reg_write(UC_ARM_REG_R0,value)
        self.uc.reg_write(UC_ARM_REG_SP,0x508000)
        self.uc.reg_write(UC_ARM_REG_CPSR,cpsr)
        self.uc.reg_write(UC_ARM_REG_LR,0x600001)
        self.stop_at=site+4
        try:self.uc.emu_start(site|1,0x600000,count=10000)
        finally:self.stop_at=None
        assert self.uc.reg_read(UC_ARM_REG_PC)==site+4
        return tuple(self.uc.reg_read(reg) for reg in CPU)


class NativeRadioFixture(RadioFixture,NativeFixture):
    """Actual RF reassembly plus native display; only RF hardware is stubbed."""
    def hook(self,uc,address,size,context):
        if address==0x34AF8:
            NativeFixture.hook(self,uc,address,size,context)
        else:
            RadioFixture.hook(self,uc,address,size,context)

    def upload(self,frame,initialize=True):
        for index in range(12):
            page,x=divmod(index,3)
            self.write(chunk_packet(page,x*40,frame[index*40:(index+1)*40],int(initialize and index==0)))


def image(seed):
    return bytes((seed+i*17)&(15 if i>=360 else 255) for i in range(480))


def direct_byte_calls(original):
    found=[]
    for address in range(BASE,BASE+len(original)-4,2):
        if original[address-BASE:address-BASE+4]==thumb_bl(address,0x3CD7A):
            found.append(address)
    return found


def run_regressions(code,layout,candidate_image=None,radio_fixture_factory=None,radio_status_supported=True):
    f=NativeFixture(code,layout,candidate_image)
    original=(ROOT/'evidence/g9pro-firmware-normalized.bin').read_bytes()
    calls=direct_byte_calls(original)
    combined=bytearray(original)
    for patch in layout['hooks']+layout['data_patches']+[layout['descriptor_patch']]:
        address=patch['address']-BASE
        before,after=bytes.fromhex(patch['old_hex']),bytes.fromhex(patch['new_hex'])
        assert combined[address:address+len(before)]==before
        combined[address:address+len(after)]=after
    combined.extend(code)
    input_checks=verify_input_paths(bytes(combined))
    pixels=set(layout['pixel_sites'])
    assert len(calls)==34 and pixels<=set(calls)
    # These remaining21 calls are command/control/address transactions.
    unchanged_calls=set(calls)-pixels
    assert len(unchanged_calls)==21
    for address in unchanged_calls:
        assert bytes(f.uc.mem_read(address,4))==thumb_bl(address,0x3CD7A)
    gate_cases=0
    for site in layout['pixel_sites']:
        for owner,renderer in ((0,0),(1,0),(1,1)):
            for value in (0,1,0x80,0xFF,0x1234):
                left=NativeFixture(code,layout,candidate_image)
                right=NativeFixture(code,layout,candidate_image)
                for item in (left,right):
                    item.set8(item.meta()+1,1);item.set8(item.meta()+4,owner)
                    item.set8(item.meta()+5,renderer);item.clear_trace()
                right.uc.mem_write(site,thumb_bl(site,0x3CD7A))
                observed=left.boundary(site,value)
                expected=right.boundary(site,value)
                assert observed==expected,(hex(site),value,owner,renderer,observed,expected)
                if owner and not renderer:
                    assert not left.mmio_writes and not left.bytes_on_bus
                else:
                    assert left.mmio_writes==right.mmio_writes
                    assert left.bytes_on_bus==right.bytes_on_bus==[value&255]
                gate_cases+=1
    frame_a,frame_b=image(7),image(23)
    f.clear_trace();initial_init=f.display_init_calls
    # Real EP0 callbacks, validation, store/CRC. No partial-frame pixel writes.
    for index in range(12):
        page,x=divmod(index,3)
        f.upload_chunk(chunk_packet(page,x*40,frame_a[index*40:(index+1)*40],int(index==0)))
        assert not any(t[:2]==bytes([0x78,0x40]) and len(t)>2 for t in f.transactions)
        assert f.active_frame()==(frame_a if index==11 else None)
    assert f.display_init_calls-initial_init==1
    assert not f.profile_loads and not f.flash_writes and not f.application_writes
    f.clear_trace();ticks=[]
    for index in range(12):
        before=len(f.transactions);ticks.append(f.tick())
        transactions=f.transactions[before:]
        page,x=divmod(index,3);x*=40
        assert transactions==[bytes([0x78,0,0xB0+page]),bytes([0x78,0,0x10|(x>>4)]),
                              bytes([0x78,0,x&15]),bytes([0x78,0x40])+frame_a[index*40:(index+1)*40]],(index,transactions)
        assert f.get8(f.meta()+5)==0
    assert f.get8(f.meta()+6)==0
    before=len(f.transactions);f.tick();assert len(f.transactions)==before
    # Native clear and one-byte output preserve custom pixels; commands pass.
    f.clear_trace();f.run(0x3D824)
    assert not any(t[:2]==bytes([0x78,0x40]) and len(t)>2 for t in f.transactions)
    f.uc.reg_write(UC_ARM_REG_R0,0xAA);f.run(0x3CE34)
    assert f.transactions[-1]==bytes([0x78,0x40])
    f.run(0x3DAFA)
    assert f.commands[-3:]==[0xAE,0x8D,0x10]
    # Dirty battery/DPI paths execute native glyph/data routines, with only
    # decimal formatting stubbed; no native pixels may overwrite custom data.
    native_dirty_cases=0
    for flag in (0x400A88,0x400A83):
        f.set8(0x400AB6,0);f.set8(0x400B2F,1);f.set8(flag,1)
        f.set8(0x40104F,1);f.set8(0x40103F,0x40);f.set8(0x401047,6)
        f.clear_trace();f.run(0x3D856)
        assert f.get8(flag)==0
        assert not any(t[:2]==bytes([0x78,0x40]) and len(t)>2 for t in f.transactions)
        assert f.active_frame()==frame_a
        native_dirty_cases+=1
    # Explicit full commit initializes at most once; flags0 avoids reset,
    # an existing sleep transition initializes through real stock wake,
    # and a user-disabled display stays disabled with a pending saved image.
    upload_policy_cases=0
    for asleep,off,initialize,expected_init in ((0,0,False,0),(1,0,True,1),(0,1,True,0)):
        policy=NativeFixture(code,layout,candidate_image)
        policy.set8(0x400A7A,asleep);policy.set8(0x400AF2,off)
        policy.upload(frame_a,initialize=initialize)
        assert policy.active_frame()==frame_a and policy.display_init_calls==expected_init,(asleep,off,initialize,policy.display_init_calls,policy.active_frame()==frame_a)
        assert policy.get8(policy.meta()+6)==1
        if off:
            policy.clear_trace();policy.tick();assert not policy.transactions
        upload_policy_cases+=1
    # Sleep may begin after the first eleven chunks, before the final SET
    # reaches the commit hook. Native wake skips panel initialization while
    # AF1 is set. A prior initialization earlier this boot is not proof that
    # the sleeping panel was reinitialized now.
    sleep_commit_policy_cases=0
    for initialized_before in (0,1):
        for native_init_suppressed in (0,1):
            policy=NativeFixture(code,layout,candidate_image)
            for index in range(11):
                page,x=divmod(index,3)
                policy.direct_chunk(chunk_packet(page,x*40,frame_a[index*40:(index+1)*40],int(index==0)))
            policy.set8(policy.meta()+19,initialized_before)
            policy.set8(0x400A7A,1);policy.set8(0x400AF1,native_init_suppressed);policy.set8(0x400AF2,0)
            policy.direct_chunk(chunk_packet(3,80,frame_a[440:480]))
            assert policy.active_frame()==frame_a and policy.display_init_calls==1,(initialized_before,native_init_suppressed,policy.display_init_calls)
            assert policy.get8(policy.meta()+19)==1 and policy.get8(0x400A7A)==0
            assert policy.get8(policy.meta()+6)==1
            policy.clear_trace();policy.tick()
            assert policy.transactions[-1]==bytes([0x78,0x40])+frame_a[:40]
            sleep_commit_policy_cases+=1
    # Sleep and physical OLED-off flags never cause a renderer wake or GPIO.
    sleep_cases=0
    for flag in (0x400A7A,0x400AF2):
        f.set8(f.meta()+6,1);f.set8(flag,1);f.clear_trace()
        f.tick();assert not f.transactions and not f.mmio_writes
        assert f.get8(f.meta()+6)==1
        f.set8(flag,0);sleep_cases+=1
    # Wake initialization reaches the real final-AF hook and restarts cursor.
    f.set8(f.meta()+16,8);f.set8(0x400A7A,1);f.clear_trace()
    f.run(0x34AF8)
    assert f.get8(0x400A7A)==0 and f.get8(f.meta()+16)==0
    assert f.get8(f.meta()+6)==1 and 0xAF in f.commands
    f.clear_trace();f.tick()
    assert f.transactions[-1]==bytes([0x78,0x40])+frame_a[:40]
    # Interrupted upload keeps the active image while a new commit restarts it.
    for index in range(5):
        page,x=divmod(index,3);f.upload_chunk(chunk_packet(page,x*40,frame_b[index*40:(index+1)*40]))
    assert f.active_frame()==frame_a
    f.upload(frame_b,initialize=False)
    assert f.active_frame()==frame_b and f.get8(f.meta()+16)==0
    f.clear_trace();f.tick();assert f.transactions[-1]==bytes([0x78,0x40])+frame_b[:40]
    # Twenty malformed GO4 records plus three outer transport cases.
    good=chunk_packet(0,0,bytes(40));bad=[]
    for offset,value in ((0,0x23),(1,63),(2,0),(3,0),(4,3),(5,2),(6,1),(7,4),(8,1),(8,39),(8,120),(9,39),(10,2),(11,1),(52,1),(61,1)):
        data=bytearray(good);data[offset]=value;data[62:]=crc16(data[:62]).to_bytes(2,'little');bad.append(bytes(data))
    data=bytearray(good);data[25]^=1;bad.append(bytes(data))
    bad.extend((chunk_packet(3,0,bytes([0xF0])*40),chunk_packet(0,40,bytes(40),1),chunk_packet(1,0,bytes(40),1)))
    malformed_cases=0
    for data in bad:
        before=f.active_frame();calls=(f.wake_calls,f.display_init_calls)
        f.clear_trace();f.direct_chunk(data)
        assert not f.transactions and f.active_frame()==before
        assert calls==(f.wake_calls,f.display_init_calls)
        malformed_cases+=1
    for length,pointer in ((63,0x400F93),(65,0x400F93),(64,0x410000)):
        f.clear_trace();f.direct_chunk(good,length,pointer)
        assert not f.transactions and f.active_frame()==frame_b
        malformed_cases+=1
    # Both actual main-loop callsites execute the original service once and
    # emit only one40-byte chunk. Neither endpoint readiness flag is modified.
    main_cases=0
    for site in (0x2EA02,0x364FC):
        f.set8(f.meta()+6,1);f.set8(f.meta()+16,0)
        f.set8(0x400B23,0);f.set8(0x400B22,1);f.set8(0x400AAE,10)
        f.clear_trace();before=f.original_main_calls
        f.stop_at=site+4
        f.uc.reg_write(UC_ARM_REG_SP,0x508000);f.uc.reg_write(UC_ARM_REG_LR,0x600001)
        try:f.uc.emu_start(site|1,0x600000,count=20000)
        finally:f.stop_at=None
        assert f.uc.reg_read(UC_ARM_REG_PC)==site+4
        assert f.original_main_calls==before+1
        assert len([t for t in f.transactions if t[:2]==bytes([0x78,0x40])])==1
        assert (f.get8(0x400B23),f.get8(0x400B22),f.get8(0x400AAE))==(0,1,10)
        main_cases+=1
    # The dirty-path setup above intentionally supplied RAM configuration;
    # the firmware itself still must not call profile loaders/NVM writers.
    assert not f.flash_writes and not f.profile_loads
    # Combined image warm reset: stock RW/BSS initialization legitimately
    # writes configuration RAM; no NVM/profile loader is allowed.
    f.stop_at=0x28D5C;f.uc.reg_write(UC_ARM_REG_CPSR,0xD3)
    try:f.uc.emu_start(0x28824,0x600000,count=500000)
    finally:f.stop_at=None
    assert f.uc.reg_read(UC_ARM_REG_PC)==0x28D5C
    assert f.uc.reg_read(UC_ARM_REG_SP)==0x40F81C
    assert f.active_frame()==frame_b and f.get8(f.meta()+6)==1 and f.get8(f.meta()+16)==0
    f.run(0x3CDDE);f.clear_trace()
    for index in range(12):
        f.tick()
        assert f.transactions[-1]==bytes([0x78,0x40])+frame_b[index*40:(index+1)*40]
    # Full RF prefix/ACK and30/30/2 reassembly now feeds the same native
    # complete-frame store and budgeted renderer, including sleeping wake.
    radio=(radio_fixture_factory or NativeRadioFixture)(code,layout,candidate_image)
    radio.set8(0x400A7A,1);radio.set8(0x400B23,0);radio.set8(0x400B22,1)
    radio.upload(frame_a)
    assert radio.active_frame()==frame_a and radio.display_init_calls==1
    assert (radio.get8(0x400B23),radio.get8(0x400B22))==(0,1)
    radio.clear_trace()
    for index in range(12):
        radio.tick()
        assert radio.transactions[-1]==bytes([0x78,0x40])+frame_a[index*40:(index+1)*40]
    if radio_status_supported:
        assert radio.read()==expected_status(0)
    assert radio.active_frame()==frame_a and not radio.flash_writes and not radio.profile_loads
    assert not f.flash_writes and not f.profile_loads
    return dict(native_pixel_gate_cases=gate_cases,unchanged_command_calls=len(unchanged_calls),
                framebuffer_ticks=12,tick_instruction_counts=ticks,
                sleep_off_cases=sleep_cases,main_loop_sites=main_cases,
                native_dirty_cases=native_dirty_cases,upload_policy_cases=upload_policy_cases,
                sleep_commit_policy_cases=sleep_commit_policy_cases,
                malformed_chunks=malformed_cases,warm_combined_reset=True,input_checks=input_checks,
                native_rf_full_frame=True,native_rf_sleep_init_once=True,
                no_per_frame_nvm=True,physical_sram_retention=False)


def main():
    code,layout=build();checks=run_regressions(code,layout)
    print(json.dumps(dict(status='PASS_COMBINED_OFFLINE_ONLY',payload_bytes=len(code),
                          payload_sha256=hashlib.sha256(code).hexdigest(),checks=checks),indent=2))


if __name__=='__main__':main()
