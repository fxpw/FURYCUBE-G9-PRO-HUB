"""Offline boundary checks for an unqualified proposed sector scheme."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from plan_firmware_sectors import FLASH_BASE, _sector_changes, plan_resources


class FirmwareSectorTests(unittest.TestCase):
    def test_header_and_code_offsets_use_resource_destination(self):
        before = b'\xFF'*134140
        after = bytearray(before)
        for offset in (0, 1, 2, 3, 0xA212, 0xA213, 0xA216, 0xA217):
            after[offset] = 0
        changed, sectors = _sector_changes(before, bytes(after))
        self.assertEqual(len(changed), 8)
        self.assertEqual([row['start'] for row in sectors], [0x2B000, 0x35000])
        self.assertEqual(sectors[0]['absent_from_official_reference'], [[0x2B000, 0x2B00A]])
        self.assertEqual(sectors[0]['official_reference_bytes'], 4086)
        self.assertEqual(sectors[1]['official_reference_bytes'], 4096)
        self.assertFalse(sectors[1]['installed_preimage_acquired'])

    def test_adjacent_bytes_across_sector_boundary_need_two_complete_preimages(self):
        before = b'\xFF'*0x2000
        after = bytearray(before)
        for offset in (0x2BFFF-FLASH_BASE, 0x2C000-FLASH_BASE):
            after[offset] = 0
        _, sectors = _sector_changes(before, bytes(after))
        self.assertEqual([row['start'] for row in sectors], [0x2B000, 0x2C000])
        self.assertTrue(all(row['must_capture_complete_sector_before_erase'] for row in sectors))

    def test_appended_ff_is_unknown_existing_data(self):
        before = b'\xFF'*134140
        changed, sectors = _sector_changes(before, before+b'\xFF'*484)
        self.assertEqual(len(changed), 484)
        self.assertEqual([row['start'] for row in sectors], [0x4B000])
        self.assertEqual(sectors[0]['absent_from_official_reference'], [[0x4BC06, 0x4C000]])
        self.assertEqual(sectors[0]['official_reference_bytes'], 3078)

    def test_extension_wholly_outside_reference_has_no_invented_baseline(self):
        _, sectors = _sector_changes(b'\xFF'*16, b'\xFF'*0x2000)
        self.assertEqual(sectors[-1]['official_reference_bytes'], 0)
        self.assertEqual(sectors[-1]['absent_from_official_reference'], [[0x2D000, 0x2E000]])

    def test_empty_shrinking_and_outside_scope_are_rejected(self):
        for before, after in ((b'', b''), (b'ab', b'a'), (b'ab', b'ab'),
                              (b'a', b'a'*0x27000)):
            with self.assertRaises(ValueError):
                _sector_changes(before, after)

    def test_unknown_images_cannot_become_a_qualified_plan(self):
        with self.assertRaisesRegex(ValueError, 'exact official'):
            plan_resources(b'\0'*24, b'\0'*24)


if __name__ == '__main__':
    unittest.main()
