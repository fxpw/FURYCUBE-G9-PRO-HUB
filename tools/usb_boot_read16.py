"""Pure codec for the pinned BK3633 SDK USB boot source's partial read.

This is NOT a G9 transport or a qualified installed-loader implementation.
It cannot open a file, port or device, enter a loader, or send any command.
The source says 4102 response bytes but actually sends 64 and fills only 16
data bytes. Decode that exact source-specific shape; never invent a 4 KiB page.
"""
from dataclasses import dataclass, field
import hashlib
import struct

SOURCE_REVISION = "7720ca89ae8dbe26bc37942a33c675c97f127509"
SOURCE_REPOSITORY = "himaexternal/BK3633_DesignKit_V06_220F"
MIN_SOURCE_ADDRESS = 0x20000
MAX_SOURCE_BUS_CAPACITY = 0x1000000  # 24-bit controller address field; not flash size.
DATA_BYTES = 16
LOGICAL_RESPONSE_BYTES = 64
_QUERY_PREFIX = bytes.fromhex("01e0fcfff4050009")
_SUCCESS_PREFIX = bytes.fromhex("040eff01e0fcf4061009")
_FAILURE_PREFIX = bytes.fromhex("040eff01e0fcf4070009")


class Read16Error(ValueError):
    """Malformed, out-of-bounds or unsuccessful source-format observation."""


def _address(address: int, declared_capacity: int) -> int:
    # Capacity must be supplied explicitly. A part-name or two matching
    # observations are not a measurement of this mouse's readable capacity.
    if type(address) is not int or type(declared_capacity) is not int:
        raise Read16Error("Address and declared capacity must be integers")
    # The source ORs an address into the controller word alongside opcode bits
    # starting at bit24. A wider address could alias or change the operation.
    if not MIN_SOURCE_ADDRESS + DATA_BYTES <= declared_capacity <= MAX_SOURCE_BUS_CAPACITY:
        raise Read16Error("Invalid declared capacity")
    if not MIN_SOURCE_ADDRESS <= address <= declared_capacity - DATA_BYTES:
        raise Read16Error("Read exceeds the declared range or source lower bound")
    return address


def encode_logical_request(address: int, *, declared_capacity: int) -> bytes:
    """Construct 12 source-format bytes; no HID envelope or transmission."""
    return _QUERY_PREFIX + struct.pack("<I", _address(address, declared_capacity))


@dataclass(frozen=True)
class Read16Observation:
    address: int
    data: bytes
    data_sha256: str
    response_sha256: str
    source_revision: str = field(default=SOURCE_REVISION, init=False)
    source_repository: str = field(default=SOURCE_REPOSITORY, init=False)
    installed_g9_compatibility_verified: bool = field(default=False, init=False)
    transport_implemented: bool = field(default=False, init=False)
    is_full_page: bool = field(default=False, init=False)
    is_firmware_backup: bool = field(default=False, init=False)
    restore_proven: bool = field(default=False, init=False)


def decode_logical_response(response: bytes, expected_address: int, *,
                            declared_capacity: int) -> Read16Observation:
    """Decode only the 64-byte logical source response, excluding stale tail.

    A 65-byte OS HID envelope, UART 4111-byte reply or alleged 4102-byte reply
    needs its own independently qualified transport. None is stripped/guessed.
    The source's incorrect extended length is a fingerprint, not a page size.
    """
    address = _address(expected_address, declared_capacity)
    if type(response) is not bytes or len(response) != LOGICAL_RESPONSE_BYTES:
        raise Read16Error("Expected exactly 64 logical source-response bytes")
    if response[:10] == _FAILURE_PREFIX:
        raise Read16Error("Source returned a failure response; no data accepted")
    if response[:10] != _SUCCESS_PREFIX:
        raise Read16Error("Wrong source response header, command or length field")
    if response[10] != 0:
        raise Read16Error("Source returned nonzero status; no data accepted")
    actual_address = struct.unpack_from("<I", response, 11)[0]
    if actual_address != address:
        raise Read16Error("Response belongs to another address")
    data = response[15:31]
    # Bytes 31..63 contain no additional read bytes in this implementation.
    # They can carry a previous global-buffer tail and are never archived as
    # flash data. There is no per-response data CRC or authentication here.
    return Read16Observation(address, data, hashlib.sha256(data).hexdigest(),
                             hashlib.sha256(response).hexdigest())
