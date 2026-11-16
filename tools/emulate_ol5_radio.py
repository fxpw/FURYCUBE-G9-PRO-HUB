"""Offline OL5 RF wrappers: real stock prefix, ACK and fragment instructions.

No device/updater access. RF FIFO, RF transmit and hardware timing are stubbed;
stock reassembly/fragmentation and new ARM validation/status code execute.
Receiver relay qualification is separate physical evidence, not emulated here.
"""
from pathlib import Path
import hashlib,json,struct,subprocess
from unicorn.arm_const import (UC_ARM_REG_R0,UC_ARM_REG_R1,UC_ARM_REG_R2,UC_ARM_REG_R3,
    UC_ARM_REG_R4,UC_ARM_REG_R5,UC_ARM_REG_R6,UC_ARM_REG_R7,UC_ARM_REG_SP,UC_ARM_REG_LR,UC_ARM_REG_PC)
from emulate_ol4_status import (FlowFixture as Ol4FlowFixture,chunk_packet,expected_status,
    run_claim_irq_regressions,run_mouse_ready_regressions)
from emulate_oled_arm import elf_text_and_symbols
from emulate_screen_key import thumb_bl
from emulate_oled_protocol import crc16

ROOT=Path(__file__).resolve().parents[1]
BASE,APPEND=0x28790,0x47400
SOURCE_HASH='f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc'
CTX,RFBUF,RFSTATE,REMAIN,SEQ=0x400F88,0x400EEF,0x400B5C,0x400BA0,0x400BA1

def sha(data):return hashlib.sha256(data).hexdigest()

def build():
    original=(ROOT/'evidence/g9pro-official-firmware-resource.bin').read_bytes()
    assert sha(original)==SOURCE_HASH
    out=ROOT/'bin/g9_ol5.o';out.parent.mkdir(exist_ok=True)
    subprocess.run(['clang','--target=arm-none-eabi','-march=armv5t','-mthumb','-c',str(ROOT/'firmware/g9_ol5.S'),'-o',str(out)],check=True,cwd=ROOT)
    code,symbols=elf_text_and_symbols(out.read_bytes())
    # Explicit OFFLINE34-page design, not a relaxation of OL4 packaging.
    # Root must review an independently built expanded candidate before use.
    maximum_append=((0x22000-16)//34-3939)*32
    assert len(code)<=maximum_append,('Explicit34-page offline allowance',len(code))
    image=(ROOT/'evidence/g9pro-firmware-normalized.bin').read_bytes()
    hooks=[]
    for address,expected,symbol in (
        (0x33C94,'11704078','g9_ol5_version_hook'),
        (0x33CD4,'00f010ff','g9_ol5_set_hook'),
        (0x342A4,'c27204d0','g9_ol5_prepare_hook'),
        (0x34308,'fff73cfc','g9_ol5_get_hook'),
        (0x37896,'06702a78','g9_ol5_ep0_mouse_ready_hook'),
        (0x3012A,'03f0d2fd','g9_ol5_radio_set_hook'),
        (0x30026,'03f0adfd','g9_ol5_radio_get_hook'),
        (0x30548,'06f030ff','g9_ol5_radio_prefix_hook'),
        (0x28D58,'a4480025','g9_store_boot_hook'),
        (0x3CDFE,'fff7deff','g9_ol5_init_done_hook'),
        (0x2EA02,'06f0d9fd','g9_ol5_loop_hook'),
        (0x364FC,'fff75cf8','g9_ol5_loop_hook')):
        assert image[address-BASE:address-BASE+4]==bytes.fromhex(expected)
        target=APPEND+symbols[symbol]['offset']
        hooks.append(dict(address=address,normalized_offset=address-BASE,old_hex=expected,new_hex=thumb_bl(address,target).hex(),target=target,symbol=symbol))
    pixel_sites=(0x3CE4A,0x3CE9A,0x3CEA0,0x3CF06,0x3D810,
                 0x3D978,0x3D97E,0x3D984,0x3D98A,0x3D9D6,0x3DA1A,0x3DA5E,0x3DAA2)
    for address in pixel_sites:
        before=thumb_bl(address,0x3CD7A)
        assert image[address-BASE:address-BASE+4]==before,hex(address)
        target=APPEND+symbols['g9_ol5_pixel_gate']['offset']
        hooks.append(dict(address=address,normalized_offset=address-BASE,old_hex=before.hex(),new_hex=thumb_bl(address,target).hex(),target=target,symbol='g9_ol5_pixel_gate'))
    descriptor=image[0x44890-BASE:0x4497C-BASE];needle=bytes.fromhex('85229583b101')
    assert descriptor.count(needle)==1
    address=0x44890+descriptor.index(needle)+3
    layout=dict(text_bytes=len(code),text_sha256=sha(code),symbols=symbols,hooks=hooks,
      descriptor_patch=dict(address=address,normalized_offset=address-BASE,old_hex='83',new_hex='3f',reason='Report22 total64'),
      data_patches=[dict(address=0x4306C,normalized_offset=0x4306C-BASE,old_hex='2c6f4000',new_hex='1c6f4000',reason='Reserve16B allocator root'),
                    dict(address=0x3201C,normalized_offset=0x3201C-BASE,old_hex='0d70',new_hex='c046',reason='Preserve NoWait screen-button fix')],
      pixel_sites=list(pixel_sites),rounded_transfer_bytes=0x22000,requires_expanded_transfer_review=True,
      append_address=APPEND,extension_crc_blocks=(len(code)+31)//32,
      minimum_resource_size=16+(3939+(len(code)+31)//32)*34,
      version_report_marker='OL5',source_sha256=SOURCE_HASH,physical_validated=False)
    return code,layout

class FlowFixture(Ol4FlowFixture):
    """Combined OL5 transport fixture with real boot allocator and CRC budget."""
    def __init__(self,code,layout,patched=True,candidate_image=None):
        super().__init__(code,layout,patched,candidate_image)
        if patched:
            if candidate_image is None:
                for patch in layout.get('data_patches',[]):
                    self.uc.mem_write(patch['address'],bytes.fromhex(patch['new_hex']))
            self.run(APPEND+layout['symbols']['g9_store_boot_hook']['offset'])

    def run(self,address):
        canary=bytes([0xA7])*96;self.uc.mem_write(0x507E00,canary)
        regs=(UC_ARM_REG_R4,UC_ARM_REG_R5,UC_ARM_REG_R6,UC_ARM_REG_R7)
        before=tuple(self.uc.reg_read(r) for r in regs)
        self.uc.reg_write(UC_ARM_REG_SP,0x508000);self.uc.reg_write(UC_ARM_REG_LR,0x600001)
        self.uc.emu_start(address|1,0x600000,count=500000)
        assert self.uc.reg_read(UC_ARM_REG_PC)==0x600000,hex(self.uc.reg_read(UC_ARM_REG_PC))
        assert bytes(self.uc.mem_read(0x507E00,len(canary)))==canary
        assert tuple(self.uc.reg_read(r) for r in regs)==before

    def store_next(self):
        pointer=struct.unpack('<I',self.uc.mem_read(0x406F20,4))[0]
        return self.get8(pointer+964+3)

class RadioFixture(FlowFixture):
    def __init__(self,code,layout,candidate_image=None):
        self.rf_fifo=b'';self.rf_sent=[];self.dispatches=0
        super().__init__(code,layout,candidate_image=candidate_image)
        self.set8(0x400A7C,0)  # RF, no live wired endpoint.
    def hook(self,uc,address,size,context):
        if address==0x373AC:
            uc.mem_write(RFBUF,self.rf_fifo.ljust(32,b'\0'));self.ret()
        elif address==0x3725E:
            self.rf_sent.append(bytes(uc.mem_read(RFBUF,uc.reg_read(UC_ARM_REG_R0))));self.ret()
        elif address in (0x371CE,0x371B2,0x36EDC,0x31C78,0x374A8,0x3BC66):self.ret()
        elif address==0x34AF8:
            self.wake_calls+=1;self.ret()  # RF wake hardware excluded.
        else:super().hook(uc,address,size,context)
    def irq(self,value):self.uc.mem_write(0x805100,struct.pack('<I',value))
    def prefix(self,report=0x22,length=64,get=False,selector=0,kind=0):
        self.rf_fifo=bytes([0x37 if get else 0x36,report,length,kind,selector,0,0,0])
        self.irq(0x40);self.run(0x30534)
        assert self.get8(RFSTATE)==(7 if get else 5)
        self.run(0x3022E)
        assert self.rf_sent[-1]==bytes([0x47 if get else 0x46,report,length,kind,selector,0,0,0])
        self.irq(0x20);self.run(0x30146)
        assert self.get8(REMAIN)==(length-2)&255 and self.get8(SEQ)==0
        assert self.get8(RFSTATE)==(12 if get and selector==0 else 11 if get else 9)
    def fragment(self,sequence,data,prefix=0x38):
        assert len(data)<=30
        self.rf_fifo=bytes([prefix,sequence])+data.ljust(30,b'\0')
        self.irq(0x40);self.run(0x3009E)
    def write(self,data,length=64,kind=0):
        self.prefix(length=length,kind=kind)
        for off in range(0,len(data)-2,30):self.fragment(off//30+1,data[2+off:2+off+30])
    def read(self,report=0x22,length=64,kind=0):
        self.prefix(report,length,get=True,kind=kind)
        self.run(0x2FFF8);assert self.get8(RFSTATE)==13
        fragments=[]
        while self.get8(REMAIN):
            self.set8(0x400B04,1) # existing RF-send readiness gate, not payload.
            old=self.get8(REMAIN);self.run(0x2FF3E)
            frame=self.rf_sent[-1]
            assert frame[:2]==bytes([0x39,len(fragments)+1])
            fragments.append(frame[2:2+min(old,30)])
            self.set8(RFSTATE,13) # TX-completion/timer hardware outside fixture.
        return bytes([report,length])+b''.join(fragments)

def run_regressions(code,layout,candidate_image=None):
    count=0
    def fixture():return RadioFixture(code,layout,candidate_image)
    # Every chunk uses a full stock RF prefix/ACK +30/30/2 reassembly.
    complete=fixture()
    for page in range(4):
        for x in (0,40,80):
            f=complete;payload=bytes((i+x+page)&(15 if page==3 else 255) for i in range(40))
            data=chunk_packet(page,x,payload,1 if page==x==0 else 0)
            f.prefix();f.fragment(1,data[2:32]);assert not f.bytes_on_bus
            f.fragment(2,data[32:62]);assert not f.bytes_on_bus
            f.fragment(3,data[62:]);assert not f.bytes_on_bus
            assert f.store_next()==page*3+x//40+1
            assert f.get8(RFSTATE)==10 and f.get8(REMAIN)==0 and f.get8(SEQ)==3
            assert not f.profile_loads and not f.flash_writes and not f.application_writes
            count+=1
    data=chunk_packet(0,0,bytes(range(40)))
    for first in (0,2,3,255):
        f=fixture();f.prefix();f.fragment(first,data[2:32]);assert f.get8(SEQ)==0 and f.get8(REMAIN)==62 and not f.bytes_on_bus;count+=1
    # A duplicate or out-of-order packet never progresses or dispatches early.
    for badseq in (0,1,3,4,255):
        f=fixture();f.prefix();f.fragment(1,data[2:32]);f.fragment(badseq,data[32:62])
        assert f.get8(SEQ)==1 and f.get8(REMAIN)==32 and not f.bytes_on_bus
        f.fragment(2,data[32:62]);f.fragment(3,data[62:]);assert f.store_next()==1 and not f.bytes_on_bus;count+=1
    # Prefix guard rejects all255 other lengths before stock length-2 or
    # profile/macro-selector stores; old RF parser has no such bound.
    for get in (False,True):
        for length in range(256):
            if length==64:continue
            f=fixture();f.rf_fifo=bytes([0x37 if get else 0x36,0x22,length,0,0,0,0,0]);f.irq(0x40)
            before=bytes(f.uc.mem_read(CTX,148));f.run(0x30534)
            assert f.get8(RFSTATE) not in (5,7) and not f.rf_sent
            assert bytes(f.uc.mem_read(CTX,148))==before
            assert not f.application_writes and not f.profile_loads and not f.flash_writes;count+=1
        for kind,selector in ((1,0),(255,0),(0,1),(0,255),(1,1)):
            f=fixture();f.set8(0x400B27,2);f.set8(0x400BA2,3)
            f.rf_fifo=bytes([0x37 if get else 0x36,0x22,64,kind,selector,0,0,0]);f.irq(0x40);f.run(0x30534)
            assert f.get8(0x400B27)==2 and f.get8(0x400BA2)==3 and not f.rf_sent;count+=1
    # RF FIFO is physically32 bytes. Truncated logical final data is padded by
    # the RF hardware fixture and must fail the GO4 CRC instead of drawing.
    f=fixture();f.prefix();f.fragment(1,data[2:32]);f.fragment(2,data[32:62]);f.fragment(3,b'')
    assert not f.bytes_on_bus;count+=1
    for offset in (2,3,4,5,6,7,8,9,10,11,52,61,62,63):
        bad=bytearray(data);bad[offset]^=0x80
        f=fixture();f.write(bytes(bad));assert not f.bytes_on_bus;count+=1
    # RF context.origin is stale: all values must be irrelevant to real origin.
    for origin in (0,1,2,255):
        f=fixture();f.set8(CTX+1,origin);f.write(data);assert f.store_next()==1 and not f.bytes_on_bus;count+=1
    for origin in (0,1,2,255):
        f=fixture();f.set8(CTX+1,origin);f.set8(0x40102C,0);f.set8(0x400B27,0)
        f.set8(0x40104F,2);f.set8(0x401040,0x40);f.set8(0x401048,6)
        reply=f.read();assert reply==expected_status(1,0,2,1600),reply.hex()
        assert not f.profile_loads and not f.flash_writes and not f.application_writes;count+=1
    f=fixture();f.install_synthetic_flash(0);reply=f.read(report=0x0B,length=8);assert reply==bytes.fromhex('0b0801084f4c3500'),reply.hex();count+=1
    # Wired validator and status remain independently guarded and operational.
    f=FlowFixture(code,layout,candidate_image=candidate_image);f.direct_chunk(data);assert f.store_next()==1 and not f.bytes_on_bus;count+=1
    f=FlowFixture(code,layout,candidate_image=candidate_image);f.direct_chunk(data,payload_pointer=0x400F91);assert not f.bytes_on_bus;count+=1
    f=FlowFixture(code,layout,candidate_image=candidate_image);f.submit_query();assert f.finish_query()==expected_status(0);count+=1
    # OL4 regression helper has literal marker expectations; adapt only the
    # expected marker in its Python byte constants, never candidate instructions.
    import types
    def marker_adjusted(fn):
        def rewrite(co):
            constants=tuple(bytes(c).replace(b'OL4',b'OL5') if isinstance(c,bytes) else c.replace('4f4c34','4f4c35').replace('ol4','ol5') if isinstance(c,str) else rewrite(c) if isinstance(c,types.CodeType) else c for c in co.co_consts)
            return co.replace(co_consts=constants)
        return types.FunctionType(rewrite(fn.__code__),fn.__globals__,fn.__name__,fn.__defaults__,fn.__closure__)
    irq=marker_adjusted(run_claim_irq_regressions)(code,layout,candidate_image)
    fifo=marker_adjusted(run_mouse_ready_regressions)(code,layout,candidate_image)
    return dict(radio_checks=count,wired_irq=irq,wired_mouse_fifo=fifo)

def main():
    code,layout=build();checks=run_regressions(code,layout)
    (ROOT/'bin/g9_ol5-layout.json').write_text(json.dumps(layout,indent=2)+'\n')
    (ROOT/'bin/g9_ol5-offline-checks.json').write_text(json.dumps(checks,indent=2)+'\n')
    print(json.dumps(dict(payload_bytes=len(code),payload_sha256=sha(code),checks=checks,layout_path=str(ROOT/'bin/g9_ol5-layout.json')),indent=2))

if __name__=='__main__':main()
