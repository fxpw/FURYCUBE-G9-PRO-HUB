"""Run vendor serialization instructions offline, with USB and CRT calls stubbed."""
from pathlib import Path
import json
import struct
import pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_EAX, UC_X86_REG_ECX, UC_X86_REG_ESP, UC_X86_REG_EIP

ROOT=Path(__file__).resolve().parents[1]
PE=pefile.PE(str(ROOT/'evidence/original/FURYCUBE.exe'))
IMAGE=PE.get_memory_mapped_image()
BASE=PE.OPTIONAL_HEADER.ImageBase
OBJ=0x1000000
PROFILE=0x2000000
STATE=0x2100000
STACK=0x5000000
STOP=0x7000000
pack=lambda x:struct.pack('<I',x)

def serialize(kind,delay_ms=None,profile_path=None):
    uc=Uc(UC_ARCH_X86,UC_MODE_32)
    uc.mem_map(BASE, (len(IMAGE)+0xfff)&~0xfff)
    uc.mem_write(BASE,IMAGE)
    uc.mem_map(OBJ,0x200000)
    uc.mem_map(PROFILE,0x200000)
    uc.mem_map(STACK,0x100000)
    uc.mem_map(STOP,0x1000)
    source=Path(profile_path) if profile_path else ROOT/'evidence/original/Data/ms_1/pro.data'
    profile=bytearray(source.read_bytes()[0x867f8:0x867f8+0x1ae64])
    if delay_ms is not None:
        for i in range(2):struct.pack_into('<H',profile,0x9a4+3*0x1188+0x5ac+i*16,delay_ms)
    uc.mem_write(PROFILE,bytes(profile))
    uc.mem_write(OBJ+0x11a318,pack(PROFILE))
    uc.mem_write(OBJ+0x1351a0,struct.pack('<18I',1,5,9,13,17,0,21,25,0,0,0,0,0,0,0,0,0,0))
    packets=[]
    def dword(a):return struct.unpack('<I',uc.mem_read(a,4))[0]
    def finish(n=0):
        sp=uc.reg_read(UC_X86_REG_ESP)
        uc.reg_write(UC_X86_REG_EIP,dword(sp))
        uc.reg_write(UC_X86_REG_ESP,sp+4+n)
    def hook(uc, address, size, _):
        sp=uc.reg_read(UC_X86_REG_ESP)
        if address==0x466d83:
            dest,value,length=[dword(sp+i) for i in (4,8,12)]
            uc.mem_write(dest,bytes([value&255])*length)
            uc.reg_write(UC_X86_REG_EAX,dest);finish()
        elif address==0x465f3f:finish()
        elif address==0x41d6b0:
            data,length=dword(sp+4),dword(sp+8)
            packets.append(bytes(uc.mem_read(data,length)))
            uc.reg_write(UC_X86_REG_EAX,1);finish(8)
        elif address==STOP:uc.emu_stop()
    uc.hook_add(UC_HOOK_CODE,hook)
    args=[0] if kind=='keys' else [STATE,STATE+4,13,PROFILE+0x9a4+3*0x1188,STATE+8]
    sp=STACK+0x80000
    uc.mem_write(sp,pack(STOP)+b''.join(pack(a) for a in args))
    uc.reg_write(UC_X86_REG_ESP,sp)
    uc.reg_write(UC_X86_REG_ECX,OBJ)
    uc.emu_start(0x41edd0 if kind=='keys' else 0x41e5b0,STOP,count=100000)
    return packets

if __name__=='__main__':
    result={}
    for kind in ('keys','macro'):
        packets=serialize(kind)
        result[kind]=[p.hex() for p in packets]
        for p in packets:
            (ROOT/'evidence'/('vendor-'+kind+'.bin')).write_bytes(p)
            print(kind,'length',len(p),'hex',p.hex())
            print('checksum',sum(p[3:-2]),int.from_bytes(p[-2:],'big'))
    (ROOT/'evidence/vendor-packets.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    original=serialize('macro')[0]
    slow=serialize('macro',50)[0]
    assert original[9:13]==bytes.fromhex('0a278127')
    assert slow[9:13]==bytes.fromhex('3227b227')
    assert sum(slow[3:-2])==int.from_bytes(slow[-2:],'big')
    assert {i for i,(a,b) in enumerate(zip(original,slow)) if a!=b}<={9,11,109,110}
    (ROOT/'evidence/macro-delay-50ms.bin').write_bytes(slow)
    print('PASS 50ms candidate changes only two delays and checksum; original x86 serializer executed offline')
