"""Build a reviewed OL4 raw candidate offline. Never access HID or run updater.
The owner accepted the loss risk for a separate physical experiment; this tool
only prepares evidence and does not claim installed backup or verified rollback.
"""
from datetime import datetime,timezone
from pathlib import Path
import hashlib,json,uuid
from emulate_ol4_status import build,run_regressions,BASE,APPEND,SOURCE_HASH
from build_oled_candidate import decode_resource,cms,verify_input_paths
from check_firmware_header import seal_header,validate_header,EDITABLE_HEADER_OFFSETS
ROOT=Path(__file__).resolve().parents[1]
SOURCE=ROOT/'evidence/g9pro-official-firmware-resource.bin'
def sha(data):return hashlib.sha256(data).hexdigest()
def main():
    original=SOURCE.read_bytes();assert len(original)==134140 and sha(original)==SOURCE_HASH
    validate_header(original)
    preserved={name:(ROOT/name).read_bytes() for name in ('firmware/g9_oled.S','firmware/g9_oled.payload.hex','firmware/g9_ol3.S','firmware/g9_ol3.payload.hex')}
    normalized,blocks,tail=decode_resource(original)
    assert len(blocks)==3939 and tail==bytes([255])*198 and BASE+len(normalized)==APPEND
    code,layout=build();patched=bytearray(normalized);patches=[]
    def change(address,before,after,reason):
        offset=address-BASE
        assert len(before)==len(after) and bytes(patched[offset:offset+len(before)])==before
        patched[offset:offset+len(after)]=after
        patches.append(dict(address=address,normalized_offset=offset,old_hex=before.hex(),new_hex=after.hex(),reason=reason))
    change(0x3201C,bytes.fromhex('0d70'),bytes.fromhex('c046'),'Preserve NoWait button fix')
    for h in layout['hooks']:
        change(h['address'],bytes.fromhex(h['old_hex']),bytes.fromhex(h['new_hex']),h['symbol'])
    d=layout['descriptor_patch'];change(d['address'],bytes.fromhex(d['old_hex']),bytes.fromhex(d['new_hex']),d['reason'])
    framed=bytearray(original[:16]);changed_blocks=[]
    for index,block in enumerate(blocks):
        payload=bytes(patched[16+32*index:16+32*(index+1)])
        if payload==block[:32]:framed.extend(block)
        else:framed.extend(payload+cms(payload).to_bytes(2,'big'));changed_blocks.append(index)
    padded=code+bytes([255])*((-len(code))%32)
    for offset in range(0,len(padded),32):
        payload=padded[offset:offset+32];framed.extend(payload+cms(payload).to_bytes(2,'big'))
    candidate=seal_header(framed);header=validate_header(candidate)
    assert {i for i in range(16) if candidate[i]!=original[i]}<=EDITABLE_HEADER_OFFSETS
    padding=(-len(framed))%4
    decoded,candidate_blocks,candidate_tail=decode_resource(candidate)
    expected=bytearray(patched)+padded;expected[:16]=candidate[:16]
    assert decoded==bytes(expected) and candidate_tail==bytes([255])*padding
    offsets={item['normalized_offset']+i for item in patches for i,(a,b) in enumerate(zip(bytes.fromhex(item['old_hex']),bytes.fromhex(item['new_hex']))) if a!=b}
    assert {i for i,(a,b) in enumerate(zip(normalized,decoded)) if i>=16 and a!=b}==offsets
    assert changed_blocks==sorted({(off-16)//32 for off in offsets}) and len(changed_blocks)==7
    assert len(candidate_blocks)==3939+len(padded)//32
    assert len(candidate)==layout['minimum_resource_size']+padding
    assert len(candidate)<=0x21000 and (len(candidate)+4095)&~4095==(len(original)+4095)&~4095==0x21000
    # One pinned EP0-completion hook suppresses only the custom report22's
    # false mouse-endpoint-ready signal. All other USB callback bytes stay stock.
    usb_hooks=[h for h in layout['hooks'] if 0x3778E<=h['address']<0x38A8A]
    assert len(usb_hooks)==1 and usb_hooks[0]['address']==0x37896 and usb_hooks[0]['old_hex']=='06702a78'
    usb_expected=bytearray(normalized[0x3778E-BASE:0x38A8A-BASE])
    usb_at=0x37896-0x3778E
    usb_expected[usb_at:usb_at+4]=bytes.fromhex(usb_hooks[0]['new_hex'])
    assert decoded[0x3778E-BASE:0x38A8A-BASE]==bytes(usb_expected)
    for index,block in enumerate(blocks):assert (block==candidate_blocks[index])==(index not in changed_blocks)
    checks=run_regressions(code,layout,candidate_image=decoded)
    input_checks=verify_input_paths(decoded)
    for name,data in preserved.items():assert (ROOT/name).read_bytes()==data
    assert SOURCE.read_bytes()==original
    stamp=datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    out=ROOT/'patch-output'/(stamp+'-OL4-'+uuid.uuid4().hex[:8]);out.mkdir(parents=True,exist_ok=False)
    target=out/'g9-pro-1.08-zero-oled-status-OL4.CANDIDATE.bin';target.write_bytes(candidate)
    manifest=dict(status='OFFLINE_CANDIDATE_CHECKS_PASSED_NO_FLASH',physical_mouse_validated=False,updater_executed=False,installed_firmware_backed_up=False,
      source_resource_sha256=sha(original),candidate_resource_sha256=sha(candidate),source_resource_size=len(original),candidate_resource_size=len(candidate),
      source_original_crc_blocks=3939,candidate_valid_crc_blocks=len(candidate_blocks),header_crc32_length=header,header_other_10_bytes_preserved=True,
      container_ff_word_padding_bytes=padding,only_changed_original_crc_blocks=changed_blocks,patches=patches,layout=layout,
      append=dict(address=APPEND,machine_code_bytes=len(code),padded_data_bytes=len(padded),crc_blocks=len(padded)//32,code_sha256=sha(code),asm_source_sha256=sha((ROOT/'firmware/g9_ol4.S').read_bytes())),
      features=dict(version_marker='OL4',oled='GO4 fixed64B chunks40px, x0/40/80,12reports/frame',status='GS4 fixed64B, A022400000000000',stock_usb_callbacks_byte_identical=False,usb_callback_patch=usb_hooks[0],report22_does_not_force_mouse_ready=True,no_persistent_global_ram=True,button_NoWait_preserved=True,native_renderer_may_overwrite=True,flash_picture_persistence=False),
      aligned_transfer=dict(bytes=0x21000,range_not_expanded=True),emulation=dict(**checks,**input_checks),
      limits=['PhysicalOLEDupload unverified','No installedfullbackup or verifiedrollback','OEMerase/writeasymmetry remains','Owneracceptedprivateexperiment doesnotqualifypublicrelease'])
    (out/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    (ROOT/'firmware/g9_ol4.payload.hex').write_text(code.hex()+'\n',encoding='ascii')
    (ROOT/'bin/g9_ol4-layout.json').write_text(json.dumps(layout,indent=2),encoding='utf-8')
    assert target.read_bytes()==candidate
    print(json.dumps(dict(candidate=str(target),manifest=str(out/'manifest.json'),candidate_sha256=sha(candidate),candidate_bytes=len(candidate),payload_sha256=sha(code),payload_bytes=len(code))))
if __name__=='__main__':main()
