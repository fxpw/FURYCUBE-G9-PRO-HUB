"""Offline synthetic frames pinned to original-instruction encoder evidence."""
import ast
from pathlib import Path
import unittest

from tools import uart_read_session as session
from tools.g9_backup_archive import CapturedPage


# Hardcoded headers/addresses, not produced using the implementation's encoder.
# These are synthetic payloads, never captured physical G9 responses.
HANDSHAKE = bytes.fromhex("040e0501e0fcaa55")
PAGE_12000 = bytes.fromhex("040eff01e0fcf40610090000200100") + b"\xa5" * 4096
PAGE_13000 = bytes.fromhex("040eff01e0fcf40610090000300100") + b"\x5a" * 4096


def new_session(**changes):
    args = dict(start=0x12000, length=8192, declared_capacity=0x14000)
    args.update(changes)
    return session.UartReadSession(**args)


def ready_for_page():
    reader = new_session()
    reader.next_request()
    reader.consume_reply(HANDSHAKE)
    reader.next_request()
    return reader


class UartReadSessionTests(unittest.TestCase):
    def assert_terminal(self, reader, state=session.SessionState.FAILED):
        self.assertEqual(reader.state, state)
        for operation in (reader.next_request, lambda: reader.consume_reply(HANDSHAKE)):
            with self.assertRaises(session.SessionError):
                operation()
            self.assertEqual(reader.state, state)

    def test_exact_handshake_and_two_sequential_pages(self):
        reader = new_session()
        self.assertEqual((reader.start, reader.length, reader.declared_capacity),
                         (0x12000, 8192, 0x14000))
        self.assertEqual(reader.pages, ())
        self.assertEqual(reader.next_request().hex(), "01e0fc02aa55")
        self.assertIsNone(reader.consume_reply(HANDSHAKE))
        self.assertEqual(reader.next_request().hex(), "01e0fcfff405000900200100")
        first = reader.consume_reply(PAGE_12000)
        self.assertIsInstance(first, CapturedPage)
        self.assertEqual(first.address, 0x12000)
        self.assertEqual(first.data, b"\xa5" * 4096)
        self.assertEqual(reader.next_request().hex(), "01e0fcfff405000900300100")
        second = reader.consume_reply(PAGE_13000)
        self.assertEqual(second.data, b"\x5a" * 4096)
        self.assertEqual(reader.pages, (first, second))
        self.assertEqual(reader.state, session.SessionState.COMPLETE)
        self.assertIsNone(reader.failure_reason)
        self.assertIsNone(reader.next_request())
        self.assertIsNone(reader.next_request())

    def test_all_range_fields_mandatory_and_keyword_only(self):
        for missing in ("start", "length", "declared_capacity"):
            args = dict(start=0, length=4096, declared_capacity=4096)
            del args[missing]
            with self.subTest(missing=missing), self.assertRaises(TypeError):
                session.UartReadSession(**args)
        with self.assertRaises(TypeError):
            session.UartReadSession(0, 4096, 4096)

    def test_invalid_ranges_rejected_before_requests(self):
        variants = [dict(start=-4096), dict(start=1), dict(start=0x14000),
                    dict(length=0), dict(length=-4096), dict(length=4097),
                    dict(length=12288), dict(declared_capacity=0),
                    dict(declared_capacity=-4096), dict(declared_capacity=4097),
                    dict(declared_capacity=0x1001000),
                    dict(start=0x100000000), dict(length=0x100000000)]
        for field in ("start", "length", "declared_capacity"):
            for value in (None, True, False, 4096.0, "0x1000"):
                variants.append({field: value})
        for changes in variants:
            with self.subTest(changes=changes), self.assertRaises(session.SessionError):
                new_session(**changes)

    def test_codec_capacity_boundary_without_allocating_capture(self):
        reader = new_session(start=0xFFF000, length=4096, declared_capacity=0x1000000)
        reader.next_request()
        reader.consume_reply(HANDSHAKE)
        self.assertEqual(reader.next_request().hex(), "01e0fcfff405000900f0ff00")

    def test_single_page_range_at_zero(self):
        reader = new_session(start=0, length=4096, declared_capacity=4096)
        reader.next_request()
        reader.consume_reply(HANDSHAKE)
        self.assertEqual(reader.next_request().hex(), "01e0fcfff405000900000000")
        frame = bytes.fromhex("040eff01e0fcf40610090000000000") + bytes(4096)
        reader.consume_reply(frame)
        self.assertEqual(reader.state, session.SessionState.COMPLETE)
        self.assertIsNone(reader.next_request())

    def test_no_second_request_while_one_is_pending(self):
        for awaiting_page in (False, True):
            reader = ready_for_page() if awaiting_page else new_session()
            if not awaiting_page:
                reader.next_request()
            with self.subTest(awaiting_page=awaiting_page), self.assertRaises(session.SessionError):
                reader.next_request()
            self.assert_terminal(reader)
            self.assertEqual(reader.pages, ())

    def test_handshake_wrong_truncated_extra_unknown_and_nonbytes_fail(self):
        variants = [b"", HANDSHAKE[:-1], HANDSHAKE + b"\0", PAGE_12000,
                    bytes.fromhex("040e0501e0fcaa54"), bytearray(HANDSHAKE), None]
        for bad in variants:
            reader = new_session()
            reader.next_request()
            with self.subTest(bad_type=type(bad).__name__), self.assertRaises(session.SessionError):
                reader.consume_reply(bad)
            self.assert_terminal(reader)
            self.assertEqual(reader.pages, ())

    def test_unsolicited_replies_fail_before_and_between_requests(self):
        for stage in ("initial", "after-handshake", "between-pages", "complete"):
            reader = new_session()
            if stage != "initial":
                reader.next_request()
                reader.consume_reply(HANDSHAKE)
            if stage in ("between-pages", "complete"):
                reader.next_request()
                reader.consume_reply(PAGE_12000)
            if stage == "complete":
                reader.next_request()
                reader.consume_reply(PAGE_13000)
            with self.subTest(stage=stage), self.assertRaises(session.SessionError):
                reader.consume_reply(HANDSHAKE)
            self.assert_terminal(reader)

    def test_invalid_page_frames_and_status_fail_without_accepting_data(self):
        variants = [b"", PAGE_12000[:-1], PAGE_12000 + b"\0", HANDSHAKE,
                    PAGE_13000, bytearray(PAGE_12000), None]
        for index in range(15):
            bad = bytearray(PAGE_12000)
            bad[index] ^= 1
            variants.append(bytes(bad))
        for number, bad in enumerate(variants):
            reader = ready_for_page()
            with self.subTest(variant=number), self.assertRaises(session.SessionError):
                reader.consume_reply(bad)
            self.assert_terminal(reader)
            self.assertEqual(reader.pages, ())
            self.assertTrue(reader.failure_reason)

    def test_duplicate_previous_page_fails_and_retains_partial_data(self):
        reader = ready_for_page()
        first = reader.consume_reply(PAGE_12000)
        reader.next_request()
        with self.assertRaises(session.SessionError):
            reader.consume_reply(PAGE_12000)
        self.assert_terminal(reader)
        self.assertEqual(reader.pages, (first,))

    def test_abort_at_each_active_stage_is_terminal_and_independent(self):
        untouched = new_session()
        for stage in range(5):
            reader = new_session()
            operations = [reader.next_request, lambda: reader.consume_reply(HANDSHAKE),
                          reader.next_request, lambda: reader.consume_reply(PAGE_12000)]
            for operation in operations[:stage]:
                operation()
            reader.abort()
            reader.abort()
            self.assert_terminal(reader, session.SessionState.ABORTED)
            self.assertIsNone(reader.failure_reason)
        self.assertEqual(untouched.next_request().hex(), "01e0fc02aa55")

    def test_abort_preserves_completed_or_failed_state(self):
        reader = ready_for_page()
        reader.consume_reply(PAGE_12000)
        reader.next_request()
        reader.consume_reply(PAGE_13000)
        reader.abort()
        self.assertEqual(reader.state, session.SessionState.COMPLETE)
        self.assertIsNone(reader.next_request())
        failed = new_session()
        with self.assertRaises(session.SessionError):
            failed.consume_reply(HANDSHAKE)
        reason = failed.failure_reason
        failed.abort()
        self.assert_terminal(failed)
        self.assertEqual(failed.failure_reason, reason)

    def test_module_imports_only_enum_and_existing_codec_and_has_no_io(self):
        # Guard the intended boundary as this core evolves: no transport, CLI,
        # file operations, subprocess, injected callbacks or indirect imports.
        tree = ast.parse(Path(session.__file__).read_text(encoding="utf-8"))
        imports = [node for node in ast.walk(tree)
                   if isinstance(node, (ast.Import, ast.ImportFrom))]
        self.assertTrue(all(isinstance(node, ast.ImportFrom) for node in imports))
        self.assertEqual({node.module for node in imports},
                         {"__future__", "enum", "tools.g9_backup_archive"})
        calls = [node for node in ast.walk(tree) if isinstance(node, ast.Call)]
        self.assertFalse(any(isinstance(node.func, ast.Name)
                             and node.func.id in {"open", "exec", "eval", "__import__"}
                             for node in calls))


if __name__ == "__main__":
    unittest.main()
