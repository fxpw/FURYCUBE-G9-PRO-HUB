"""Replay OLED delivery with missing packets on a local OL9, OLA or OLB image.

Runs mouse-side ARM code. The receiver, RF FIFO/IRQ timing and elapsed time
between firmware timer services are outside the model. Never accesses a device.
"""
from argparse import ArgumentParser
import hashlib
import json
from pathlib import Path

from build_oled_candidate import decode_resource
from emulate_ol5_radio import BASE, RFBUF, RFSTATE, REMAIN, SEQ
from emulate_embedded_oled import expect_render
from emulate_ol9_carrier import Fixture, carrier
from unicorn import UC_HOOK_CODE
from unicorn.arm_const import UC_ARM_REG_PC, UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_SP


RF_TRANSACTION_TIMER = 0x400B62
RF_CHANNEL_TIMER = 0x400B5F
PREFIX = bytes([0x36, 4, 56, 0, 0, 0, 0, 0])
CHUNK_BYTES = 40
CHUNKS = 12


class DispatchFixture(Fixture):
    def hook(self, uc, address, size, context):
        if address in (0x36EC4, 0x36EFC):
            self.ret()  # RF peripheral recovery and return to polling.
        elif address in (0x371CE, 0x371B2):
            return  # Execute the real channel rotation and its timer reload.
        else:
            super().hook(uc, address, size, context)

    def dispatch(self, irq=0, fifo=b""):
        self.rf_fifo = fifo
        self.irq(irq)
        self.run(0x306CC)

    def receive_prefix(self):
        assert self.get8(RFSTATE) == 0
        # Supply the receive-window state and RF timer armed by idle service.
        self.set8(RFSTATE, 1)
        self.set8(RF_TRANSACTION_TIMER, 250)
        self.dispatch(0x40, PREFIX)
        assert self.get8(RFSTATE) == 5
        self.dispatch()
        assert self.get8(RFSTATE) == 6
        assert self.rf_sent[-1] == bytes([0x46]) + PREFIX[1:]
        self.dispatch(0x20)
        assert self.get8(RFSTATE) == 9 and self.get8(REMAIN) == 54

    def receive_body(self, sequence, body):
        self.dispatch(0x40, bytes([0x38, sequence]) + body)

    def advance_radio_timeout(self, ticks):
        for _ in range(ticks):
            self.uc.emu_start(0x2AF33, 0x2AF46, count=12)

    def radio_input_poll_scheduled(self):
        """Execute the stock polling gate using the current radio state."""
        radio_input_poll = 0x35D72
        skipped_poll = 0x35D90
        sample_input = 0x35DA2
        controls = {0x400A7E: 0, 0x400A9A: 0, 0x400B19: 1}
        saved = {address: self.get8(address) for address in controls}
        stack = self.uc.reg_read(UC_ARM_REG_SP)

        def stop_before_polling(uc, address, size, context):
            if address in (skipped_poll, sample_input):
                uc.emu_stop()

        hook = self.uc.hook_add(UC_HOOK_CODE, stop_before_polling)
        try:
            for address, value in controls.items():
                self.set8(address, value)
            self.uc.reg_write(UC_ARM_REG_SP, 0x508000)
            self.uc.emu_start(radio_input_poll | 1, sample_input + 4, count=24)
            branch = self.uc.reg_read(UC_ARM_REG_PC)
            assert branch in (skipped_poll, sample_input), hex(branch)
            return branch == sample_input
        finally:
            self.uc.hook_del(hook)
            self.uc.reg_write(UC_ARM_REG_SP, stack)
            for address, value in saved.items():
                self.set8(address, value)

    def send_chunk(self, index, frame):
        page, part = divmod(index, 3)
        start = index * CHUNK_BYTES
        packet = carrier(page, part * CHUNK_BYTES, frame[start:start + CHUNK_BYTES])
        self.receive_prefix()
        self.receive_body(1, packet[2:32])
        self.receive_body(2, packet[32:])
        assert self.get8(RFSTATE) == 10 and self.get8(REMAIN) == 0
        self.dispatch()
        assert self.get8(RFSTATE) == 0


def audit(make_fixture, old_frame, new_frame):
    changed = [
        index for index in range(CHUNKS)
        if old_frame[index * CHUNK_BYTES:(index + 1) * CHUNK_BYTES]
        != new_frame[index * CHUNK_BYTES:(index + 1) * CHUNK_BYTES]
    ]
    assert 0 < len(changed) < CHUNKS
    positions = [index for index, (old, new) in enumerate(zip(old_frame, new_frame))
                 if old != new]
    runs = sum(index == 0 or pos != positions[index - 1] + 1
               for index, pos in enumerate(positions))
    # Estimate a possible delta, not a supported wire format. Count byte,
    # CRC32 of each complete frame, two bytes per run and replacement bytes.
    delta_size = 1 + 8 + 2 * runs + len(positions)

    fixture = make_fixture()
    for index in range(CHUNKS):
        fixture.send_chunk(index, old_frame)
    assert fixture.active_frame() == old_frame
    expect_render(fixture, old_frame, range(CHUNKS))
    config = bytes(fixture.uc.mem_read(0x401020, 0x8E))

    for omitted in range(CHUNKS):
        for index in range(CHUNKS):
            if index != omitted:
                fixture.send_chunk(index, new_frame)
        assert fixture.active_frame() == old_frame, omitted

    for index in changed:
        fixture.send_chunk(index, new_frame)
    assert fixture.active_frame() == old_frame

    for index in range(CHUNKS):
        fixture.send_chunk(index, new_frame)
    assert fixture.active_frame() == new_frame
    expect_render(fixture, new_frame, range(CHUNKS))
    assert bytes(fixture.uc.mem_read(0x401020, 0x8E)) == config
    assert not fixture.legacy_dispatches and not fixture.flash_writes

    assert fixture.radio_input_poll_scheduled()
    fixture.receive_prefix()
    first_packet = carrier(0, 0, old_frame[:CHUNK_BYTES])
    fixture.receive_body(1, first_packet[2:32])
    assert fixture.get8(RFSTATE) == 9 and fixture.get8(REMAIN) == 24
    assert fixture.get8(SEQ) == 1
    assert not fixture.radio_input_poll_scheduled()

    # Lose body 2, then deliver the next prefix while the previous SET is open.
    ack_count = len(fixture.rf_sent)
    fixture.dispatch(0x40, PREFIX)
    assert len(fixture.rf_sent) == ack_count
    assert fixture.get8(RFSTATE) == 9 and fixture.get8(SEQ) == 1
    assert fixture.active_frame() == new_frame

    # Expiring the RF channel timer rotates channels without ending state 9.
    fixture.set8(RF_CHANNEL_TIMER, 0)
    fixture.dispatch()
    assert fixture.get8(RF_CHANNEL_TIMER) == 24 and fixture.get8(RFSTATE) == 9

    # Execute just the transaction countdown instructions, with tick cadence
    # supplied by the model. The dispatcher owns the state reset at expiry.
    ticks = fixture.get8(RF_TRANSACTION_TIMER)
    assert ticks > 0
    fixture.advance_radio_timeout(ticks)
    assert fixture.get8(RF_TRANSACTION_TIMER) == 0
    fixture.dispatch()
    assert fixture.get8(RFSTATE) == 0
    assert fixture.radio_input_poll_scheduled()
    for index in range(CHUNKS):
        fixture.send_chunk(index, old_frame)
    assert fixture.active_frame() == old_frame
    assert not fixture.legacy_dispatches and not fixture.flash_writes

    return {
        "missing_chunk_positions_preserve_previous_frame": CHUNKS,
        "changed_chunks_zero_based": changed,
        "changed_bytes": len(positions),
        "changed_contiguous_runs": runs,
        "illustrative_delta_bytes": delta_size,
        "changed_chunks_only_preserve_previous_frame": True,
        "full_ordered_frame_commits": True,
        "missing_second_rf_body_blocks_next_prefix": True,
        "pending_radio_set_skips_stock_input_poll_until_reset": True,
        "channel_rotation_keeps_pending_set": True,
        "transaction_timer_services_to_reset": ticks,
        "full_frame_commits_after_timeout": True,
    }


def audit_delta_cost(make_fixture, old_frame, new_frame, packet):
    """Count executed instructions at protocol boundaries, excluding RF wait time."""
    assert len(packet) == 56 and packet[:2] == bytes([4, 56]) and packet[6] == 0xFE
    fixture = make_fixture()
    for index in range(CHUNKS):
        fixture.send_chunk(index, old_frame)
    expect_render(fixture, old_frame, range(CHUNKS))
    assert fixture.radio_input_poll_scheduled()

    start = fixture.total_instructions
    fixture.receive_prefix()
    after_prefix = fixture.total_instructions
    fixture.receive_body(1, packet[2:32])
    after_body1 = fixture.total_instructions
    wait_after_body1 = fixture.get8(RF_TRANSACTION_TIMER)
    fixture.receive_body(2, packet[32:])
    after_body2 = fixture.total_instructions
    fixture.dispatch()
    after_finish = fixture.total_instructions
    assert fixture.get8(RFSTATE) == 0 and fixture.radio_input_poll_scheduled()
    assert fixture.active_frame() == new_frame
    assert not fixture.transactions, "warm commit should leave drawing to the loop"

    drawing = expect_render(fixture, new_frame, range(CHUNKS))
    changed_chunks = sum(
        old_frame[start:start + CHUNK_BYTES] != new_frame[start:start + CHUNK_BYTES]
        for start in range(0, CHUNKS * CHUNK_BYTES, CHUNK_BYTES)
    )
    return {
        "modeled_instructions": {
            "prefix_and_ack": after_prefix - start,
            "first_body": after_body1 - after_prefix,
            "second_body_and_commit": after_body2 - after_body1,
            "finish": after_finish - after_body2,
            "all_draw_calls": sum(drawing),
            "longest_draw_call": max(drawing),
        },
        "remaining_wait_ticks_after_first_body": wait_after_body1,
        "draw_chunks": len(drawing),
        "changed_chunks": changed_chunks,
        "input_poll_eligible_before_drawing": True,
    }


def audit_vendor_envelope(make_fixture):
    """Run the stock notification branch up to its RF send-queue boundary."""
    device = make_fixture()
    setting_pending, battery_pending = 0x400AFA, 0x400AFB
    setting_payload, battery_payload = 0x400B4F, 0x400B53
    battery = bytes.fromhex("c9400156")
    candidate = bytes.fromhex("c9d13412")
    queued, ordinary_input = 0x35DFE, 0x35E98

    def stop_at_boundary(uc, address, size, context):
        if address in (queued, ordinary_input):
            uc.emu_stop()

    def compose():
        device.uc.reg_write(UC_ARM_REG_R4, RFBUF)
        device.uc.reg_write(UC_ARM_REG_R5, 0)
        device.uc.emu_start(0x35E49, 0x35F84, count=150)
        address = device.uc.reg_read(UC_ARM_REG_PC)
        assert address in (queued, ordinary_input), hex(address)
        return bytes(device.uc.mem_read(RFBUF, 32)) if address == queued else None

    hook = device.uc.hook_add(UC_HOOK_CODE, stop_at_boundary)
    try:
        device.set8(setting_pending, 1)
        device.uc.mem_write(setting_payload, candidate)
        device.set8(battery_pending, 1)
        device.uc.mem_write(battery_payload, battery)

        first = compose()
        assert first == (bytes([0x35]) + candidate).ljust(32, b"\0")
        assert device.get8(setting_pending) == 0 and device.get8(battery_pending) == 1
        assert bytes(device.uc.mem_read(battery_payload, 4)) == battery

        second = compose()
        assert second == (bytes([0x35]) + battery).ljust(32, b"\0")
        assert device.get8(battery_pending) == 0
        assert compose() is None
    finally:
        device.uc.hook_del(hook)
    return {
        "experimental_payload_copied_to_mouse_queue": first[:5].hex(),
        "normal_battery_copied_after_it": second[:5].hex(),
        "no_pending_vendor_report_falls_through_to_ordinary_input": True,
        "receiver_forwarding_validated": False,
    }


def main():
    if not __debug__:
        raise RuntimeError("Assertions are required")
    parser = ArgumentParser(description=__doc__)
    parser.add_argument("resource", type=Path)
    parser.add_argument("previous_frame", type=Path)
    parser.add_argument("replacement_frame", type=Path)
    parser.add_argument("--delta-wire", type=Path)
    parser.add_argument("--vendor-envelope", action="store_true")
    args = parser.parse_args()
    resource = args.resource.read_bytes()
    manifest = json.loads(args.resource.with_name("manifest.json").read_text())
    assert manifest["resource_sha256"] == hashlib.sha256(resource).hexdigest()
    layout = manifest["layout"]
    assert layout["version_report_marker"] in ("OL9", "OLA", "OLB")
    image, _, _ = decode_resource(resource)
    start = layout["append_address"] - BASE
    code = image[start:start + layout["text_bytes"]]
    assert layout["text_sha256"] == hashlib.sha256(code).hexdigest()
    old_frame = args.previous_frame.read_bytes()
    new_frame = args.replacement_frame.read_bytes()
    assert len(old_frame) == len(new_frame) == CHUNKS * CHUNK_BYTES
    def make_fixture():
        return DispatchFixture(code, layout, image)
    result = audit(make_fixture, old_frame, new_frame)
    if args.delta_wire:
        assert layout["version_report_marker"] in ("OLA", "OLB")
        result["delta_cost"] = audit_delta_cost(
            make_fixture, old_frame, new_frame, args.delta_wire.read_bytes()
        )
    if args.vendor_envelope:
        result["vendor_envelope"] = audit_vendor_envelope(make_fixture)
    result["resource_sha256"] = manifest["resource_sha256"]
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
