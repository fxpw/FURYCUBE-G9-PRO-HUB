"""Offline, in-memory session for an unqualified G9 UART read representation.

This module emits bytes; it never opens a transport or accesses a device/file.
Only the pinned AA55 handshake and existing READ09 page codec are supported.
Capacity is an explicit caller claim, not measured or inferred hardware capacity.
A completed session does not prove physical acquisition, independent loader
entry, security policy, byte representation, or restoration compatibility.

There are no retries, reconnects, resets, reboot commands, baud changes, MID
queries, fallback paths or automatic probing. Framing, timeout handling, power
sequencing and electrical qualification belong to a future qualified transport;
this module does not establish live compatibility with the mouse.
"""
from __future__ import annotations

from enum import Enum

from tools.g9_backup_archive import (
    ArchiveError,
    CapturedPage,
    MAX_DECLARED_CAPACITY,
    PAGE_BYTES,
    decode_page_reply,
    encode_page_request,
)


HANDSHAKE_REQUEST = bytes.fromhex("01e0fc02aa55")
HANDSHAKE_REPLY = bytes.fromhex("040e0501e0fcaa55")


class SessionError(ArchiveError):
    """Invalid session declaration, sequence or reply."""


class SessionState(str, Enum):
    READY_HANDSHAKE = "ready-handshake"
    AWAITING_HANDSHAKE = "awaiting-handshake"
    READY_PAGE = "ready-page"
    AWAITING_PAGE = "awaiting-page"
    COMPLETE = "complete"
    FAILED = "failed"
    ABORTED = "aborted"


class UartReadSession:
    """One handshake followed by exactly the declared sequential page range.

    All three keyword arguments are mandatory byte counts/addresses, expressed
    as integers (not booleans), aligned to PAGE_BYTES. The range must fit within
    declared_capacity and the codec's implementation limit. Declaration errors
    are rejected during construction, before any bytes can be emitted.

    next_request() emits one request and then requires consume_reply(). Calling
    it again while a reply is pending fails the session without emitting bytes.
    consume_reply() accepts one complete frame, not stream fragments. Any invalid
    or unsolicited reply fails permanently; accepted earlier pages remain
    available as partial data, never as proof of a complete physical backup.
    After completion next_request() returns None. Failure or abort causes later
    requests/replies to raise SessionError. abort() is idempotent and preserves
    an existing terminal state. Create a separate instance for a separate pass;
    that alone cannot prove an independent hardware acquisition.
    """

    def __init__(self, *, start: int, length: int, declared_capacity: int):
        for name, value in (("start", start), ("length", length),
                            ("declared_capacity", declared_capacity)):
            if type(value) is not int:
                raise SessionError(name + " must be an explicit integer")
            if value % PAGE_BYTES:
                raise SessionError(name + " must be aligned to 4096 bytes")
        if not PAGE_BYTES <= declared_capacity <= MAX_DECLARED_CAPACITY:
            raise SessionError("declared_capacity must be positive and within codec limits")
        if start < 0 or length <= 0 or start + length > declared_capacity:
            raise SessionError("The positive declared range must fit within declared_capacity")
        self._start = start
        self._length = length
        self._declared_capacity = declared_capacity
        self._next_address = start
        self._state = SessionState.READY_HANDSHAKE
        self._failure_reason: str | None = None
        self._pages: list[CapturedPage] = []

    @property
    def start(self) -> int:
        return self._start

    @property
    def length(self) -> int:
        return self._length

    @property
    def declared_capacity(self) -> int:
        return self._declared_capacity

    @property
    def state(self) -> SessionState:
        return self._state

    @property
    def failure_reason(self) -> str | None:
        return self._failure_reason

    @property
    def pages(self) -> tuple[CapturedPage, ...]:
        """Accepted immutable pages, including any incomplete failed pass."""
        return tuple(self._pages)

    def _fail(self, reason: str) -> None:
        self._failure_reason = reason
        self._state = SessionState.FAILED
        raise SessionError(reason)

    def _require_active(self) -> None:
        if self._state in (SessionState.FAILED, SessionState.ABORTED):
            raise SessionError("Session is terminal: " + self._state.value)

    def next_request(self) -> bytes | None:
        """Return the next bytes once, or None after the exact range completes."""
        self._require_active()
        if self._state == SessionState.COMPLETE:
            return None
        if self._state == SessionState.READY_HANDSHAKE:
            self._state = SessionState.AWAITING_HANDSHAKE
            return HANDSHAKE_REQUEST
        if self._state == SessionState.READY_PAGE:
            request = encode_page_request(self._next_address)
            self._state = SessionState.AWAITING_PAGE
            return request
        self._fail("A request is already awaiting its reply; no retry is permitted")

    def consume_reply(self, reply: bytes) -> CapturedPage | None:
        """Accept exactly one pending full reply; reject invalid frames terminally."""
        self._require_active()
        if self._state == SessionState.AWAITING_HANDSHAKE:
            if not isinstance(reply, bytes) or reply != HANDSHAKE_REPLY:
                self._fail("Expected the exact eight-byte AA55 handshake reply")
            self._state = SessionState.READY_PAGE
            return None
        if self._state != SessionState.AWAITING_PAGE:
            self._fail("Unsolicited reply without a pending request")
        try:
            page = decode_page_reply(reply, self._next_address)
        except ArchiveError as error:
            self._fail(str(error))
        self._pages.append(page)
        self._next_address += PAGE_BYTES
        self._state = (SessionState.COMPLETE
                       if self._next_address == self._start + self._length
                       else SessionState.READY_PAGE)
        return page

    def abort(self) -> None:
        """Stop an active session with no further bytes; preserve terminal states."""
        if self._state not in (SessionState.COMPLETE, SessionState.FAILED,
                               SessionState.ABORTED):
            self._state = SessionState.ABORTED
