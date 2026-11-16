"""Verify the exact OL7 PE resource load and bounded34-page transfer offline."""
from pathlib import Path
import argparse,json
from ol7_updater_package import verify, sha, RESOURCE_BYTES, RESOURCE_HASH, resources
from emulate_updater_prepare import run_prepare
from emulate_updater_transfer import binary, run, transfer_bounds

if not __debug__:
    raise RuntimeError('Assertions are required; do not use -O')

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('resource',type=Path);parser.add_argument('package',type=Path)
    options=parser.parse_args();resource=options.resource.read_bytes();image=options.package.read_bytes()
    assert len(resource)==RESOURCE_BYTES and sha(resource)==RESOURCE_HASH
    package=verify(binary,image,resource)
    assert resources(image)[('BIN',129,2052)]==resource
    _,old=run_prepare(binary,resource)
    assert old['overread_bytes']==3144 and not old['padding_ff']
    prepared,corrected=run_prepare(image,resource)
    assert corrected['overread_bytes']==0 and corrected['padding_ff']
    assert len(prepared)==0x22000
    transfer=run('normal',len(prepared),image_bytes=image,payload_data=prepared)
    bounds=transfer_bounds(transfer,len(prepared),bounded_erases=True)
    result=dict(status='PASS_OFFLINE_EXACT_OL7_PACKAGE',package=package,original=old,
        corrected=corrected,transfer=bounds,prepared_sha256=sha(prepared),device_access=False)
    (options.package.parent/'updater-emulation.json').write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result,indent=2))

if __name__=='__main__':main()
