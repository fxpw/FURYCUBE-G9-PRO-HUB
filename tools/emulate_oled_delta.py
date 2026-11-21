"""Build and exercise the experimental radio OLED delta firmware offline."""
from argparse import ArgumentParser
from binascii import crc_hqx
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct
from uuid import uuid4
from zlib import crc32

from audit_radio_oled_delivery import DispatchFixture, RF_TRANSACTION_TIMER
from emulate_embedded_oled import (
    ROOT, build, encode_snapshot, expect_render, pack, reset,
    run_snapshot_checks, verify_startup_transition,
)
from emulate_ol9_carrier import carrier, run_checks, seal_carrier
from emulate_ol5_radio import RFBUF, RFSTATE
from unicorn import UC_HOOK_CODE
from unicorn.arm_const import UC_ARM_REG_PC, UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7

SHORT_WAIT_TICKS = 64
STOCK_WAIT_TICKS = 250


def delta_payload(base, target):
    runs = []
    cursor = 0
    while cursor < len(target):
        if base[cursor] == target[cursor]:
            cursor += 1
            continue
        start = cursor
        while cursor < len(target) and cursor - start < 128 and base[cursor] != target[cursor]:
            cursor += 1
        pixels = target[start:cursor]
        runs.append(struct.pack("<H", start | ((len(pixels) - 1) << 9)) + pixels)
    data = struct.pack("<IIB", crc32(base), crc32(target), len(runs)) + b"".join(runs)
    assert runs and len(data) <= 40
    return data.ljust(40, b"\0"), len(data)


def delta_packet(payload):
    packet = bytearray(carrier(0, 0, payload))
    packet[6] = 0xFE
    return seal_carrier(packet)


def audit_compact_run(make_fixture, dirty_draw):
    base = bytes(480)
    target = bytearray(base)
    start, length = 360, 29
    target[start:start + length:2] = bytes([1]) * 15
    descriptor = start | ((length - 1) << 9)
    payload = struct.pack("<IIBH", crc32(base), crc32(target), 1, descriptor)
    payload += target[start:start + length]
    assert len(payload) == 40
    device = make_fixture()
    device.run(0x3CDDE)
    upload(device, base)
    expect_render(device, base, range(12))
    send_packet(device, delta_packet(payload))
    assert device.active_frame() == target
    expect_render(device, target, [9] if dirty_draw else range(12))
    return len(payload)


def send_packet(fixture, packet, drop_second_body=False):
    fixture.receive_prefix()
    fixture.receive_body(1, packet[2:32])
    if drop_second_body:
        assert fixture.get8(RFSTATE) == 9
        return
    fixture.receive_body(2, packet[32:])
    assert fixture.get8(RFSTATE) == 10
    fixture.dispatch()
    assert fixture.get8(RFSTATE) == 0


def upload(fixture, frame):
    for index in range(12):
        fixture.send_chunk(index, frame)


def changed_draw_chunks(base, target):
    return [
        index for index in range(12)
        if base[index * 40:(index + 1) * 40] != target[index * 40:(index + 1) * 40]
    ]


def audit_short_wait(make_fixture, make_reference, base, target, packet, dirty_draw=False):
    fixture, reference = make_fixture(), make_reference()
    for device in (fixture, reference):
        device.run(0x3CDDE)
        upload(device, base)
        expect_render(device, base, range(12))
        assert device.radio_input_poll_scheduled()
        send_packet(device, packet, drop_second_body=True)
        assert not device.radio_input_poll_scheduled()
    assert fixture.get8(RF_TRANSACTION_TIMER) == SHORT_WAIT_TICKS
    assert reference.get8(RF_TRANSACTION_TIMER) == STOCK_WAIT_TICKS

    for device in (fixture, reference):
        device.advance_radio_timeout(SHORT_WAIT_TICKS - 1)
        device.dispatch()
        assert device.get8(RFSTATE) == 9
        device.advance_radio_timeout(1)
        device.dispatch()
    assert fixture.get8(RFSTATE) == 0 and fixture.radio_input_poll_scheduled()
    assert reference.get8(RFSTATE) == 9 and not reference.radio_input_poll_scheduled()
    fixture.set8(RFSTATE, 1)  # Radio listening resumes after native reset.
    fixture.receive_body(2, packet[32:])
    assert fixture.get8(RFSTATE) in (0, 1) and fixture.active_frame() == base
    fixture.set8(RFSTATE, 0)

    send_packet(fixture, packet, drop_second_body=True)
    fixture.advance_radio_timeout(SHORT_WAIT_TICKS - 1)
    fixture.receive_body(2, packet[32:])
    assert fixture.get8(RFSTATE) == 10
    fixture.dispatch()
    assert fixture.active_frame() == target and fixture.radio_input_poll_scheduled()
    expect_render(fixture, target, changed_draw_chunks(base, target) if dirty_draw else range(12))
    assert not fixture.flash_writes and not fixture.legacy_dispatches

    # The same stock transport and report length also carry full frames and
    # sensor settings. No incomplete packet is passed to the setter here.
    ordinary_sensor = bytearray(carrier(0, 0, base[:40]))
    ordinary_sensor[24], ordinary_sensor[55] = 1, 0
    ordinary_sensor = seal_carrier(ordinary_sensor)
    full_frame = carrier(0, 0, base[:40])
    near_match = bytearray(packet)
    near_match[24] = 1
    for body in (ordinary_sensor, full_frame, near_match):
        device = make_fixture()
        send_packet(device, body, drop_second_body=True)
        assert device.get8(RF_TRANSACTION_TIMER) == STOCK_WAIT_TICKS

    device = make_fixture()
    device.receive_prefix()
    device.set8(RF_TRANSACTION_TIMER, SHORT_WAIT_TICKS - 1)
    device.receive_body(1, packet[2:32])
    assert device.get8(RF_TRANSACTION_TIMER) == SHORT_WAIT_TICKS - 1
    return dict(stock_wait_ticks=STOCK_WAIT_TICKS, delta_wait_ticks=SHORT_WAIT_TICKS,
                input_eligible_after_timeout=True, late_body_ignored=True,
                accepts_body_before_expiry=True, legacy_wait_preserved=True,
                does_not_extend_remaining_wait=True)


def audit_dirty_draw(make_fixture, base, target, packet):
    changed = changed_draw_chunks(base, target)
    assert 0 < len(changed) < 12
    reverse = delta_packet(delta_payload(target, base)[0])

    def start_redraw():
        device = make_fixture()
        device.run(0x3CDDE)
        upload(device, base)
        expect_render(device, base, range(12))
        send_packet(device, packet)
        assert device.active_frame() == target
        assert device.radio_input_poll_scheduled()
        return device

    fixture = start_redraw()
    work = expect_render(fixture, target, changed)
    send_packet(fixture, reverse)
    expect_render(fixture, base, changed)

    fixture = start_redraw()
    fixture.tick()
    send_packet(fixture, reverse)
    expect_render(fixture, base, range(12))

    fixture = start_redraw()
    fixture.tick()
    upload(fixture, base)
    expect_render(fixture, base, range(12))

    fixture = start_redraw()
    fixture.tick()
    reset(fixture, clear_ram=False)
    fixture.run(0x3CDDE)
    assert fixture.active_frame() == target
    expect_render(fixture, target, range(12))

    fixture = start_redraw()
    fixture.tick()
    fixture.run(0x3CDDE)
    expect_render(fixture, target, range(12))

    for flag in (0x400A7A, 0x400AF2):
        fixture = start_redraw()
        fixture.tick()
        fixture.set8(flag, 1)
        fixture.clear_trace()
        fixture.tick()
        assert not fixture.transactions
        fixture.set8(flag, 0)
        expect_render(fixture, target, range(12))

    return dict(changed_chunks=changed, draw_calls=len(work),
                modeled_draw_instructions=sum(work),
                overlapping_delta_and_full_restore=True,
                warm_reset_and_display_reinit_restore=True,
                sleep_and_disabled_screen_restore=True)


def audit(make_fixture, make_ol9_fixture, base, target, dirty_draw=False):
    payload, size = delta_payload(base, target)
    packet = delta_packet(payload)
    f = make_fixture()
    send_packet(f, packet)
    assert f.active_frame() is None, "delta on empty bank"
    f.run(0x3CDDE)
    f.tick()  # Seed the compiled, autonomously drawn battery layout.
    local = f.active_frame()
    assert local is not None
    local_payload, _ = delta_payload(local, bytes([local[0] ^ 1]) + local[1:])
    send_packet(f, delta_packet(local_payload))
    assert f.active_frame() == local, "delta must not claim an embedded layout"

    upload(f, base)
    expect_render(f, base, range(12))
    config = bytes(f.uc.mem_read(0x401020, 0x8E))
    meta = bytes(f.uc.mem_read(f.meta(), 24))

    corruptions = []
    for offset, value in ((0, payload[0] ^ 1), (4, payload[4] ^ 1),
                          (8, 0), (8, 11), (9, 255), (10, 255), (39, 1)):
        bad = bytearray(payload)
        bad[offset] = value
        corruptions.append(delta_packet(bad))
    wrong_header = bytearray(packet)
    wrong_header[7] = 1
    corruptions.append(seal_carrier(wrong_header))
    bad_carrier_crc = bytearray(packet)
    bad_carrier_crc[52] ^= 1
    corruptions.append(bad_carrier_crc)
    # Overlapping runs, out-of-frame offset and invalid hidden pixel, all
    # carried with valid outer checksums.
    header = payload[:8]
    corruptions.append(delta_packet((header + b"\2\0\0\x11\0\0\x22").ljust(40, b"\0")))
    corruptions.append(delta_packet((header + b"\1\xe0\1\x22").ljust(40, b"\0")))
    hidden_pixel = base[:360] + bytes([base[360] | 0x80]) + base[361:]
    hidden_payload, _ = delta_payload(base, hidden_pixel)
    corruptions.append(delta_packet(hidden_payload))
    for index, bad in enumerate(corruptions):
        send_packet(f, bad)
        assert f.active_frame() == base, index
        assert bytes(f.uc.mem_read(f.meta(), 24)) == meta, index
        f.clear_trace()
        f.tick()
        assert not f.transactions, index

    # The inactive bank is owned by the full transfer until it completes.
    f.send_chunk(0, base)
    send_packet(f, packet)
    assert f.store_next() == 1
    for index in range(1, 12):
        f.send_chunk(index, base)
    expect_render(f, base, range(12))
    send_packet(f, packet)
    assert f.active_frame() == target
    assert f.get8(0x400B9F) == 0, "accepted delta left the SET04 profile selector active"
    expect_render(f, target, changed_draw_chunks(base, target) if dirty_draw else range(12))
    send_packet(f, packet)
    assert f.active_frame() == target  # Same packet compares against old base.
    assert f.get8(0x400B9F) == 0, "rejected delta left the SET04 profile selector active"
    f.clear_trace()
    f.tick()
    assert not f.transactions
    assert bytes(f.uc.mem_read(0x401020, 0x8E)) == config
    assert not f.flash_writes and not f.legacy_dispatches

    reset(f, clear_ram=False)
    assert f.active_frame() == target
    reset(f, clear_ram=True)
    assert f.active_frame() is None
    send_packet(f, packet)
    assert f.active_frame() is None
    upload(f, base)
    send_packet(f, packet, drop_second_body=True)
    assert f.active_frame() == base

    old = make_ol9_fixture()
    upload(old, base)
    send_packet(old, packet)
    assert old.active_frame() == base
    assert not old.flash_writes and not old.legacy_dispatches
    return dict(
        delta_size=size, sent_reports=1, committed_and_drew=True,
        malformed_cases_rejected=len(corruptions), duplicate_harmless=True,
        staging_unchanged=True, old_firmware_preserves_frame=True,
        cold_boot_requires_new_base=True, warm_boot_retention=True,
        lost_second_body_preserves_frame=True, settings_unchanged=True,
        radio_selector_restored=True,
    ), packet


def audit_radio_ack(make_fixture, base, target, radio_retry=False):
    device = make_fixture()
    queued, stock_input, stock_idle = 0x35E00, 0x35EC0, 0x35F64
    queue_ready = 0x400A76
    input_flags = (0x400AF3, 0x400A77, 0x400A78, 0x400F67, 0x400AF5)

    def stop_at_branch(uc, address, size, context):
        if address in (queued, stock_input, stock_idle):
            uc.emu_stop()

    def service_notifications():
        device.uc.reg_write(UC_ARM_REG_R4, RFBUF)
        device.uc.reg_write(UC_ARM_REG_R5, 0)
        device.uc.reg_write(UC_ARM_REG_R6, queue_ready)
        device.uc.reg_write(UC_ARM_REG_R7, 1)
        device.set8(queue_ready, 0)
        device.uc.mem_write(RFBUF, bytes([0xA5]) * 32)
        device.uc.emu_start(0x35E49, 0x35F84, count=320)
        branch = device.uc.reg_read(UC_ARM_REG_PC)
        assert branch in (queued, stock_input, stock_idle), hex(branch)
        assert device.get8(queue_ready) == (branch == queued)
        return branch, bytes(device.uc.mem_read(RFBUF, 32))

    def expected(frame):
        return (b"\x35\xc9\xd1" + crc_hqx(frame, 0xFFFF).to_bytes(2, "little")).ljust(32, b"\0")

    trace_hook = device.uc.hook_add(UC_HOOK_CODE, stop_at_branch)
    try:
        device.run(0x3CDDE)
        device.tick()
        assert service_notifications()[0] == stock_idle  # Embedded layout only.
        for index in range(11):
            device.send_chunk(index, base)
        assert service_notifications()[0] == stock_idle  # Incomplete transfer.
        device.send_chunk(11, base)
        for index in range(11):
            device.clear_trace()
            device.tick()
            assert device.transactions[-1] == b"\x78\x40" + base[index * 40:(index + 1) * 40]
            assert service_notifications()[0] == stock_idle  # Partial OLED draw.
        device.clear_trace()
        device.tick()
        assert device.transactions[-1] == b"\x78\x40" + base[440:]

        for flag in input_flags:
            device.set8(flag, 1)
            assert service_notifications() == (stock_input, bytes([0xA5]) * 32)
            device.set8(flag, 0)
        battery = bytes.fromhex("c9400156")
        device.uc.mem_write(0x400B53, battery)
        device.set8(0x400AFB, 1)
        assert service_notifications() == (queued, (b"\x35" + battery).ljust(32, b"\0"))
        assert service_notifications() == (queued, expected(base))
        assert service_notifications()[0] == stock_idle  # One per commit.

        packet = delta_packet(delta_payload(base, target)[0])
        send_packet(device, packet, drop_second_body=True)
        assert device.active_frame() == base
        assert service_notifications()[0] == stock_idle
        device.advance_radio_timeout(SHORT_WAIT_TICKS)
        device.dispatch()
        send_packet(device, packet)
        assert service_notifications()[0] == stock_idle
        expect_render(device, target, range(12))
        for flag in (0x400A7A, 0x400AF2):
            device.set8(flag, 1)
            assert service_notifications()[0] == stock_idle
            device.set8(flag, 0)
        assert service_notifications() == (queued, expected(target))
        assert service_notifications()[0] == stock_idle
        if radio_retry:
            generation = bytes(device.uc.mem_read(device.meta() + 8, 4))
            altered = bytearray(delta_payload(base, target)[0])
            altered[-1] ^= 1
            send_packet(device, delta_packet(altered))
            assert service_notifications()[0] == stock_idle

            # The device rendered the update, but the host dropped its receipt.
            # The exact replay produces a new receipt and leaves the panel alone.
            device.clear_trace()
            send_packet(device, packet)
            device.tick()
            assert not device.transactions
            assert bytes(device.uc.mem_read(device.meta() + 8, 4)) == generation
            assert service_notifications() == (queued, expected(target))
            assert service_notifications()[0] == stock_idle

            # A later full frame with identical pixels is a separate commit.
            upload(device, target)
            expect_render(device, target, range(12))
            assert service_notifications() == (queued, expected(target))
            send_packet(device, packet)
            assert service_notifications()[0] == stock_idle
    finally:
        device.uc.hook_del(trace_hook)

    return dict(complete_frame=expected(base)[:5].hex(), delta=expected(target)[:5].hex(),
                input_priority_cases=len(input_flags), battery_priority=True,
                waits_for_draw=True, lost_body_no_ack=True,
                exact_replay_renews_ack_without_render=radio_retry, receiver_tested=False)


def main():
    if not __debug__:
        raise RuntimeError("Assertions are required.")
    parser = ArgumentParser(description=__doc__)
    parser.add_argument("snapshot", type=Path)
    parser.add_argument("previous_frame", type=Path)
    parser.add_argument("replacement_frame", type=Path)
    parser.add_argument("--host-packet", type=Path)
    parser.add_argument("--short-wait", action="store_true")
    parser.add_argument("--dirty-draw", action="store_true")
    parser.add_argument("--radio-ack", action="store_true", help="Build an offline passive acknowledgement probe.")
    parser.add_argument("--radio-retry", action="store_true", help="Build with repeated acknowledgements for exact delta replays.")
    args = parser.parse_args()
    base, target = args.previous_frame.read_bytes(), args.replacement_frame.read_bytes()
    assert len(base) == len(target) == 480

    snapshot = encode_snapshot(args.snapshot.read_bytes())
    old_code, old_layout = build(*snapshot)
    _, old_image = pack(old_code, old_layout)
    radio_ack = args.radio_ack or args.radio_retry
    fast_wait = args.short_wait or args.dirty_draw or radio_ack
    code, layout = build(*snapshot, delta=True, fast_wait=fast_wait, dirty_draw=args.dirty_draw,
                         radio_ack=radio_ack, radio_retry=args.radio_retry)
    resource, image = pack(code, layout)
    checks, packet = audit(
        lambda: DispatchFixture(code, layout, image),
        lambda: DispatchFixture(old_code, old_layout, old_image), base, target, args.dirty_draw,
    )
    checks["compact_run_bytes"] = audit_compact_run(
        lambda: DispatchFixture(code, layout, image), args.dirty_draw,
    )
    if args.host_packet:
        assert args.host_packet.read_bytes() == packet
        checks["cross_language_packet_matches"] = True
    if fast_wait:
        reference_code, reference_layout = build(*snapshot, delta=True)
        _, reference_image = pack(reference_code, reference_layout)
        checks["radio_wait"] = audit_short_wait(
            lambda: DispatchFixture(code, layout, image),
            lambda: DispatchFixture(reference_code, reference_layout, reference_image),
            base, target, packet, args.dirty_draw,
        )
    if args.dirty_draw:
        checks["dirty_draw"] = audit_dirty_draw(
            lambda: DispatchFixture(code, layout, image), base, target, packet
        )
    if radio_ack:
        checks["radio_ack"] = audit_radio_ack(lambda: DispatchFixture(code, layout, image), base, target, args.radio_retry)
    verify_startup_transition(code, layout, image, snapshot[0])
    snapshot_checks = run_snapshot_checks(code, layout, image, snapshot[0], snapshot[1])
    regressions = run_checks(code, layout, candidate_image=image)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    folder = ROOT / "patch-output" / (stamp + "-oled-delta-" + uuid4().hex[:8])
    folder.mkdir(parents=True)
    candidate = folder / "g9-oled-delta.OFFLINE.bin"
    candidate.write_bytes(resource)
    manifest = dict(
        status="OFFLINE_ONLY_NO_DEVICE_OR_UPDATER_ACCESS",
        resource_sha256=hashlib.sha256(resource).hexdigest(), resource_bytes=len(resource),
        layout=layout, delta_checks=checks, snapshot_checks=snapshot_checks,
        regressions=regressions,
    )
    (folder / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(json.dumps(dict(candidate=str(candidate), sha256=manifest["resource_sha256"],
                          bytes=len(resource), delta_checks=checks), indent=2))


if __name__ == "__main__":
    main()
