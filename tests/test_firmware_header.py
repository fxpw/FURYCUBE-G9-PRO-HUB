"""Pure synthetic image-format regressions; no proprietary input is required."""
from pathlib import Path
import importlib.util
import struct
import unittest


spec = importlib.util.spec_from_file_location('check_firmware_header', Path(__file__).resolve().parents[1]/'tools/check_firmware_header.py')
codec = importlib.util.module_from_spec(spec)
spec.loader.exec_module(codec)


def fixture(body=b'own synthetic firmware body'):
    return struct.pack('<IHHIBBH', 0, 0x23, 0, 0x42424242, 0xFF, 0x55, 0x6ECC)+body


class FirmwareHeaderTests(unittest.TestCase):
    def test_crc_known_vector_and_independent_bitwise_algorithm(self):
        self.assertEqual(codec.body_crc32(bytes(16)+b'123456789'), 0x340BC6D9)
        for count in (0, 1, 12, 16, 255, 134124):
            body = bytes((i*7+13)&255 for i in range(count))
            value = 0xFFFFFFFF
            for byte in body:
                value ^= byte
                for _ in range(8):
                    value = (value >> 1) ^ (0xEDB88320 if value & 1 else 0)
            self.assertEqual(codec.body_crc32(bytes(16)+body), value)

    def test_seal_padding_lengths_and_exact_ten_preserved_bytes(self):
        for count in range(1, 65):
            before = bytearray(fixture(bytes(range(count))))
            snapshot = bytes(before)
            after = codec.seal_header(before)
            result = codec.validate_header(after)
            self.assertEqual(bytes(before), snapshot, 'Caller buffer was mutated')
            self.assertEqual(len(after) % 4, 0)
            self.assertEqual(result['declared_container_bytes'], len(after))
            self.assertEqual(after[16:16+count], snapshot[16:])
            self.assertEqual(after[len(snapshot):], bytes([255])*((-len(snapshot)) % 4))
            self.assertEqual([after[i] for i in range(16) if i not in codec.EDITABLE_HEADER_OFFSETS],
                             [snapshot[i] for i in range(16) if i not in codec.EDITABLE_HEADER_OFFSETS])
            self.assertEqual(codec.seal_header(after), after)

    def test_changed_code_even_with_repaired_block_crc_rejects_stale_header(self):
        before = codec.seal_header(fixture(bytes(range(32))+b'\x12\x34'))
        bad = bytearray(before)
        bad[16] ^= 1
        bad[48:50] = b'\xAB\xCD'  # A block CRC edit does not repair the outer CRC.
        with self.assertRaisesRegex(codec.FirmwareHeaderError, 'CRC32'):
            codec.validate_header(bad)
        self.assertEqual(codec.validate_header(codec.seal_header(bad))['container_bytes'], len(bad))

    def test_append_and_unaligned_ol3_shape_reject_stale_length(self):
        original = codec.seal_header(fixture(bytes(134124)))
        self.assertEqual(len(original), 134140)
        for size in (134248, 134622):
            stale = original+bytes([0xA7])*(size-len(original))
            with self.assertRaisesRegex(codec.FirmwareHeaderError, 'length'):
                codec.validate_header(stale)
            sealed = codec.seal_header(stale)
            self.assertEqual(len(sealed), size+((-size) % 4))
            self.assertEqual(struct.unpack_from('<H', sealed, 6)[0]*4, len(sealed))
            codec.validate_header(sealed)

    def test_tail_is_part_of_crc_and_non_crc_header_bytes_are_not_authenticated(self):
        valid = codec.seal_header(fixture(b'own'))
        bad = valid[:-1]+bytes([valid[-1]^1])
        with self.assertRaises(codec.FirmwareHeaderError):
            codec.validate_header(bad)
        changed_identity = bytearray(valid)
        changed_identity[8] ^= 1
        # CRC covers the body, so header consistency alone is not identity/authentication.
        self.assertTrue(codec.validate_header(changed_identity)['package_header_consistent'])
        self.assertEqual(codec.validate_header(valid)['installed_loader_enforces_header'], 'unknown')

    def test_truncation_and_word_length_sentinel(self):
        for size in (0, 1, 15):
            with self.assertRaises(codec.FirmwareHeaderError):
                codec.inspect_header(bytes(size))
            with self.assertRaises(codec.FirmwareHeaderError):
                codec.seal_header(bytes(size))
        with self.assertRaises(codec.FirmwareHeaderError):
            codec.seal_header(fixture(bytes(0xFFFF*4-16)))
        maximum = codec.seal_header(fixture(bytes(codec.MAX_WORDS*4-16)))
        self.assertEqual(codec.validate_header(maximum)['length_words'], codec.MAX_WORDS)
        with self.assertRaises(TypeError):
            codec.seal_header('not a binary image')


if __name__ == '__main__':
    unittest.main()
