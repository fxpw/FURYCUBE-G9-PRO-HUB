"""Adjust the two delays of the captured zero-key macro in a stopped HUB copy."""
import argparse
from datetime import datetime, timezone
from pathlib import Path
import shutil
import struct

p=argparse.ArgumentParser()
p.add_argument('path',type=Path)
p.add_argument('--delay-ms',type=int,default=50)
args=p.parse_args()
if not 1<=args.delay_ms<=127:
    p.error('This diagnostic supports 1..127 ms only')
raw=bytearray(args.path.read_bytes())
if len(raw)!=3096 or struct.unpack_from('<I',raw,0x34)[0]!=2:
    raise SystemExit('Unexpected macro layout; no changes made')
if [raw[0x39],raw[0x49]]!=[0x27,0x27] or [raw[0x3a],raw[0x4a]]!=[1,2]:
    raise SystemExit('Expected zero key down/up; no changes made')
before=[struct.unpack_from('<H',raw,off)[0] for off in (0x3c,0x4c)]
backup=args.path.with_name(args.path.name+'.'+datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')+'.bak')
shutil.copy2(args.path,backup)
for off in (0x3c,0x4c):
    struct.pack_into('<H',raw,off,args.delay_ms)
args.path.write_bytes(raw)
print('Delays',before,'->',[args.delay_ms,args.delay_ms])
print('Backup:',backup)
