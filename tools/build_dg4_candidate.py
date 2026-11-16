"""Pin and frame DG4 raw firmware offline; no PE or device/updater execution."""
from datetime import datetime,timezone
from pathlib import Path
import hashlib,json,uuid
from emulate_dg4_minimal import build,run_checks,ROOT,BASE,APPEND,SOURCE_HASH
from build_oled_candidate import cms,decode_resource
from check_firmware_header import seal_header,validate_header,EDITABLE_HEADER_OFFSETS
if not __debug__:raise RuntimeError('Assertions are required; do not use -O')
PAYLOAD_BYTES=160
PAYLOAD_HASH='68b649a99d2d935e7941a742e3dacaa81f9bb07a7fb0d48bf525be94ab8ba5d0'
RESOURCE_BYTES=134112
RESOURCE_HASH='5b5bad4b0c15a60af976f6b96a5d1d912d1a0d6af6c26587ace0aed0f40b0074'

def sha(data):return hashlib.sha256(data).hexdigest()
def build_resource(original,code,layout):
    assert len(original)==134140 and sha(original)==SOURCE_HASH
    validate_header(original)
    normalized,blocks,tail=decode_resource(original)
    assert len(blocks)==3939 and tail==b'\xff'*198 and BASE+len(normalized)==APPEND
    patched=bytearray(normalized);records=layout['hooks']+layout['data_patches']
    assert len(records)==5 and {r['address'] for r in records}=={0x3201C,0x342A4,0x37896,0x3D33A,0x305D2}
    for record in records:
        pos=record['normalized_offset'];old,new=bytes.fromhex(record['old_hex']),bytes.fromhex(record['new_hex'])
        assert pos==record['address']-BASE and len(old)==len(new) and patched[pos:pos+len(old)]==old
        patched[pos:pos+len(new)]=new
    padded=code+b'\xff'*((-len(code))%32);framed=bytearray(original[:16]);changed=[]
    for index,block in enumerate(blocks):
        data=bytes(patched[16+index*32:16+(index+1)*32])
        if data==block[:32]:framed.extend(block)
        else:framed.extend(data+cms(data).to_bytes(2,'big'));changed.append(index)
    assert len(changed)==5
    for offset in range(0,len(padded),32):
        data=padded[offset:offset+32];framed.extend(data+cms(data).to_bytes(2,'big'))
    candidate=seal_header(framed);validate_header(candidate)
    assert len(candidate)<=134140,'DG4 must fit original BIN resource slot'
    assert len(candidate)<=0x21000 and (len(candidate)+4095)&~4095==0x21000
    assert {i for i in range(16) if candidate[i]!=original[i]}<=EDITABLE_HEADER_OFFSETS
    decoded,cb,ct=decode_resource(candidate)
    expected=patched+padded;expected[:16]=candidate[:16]
    assert decoded==expected and len(cb)==3939+len(padded)//32
    assert ct==b'\xff'*((-len(framed))%4)
    for index,block in enumerate(blocks):assert (block==cb[index])==(index not in changed)
    return candidate,decoded,changed

def main():
    original=(ROOT/'evidence/g9pro-official-firmware-resource.bin').read_bytes()
    code,layout=build();candidate,decoded,changed=build_resource(original,code,layout)
    assert len(code)==PAYLOAD_BYTES and sha(code)==PAYLOAD_HASH,'DG4 payload unpinned'
    assert len(candidate)==RESOURCE_BYTES and sha(candidate)==RESOURCE_HASH,'DG4 resource unpinned'
    checks=run_checks(code,layout,decoded)
    assert (ROOT/'evidence/g9pro-official-firmware-resource.bin').read_bytes()==original
    stamp=datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    out=ROOT/'patch-output'/(stamp+'-DG4-'+uuid.uuid4().hex[:8]);out.mkdir(parents=True)
    target=out/'g9-pro-1.08-oem-stock-oled-rf-state-DG4.CANDIDATE.bin';target.write_bytes(candidate)
    manifest=dict(status='OFFLINE_DG4_MINIMAL_OEM_DIAGNOSTIC_NOT_PHYSICALLY_QUALIFIED',
        candidate_role='DG2 baseline with temporary stock-font RF state scalar; no custom OLED or claimed RF fix',
        source_sha256=SOURCE_HASH,resource_bytes=len(candidate),resource_sha256=sha(candidate),
        payload_bytes=len(code),payload_sha256=sha(code),layout=layout,checks=checks,
        changed_original_blocks=changed,append_crc_blocks=layout['extension_crc_blocks'],
        transfer_bytes=0x21000,update_pages=33,required_bounded_erase='[0x2B000,0x51000)',
        version_marker='OEM_UNCHANGED',custom_oled_supported=False,physical_rf_control_confirmed=False,
        telemetry='Native DPI number becomes1000+(lastReport<<4)+(RFstate&15), includingUSB;context is not a counter',
        owner_sampling='After RF probe, move thenstop; nativeAB6 countdown permits queuedDPI redraw; no guaranteedwalltime',
        device_access=False,updater_executed=False,independent_recovery_verified=False)
    (out/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    (ROOT/'bin/g9_dg4-offline-checks.json').write_text(json.dumps(checks,indent=2)+'\n')
    print(json.dumps(dict(candidate=str(target),sha256=sha(candidate),bytes=len(candidate))))
if __name__=='__main__':main()
