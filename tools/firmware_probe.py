"""Offline Thumb disassembly with PC-relative literal and string annotations."""
from pathlib import Path
import argparse
import struct
from capstone import Cs, CS_ARCH_ARM, CS_MODE_THUMB

p = argparse.ArgumentParser()
p.add_argument('start', type=lambda x: int(x, 0))
p.add_argument('end', type=lambda x: int(x, 0))
args = p.parse_args()
data = (Path(__file__).resolve().parents[1] / 'evidence/g9pro-firmware-normalized.bin').read_bytes()
# Derived from three independent descriptor pointers. Example: device descriptor
# file offset 0x1c232 is referenced as 0x449c2 in the USB descriptor table.
# The first 16 bytes are the update header, so ARM vectors start at 0x287a0.
base = 0x28790
cs = Cs(CS_ARCH_ARM, CS_MODE_THUMB)
cs.skipdata = True
for ins in cs.disasm(data[args.start-base:args.end-base], args.start):
    annotation = ''
    if ins.mnemonic in ('ldr', 'adr') and '#' in ins.op_str and (ins.mnemonic == 'adr' or '[pc,' in ins.op_str):
        offset = int(ins.op_str.split('#')[1].split(']')[0], 0)
        address = ((ins.address+4) & ~3) + offset
        if base <= address < base+len(data)-4:
            raw = data[address-base:address-base+100]
            if ins.mnemonic == 'ldr':
                annotation = ' ; literal ' + hex(struct.unpack_from('<I', raw)[0])
            else:
                annotation = ' ; ' + repr(raw.split(b'\0')[0])
    print(f'{ins.address:08x}: {ins.mnemonic:8} {ins.op_str}{annotation}')
