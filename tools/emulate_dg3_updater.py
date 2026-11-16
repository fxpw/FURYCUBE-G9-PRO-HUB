"""Exact DG3 resource preparation and original 33-page transfer in x86 Unicorn only."""
from pathlib import Path
import argparse
import json
from dg3_updater_package import RESOURCE_BYTES, RESOURCE_HASH, sha, verify
from emulate_updater_prepare import run_prepare
from emulate_updater_transfer import binary, run, transfer_bounds

if not __debug__:
    raise RuntimeError('Assertions are required; do not use -O')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('resource', type=Path)
    parser.add_argument('package', type=Path)
    options = parser.parse_args()
    resource, package = options.resource.read_bytes(), options.package.read_bytes()
    assert len(resource) == RESOURCE_BYTES and sha(resource) == RESOURCE_HASH
    structure = verify(binary, package, resource)
    _, original = run_prepare(binary, resource)
    prepared, corrected = run_prepare(package, resource)
    assert original['overread_bytes'] == 0x21000 - RESOURCE_BYTES and not original['padding_ff']
    assert corrected['overread_bytes'] == 0 and corrected['padding_ff']
    assert prepared == resource + b'\xff' * (0x21000 - RESOURCE_BYTES)
    result = run('normal', 0x21000, image_bytes=package, payload_data=prepared)
    bounds = transfer_bounds(result, 0x21000)
    assert bounds['program_pages'] == 33 and bounds['program_end'] == 0x4C00A
    summary = dict(status='PASS_OFFLINE_EXACT_DG3_PACKAGE', structure=structure,
        original=original, corrected=corrected, transfer=bounds,
        prepared_sha256=sha(prepared), device_access=False, updater_executed=False)
    (options.package.parent / 'updater-emulation.json').write_text(json.dumps(summary, indent=2) + '\n')
    print(json.dumps(summary, indent=2))


if __name__ == '__main__':
    main()
