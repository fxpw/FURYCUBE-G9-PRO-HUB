"""Original G9 Thumb routines with synthetic flat flash ONLY, no hardware IO.

Requires the locally cached official normalized image and Unicorn. No source
download, vendor executable, USB/serial or real flash access. Flash APIs are
Python bytearray models; this does not qualify geometry/encryption/durability
or prove compatibility with the currently installed G9 image or bootloader.
"""
from pathlib import Path
import argparse
import hashlib
import json
from unicorn import Uc, UC_ARCH_ARM, UC_MODE_THUMB, UC_HOOK_CODE
from unicorn.arm_const import (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2,
                               UC_ARM_REG_R3, UC_ARM_REG_SP, UC_ARM_REG_LR,
                               UC_ARM_REG_PC)

ROOT = Path(__file__).resolve().parents[1]
BASE = 0x28790
IMAGE = (ROOT/'evidence/g9pro-firmware-normalized.bin').read_bytes()
assert hashlib.sha256(IMAGE).hexdigest() == 'e0cc9bcb8930ea430d8bc14fefb746f189f0f5f743b81c0b32a5b161f7fba5db'
STOP=0x600000
REGS=(UC_ARM_REG_R0,UC_ARM_REG_R1,UC_ARM_REG_R2,UC_ARM_REG_R3)

def sha(value): return hashlib.sha256(value).hexdigest()

class Fixture:
    def __init__(self, flash):
        self.uc=Uc(UC_ARCH_ARM,UC_MODE_THUMB)
        for address,size in ((0x20000,0x30000),(0x400000,0x20000),(0x500000,0x10000),(STOP,0x1000)):
            self.uc.mem_map(address,size)
        self.uc.mem_write(BASE,IMAGE)
        self.flash=bytearray(flash)
        self.events=[]
        self.reset=False
        self.cut_after_erase=None
        self.cut=False
        self.uc.hook_add(UC_HOOK_CODE,self.hook)
    def ret(self):self.uc.reg_write(UC_ARM_REG_PC,self.uc.reg_read(UC_ARM_REG_LR))
    def hook(self,uc,address,size,ctx):
        r0,r1,r2,r3=(uc.reg_read(r) for r in REGS)
        if address==STOP: uc.emu_stop(); return
        if address==0x39BC2:
            assert r1+r2<=len(self.flash)
            uc.mem_write(r3,bytes(self.flash[r1:r1+r2]))
            self.events.append(dict(kind='read',address=r1,length=r2))
        elif address==0x39BF0:
            assert r1+r2<=len(self.flash)
            data=bytes(uc.mem_read(r3,r2))
            assert all((a&b)==b for a,b in zip(self.flash[r1:r1+r2],data)), 'Programmed non-erased bits'
            self.flash[r1:r1+r2]=data
            self.events.append(dict(kind='program',address=r1,length=r2,sha256=sha(data)))
        elif address==0x39C56:
            assert r1%4096==0 and r2%4096==0
            self.flash[r1:r1+r2]=b'\xff'*r2
            self.events.append(dict(kind='erase',address=r1,length=r2))
            if self.cut_after_erase==r1:
                self.cut=True;uc.emu_stop();return
        elif address==0x24F9C:
            uc.mem_write(r0,bytes(uc.mem_read(r1,r2)))
            self.events.append(dict(kind='memcpy',source=r1,destination=r0,length=r2))
            self.ret(); return
        elif address==0x24FEC:
            uc.mem_write(r0,bytes(r1));self.ret();return
        elif address in (0x397A0,0x39816,0x374A8,0x3BC66,0x31C78):
            self.ret();return
        elif address==0x3B618:
            self.reset=True;uc.emu_stop();return
        else:return
        uc.reg_write(UC_ARM_REG_R0,0);self.ret()
    def run(self,entry,*args):
        self.uc.reg_write(UC_ARM_REG_SP,0x508000)
        self.uc.reg_write(UC_ARM_REG_LR,STOP|1)
        for reg,value in zip(REGS,args):self.uc.reg_write(reg,value)
        self.uc.emu_start(entry|1,STOP,count=150000)
        assert self.reset or self.cut or self.uc.reg_read(UC_ARM_REG_PC)==STOP,hex(self.uc.reg_read(UC_ARM_REG_PC))

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,help='Optional own JSON fixture report, not a flash dump')
    args=parser.parse_args()
    # Non-FF varying synthetic data makes loss/preservation visible.
    initial=bytes(((i*17+(i>>5)*7+19)&255) for i in range(0x80000))
    bank=initial[0x7D000:0x7E000]
    marker=bytes.fromhex('1234aa00002000000000000000000000')
    macro=bytes((i*11+3)&255 for i in range(106))
    shared=macro+sum(macro).to_bytes(2,'little')+bytes((i*13+7)&255 for i in range(20))
    assert int.from_bytes(shared[106:108],'little')==sum(shared[:106])
    reports=[]
    # All 32 real store paths, not a Python implementation of the loop.
    for slot in range(32):
        f=Fixture(initial);f.uc.mem_write(0x400DF0,shared);f.run(0x30EB0,slot)
        expected=bytearray(bank);expected[slot*128:(slot+1)*128]=shared
        assert f.flash[0x7D000:0x7E000]==expected,slot
        assert f.flash[0x52000:0x53000]==bank,slot
        reads=[e for e in f.events if e['kind']=='read']
        writes=[e for e in f.events if e['kind']=='program']
        erases=[e for e in f.events if e['kind']=='erase']
        assert erases==[dict(kind='erase',address=0x52000,length=4096),dict(kind='erase',address=0x7D000,length=4096)]
        assert len(reads)==len(writes)==8
        reports.append(dict(slot=slot,macroBankMatchesNew128=True,scratchMatchesOLD4096=True,
                            selectedSlotHidden20Changed=True,events=f.events))
    # 108 equal bytes + different hidden20 yield different physical records,
    # but exactly the same known loader-visible 108 bytes.
    f1,f2=Fixture(initial),Fixture(initial)
    f1.uc.mem_write(0x400DF0,shared[:108]+b'\xA5'*20)
    f2.uc.mem_write(0x400DF0,shared[:108]+b'\x5A'*20)
    f1.run(0x30EB0,19);f2.run(0x30EB0,19)
    at=0x7D000+19*128
    assert f1.flash[at:at+108]==f2.flash[at:at+108]
    assert f1.flash[at+108:at+128]!=f2.flash[at+108:at+128]
    # Real update-entry -> real1KiB RMW wrapper; only low flash APIs are mocked.
    enter=Fixture(initial);enter.run(0x39446)
    assert enter.reset
    assert enter.flash[0x7D000:0x7D010]==marker
    assert enter.flash[0x7D010:0x7D400]==bank[16:1024]
    assert enter.flash[0x7D400:0x7E000]==b'\xFF'*3072
    assert enter.flash[0x52000:0x53000]==initial[0x52000:0x53000]
    # Load only108 from the actual copied128 slot and active macro buffer.
    load=Fixture(initial);load.run(0x30FFA,19)
    assert load.events[0]==dict(kind='read',address=0x7D000+19*128,length=108)
    assert bytes(load.uc.mem_read(0x4010AE,108))==bank[19*128:19*128+108]
    cut_scratch=Fixture(initial);cut_scratch.uc.mem_write(0x400DF0,shared)
    cut_scratch.cut_after_erase=0x52000;cut_scratch.run(0x30EB0,19)
    assert cut_scratch.flash[0x7D000:0x7E000]==bank
    assert cut_scratch.flash[0x52000:0x53000]==b'\xFF'*4096
    cut_bank=Fixture(initial);cut_bank.uc.mem_write(0x400DF0,shared)
    cut_bank.cut_after_erase=0x7D000;cut_bank.run(0x30EB0,19)
    assert cut_bank.flash[0x7D000:0x7E000]==b'\xFF'*4096
    assert cut_bank.flash[0x52000:0x53000]==bank
    result=dict(offline_only=True,physical_device_tested=False,flash_apis='synthetic flat 4KiB sector model',
                normalized_sha256=sha(IMAGE),original_routine_invocations=38,stores=reports,entry_events=enter.events,
                entry_marker_first16=True,entry_loses_last3072=True,
                setter_scratch_is_previous_bank=True,logical108_cannot_recover_hidden20=True,
                loader_reads108_only=True,
                post_erase_cuts=dict(scratchEraseBankIntact=True,bankEraseScratchOLDAndBankErased=True),
                caveat='No installed boot, encryption/raw representation, flash geometry or power-failure durability qualified')
    if args.output:
        args.output.parent.mkdir(parents=True,exist_ok=True)
        args.output.write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps({key:value for key,value in result.items() if key not in ('stores','entry_events')},indent=2))

if __name__=='__main__':main()
