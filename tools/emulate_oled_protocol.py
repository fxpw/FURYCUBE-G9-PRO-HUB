"""Offline OLED investigation and proposed-protocol reference fixture.

NO USB access, firmware writes, physical tests or generated firmware images.
The new protocol handler here is Python reference code, NOT injected ARM code.
The stock Set_Report dispatcher and OLED draw-block instructions do execute in
Unicorn, with ROM, I2C, logging, delay and wake hardware calls intercepted.
"""

from pathlib import Path
import struct
from unicorn import Uc, UC_ARCH_ARM, UC_MODE_THUMB, UC_HOOK_CODE
from unicorn.arm_const import (
    UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3,
    UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC,
)

ROOT = Path(__file__).resolve().parents[1]
BASE = 0x28790
STOP = 0x600000
REPORT = 0x22  # HEX 22, decimal 34. Stock application ignores SET; bootloader unaudited.
LENGTH = 132
WIDTH, VISIBLE_HEIGHT, STORAGE_HEIGHT = 120, 28, 32
PAGES, PAGE_BYTES, FRAME_BYTES = 4, 120, 480
WRITE_PAGE, COMMIT, RESTORE = 1, 2, 3


def crc16(data):
    """New protocol CRC-16/CCITT-FALSE; unrelated to vendor firmware packaging."""
    crc = 0xFFFF
    for value in data:
        crc ^= value << 8
        for _ in range(8):
            crc = ((crc << 1) ^ (0x1021 if crc & 0x8000 else 0)) & 0xFFFF
    return crc


def packet(operation, sequence=0, page=0xFF, payload=b''):
    assert len(payload) <= PAGE_BYTES
    data = bytearray(LENGTH)
    data[:10] = bytes([REPORT, LENGTH, ord('G'), ord('O'), 1, operation,
                       sequence, page, len(payload), 0])
    data[10:10+len(payload)] = payload
    data[130:132] = struct.pack('<H', crc16(data[:130]))
    return bytes(data)


class StockInstructions:
    def __init__(self):
        self.image = (ROOT / 'evidence/g9pro-firmware-normalized.bin').read_bytes()
        self.uc = Uc(UC_ARCH_ARM, UC_MODE_THUMB)
        self.uc.mem_map(0x20000, 0x30000)
        self.uc.mem_write(BASE, self.image)
        self.uc.mem_map(0x400000, 0x20000)
        self.uc.mem_map(0x500000, 0x10000)
        self.uc.mem_map(STOP, 0x1000)
        self.commands = []
        self.bytes_on_bus = []
        self.upgrade_called = False
        self.uc.hook_add(UC_HOOK_CODE, self.hook)

    def ret(self):
        self.uc.reg_write(UC_ARM_REG_PC, self.uc.reg_read(UC_ARM_REG_LR))

    def hook(self, uc, address, _size, _context):
        if address == 0x25078:
            table_address = uc.reg_read(UC_ARM_REG_LR) & ~1
            index = uc.reg_read(UC_ARM_REG_R3)
            table = bytes(uc.mem_read(table_address, 100))
            # ARM compiler ROM helper clamps an out-of-range selector to the
            # final/default entry. The stock SET table has max selector 13.
            index = min(index, table[0])
            uc.reg_write(UC_ARM_REG_PC, table_address + 2 * table[index + 1] | 1)
        elif address == 0x24FEC:  # ROM zero-fill(dst, count), NOT 3-argument memset.
            uc.mem_write(uc.reg_read(UC_ARM_REG_R0), bytes(uc.reg_read(UC_ARM_REG_R1)))
            self.ret()
        elif address == 0x3CDBE:  # OLED command transaction.
            self.commands.append(uc.reg_read(UC_ARM_REG_R0) & 255)
            self.ret()
        elif address == 0x3CD7A:  # Low-level I2C byte output.
            self.bytes_on_bus.append(uc.reg_read(UC_ARM_REG_R0) & 255)
            self.ret()
        elif address in (0x34AF8, 0x374A8, 0x3BC66, 0x3CDAE, 0x3CD6A, 0x3BA36):
            self.ret()  # Wake, log, bus start/stop and delay hardware.
        elif address == 0x39446:
            self.upgrade_called = True
            uc.emu_stop()  # Never execute update/flash hardware, even offline.

    def run(self, address):
        self.uc.reg_write(UC_ARM_REG_SP, 0x508000)
        self.uc.reg_write(UC_ARM_REG_LR, STOP | 1)
        self.uc.emu_start(address | 1, STOP, count=20000)
        assert self.upgrade_called or self.uc.reg_read(UC_ARM_REG_PC) == STOP

    def draw_page(self, page, payload):
        assert 0 <= page < PAGES and len(payload) == PAGE_BYTES
        self.commands.clear()
        self.bytes_on_bus.clear()
        self.uc.mem_write(0x410000, payload)
        for register, value in ((UC_ARM_REG_R0, 0), (UC_ARM_REG_R1, page),
                                (UC_ARM_REG_R2, PAGE_BYTES), (UC_ARM_REG_R3, 0x410000)):
            self.uc.reg_write(register, value)
        self.run(0x3CEE6)
        assert self.commands == [0xB0 + page, 0x10, 0]
        assert self.bytes_on_bus == [0x78, 0x40] + list(payload)

    def audit_stock_set(self, report):
        self.upgrade_called = False
        self.uc.mem_write(0x400F8A, bytes([report]))  # request.report_id
        self.uc.mem_write(0x400F8C, struct.pack('<I', 0x400F93))  # request.payload
        self.uc.mem_write(0x400F93, bytes(132))
        self.run(0x33CD2)
        return self.upgrade_called


class ProposedHandler:
    """Host/firmware contract reference; no claim of an implemented ARM handler.

    Streams one page directly to the native draw primitive. There is no atomic
    whole-frame swap and no flash persistence. Model.frame is a test oracle,
    NOT a claim that persistent framebuffer RAM has been reserved in firmware.
    """
    def __init__(self, draw):
        self.draw = draw
        self.owner = False
        self.sequence = None
        self.pages = 0
        self.frame = bytearray(FRAME_BYTES)
        self.last_committed = None

    def accept(self, data):
        # Validate everything before changing ownership or touching I2C.
        if len(data) != LENGTH:
            raise ValueError('length')
        if data[:5] != bytes([REPORT, LENGTH, ord('G'), ord('O'), 1]):
            raise ValueError('header')
        if data[9] != 0 or struct.unpack_from('<H', data, 130)[0] != crc16(data[:130]):
            raise ValueError('flags/CRC')
        operation, sequence, page, count = data[5:9]
        if operation == WRITE_PAGE:
            if page >= PAGES or count != PAGE_BYTES:
                raise ValueError('page/count')
            payload = data[10:130]
            if page == 3 and any(value & 0xF0 for value in payload):
                raise ValueError('pixels outside configured 28-row canvas')
            if page != 0 and (not self.owner or sequence != self.sequence):
                raise ValueError('sequence: start with page 0')
            self.owner = True
            if page == 0:
                self.sequence, self.pages = sequence, 0
            self.draw(page, payload)
            self.frame[page*PAGE_BYTES:(page+1)*PAGE_BYTES] = payload
            self.pages |= 1 << page
        elif operation in (COMMIT, RESTORE):
            if page != 0xFF or count or any(data[10:130]):
                raise ValueError('control payload')
            if operation == COMMIT:
                if not self.owner or self.sequence != sequence or self.pages != 0xF:
                    raise ValueError('incomplete frame')
                self.last_committed = sequence
            else:
                self.owner, self.sequence, self.pages = False, None, 0
        else:
            raise ValueError('operation')

    def capabilities(self):
        data = bytearray(LENGTH)
        data[:10] = bytes([REPORT, LENGTH]) + b'G9OLED1\0'
        data[10:20] = bytes([WIDTH, VISIBLE_HEIGHT, STORAGE_HEIGHT, PAGES, 1, 0,
                             int(self.owner), self.sequence or 0, self.pages, 0])
        data[20:22] = bytes([self.last_committed or 0, int(self.last_committed is not None)])
        data[130:132] = struct.pack('<H', crc16(data[:130]))
        return bytes(data)


def verify_capabilities(data):
    return (len(data) == LENGTH and data[:10] == bytes([REPORT, LENGTH]) + b'G9OLED1\0'
            and data[10:16] == bytes([WIDTH, VISIBLE_HEIGHT, STORAGE_HEIGHT, PAGES, 1, 0])
            and struct.unpack_from('<H', data, 130)[0] == crc16(data[:130]))


def pack_pixels(pixels):
    """Proposed logical canvas: page-major, bit 0 is the first row in a page."""
    assert len(pixels) == VISIBLE_HEIGHT and all(len(row) == WIDTH for row in pixels)
    frame = bytearray(FRAME_BYTES)
    for y, row in enumerate(pixels):
        for x, value in enumerate(row):
            if value:
                frame[(y // 8) * WIDTH + x] |= 1 << (y % 8)
    return bytes(frame)


def main():
    stock = StockInstructions()
    assert not stock.audit_stock_set(REPORT)
    assert stock.audit_stock_set(0x10)
    print('PASS: stock application SET 0x22 takes default return; SET 0x10 reaches upgrade')
    stock.upgrade_called = False
    pixels = [[int(x == y or x == WIDTH - 1 - y) for x in range(WIDTH)]
              for y in range(VISIBLE_HEIGHT)]
    frame = pack_pixels(pixels)
    assert len(frame) == FRAME_BYTES and not any(value & 0xF0 for value in frame[360:])
    handler = ProposedHandler(stock.draw_page)
    assert verify_capabilities(handler.capabilities())
    assert not verify_capabilities(bytes(LENGTH))
    for page in range(PAGES):
        handler.accept(packet(WRITE_PAGE, 7, page, frame[page*120:(page+1)*120]))
    handler.accept(packet(COMMIT, 7))
    assert bytes(handler.frame) == frame and handler.last_committed == 7
    print('PASS: four 132-byte page packets -> actual native draw-block instructions -> 480 data bytes')
    print('PASS: native draw emits page B0..B3, column 10/00, I2C 78/40 and 120 page bytes')
    negative = [
        packet(WRITE_PAGE, 7, 4, bytes(120)),
        packet(WRITE_PAGE, 7, 1, bytes(119)),
        packet(WRITE_PAGE, 8, 1, bytes(120)),
        packet(0xFF),
        packet(WRITE_PAGE, 7, 3, bytes([0xF0])*120),
        packet(WRITE_PAGE, 7, 0, bytes(120))[:-1],
        packet(WRITE_PAGE, 7, 0, bytes(120)) + b'\0',
    ]
    corrupt = bytearray(packet(WRITE_PAGE, 7, 0, bytes(120)))
    corrupt[20] ^= 1
    negative.append(bytes(corrupt))
    for item in negative:
        before = bytes(handler.frame), handler.owner, handler.sequence, handler.pages
        try:
            handler.accept(item)
        except ValueError:
            pass
        else:
            raise AssertionError('Malformed packet accepted')
        assert before == (bytes(handler.frame), handler.owner, handler.sequence, handler.pages)
    incomplete = ProposedHandler(stock.draw_page)
    try:
        incomplete.accept(packet(COMMIT, 9))
    except ValueError:
        pass
    else:
        raise AssertionError('Incomplete COMMIT accepted')
    handler.accept(packet(RESTORE))
    assert not handler.owner
    print('PASS: malformed lengths, page, count, sequence, opcode, pixels, CRC and incomplete COMMIT rejected')
    print('PROPOSED PROTOCOL ONLY: no ARM uploader/ownership integration/flash persistence implemented')
    print('Bootloader 0x22 behavior unaudited; no permission to send these frames to physical hardware')


if __name__ == '__main__':
    main()
