"""Isolated retention-store proof. No hardware, updater or candidate generation.

Compile new assembly in a temporary directory; execute the pinned stock
malloc/free and uninstalled retention helpers. Only simulated allocator end
and main-entry branch are patched. No existing firmware artifacts are written.
"""
from pathlib import Path
import hashlib
import json
import struct
import subprocess
import tempfile

from unicorn import Uc, UC_ARCH_ARM, UC_MODE_THUMB, UC_HOOK_CODE, UC_HOOK_MEM_WRITE
from unicorn.arm_const import (
    UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3,
    UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7,
    UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_CPSR,
)
from build_oled_candidate import decode_resource
from emulate_oled_arm import elf_text_and_symbols
from emulate_screen_key import thumb_bl

ROOT = Path(__file__).resolve().parents[1]
BASE, EXTENSION, ROOT_STATE = 0x28790, 0x47400, 0x406F1C
META, STOP, INPUT = 964, 0x500000, 0x500100
MAGIC = 0x32524F47
SOURCE_SHA = 'f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc'
REGISTERS = (UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3,
             UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7)


def compile_store():
    with tempfile.TemporaryDirectory(prefix='g9-retention-offline-') as directory:
        obj = Path(directory) / 'store.o'
        subprocess.run(['clang', '--target=arm-none-eabi', '-march=armv5t',
                        '-mthumb', '-c', str(ROOT / 'firmware/g9_screen_store.S'),
                        '-o', str(obj)], check=True)
        return elf_text_and_symbols(obj.read_bytes())


class Fixture:
    def __init__(self, image, code, symbols):
        self.uc = Uc(UC_ARCH_ARM, UC_MODE_THUMB)
        self.uc.mem_map(0x28000, 0x22000)
        self.uc.mem_write(BASE, image)
        self.uc.mem_write(EXTENSION, code)
        self.uc.mem_map(0x400000, 0x10000)
        self.uc.mem_map(STOP, 0x1000)
        self.symbols = symbols
        self.writes, self.calls, self.allocator_cpsr = [], [], []
        self.instructions = 0
        self.frame_instructions = []
        # Reserve exactly16B from an arena owned by the stock allocator.
        assert bytes(self.uc.mem_read(0x4306C, 4)) == bytes.fromhex('2c6f4000')
        self.uc.mem_write(0x4306C, struct.pack('<I', ROOT_STATE))
        assert bytes(self.uc.mem_read(0x28D58, 4)) == bytes.fromhex('a4480025')
        self.uc.mem_write(0x28D58, thumb_bl(0x28D58, self.address('g9_store_boot_hook')))
        self.uc.hook_add(UC_HOOK_CODE, self.on_code)
        self.uc.hook_add(UC_HOOK_MEM_WRITE, self.on_write)

    def address(self, symbol):
        return EXTENSION + self.symbols[symbol]['offset']

    def on_code(self, uc, address, size, data):
        self.instructions += 1
        if address in (0x43004, 0x42FB0):
            self.calls.append((address, uc.reg_read(UC_ARM_REG_R0)))
            self.allocator_cpsr.append(uc.reg_read(UC_ARM_REG_CPSR))
        if address == STOP or address == 0x28D5C:
            uc.emu_stop()

    def on_write(self, uc, access, address, size, value, data):
        self.writes.append((address, size))

    def word(self, address):
        return struct.unpack('<I', self.uc.mem_read(address, 4))[0]

    def call(self, function, r0=0, r1=0):
        address = self.address(function) if isinstance(function, str) else function
        self.uc.reg_write(UC_ARM_REG_CPSR, 0xD3)  # Actual reset SVC IRQ/FIQ mask.
        self.uc.reg_write(UC_ARM_REG_SP, 0x40F800)
        self.uc.reg_write(UC_ARM_REG_R0, r0)
        self.uc.reg_write(UC_ARM_REG_R1, r1)
        self.uc.reg_write(UC_ARM_REG_LR, STOP | 1)
        self.uc.emu_start(address | 1, STOP + 2, count=400000)
        assert self.uc.reg_read(UC_ARM_REG_PC) in (STOP, 0x28D5C)
        assert self.uc.reg_read(UC_ARM_REG_SP) == 0x40F800
        assert self.uc.reg_read(UC_ARM_REG_CPSR) & 0xFF == 0xF3
        return self.uc.reg_read(UC_ARM_REG_R0)

    def boot(self):
        for register in REGISTERS:
            self.uc.reg_write(register, 0x13570000 + register)
        result = self.call(0x28D58)
        assert result == 0x40006D and self.uc.reg_read(UC_ARM_REG_R5) == 0
        for register in REGISTERS:
            if register not in (UC_ARM_REG_R1, UC_ARM_REG_R5):
                assert self.uc.reg_read(register) == 0x13570000 + register
        assert self.uc.reg_read(UC_ARM_REG_R1) == 0  # Fixture r1 input.
        assert self.word(ROOT_STATE + 8) == 1
        return self.word(ROOT_STATE + 4)

    def chunk(self, index, data):
        assert len(data) == 40
        self.uc.mem_write(INPUT, data)
        return self.call('g9_store_chunk', index, INPUT)

    def active(self):
        pointer = self.call('g9_store_active')
        return bytes(self.uc.mem_read(pointer, 480)) if pointer else None

    def frame(self, pixels):
        assert len(pixels) == 480
        before = self.instructions
        for index in range(12):
            assert self.chunk(index, pixels[index*40:(index+1)*40]) == (2 if index == 11 else 1)
        self.frame_instructions.append(self.instructions-before)

    def arm_reset(self):
        self.uc.reg_write(UC_ARM_REG_CPSR,0xD3)
        self.uc.emu_start(0x28824,STOP+2,count=500000)
        assert self.uc.reg_read(UC_ARM_REG_PC) == 0x28D5C
        assert self.uc.reg_read(UC_ARM_REG_SP) == 0x40F81C
        assert self.uc.reg_read(UC_ARM_REG_CPSR) & 0xFF == 0xF3


def pixels(seed):
    return bytes(((index * 13 + seed) & (15 if index >= 360 else 255))
                 for index in range(480))


def main():
    original = (ROOT / 'evidence/g9pro-official-firmware-resource.bin').read_bytes()
    assert hashlib.sha256(original).hexdigest() == SOURCE_SHA
    image, _, _ = decode_resource(original)
    code, symbols = compile_store()
    # Execute the real ARM reset path, all banked-stack setup, data copy,
    # BSS zero and ARM->Thumb main entry. No startup or ROM stubs.
    startup = Fixture(image, code, symbols)
    startup.uc.mem_write(ROOT_STATE, bytes([0xA5])*16)
    startup.uc.reg_write(UC_ARM_REG_CPSR, 0xD3)
    startup.uc.emu_start(0x28824, STOP+2, count=200000)
    assert startup.uc.reg_read(UC_ARM_REG_PC) == 0x28D5C
    assert startup.uc.reg_read(UC_ARM_REG_SP) == 0x40F81C
    assert startup.uc.reg_read(UC_ARM_REG_CPSR) & 0xFF == 0xF3
    assert startup.calls == [(0x43004,1024)]
    assert startup.allocator_cpsr[0] & 0xC0 == 0xC0
    assert startup.word(ROOT_STATE) == MAGIC
    assert startup.word(ROOT_STATE+4) == 0x404F34
    reference = Fixture(image,code,symbols)
    reference.uc.mem_write(0x28D58,bytes.fromhex('a4480025'))
    reference.arm_reset()
    for register in (UC_ARM_REG_R0,)+REGISTERS+(UC_ARM_REG_SP,UC_ARM_REG_CPSR):
        assert startup.uc.reg_read(register) == reference.uc.reg_read(register)
    assert startup.active() is None
    f = Fixture(image, code, symbols)
    pointer = f.boot()
    assert pointer == 0x404F34
    assert f.word(ROOT_STATE) == MAGIC
    assert f.word(pointer-4) == 1032
    assert f.active() is None
    assert f.calls == [(0x43004, 1024)]
    frame_a, frame_b = pixels(7), pixels(19)
    f.frame(frame_a)
    assert f.active() == frame_a
    committed_pointer = f.call('g9_store_active')
    # An incomplete second upload must not replace the last complete image.
    for index in range(6):
        assert f.chunk(index, frame_b[index*40:(index+1)*40]) == 1
        assert f.active() == frame_a
    assert f.chunk(7, frame_b[280:320]) == 0
    assert f.active() == frame_a
    # Nonzero restart is refused after aborted upload.
    assert f.chunk(6, frame_b[240:280]) == 0
    # Each repeated/missing/invalid index leaves previous frame intact.
    for bad_index in (12, 255, 0xFFFFFFFF):
        assert f.chunk(bad_index, bytes(40)) == 0
        assert f.active() == frame_a
    for index in range(9):
        assert f.chunk(index, frame_b[index*40:(index+1)*40]) == 1
    assert f.chunk(9, bytes([0x10])*40) == 0
    assert f.active() == frame_a
    f.frame(frame_b)
    assert f.active() == frame_b
    assert f.call('g9_store_active') != committed_pointer
    assert f.word(pointer+META+8) == 2
    # USB teardown only frees its own allocations, not retention allocation.
    others = [f.call(0x43004, length) for length in (64, 64, 132, 8, 16)]
    assert all(p and p >= pointer+1024 for p in others)
    for p in others:
        f.call(0x42FB0, p)
    assert f.active() == frame_b
    assert f.call(0x43004, 9000) == 0
    assert f.active() == frame_b
    # All helper/allocator writes are confined to documented regions.
    allowed = ((0x400D2C,0x400D34), (0x404F30,0x406F1C),
               (ROOT_STATE,ROOT_STATE+16), (0x40F700,0x40F800))
    assert all(any(low <= a and a+s <= high for low,high in allowed)
               for a,s in f.writes)
    # Exhaustion at boot gives native fallback and never publishes valid magic.
    failure = Fixture(image, code, symbols)
    failure.uc.mem_write(0x400D30, struct.pack('<I', 1))
    assert failure.boot() == 0
    assert failure.word(ROOT_STATE) == 0 and failure.active() is None
    assert failure.chunk(0,bytes(40)) == 0
    # Both banks survive a real warm ARM reset, including lazy allocator rebuild.
    # The first4 allocation bytes are deliberately not part of either image.
    warm_cases = 0
    for seed in (31,43):
        retained = pixels(seed)
        f.frame(retained)
        assert f.chunk(0,pixels(seed+1)[:40]) == 1
        generation = f.word(pointer+META+8)
        f.arm_reset()
        for register in (UC_ARM_REG_R0,)+REGISTERS+(UC_ARM_REG_SP,UC_ARM_REG_CPSR):
            assert f.uc.reg_read(register) == reference.uc.reg_read(register), (register,hex(f.uc.reg_read(register)),hex(reference.uc.reg_read(register)))
        assert f.word(ROOT_STATE+4) == pointer
        assert f.active() == retained
        assert f.word(pointer+META+8) == generation
        assert bytes(f.uc.mem_read(pointer+META+2,2)) == bytes(2)
        assert f.uc.mem_read(pointer+META+6,1) == bytes([1])
        warm_cases += 1
    saved_ram = bytes(f.uc.mem_read(0x400000,0x10000))
    # Valid magic is insufficient: bad pointer/bank/valid/owner/CRC/data reject.
    corruption_cases = 0
    active_pointer = f.call('g9_store_active')
    for address,data in (
            (ROOT_STATE,bytes(4)),
            (ROOT_STATE+4,struct.pack('<I',0x400A00)),
            (pointer+META,bytes([2])),
            (pointer+META+1,bytes([0])),
            (pointer+META+4,bytes([2])),
            (pointer+META+12,bytes([0x12,0x34])),
            (active_pointer,bytes([0xFF]))):
        damaged = Fixture(image,code,symbols)
        damaged.uc.mem_write(0x400000,saved_ram)
        damaged.uc.mem_write(address,data)
        damaged.arm_reset()
        assert damaged.active() is None
        corruption_cases += 1
    # Lost arena simulates an unpreserved loader/power boundary: reject it.
    lost = Fixture(image,code,symbols)
    lost.uc.mem_write(0x400000,saved_ram)
    lost.uc.mem_write(0x404F30,bytes(ROOT_STATE-0x404F30))
    lost.arm_reset()
    assert lost.active() is None
    # An unexpected earlier allocation changes malloc's returned pointer.
    mismatch = Fixture(image,code,symbols)
    mismatch.uc.mem_write(0x400000,saved_ram)
    mismatch.uc.mem_write(0x400D2C,bytes(8))
    assert mismatch.call(0x43004,64) == pointer
    new_pointer = mismatch.boot()
    assert new_pointer != pointer and mismatch.active() is None
    # Completely cleared RAM is cold boot, not simulated power persistence.
    cold = Fixture(image,code,symbols)
    cold.arm_reset()
    assert cold.active() is None
    assert hashlib.sha256((ROOT / 'evidence/g9pro-official-firmware-resource.bin').read_bytes()).hexdigest() == SOURCE_SHA
    print(json.dumps(dict(status='PASS_ISOLATED_OFFLINE_ONLY',
                          code_bytes=len(code), code_sha256=hashlib.sha256(code).hexdigest(),
                          symbols=symbols, root=[hex(ROOT_STATE),hex(ROOT_STATE+16)],
                          allocation=1024, allocator_overhead=8,
                          complete_frames=2, native_fallback=True,
                          real_arm_startup_and_banked_stacks=True,
                          boot_allocation_irq_fiq_masked=True,
                          warm_reset_banks=warm_cases,
                          corruption_rejections=corruption_cases,
                          lost_arena_native_fallback=True,
                          reallocation_mismatch_native_fallback=True,
                          incomplete_upload_preserves_last=True,
                          per_frame_malloc_calls=0, per_frame_nvm_calls=0,
                          frame_store_instruction_counts=f.frame_instructions,
                          native_redraw_hooks_implemented=False,
                          radio_integration=False, physical_validated=False), indent=2))


if __name__ == '__main__':
    main()
