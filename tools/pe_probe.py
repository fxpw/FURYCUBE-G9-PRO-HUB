"""Static PE strings, imports and code references; never loads the target."""
import argparse
from pathlib import Path
import re
import struct
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_32

p = argparse.ArgumentParser()
p.add_argument('binary')
p.add_argument('--strings')
p.add_argument('--refs', nargs='*', default=[])
p.add_argument('--range', nargs=2)
args = p.parse_args()
pe = pefile.PE(args.binary)
raw = Path(args.binary).read_bytes()
base = pe.OPTIONAL_HEADER.ImageBase
def va(off):
    return base + pe.get_rva_from_offset(off)
if args.strings:
    for encoding, regex in [('ascii', rb'[ -~]{4,}'), ('utf-16le', rb'(?:[ -~]\x00){4,}')]:
        for m in re.finditer(regex, raw):
            s = m.group().decode(encoding)
            if re.search(args.strings, s, re.I):
                print(hex(va(m.start())), encoding, repr(s))
if args.refs:
    for s in args.refs:
        address = int(s, 0)
        pattern = struct.pack('<I', address)
        for m in re.finditer(re.escape(pattern), raw):
            print('REF', hex(address), 'at', hex(va(m.start())))
if args.range:
    start, end = map(lambda x:int(x,0), args.range)
    code = pe.get_data(start-base, end-start)
    cs = Cs(CS_ARCH_X86, CS_MODE_32)
    for ins in cs.disasm(code, start):
        print(f'{ins.address:08x}: {ins.bytes.hex():22} {ins.mnemonic:8} {ins.op_str}')
