"""Prepare a writable diagnostic copy; leave installed files intact."""
from pathlib import Path
import shutil

root = Path(__file__).resolve().parents[1]
hub = root / 'portable-hub'
original = hub / 'hiddriver_1.dll'
real = hub / 'real_hiddriver_1.dll'
if not real.exists():
    shutil.copy2(original, real)
shutil.copy2(root / 'bin' / 'hiddriver_1.dll', original)
api = hub / 'hidapi.dll'
real_api = hub / 'real_hidapi.dll'
if not real_api.exists():
    shutil.copy2(api, real_api)
shutil.copy2(root / 'bin' / 'hidapi.dll', api)
exe = hub / 'FURYCUBE.exe'
raw = exe.read_bytes()
before = b"level='requireAdministrator'"
after = b"level='asInvoker'".ljust(len(before), b' ')
if before in raw:
    assert len(before) == len(after)
    raw = raw.replace(before, after)
    exe.write_bytes(raw)
    print('Diagnostic copy manifest: asInvoker')
else:
    print('Manifest unchanged; requireAdministrator attribute absent')
