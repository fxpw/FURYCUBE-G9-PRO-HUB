"""Offline analysis only. No USB access and no firmware writes."""
from pathlib import Path
import hashlib
import re
import struct
from capstone import Cs, CS_ARCH_ARM, CS_MODE_ARM, CS_MODE_THUMB

root=Path(__file__).resolve().parents[1]
source=root/'evidence/g9pro-official-firmware-resource.bin'
raw=source.read_bytes()
# The package has a 16-byte header, then 32-byte data / 2-byte checkword blocks.
data=bytearray(raw[:16])
for off in range(16,len(raw),34):
    data.extend(raw[off:off+32])
data=bytes(data)
(root/'evidence/g9pro-firmware-normalized.bin').write_bytes(data)
print('Source SHA256',hashlib.sha256(raw).hexdigest(),'raw',len(raw),'normalized',len(data))
base=0x28790
strings=[]
for m in re.finditer(rb'[ -~]{7,}',data):
    text=m.group().decode('ascii')
    if re.search('key|report|keyboard|macro|mouse|usb|RF_MODE|version',text,re.I):
        address=base+m.start()
        refs=[base+x.start() for x in re.finditer(re.escape(struct.pack('<I',address)),data)]
        strings.append((hex(address),text,[hex(a) for a in refs]))
for item in strings:print(item)
for mode,name in [(CS_MODE_ARM,'arm'),(CS_MODE_THUMB,'thumb')]:
    cs=Cs(CS_ARCH_ARM,mode);cs.skipdata=True
    with (root/'evidence'/('g9pro-firmware-'+name+'.asm')).open('w') as f:
        for ins in cs.disasm(data,base):
            f.write(f'{ins.address:08x}: {ins.bytes.hex():12} {ins.mnemonic:8} {ins.op_str}\n')
