"""Separate pinned OL9 carrier resource. Offline only; never launch an updater."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import uuid
from emulate_ol9_carrier import build, run_checks
from emulate_ol5_radio import ROOT, BASE, APPEND, SOURCE_HASH
from build_oled_candidate import cms, decode_resource
from check_firmware_header import seal_header, validate_header, EDITABLE_HEADER_OFFSETS

if not __debug__:
    raise RuntimeError('Assertions are required; do not use -O')
PAYLOAD_BYTES=2080
PAYLOAD_HASH='1162dce22978468529baffe68f1b8a6b9eb868ee0b2c9b59f268a565f3d4f138'
APPEND_BLOCKS=65
TOTAL_BLOCKS=3939+APPEND_BLOCKS
RESOURCE_BYTES=136152 # OEM container rounds to4-byte alignment.
# Filled after independent framing inspection, before a candidate is emitted.
RESOURCE_HASH='385a653c071cf509d376f8efe2c5d96333cfc68ee7a4d13f7f2b35b8a18dab0e'
def sha(data):return hashlib.sha256(data).hexdigest()

def build_resource(original, code, layout):
    assert len(original)==134140 and sha(original)==SOURCE_HASH
    validate_header(original)
    assert len(code)==PAYLOAD_BYTES and sha(code)==PAYLOAD_HASH
    normalized,blocks,tail=decode_resource(original)
    assert len(blocks)==3939 and tail==b'\xff'*198 and BASE+len(normalized)==APPEND
    patched=bytearray(normalized);changed=set()
    records=layout['hooks']+layout['data_patches']+[layout['descriptor_patch']]
    assert len(layout['hooks'])==25 and len(layout['data_patches'])==2
    for record in records:
        offset=record['address']-BASE
        old,new=bytes.fromhex(record['old_hex']),bytes.fromhex(record['new_hex'])
        assert offset==record['normalized_offset'] and offset>=16
        assert len(old)==len(new) and offset+len(old)<=len(patched)
        assert patched[offset:offset+len(old)]==old
        span=set(range(offset,offset+len(old)));assert not span&changed
        changed|=span;patched[offset:offset+len(old)]=new
    padded=code+b'\xff'*((-len(code))%32)
    assert len(padded)==APPEND_BLOCKS*32
    framed=bytearray(original[:16]);changed_blocks=[]
    for index,block in enumerate(blocks):
        data=bytes(patched[16+index*32:16+(index+1)*32])
        if data==block[:32]:framed.extend(block)
        else:
            framed.extend(data+cms(data).to_bytes(2,'big'));changed_blocks.append(index)
    assert len(changed_blocks)==28
    for offset in range(0,len(padded),32):
        data=padded[offset:offset+32];framed.extend(data+cms(data).to_bytes(2,'big'))
    candidate=seal_header(framed)
    assert len(candidate)==RESOURCE_BYTES and (len(candidate)+4095)&~4095==0x22000
    assert {i for i in range(16) if original[i]!=candidate[i]}<=EDITABLE_HEADER_OFFSETS
    validate_header(candidate)
    decoded,framed_blocks,padding=decode_resource(candidate)
    expected=patched+padded;expected[:16]=candidate[:16]
    assert decoded==expected and len(framed_blocks)==TOTAL_BLOCKS and padding==b'\xff'*(RESOURCE_BYTES-(16+TOTAL_BLOCKS*34))
    for index,block in enumerate(blocks):
        assert (block==framed_blocks[index])==(index not in changed_blocks)
    return candidate,decoded,changed_blocks

def main():
    source=ROOT/'evidence/g9pro-official-firmware-resource.bin'
    original=source.read_bytes();code,layout=build()
    candidate,decoded,changed_blocks=build_resource(original,code,layout)
    assert RESOURCE_HASH is not None and sha(candidate)==RESOURCE_HASH,'OL9 resource not pinned'
    checks=run_checks(code,layout,candidate_image=decoded)
    assert source.read_bytes()==original
    stamp=datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    out=ROOT/'patch-output'/(stamp+'-OL9-'+uuid.uuid4().hex[:8]);out.mkdir(parents=True)
    target=out/'g9-pro-1.08-oled-radio-carrier-OL9.CANDIDATE.bin';target.write_bytes(candidate)
    manifest=dict(status='OFFLINE_OL9_RESOURCE_CHECKED_NOT_PHYSICALLY_QUALIFIED',
        source_sha256=sha(original),resource_sha256=sha(candidate),resource_bytes=len(candidate),
        payload_bytes=len(code),payload_sha256=sha(code),layout=layout,checks=checks,
        changed_original_blocks=changed_blocks,append_crc_blocks=APPEND_BLOCKS,
        update_pages=34,required_bounded_erase='[0x2B000,0x51000)',
        physical_receiver_qualified=False,device_access=False,updater_executed=False,
        independent_recovery_verified=False)
    (out/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    print(json.dumps(dict(candidate=str(target),sha256=sha(candidate),bytes=len(candidate))))

if __name__=='__main__':main()
