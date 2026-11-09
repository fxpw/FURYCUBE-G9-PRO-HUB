"""Independent synthetic packet checks; no device, file or vendor input."""
import hashlib
from pathlib import Path
import struct
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from usb_boot_read16 import (Read16Error, decode_logical_response,
                             encode_logical_request)

CAPACITY = 0x80000  # Explicit fixture bound; not a measured G9 capacity.
DATA = bytes(range(0x90, 0xA0))


def response(address=0x20000, tail=b"\xCC" * 33):
    return bytes.fromhex("040eff01e0fcf406100900") + struct.pack("<I", address) + DATA + tail


class UsbBootRead16Tests(unittest.TestCase):
    def test_literal_request_and_unaligned_source_addresses(self):
        self.assertEqual(encode_logical_request(0x20000, declared_capacity=CAPACITY),
                         bytes.fromhex("01e0fcfff405000900000200"))
        for address in (0x20001, 0x2001F, 0x200F8, 0x7FFF0):
            frame = encode_logical_request(address, declared_capacity=CAPACITY)
            self.assertEqual(struct.unpack_from("<I", frame, 8)[0], address)
            self.assertEqual(decode_logical_response(response(address), address,
                             declared_capacity=CAPACITY).data, DATA)

    def test_capacity_and_source_guard_are_not_guessed(self):
        for address in (0, 0x1FFFF, 0x7FFF1, 0x80000, -1, True, "0x20000"):
            with self.assertRaises(Read16Error):
                encode_logical_request(address, declared_capacity=CAPACITY)
        for bound in (0, 0x20000, 0x2000F, 0x1000001, 0x100000000, True, "512KB"):
            with self.assertRaises(Read16Error):
                encode_logical_request(0x20000, declared_capacity=bound)
        # Minimal declared region and final complete span of the 24-bit bus.
        # These are codec boundaries, not assertions about installed flash.
        self.assertEqual(encode_logical_request(0x20000, declared_capacity=0x20010),
                         bytes.fromhex("01e0fcfff405000900000200"))
        self.assertEqual(encode_logical_request(0xFFFFF0, declared_capacity=0x1000000),
                         bytes.fromhex("01e0fcfff4050009f0ffff00"))
        self.assertEqual(decode_logical_response(response(0xFFFFF0), 0xFFFFF0,
                         declared_capacity=0x1000000).data, DATA)
        for address, bound in ((0x20001, 0x20010), (0xFFFFF1, 0x1000000),
                               (0x1000000, 0x1000000)):
            with self.assertRaises(Read16Error):
                encode_logical_request(address, declared_capacity=bound)
            with self.assertRaises(Read16Error):
                decode_logical_response(response(address), address, declared_capacity=bound)

    def test_first16_only_and_provenance(self):
        observation = decode_logical_response(response(), 0x20000, declared_capacity=CAPACITY)
        self.assertEqual(observation.data, DATA)
        self.assertEqual(len(observation.data), 16)
        self.assertEqual(observation.data_sha256, hashlib.sha256(DATA).hexdigest())
        self.assertFalse(observation.installed_g9_compatibility_verified)
        self.assertFalse(observation.transport_implemented)
        self.assertFalse(observation.is_full_page)
        self.assertFalse(observation.is_firmware_backup)
        self.assertFalse(observation.restore_proven)

    def test_stale_tail_never_becomes_flash_data(self):
        first = decode_logical_response(response(), 0x20000, declared_capacity=CAPACITY)
        for tail in (b"\0" * 33, b"\xFF" * 33, bytes(range(33))):
            changed = decode_logical_response(response(tail=tail), 0x20000, declared_capacity=CAPACITY)
            self.assertEqual(changed.data, first.data)
            self.assertEqual(changed.data_sha256, first.data_sha256)
            self.assertNotEqual(changed.response_sha256, first.response_sha256)

    def test_failure_status_and_wrong_address_reject_all_data(self):
        failure = bytes.fromhex("040eff01e0fcf407000906f0ff010000") + b"\xDD" * 48
        with self.assertRaisesRegex(Read16Error, "failure response"):
            decode_logical_response(failure, 0x20000, declared_capacity=CAPACITY)
        for status in (1, 6, 255):
            packet = bytearray(response()); packet[10] = status
            with self.assertRaises(Read16Error):
                decode_logical_response(bytes(packet), 0x20000, declared_capacity=CAPACITY)
        with self.assertRaises(Read16Error):
            decode_logical_response(response(0x20010), 0x20000, declared_capacity=CAPACITY)

    def test_no_hid_or_uart_envelope_guessing(self):
        for packet in (response()[:63], b"\0" + response(), response() + b"\0",
                       response() + b"\0" * (4102 - 64), b"\0" * 4111,
                       bytearray(response()), None):
            with self.assertRaises(Read16Error):
                decode_logical_response(packet, 0x20000, declared_capacity=CAPACITY)

    def test_header_mutation_and_missing_fields(self):
        for index in range(10):
            packet = bytearray(response()); packet[index] ^= 1
            with self.assertRaises(Read16Error):
                decode_logical_response(bytes(packet), 0x20000, declared_capacity=CAPACITY)
        with self.assertRaises(TypeError):
            encode_logical_request(0x20000)


if __name__ == "__main__":
    unittest.main()
