"""Pure offline fixtures. No serial/USB device or vendor executable is used."""
import base64
import ast
import copy
import importlib.util
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location("g9_backup_archive", Path(__file__).resolve().parents[1] / "tools/g9_backup_archive.py")
codec = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = codec
spec.loader.exec_module(codec)


def reply(address, fill):
    # Header, offsets and lengths are pinned against original BKFIL instruction
    # emulation. These packets are SYNTHETIC, not physical G9 responses.
    return bytes.fromhex("040eff01e0fcf406100900") + struct.pack("<I", address) + bytes([fill]) * 4096


def capture(number=1):
    return {"schema": codec.SCHEMA, "sourceKind": "offline-fixture", "transport": "UART-HCI09-unqualified",
            "captureId": "synthetic-pass-" + str(number), "capturedAtUtc": "2026-11-09T07:00:0" + str(number) + "Z",
            "declaredCapacityBytes": 8192, "deviceIdentityEvidence": "Synthetic test; not a real mouse",
            "capacityEvidence": "Two synthetic pages; not an asserted G9 capacity",
            "formatEvidence": "Only codec fixture byte layout is established",
            "pages": [{"address": 0, "requestHex": "01e0fcfff405000900000000", "replyBase64": base64.b64encode(reply(0, 0xA5)).decode()},
                      {"address": 4096, "requestHex": "01e0fcfff405000900100000", "replyBase64": base64.b64encode(reply(4096, 0x5A)).decode()}]}


class BackupArchiveTests(unittest.TestCase):
    def test_offline_only_and_device_paths_rejected(self):
        source = Path(codec.__file__).read_text()
        imports = {node.module.split(".")[0] for node in ast.walk(ast.parse(source)) if isinstance(node, ast.ImportFrom)}
        imports.update(alias.name.split(".")[0] for node in ast.walk(ast.parse(source)) if isinstance(node, ast.Import) for alias in node.names)
        self.assertFalse(imports & {"serial", "ctypes", "subprocess", "socket", "usb", "hid", "win32file", "requests"})
        for path in [r"\\.\COM1", r"\\?\GLOBALROOT\Device\Serial0", r"\\server\capture.json", "COM1", "NUL", "COM2.json", "CONOUT$"]:
            with self.subTest(path=path), self.assertRaises(codec.ArchiveError):
                codec.read_capture_file(Path(path))
        if Path("/dev/null").exists():
            with self.assertRaises(codec.ArchiveError):
                codec.read_capture_file(Path("/dev/null"))

    def test_native_encoder_fixture(self):
        self.assertEqual(codec.encode_page_request(0x12000).hex(), "01e0fcfff405000900200100")
        self.assertEqual(codec.encode_page_request("0x12000").hex(), "01e0fcfff405000900200100")
        for value in [-4096, 1, 0x12345, 0x100000000, True, 1.0, "12345", "0x100000000"]:
            with self.subTest(value=value), self.assertRaises(codec.ArchiveError):
                codec.encode_page_request(value)

    def test_strict_native_reply(self):
        frame = reply(0x12000, 0xA5)
        page = codec.decode_page_reply(frame, 0x12000)
        self.assertEqual(page.data, bytes([0xA5]) * 4096)
        self.assertEqual(len(page.data_sha256), 64)
        self.assertEqual(len(page.wire_sha256), 64)
        for index in list(range(10)) + [10, 11, 12, 13, 14]:
            bad = bytearray(frame); bad[index] ^= 1
            with self.subTest(index=index), self.assertRaises(codec.ArchiveError):
                codec.decode_page_reply(bytes(bad), 0x12000)
        for bad in [b"", frame[:-1], frame + b"\0"]:
            with self.assertRaises(codec.ArchiveError):
                codec.decode_page_reply(bad, 0x12000)
        with self.assertRaises(codec.ArchiveError):
            codec.decode_page_reply(frame, 0x13000)

    def test_fixture_opt_in_and_complete_coverage(self):
        with self.assertRaises(codec.ArchiveError):
            codec.verify_capture(capture())
        original = capture(); original["pages"].reverse()
        verified = codec.verify_capture(original, allow_fixture=True)
        self.assertEqual(verified.image, bytes([0xA5])*4096 + bytes([0x5A])*4096)
        self.assertEqual([page.address for page in verified.pages], [0, 4096])

    def test_no_holes_duplicates_partial_or_guessed_capacity(self):
        variants = []
        for capacity in [None, 0, -4096, 4097, True, 32*1024*1024]:
            bad = capture(); bad["declaredCapacityBytes"] = capacity; variants.append(bad)
        bad = capture(); bad["pages"].pop(); variants.append(bad)
        bad = capture(); bad["pages"].append(copy.deepcopy(bad["pages"][0])); variants.append(bad)
        bad = capture(); bad["pages"][1] = copy.deepcopy(bad["pages"][0]); variants.append(bad)
        bad = capture(); bad["pages"][1]["address"] = 8192; variants.append(bad)
        bad = capture(); bad["pages"][0]["address"] = 1; variants.append(bad)
        bad = capture(); bad["pages"][0]["replyBase64"] = ""; variants.append(bad)
        bad = capture(); bad["pages"][0]["requestHex"] = "01e0fcfff405000900100000"; variants.append(bad)
        bad = capture(); bad["pages"][0]["replyBase64"] = "!"*len(bad["pages"][0]["replyBase64"]); variants.append(bad)
        bad = capture(); bad["capacityEvidence"] = ""; variants.append(bad)
        bad = capture(); bad["deviceIdentityEvidence"] = ""; variants.append(bad)
        bad = capture(); bad["transport"] = "normal-USB-HID09-macro"; variants.append(bad)
        for i, bad in enumerate(variants):
            with self.subTest(variant=i), self.assertRaises(codec.ArchiveError):
                codec.verify_capture(bad, allow_fixture=True)

    def test_two_passes_must_match(self):
        first = codec.verify_capture(capture(1), allow_fixture=True)
        second = codec.verify_capture(capture(2), allow_fixture=True)
        codec.compare_captures(first, second)
        with self.assertRaises(codec.ArchiveError):
            codec.compare_captures(first, first)
        same_time = capture(2); same_time["capturedAtUtc"] = "2026-11-09T07:00:01+00:00"
        with self.assertRaises(codec.ArchiveError):
            codec.compare_captures(first, codec.verify_capture(same_time, allow_fixture=True))
        for field, value in [("deviceIdentityEvidence", "other"), ("capacityEvidence", "other"), ("formatEvidence", "other")]:
            bad = capture(2); bad[field] = value
            with self.subTest(field=field), self.assertRaises(codec.ArchiveError):
                codec.compare_captures(first, codec.verify_capture(bad, allow_fixture=True))
        bad = capture(2); bad["pages"][1]["replyBase64"] = base64.b64encode(reply(4096, 0xA4)).decode()
        with self.assertRaises(codec.ArchiveError):
            codec.compare_captures(first, codec.verify_capture(bad, allow_fixture=True))

    def test_archive_io_hashes_and_gate(self):
        with tempfile.TemporaryDirectory(prefix="g9-offline-archive-tests-") as directory:
            root = Path(directory); a = root/"a.json"; b = root/"b.json"
            a.write_text(json.dumps(capture(1))); b.write_text(json.dumps(capture(2)))
            output = codec.build_archive(a, b, root/"out", allow_fixture=True)
            manifest = json.loads((output/"manifest.json").read_text())
            self.assertEqual((output/"captured-flash.bin").stat().st_size, 8192)
            self.assertEqual(manifest["coverage"], "COMPLETE_FOR_DECLARED_CAPACITY")
            self.assertTrue(manifest["twoCapturedPassesEqual"])
            for name in ["physicalBackupVerified", "appIndependentEntryVerified", "restorationVerified", "independentAcquisitionVerified"]:
                self.assertFalse(manifest[name])
            self.assertEqual(manifest["recoveryGate"], "BLOCKED")
            for name, item in manifest["files"].items():
                self.assertEqual(codec.sha256((output/name).read_bytes()), item["sha256"])
            self.assertEqual((output/"capture-a.json").read_bytes(), a.read_bytes())
            output2 = codec.build_archive(a, b, root/"out", allow_fixture=True)
            self.assertNotEqual(output, output2)
            self.assertTrue((output/"manifest.json").exists())

    def test_empty_duplicate_and_malformed_files_never_succeed(self):
        with tempfile.TemporaryDirectory(prefix="g9-offline-archive-tests-") as directory:
            root=Path(directory); a=root/"a.json"; b=root/"b.json"
            b.write_text(json.dumps(capture(2)))
            for data in [b"", b"{}", b'{"schema":1,"schema":2}', b"{", b"[]", b"\xff"]:
                a.write_bytes(data)
                with self.subTest(data=data), self.assertRaises(codec.ArchiveError):
                    codec.build_archive(a,b,root/"out",allow_fixture=True)
                self.assertFalse((root/"out").exists())
            a.write_bytes(b.read_bytes())
            with self.assertRaises(codec.ArchiveError):
                codec.build_archive(a,b,root/"out",allow_fixture=True)
            self.assertFalse((root/"out").exists())

    def test_failed_file_save_has_no_success_manifest(self):
        with tempfile.TemporaryDirectory(prefix="g9-offline-archive-tests-") as directory:
            root=Path(directory); a=root/"a.json"; b=root/"b.json"
            a.write_text(json.dumps(capture(1))); b.write_text(json.dumps(capture(2)))
            with mock.patch.object(codec, "_write_checked", side_effect=OSError("synthetic disk failure")):
                with self.assertRaises(OSError):
                    codec.build_archive(a,b,root/"out",allow_fixture=True)
            self.assertFalse(list((root/"out").glob("*/manifest.json")))


if __name__ == "__main__":
    unittest.main()
