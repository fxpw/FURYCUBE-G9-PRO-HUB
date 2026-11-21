"""Pinned updater instructions in Unicorn only; no PE/USB/CRT execution.
All imported HID/OS and memory routines are offline local test doubles.
"""
from pathlib import Path
import hashlib, struct, json
import pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
from unicorn.x86_const import *

ROOT = Path(__file__).resolve().parents[1]
if not __debug__:
    raise RuntimeError('Updater verification requires assertions; do not use -O')
matches = list((ROOT/'evidence/firmware-readonly').rglob('Update.exe'))
assert len(matches) == 1, 'Exactly one pinned official Update.exe is required'
path = matches[0]
binary = path.read_bytes()
assert hashlib.sha256(binary).hexdigest() == '558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4'
pe = pefile.PE(data=binary)
BASE, CTX, DATA, STACK, STOP, STUB = 0x400000, 0x20000000, 0x30000000, 0x70000000, 0x71000000, 0x71001000

def u32(u,a): return struct.unpack('<I',u.mem_read(a,4))[0]
def put(u,a,v): u.mem_write(a,struct.pack('<I',v & 0xffffffff))
def args(u,n):
    s=u.reg_read(UC_X86_REG_ESP)
    return [u32(u,s+4+4*i) for i in range(n)]
def ret(u,result=0,pop=0):
    s=u.reg_read(UC_X86_REG_ESP)
    u.reg_write(UC_X86_REG_EAX,result & 0xffffffff)
    u.reg_write(UC_X86_REG_ESP,s+4+pop)
    u.reg_write(UC_X86_REG_EIP,u32(u,s))

def run(mode='normal',transfer_length=0x21000, bounded_erases=False, image_bytes=None, payload_data=None,
        allow_reloc_growth=False):
    assert transfer_length in (0x21000, 0x22000), 'Only the reviewed 33/34-page test sizes'
    assert not bounded_erases or transfer_length == 0x22000
    loaded = pe if image_bytes is None else pefile.PE(data=image_bytes)
    image_size = loaded.OPTIONAL_HEADER.SizeOfImage
    original_size = pe.OPTIONAL_HEADER.SizeOfImage
    assert loaded.OPTIONAL_HEADER.ImageBase == BASE
    assert image_size == original_size or (allow_reloc_growth and image_size == original_size + 4096)
    u=Uc(UC_ARCH_X86,UC_MODE_32)
    u.mem_map(BASE,(image_size+4095)&~4095)
    u.mem_write(BASE,loaded.get_memory_mapped_image())
    if bounded_erases:
        # Offline proposal ONLY. Preserve the original erase footprint while
        # 0x4036C5 reloads the actual 34-page length for programming/checking.
        # A real package must separately pin this instruction and resource.
        assert bytes(u.mem_read(0x4033B9,6)) == bytes.fromhex('8b87dc000000')
        u.mem_write(0x4033B9, bytes.fromhex('b80010020090'))
    u.mem_map(CTX,0x10000);u.mem_map(DATA,0x100000)
    u.mem_map(STACK,0x20000);u.mem_write(STACK,bytes([0xa7])*0x20000)
    u.mem_map(STOP,0x10000)
    imports={}
    for dll in pe.DIRECTORY_ENTRY_IMPORT:
        for imp in dll.imports:
            name=(imp.name or b'').decode()
            address=STUB+len(imports)*16
            imports[address]=name
            put(u,imp.address,address)
    for offset,value in ((0xD8,0x2B00A),(0xDC,transfer_length),(0xE4,0),(0xEC,DATA),(0x120,10)):
        put(u,CTX+offset,value)
    payload=bytes((i*7+19)&255 for i in range(transfer_length)) if payload_data is None else payload_data
    assert len(payload) == transfer_length
    u.mem_write(DATA,payload)
    for node,text in ((CTX+0x1000,b'normal\0'),(CTX+0x2000,b'loader\0')):
        put(u,node,node+0x100);u.mem_write(node+0x100,text)
        u.mem_write(node+0x18,bytes.fromhex('0b000000'))
    s=STACK+0x1f000;u.mem_write(s,struct.pack('<II',STOP,CTX));u.reg_write(UC_X86_REG_ESP,s)
    writes=[];reads=[];features=[];enumerations=[];commands=[];stream=bytearray();last=None

    def hook(u,a,size,_):
        nonlocal last,stream
        if a==STOP:u.emu_stop();return
        if a==0x54E760:
            s=u.reg_read(UC_X86_REG_ESP);target=u32(u,s)
            u.reg_write(UC_X86_REG_ESP,s+4-u.reg_read(UC_X86_REG_EAX));u.reg_write(UC_X86_REG_EIP,target);return
        if a==0x54FDA0:
            dest,value,count=args(u,3);u.mem_write(dest,bytes([value&255])*count);ret(u,dest);return
        if a==0x54F820:
            dest,src,count=args(u,3);u.mem_write(dest,bytes(u.mem_read(src,count)));ret(u,dest);return
        if a==0x54DF3A:ret(u,u.reg_read(UC_X86_REG_EAX));return
        if a in imports:
            name=imports[a]
            if name in ('QueryPerformanceFrequency','QueryPerformanceCounter'):
                dest=args(u,1)[0];u.mem_write(dest,struct.pack('<Q',1000000));ret(u,1,4);return
            if name=='Sleep':ret(u,0,4);return
            if name=='hid_enumerate':
                vid,pid=args(u,2);enumerations.append((vid,pid))
                ret(u,CTX+0x2000 if vid==0xA745 else CTX+0x1000);return
            if name=='hid_open_path':ret(u,0x2222 if args(u,1)[0]==CTX+0x2100 else 0x1111);return
            if name=='hid_free_enumeration':ret(u);return
            if name=='hid_send_feature_report':
                handle,pointer,count=args(u,3);features.append(bytes(u.mem_read(pointer,count)).hex());ret(u,count);return
            if name=='hid_write':
                handle,pointer,count=args(u,3);report=bytes(u.mem_read(pointer,count))
                assert handle==0x2222 and count==65 and report[0]==0
                writes.append(report)
                if stream:
                    stream.extend(report[1:])
                elif report[1:5]==bytes.fromhex('01e0fcff') and report[8]==7:
                    stream=bytearray(report[1:])
                else:
                    last=report[1:];commands.append(last)
                if len(stream)>=0x100c:
                    last=bytes(stream[:0x100c]);commands.append(last);stream.clear()
                ret(u,count);return
            if name=='hid_read':
                handle,pointer,count=args(u,3);assert handle==0x2222 and count==65 and last is not None
                if last[:4]==bytes.fromhex('01e0fc09'):
                    reply=bytes.fromhex('040e0801e0fc10')+bytes.fromhex('12345678')
                    if mode=='verify-tail-different':reply=reply[:7]+bytes.fromhex('efcdab89')
                    if mode=='verify-header-wrong':reply=bytes([5])+reply[1:]
                elif last[7]==0x0f:
                    reply=bytes.fromhex('040eff01e0fcf407000f')+bytes([0])+last[8:13]
                    if mode=='erase-status-nonzero':reply=reply[:10]+bytes([0x99])+reply[11:]
                    if mode=='erase-address-wrong':reply=reply[:12]+bytes([reply[12]^1])+reply[13:]
                else:
                    assert last[7]==7,last[:20].hex()
                    reply=bytes.fromhex('040eff01e0fcf4060007')+bytes([0])+last[8:12]
                    if mode=='write-status-nonzero':reply=reply[:10]+bytes([0x99])+reply[11:]
                    if mode=='write-address-wrong':reply=reply[:11]+bytes([reply[11]^1])+reply[12:]
                reply=reply.ljust(65,b'\xa6');u.mem_write(pointer,reply);reads.append(reply)
                ret(u,1 if mode=='short-read-count' else 65);return
            raise AssertionError('Unexpected import '+name)
        assert 0x402F50<=a<0x403CD9,hex(a)

    u.hook_add(UC_HOOK_CODE,hook)
    u.emu_start(0x402F50,STOP+1,count=1000000)
    assert u.reg_read(UC_X86_REG_EIP)==STOP
    result=dict(mode=mode,features=features,enumerations=enumerations,writes=len(writes),reads=len(reads),
                state=u32(u,CTX+0x124),result=u32(u,CTX+0x12c),
                commands=[dict(head=c[:16].hex(),length=len(c)) for c in commands],
                read_headers=[r[:18].hex() for r in reads],first_write=writes[0].hex())
    if mode not in ('verify-header-wrong','erase-address-wrong','write-address-wrong'):
        program=[c for c in commands if c[:8]==bytes.fromhex('01e0fcfff4051007')]
        assert len(program)==transfer_length//4096
        assert b''.join(c[12:] for c in program)==payload
        assert [struct.unpack('<I',c[8:12])[0] for c in program]==list(range(0x2b00a,0x2b00a+transfer_length,4096))
        assert features==['1008000000000000','0b08000000000000']
        assert result['state']==4 and result['result']==4
    return result


def transfer_bounds(result, length, bounded_erases=False):
    erases=[]
    for command in result['commands']:
        data=bytes.fromhex(command['head'])
        if len(data)>12 and data[7]==15:
            start=int.from_bytes(data[9:13],'little')
            size={0x20:0x1000,0xD8:0x10000}[data[8]]
            erases.append(dict(start=start,end=start+size,opcode=data[8]))
    assert erases[0]['start']==0x2B000
    assert all(a['end']==b['start'] for a,b in zip(erases,erases[1:]))
    assert erases[-1]['end']==(0x51000 if length==0x21000 or bounded_erases else 0x52000)
    assert 0x2B00A+length <= erases[-1]['end']
    return dict(transfer_bytes=length,program_pages=length//4096,
        program_start=0x2B00A,program_end=0x2B00A+length,erases=erases,
        host_state=result['state'],host_result=result['result'], bounded_erases_proposal=bounded_erases)


def main():
    expected={'normal':4,'verify-tail-different':4,'verify-header-wrong':3,'short-read-count':4,
              'erase-status-nonzero':4,'write-status-nonzero':4,'write-address-wrong':2,'erase-address-wrong':1}
    results=[]
    for length, bounded in ((0x21000,False),(0x22000,False),(0x22000,True)):
        normal=run('normal',length,bounded)
        bounds=transfer_bounds(normal,length,bounded)
        negative={mode:run(mode,length,bounded)['result'] for mode in expected if mode!='normal'}
        assert negative=={mode:value for mode,value in expected.items() if mode!='normal'}
        results.append(dict(**bounds,negative_cases=negative))
    print(json.dumps(dict(offline_only=True,native_vendor_executed=False,device_access=False,
        resource_loading_emulated=False,synthetic_payload=True,results=results),indent=2))


if __name__=='__main__':main()
