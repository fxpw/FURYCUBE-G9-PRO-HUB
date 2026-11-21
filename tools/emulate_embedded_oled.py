"""Compile and emulate a cold-boot OLED snapshot with autonomous battery drawing."""
from argparse import ArgumentParser
from datetime import datetime, timezone
from pathlib import Path
from tempfile import TemporaryDirectory
import hashlib
import json
import subprocess
import uuid

from unicorn.arm_const import UC_ARM_REG_CPSR, UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_SP

from build_oled_candidate import cms, decode_resource
from build_ol9_candidate import PAYLOAD_HASH
from check_firmware_header import seal_header, validate_header
from emulate_ol5_radio import ROOT, BASE, APPEND, SOURCE_HASH
from emulate_ol5_retention import image
from emulate_ol9_carrier import Fixture, build as build_ol9, carrier, run_checks
from emulate_oled_arm import elf_text_and_symbols
from emulate_oled_protocol import crc16
from emulate_screen_key import thumb_bl


FRAME_BYTES = 480
CHUNK_BYTES = 40
LEVELS = 11
TRANSFER_BYTES = 0x22000
BATTERY = 0x400AC6
PANEL_ASLEEP = 0x400A7A
PANEL_OFF = 0x400AF2
STARTUP_CLEAR = 0x28E8E
STARTUP_STATUS = 0x28E92
STARTUP_DONE = 0x28E96


def sha(data):
    return hashlib.sha256(data).hexdigest()


def chunk(frame, index):
    start = index * CHUNK_BYTES
    return frame[start:start + CHUNK_BYTES]


def encode_snapshot(raw):
    if len(raw) != LEVELS * FRAME_BYTES:
        raise ValueError("Expected 11 full 120 x 28 frames, from 0 to 100 percent.")
    frames = [raw[i:i + FRAME_BYTES] for i in range(0, len(raw), FRAME_BYTES)]
    if any(byte & 0xF0 for frame in frames for byte in frame[360:]):
        raise ValueError("Frame uses pixels outside the visible display.")
    base = frames[0]
    changed = []
    mapping = bytearray(12)
    table = bytearray()
    for index in range(12):
        thresholds, masks = bytearray(), bytearray()
        for pos in range(index * CHUNK_BYTES, (index + 1) * CHUNK_BYTES):
            values = [frame[pos] ^ base[pos] for frame in frames]
            start = next((i for i, value in enumerate(values) if value), LEVELS)
            mask = values[start] if start < LEVELS else 0
            if values != [mask if i >= start else 0 for i in range(LEVELS)]:
                raise ValueError(f"Byte {pos} changes more than once as charge rises.")
            thresholds.append(start)
            masks.append(mask)
        if any(masks):
            changed.append(index)
            mapping[index] = len(changed)
            table.extend(thresholds + masks)
    if not changed:
        raise ValueError("The battery pixels are fully hidden or outside the canvas.")
    return frames, changed, mapping, table


def bytes_directives(data):
    return "\n".join(".byte " + ",".join(map(str, data[i:i + 16]))
                     for i in range(0, len(data), 16))


def build(frames, changed, mapping, table, delta=False, fast_wait=False, dirty_draw=False,
          radio_ack=False, radio_retry=False):
    if fast_wait and not delta:
        raise ValueError("The shorter radio wait requires the delta protocol.")
    if dirty_draw and not fast_wait:
        raise ValueError("The selective redraw build requires the shorter radio wait.")
    if radio_ack and (not fast_wait or dirty_draw):
        raise ValueError("The radio acknowledgement probe is based on OLB.")
    if radio_retry and not radio_ack:
        raise ValueError("Replay requires the radio acknowledgement hook.")
    stock_ol9, layout = build_ol9()
    assert sha(stock_ol9) == PAYLOAD_HASH
    with TemporaryDirectory(prefix="g9-snapshot-") as folder:
        directory = Path(folder)
        include = directory / "g9_embedded_snapshot.inc"
        include.write_text(
            f".Llocal_header:\n.byte {len(changed)},0\n.hword {crc16(frames[0])}\n"
            ".hword .Llocal_frame-.Llocal_header\n.hword .Llocal_table-.Llocal_header\n"
            + bytes_directives(mapping) + "\n" + bytes_directives(changed)
            + "\n.balign 4\n.Llocal_frame:\n" + bytes_directives(frames[0])
            + "\n.Llocal_table:\n" + bytes_directives(table) + "\n.balign 4\n",
            encoding="ascii",
        )
        wrapper = directory / "snapshot.S"
        wrapper.write_text(
            '.equ G9_EMBEDDED_OLED, 1\n'
            + ('.equ G9_OLED_DELTA, 1\n' if delta else '')
            + ('.equ G9_OLED_DELTA_FAST_WAIT, 1\n' if fast_wait else '')
            + ('.equ G9_OLED_DIRTY_DRAW, 1\n' if dirty_draw else '')
            + ('.equ G9_OLED_RADIO_ACK, 1\n' if radio_ack else '')
            + ('.equ G9_OLED_RADIO_RETRY, 1\n' if radio_retry else '')
            + '.include "firmware/g9_ol9.S"\n', encoding="ascii"
        )
        output = directory / "snapshot.o"
        subprocess.run(["clang", "--target=arm-none-eabi", "-march=armv5t",
                        "-mthumb", "-I", folder, "-c", str(wrapper), "-o", str(output)],
                       cwd=ROOT, check=True)
        code, symbols = elf_text_and_symbols(output.read_bytes())
    for hook in layout["hooks"]:
        hook["target"] = APPEND + symbols[hook["symbol"]]["offset"]
        hook["new_hex"] = thumb_bl(hook["address"], hook["target"]).hex()
    stock_image = (ROOT / "evidence/g9pro-firmware-normalized.bin").read_bytes()
    extra_hooks = [(STARTUP_CLEAR, 0x3D824, "g9_embedded_startup_hook")]
    if fast_wait:
        extra_hooks.append((0x300DE, 0x24F9C, "g9_delta_first_body_hook"))
    for address, stock_target, symbol in extra_hooks:
        offset = address - BASE
        stock_call = thumb_bl(address, stock_target)
        assert stock_image[offset:offset + 4] == stock_call
        target = APPEND + symbols[symbol]["offset"]
        layout["hooks"].append(dict(address=address, normalized_offset=offset,
                                    old_hex=stock_call.hex(), new_hex=thumb_bl(address, target).hex(),
                                    target=target, symbol=symbol))
    marker = "OLB" if fast_wait else "OLA" if delta else "OL9"
    if dirty_draw:
        marker = "OLC"
    if radio_ack:
        address = 0x35EBC
        offset = address - BASE
        before = bytes.fromhex("002851d0")
        assert stock_image[offset:offset + len(before)] == before
        symbol = "g9_radio_ack_idle_hook"
        target = APPEND + symbols[symbol]["offset"]
        layout["hooks"].append(dict(address=address, normalized_offset=offset,
                                    old_hex=before.hex(), new_hex=thumb_bl(address, target).hex(),
                                    target=target, symbol=symbol))
        marker = "OLE" if radio_retry else "OLD"
    layout.update(symbols=symbols, text_bytes=len(code), text_sha256=sha(code),
                  version_report_marker=marker,
                  extension_crc_blocks=(len(code) + 31) // 32)
    layout["minimum_resource_size"] = 16 + (3939 + layout["extension_crc_blocks"]) * 34
    if layout["minimum_resource_size"] > TRANSFER_BYTES:
        raise ValueError("Snapshot exceeds the existing 34-page transfer size.")
    return code, layout


def pack(code, layout):
    original = (ROOT / "evidence/g9pro-official-firmware-resource.bin").read_bytes()
    assert sha(original) == SOURCE_HASH
    validate_header(original)
    decoded, blocks, padding = decode_resource(original)
    assert len(blocks) == 3939 and BASE + len(decoded) == APPEND
    assert padding == b"\xff" * 198
    image_bytes = bytearray(decoded)
    touched = set()
    for patch in layout["hooks"] + layout["data_patches"] + [layout["descriptor_patch"]]:
        before, after = bytes.fromhex(patch["old_hex"]), bytes.fromhex(patch["new_hex"])
        offset = patch["address"] - BASE
        span = set(range(offset, offset + len(before)))
        assert 16 <= offset and len(before) == len(after) and not (touched & span)
        assert image_bytes[offset:offset + len(before)] == before
        image_bytes[offset:offset + len(after)] = after
        touched.update(span)
    image_bytes.extend(code + b"\xff" * (-len(code) % 32))
    packed = bytearray(original[:16])
    for start in range(16, len(image_bytes), 32):
        block = image_bytes[start:start + 32]
        packed.extend(block + cms(block).to_bytes(2, "big"))
    resource = seal_header(packed)
    assert (len(resource) + 4095) & ~4095 == TRANSFER_BYTES
    validate_header(resource)
    actual, _, trailing = decode_resource(resource)
    image_bytes[:16] = actual[:16]
    assert actual == image_bytes and trailing == b"\xff" * len(trailing)
    return resource, actual


def expect_render(fixture, expected, chunks):
    fixture.clear_trace()
    instructions = []
    for index in chunks:
        start = len(fixture.transactions)
        instructions.append(fixture.tick())
        page, part = divmod(index, 3)
        x = part * CHUNK_BYTES
        assert fixture.transactions[start:] == [
            bytes([0x78, 0, 0xB0 + page]), bytes([0x78, 0, 0x10 | (x >> 4)]),
            bytes([0x78, 0, x & 15]), bytes([0x78, 0x40]) + chunk(expected, index),
        ], index
    fixture.clear_trace()
    fixture.tick()
    assert not fixture.transactions
    return instructions


def reset(fixture, clear_ram):
    if clear_ram:
        fixture.uc.mem_write(0x400000, bytes(0x10000))
    fixture.stop_at = 0x28D5C
    fixture.uc.reg_write(UC_ARM_REG_CPSR, 0xD3)
    try:
        fixture.uc.emu_start(0x28824, 0x600000, count=500000)
    finally:
        fixture.stop_at = None
    assert fixture.uc.reg_read(UC_ARM_REG_PC) == 0x28D5C


class StartupFixture(Fixture):
    def __init__(self, *args):
        self.phase = {}
        super().__init__(*args)

    def hook(self, uc, address, size, context):
        if address == 0x31CB6:  # Splash dwell is independent of OLED pixel writes.
            self.ret()
            return
        if address in (STARTUP_CLEAR, STARTUP_STATUS, STARTUP_DONE):
            self.phase[address] = len(self.transactions)
        super().hook(uc, address, size, context)

    def startup(self, splash):
        logo_flag = int.from_bytes(self.uc.mem_read(0x28FF4, 4), "little")
        self.set8(logo_flag, 5 if splash else 0)
        self.set8(BATTERY, 40)
        self.clear_trace()
        self.stop_at = STARTUP_DONE
        self.uc.reg_write(UC_ARM_REG_SP, 0x508000)
        self.uc.reg_write(UC_ARM_REG_LR, 0x600001)
        try:
            self.uc.emu_start(0x28E73, 0x600000, count=500000)
        finally:
            self.stop_at = None
        assert self.uc.reg_read(UC_ARM_REG_PC) == STARTUP_DONE
        return (
            self.transactions[:self.phase[STARTUP_CLEAR]],
            self.transactions[self.phase[STARTUP_CLEAR]:],
        )


def verify_startup_transition(code, layout, candidate, frames):
    for splash in (False, True):
        previous = StartupFixture(code, layout, candidate)
        previous.uc.mem_write(STARTUP_CLEAR, thumb_bl(STARTUP_CLEAR, 0x3D824))
        old_logo, old_status = previous.startup(splash)
        assert any(t.startswith(b"\x78\x40") and len(t) > 2 for t in old_status)
        new = StartupFixture(code, layout, candidate)
        logo, status = new.startup(splash)
        assert logo == old_logo
        assert any(t.startswith(b"\x78\x40") and len(t) > 2 for t in logo) == splash
        assert not any(t.startswith(b"\x78\x40") and len(t) > 2 for t in status)
        expect_render(new, frames[6], range(12))
        assert not new.flash_writes and not new.profile_loads


def run_snapshot_checks(code, layout, candidate, frames, changed):
    device = Fixture(code, layout, candidate)
    assert device.active_frame() is None
    device.tick()
    assert not device.transactions
    device.run(0x3CDDE)
    device.set8(BATTERY, 28)
    expect_render(device, frames[7], range(12))
    assert device.active_frame() == frames[0]
    device.set8(BATTERY, 21)
    device.tick()
    assert not device.transactions
    for level in (8, 10, 9, 4, 0, 1, 2, 3, 5, 6, 7):
        device.set8(BATTERY, 100 - level * 10)
        expect_render(device, frames[level], changed)
    for flag in (PANEL_ASLEEP, PANEL_OFF):
        device.set8(flag, 1)
        device.set8(BATTERY, 0)
        device.tick()
        assert not device.transactions and not device.mmio_writes
        device.set8(flag, 0)
    device.run(0x3CDDE)
    expect_render(device, frames[10], range(12))
    reset(device, clear_ram=False)
    assert device.active_frame() == frames[0]
    device.run(0x3CDDE)
    device.set8(BATTERY, 64)
    expect_render(device, frames[3], range(12))

    replacement = image(31)
    device.packet(carrier(0, 0, chunk(replacement, 0), True))
    assert device.active_frame() == frames[0]
    device.set8(BATTERY, 50)
    expect_render(device, frames[5], changed)
    device.upload(replacement)
    expect_render(device, replacement, range(12))
    device.set8(BATTERY, 20)
    device.tick()
    assert not device.transactions
    reset(device, clear_ram=False)
    assert device.active_frame() == replacement
    device.run(0x3CDDE)
    expect_render(device, replacement, range(12))
    reset(device, clear_ram=True)
    assert device.active_frame() is None
    device.run(0x3CDDE)
    device.set8(BATTERY, 71)
    expect_render(device, frames[2], range(12))
    assert not device.flash_writes and not device.profile_loads
    return dict(cold_boot_with_cleared_sram=True, warm_retention=True,
                threshold_transitions=11, no_update_in_same_bucket=True,
                chunks_per_charge_update=len(changed), native_pixel_transactions=True,
                partial_host_upload_preserves_overlay=True, complete_host_upload_replaces_overlay=True,
                panel_sleep_and_off_no_traffic=True, custom_flash_writes=0)


def main():
    if not __debug__:
        raise RuntimeError("Assertions are required.")
    parser = ArgumentParser(description=__doc__)
    parser.add_argument("snapshot", type=Path)
    args = parser.parse_args()
    raw = args.snapshot.read_bytes()
    frames, changed, mapping, table = encode_snapshot(raw)
    code, layout = build(frames, changed, mapping, table)
    resource, decoded = pack(code, layout)
    verify_startup_transition(code, layout, decoded, frames)
    snapshot_checks = run_snapshot_checks(code, layout, decoded, frames, changed)
    snapshot_checks.update(startup_logo_transactions_preserved=True, startup_status_pixels_blocked=True)
    regressions = run_checks(code, layout, candidate_image=decoded)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    folder = ROOT / "patch-output" / (stamp + "-embedded-oled-" + uuid.uuid4().hex[:8])
    folder.mkdir(parents=True)
    candidate = folder / "g9-embedded-oled.OFFLINE.bin"
    candidate.write_bytes(resource)
    manifest = dict(status="OFFLINE_ONLY_NO_DEVICE_OR_UPDATER_ACCESS",
                    snapshot_sha256=sha(raw), resource_sha256=sha(resource),
                    resource_bytes=len(resource), transfer_pages=34, layout=layout,
                    snapshot_checks=snapshot_checks, regressions=regressions)
    (folder / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(json.dumps(dict(candidate=str(candidate), resource_sha256=sha(resource),
                          payload_bytes=len(code), snapshot_checks=snapshot_checks), indent=2))


if __name__ == "__main__":
    main()
