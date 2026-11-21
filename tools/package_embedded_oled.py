"""Repack and emulate the Windows updater for a checked offline OLED snapshot."""
from argparse import ArgumentParser
from pathlib import Path
import json
import struct
import subprocess
import uuid

import pefile

from check_firmware_header import validate_header
from emulate_updater_prepare import run_prepare
from emulate_updater_transfer import run, transfer_bounds
from ol9_updater_package import (ROOT, SOURCE_HASH, bounded_patches, patch_host,
                                 resource_section, resources, sha)


def windows_path(path):
    return subprocess.check_output(["wslpath", "-w", str(path)], text=True).strip()


def align(value, alignment):
    return (value + alignment - 1) // alignment * alignment


def repack(original, staged, resource):
    """Keep application sections byte-identical, with the terminal reloc section moved."""
    before, after = (pefile.PE(data=payload) for payload in (original, staged))
    old_data, new_data = resource_section(before), resource_section(after)
    old_reloc, new_reloc = before.sections[-1], after.sections[-1]
    assert old_reloc.Name == new_reloc.Name == b".reloc\0\0"
    assert before.sections[-2].Name == after.sections[-2].Name == b".rsrc\0\0\0"
    assert len(before.sections) == len(after.sections) == 5
    assert all(bytes(a.__pack__()) == bytes(b.__pack__()) and a.get_data() == b.get_data()
               for a, b in zip(before.sections[:-2], after.sections[:-2]))
    assert old_data.VirtualAddress == new_data.VirtualAddress
    assert old_data.PointerToRawData == new_data.PointerToRawData
    assert 0 <= new_data.SizeOfRawData - old_data.SizeOfRawData <= 4096
    assert old_reloc.SizeOfRawData == new_reloc.SizeOfRawData
    assert old_reloc.Misc_VirtualSize == new_reloc.Misc_VirtualSize
    assert old_reloc.get_data() == new_reloc.get_data()
    assert len(original) == old_reloc.PointerToRawData + old_reloc.SizeOfRawData
    assert len(staged) == new_reloc.PointerToRawData + new_reloc.SizeOfRawData
    assert new_reloc.PointerToRawData == new_data.PointerToRawData + new_data.SizeOfRawData
    section_alignment = before.OPTIONAL_HEADER.SectionAlignment
    assert new_reloc.VirtualAddress == align(new_data.VirtualAddress + new_data.Misc_VirtualSize,
                                            section_alignment)
    assert 0 <= new_reloc.VirtualAddress - old_reloc.VirtualAddress <= section_alignment
    old_directories = before.OPTIONAL_HEADER.DATA_DIRECTORY
    new_directories = after.OPTIONAL_HEADER.DATA_DIRECTORY
    assert old_directories[2].VirtualAddress == new_directories[2].VirtualAddress == old_data.VirtualAddress
    assert new_directories[2].Size == new_data.Misc_VirtualSize
    assert old_directories[5].VirtualAddress == old_reloc.VirtualAddress
    assert new_directories[5].VirtualAddress == new_reloc.VirtualAddress
    assert old_directories[5].Size == new_directories[5].Size == old_reloc.Misc_VirtualSize
    for index, (old, new) in enumerate(zip(old_directories, new_directories)):
        if index not in (2, 5):
            assert (old.VirtualAddress, old.Size) == (new.VirtualAddress, new.Size)

    result = bytearray(original[:old_data.PointerToRawData])
    result.extend(new_data.get_data())
    result.extend(old_reloc.get_data())
    image_size = align(new_reloc.VirtualAddress + new_reloc.Misc_VirtualSize, section_alignment)
    initialized_size = sum(section.SizeOfRawData for section in after.sections
                           if section.Characteristics & 0x40)
    assert after.OPTIONAL_HEADER.SizeOfImage == image_size
    for offset, value in (
        (old_data.get_field_absolute_offset("Misc_VirtualSize"), new_data.Misc_VirtualSize),
        (old_data.get_field_absolute_offset("SizeOfRawData"), new_data.SizeOfRawData),
        (old_reloc.get_field_absolute_offset("VirtualAddress"), new_reloc.VirtualAddress),
        (old_reloc.get_field_absolute_offset("PointerToRawData"), new_reloc.PointerToRawData),
        (old_directories[2].get_field_absolute_offset("Size"), new_directories[2].Size),
        (old_directories[5].get_field_absolute_offset("VirtualAddress"), new_reloc.VirtualAddress),
        (before.OPTIONAL_HEADER.get_field_absolute_offset("SizeOfImage"), image_size),
        (before.OPTIONAL_HEADER.get_field_absolute_offset("SizeOfInitializedData"), initialized_size),
    ):
        struct.pack_into("<I", result, offset, value)
    packed = patch_host(bytes(result), bounded_patches(len(resource)))
    old_entries, new_entries = resources(original), resources(packed)
    assert old_entries.keys() == new_entries.keys()
    assert old_entries.pop(("BIN", 129, 2052)) != resource
    assert new_entries.pop(("BIN", 129, 2052)) == resource
    assert old_entries == new_entries
    packed_pe = pefile.PE(data=packed)
    assert packed_pe.sections[-1].get_data() == old_reloc.get_data()
    return packed, dict(resource_sha256=sha(resource), updater_sha256=sha(packed),
                        other_resources_preserved=len(old_entries),
                        relocation_bytes_preserved=True,
                        old_relocation_rva=old_reloc.VirtualAddress,
                        new_relocation_rva=new_reloc.VirtualAddress)


def main():
    parser = ArgumentParser(description=__doc__)
    parser.add_argument("resource", type=Path)
    args = parser.parse_args()
    resource_path = args.resource.resolve(strict=True)
    resource = resource_path.read_bytes()
    proof = json.loads((resource_path.parent / "manifest.json").read_text())
    assert proof["status"] == "OFFLINE_ONLY_NO_DEVICE_OR_UPDATER_ACCESS"
    assert proof["snapshot_checks"]["cold_boot_with_cleared_sram"]
    assert sha(resource) == proof["resource_sha256"]
    assert len(resource) == proof["resource_bytes"]
    validate_header(resource)
    matches = list((ROOT / "evidence/firmware-readonly").rglob("Update.exe"))
    assert len(matches) == 1
    original = matches[0].read_bytes()
    assert sha(original) == SOURCE_HASH
    output = resource_path.parent / "FURYCUBE-G9-PRO-embedded-OLED.exe.disabled"
    if output.exists():
        raise FileExistsError(output)
    staging = resource_path.parent / f"embedded-updater-staging-{uuid.uuid4().hex[:8]}.disabled"
    subprocess.run(["powershell.exe", "-NoProfile", "-File",
                    windows_path(ROOT / "tools/replace-embedded-resource.ps1"),
                    "-Hub", windows_path(ROOT / "bin/FURYCUBE-G9-PRO-HUB.exe"),
                    "-Original", windows_path(matches[0]),
                    "-Resource", windows_path(resource_path),
                    "-Staging", windows_path(staging)], check=True)
    packed, verified = repack(original, staging.read_bytes(), resource)
    prepared, copy_check = run_prepare(packed, resource)
    assert copy_check["overread_bytes"] == 0 and copy_check["padding_ff"]
    assert len(prepared) == 0x22000 and prepared[:len(resource)] == resource
    transfer = run("normal", len(prepared), image_bytes=packed, payload_data=prepared,
                   allow_reloc_growth=True)
    bounds = transfer_bounds(transfer, len(prepared), bounded_erases=True)
    with output.open("xb") as stream:
        stream.write(packed)
    staging.unlink()
    report = dict(status="OFFLINE_PACKAGE_CHECKED", package=verified,
                  prepared_sha256=sha(prepared), copy=copy_check, transfer=bounds)
    (resource_path.parent / "updater-emulation.json").write_text(
        json.dumps(report, indent=2) + "\n")
    print(json.dumps(dict(path=str(output), **report), indent=2))


if __name__ == "__main__":
    if not __debug__:
        raise RuntimeError("Assertions are required.")
    main()
