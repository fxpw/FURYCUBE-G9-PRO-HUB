"""Read-only inspection of the vendor's binary settings (Python 3)."""
from pathlib import Path
import hashlib
import json
import struct

ROOT = Path(__file__).resolve().parents[1]
ORIGINAL = ROOT / 'evidence' / 'original'

def hexdump(data, base=0):
    for i in range(0, len(data), 16):
        row = data[i:i+16]
        print(f'{base+i:08x}  {row.hex(" "):47}  ' + ''.join(chr(c) if 32 <= c < 127 else '.' for c in row))

if __name__ == '__main__':
    manifest = []
    for p in sorted(ORIGINAL.rglob('*')):
        if p.is_file():
            data = p.read_bytes()
            manifest.append(dict(path=str(p.relative_to(ORIGINAL)), size=len(data), sha256=hashlib.sha256(data).hexdigest()))
    (ROOT / 'evidence' / 'original-sha256.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    for p in sorted((ORIGINAL / 'Data').rglob('*.data')):
        data = p.read_bytes()
        print('\nFILE', p.relative_to(ORIGINAL), 'SIZE', len(data))
        hexdump(data[:256])
        if p.name == 'macro.data':
            print('UTF16 prefix:', repr(data[:64].decode('utf-16le', errors='replace')))
        else:
            runs = []
            start = None
            for i, b in enumerate(data):
                if b and start is None:
                    start = i
                if start is not None and (not b or i == len(data)-1):
                    end = i if not b else i+1
                    if len(runs) < 100:
                        runs.append((hex(start), data[start:end].hex()))
                    start = None
            print('First nonzero runs:', runs)
