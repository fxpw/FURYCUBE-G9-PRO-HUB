"""Offline PE assertions and isolated original-function emulation. No transport.

Needs pefile + Unicorn in the existing research venv. Vendor PE files are data;
every executed call outside the allowlisted original routines is a local stub.
"""
from pathlib import Path
import hashlib
import json
import struct

import pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_EAX, UC_X86_REG_ECX, UC_X86_REG_ESP, UC_X86_REG_EIP

ROOT = Path(__file__).resolve().parents[1]
SDK_HASH = "9240adf66b69d0e61bc6f87089bac441d042bc97c9a159401c174f790b447e0c"
OEM_HASH = "558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4"


def pinned(pattern, digest):
    paths = list(ROOT.glob(pattern))
    assert len(paths) == 1, "Expected one pinned local PE"
    data = paths[0].read_bytes()
    assert hashlib.sha256(data).hexdigest() == digest, "Source SHA mismatch"
    return pefile.PE(data=data)


class Sandbox:
    BASE, STACK, CTX, STOP, CLOSE, SLEEP = 0x400000, 0x70000000, 0x20000000, 0x71000000, 0x72000000, 0x72000010

    def __init__(self, pe, ranges):
        self.u = Uc(UC_ARCH_X86, UC_MODE_32)
        img = pe.get_memory_mapped_image()
        self.u.mem_map(self.BASE, (len(img) + 4095) & ~4095)
        self.u.mem_write(self.BASE, img)
        for addr, size in [(self.STACK, 0x20000), (self.CTX, 0x10000), (self.STOP, 0x1000), (self.CLOSE, 0x1000)]:
            self.u.mem_map(addr, size)
        self.ranges, self.stubs, self.frames, self.reads, self.closed = ranges, {}, [], [], 0
        self.put32(0x603374, self.CLOSE)
        self.put32(0x603358, self.SLEEP)
        self.stubs[self.CLOSE] = lambda: self.close()
        self.stubs[self.SLEEP] = lambda: self.ret(4)
        self.stubs[0x5B0E21] = lambda: self.ret()
        self.stubs[0x5B1040] = self.memset
        self.stubs[0x5B28D0] = self.memcpy
        self.stubs[0x40FE20] = self.send_sink
        self.u.hook_add(UC_HOOK_CODE, self.hook)

    def put32(self, addr, value):
        self.u.mem_write(addr, struct.pack("<I", value))

    def word(self, addr):
        return struct.unpack("<I", self.u.mem_read(addr, 4))[0]

    def arg(self, index):
        return self.word(self.u.reg_read(UC_X86_REG_ESP) + 4 + index * 4)

    def ret(self, pop=0, result=None):
        sp = self.u.reg_read(UC_X86_REG_ESP)
        pc = self.word(sp)
        self.u.reg_write(UC_X86_REG_ESP, sp + 4 + pop)
        if result is not None:
            self.u.reg_write(UC_X86_REG_EAX, result)
        self.u.reg_write(UC_X86_REG_EIP, pc)

    def hook(self, _, pc, size, user):
        if pc == self.STOP:
            self.u.emu_stop()
        elif pc in self.stubs:
            self.stubs[pc]()
        elif not any(lo <= pc < hi for lo, hi in self.ranges):
            raise AssertionError("Unexpected control transfer: " + hex(pc))

    def close(self):
        self.closed += 1
        self.ret(4, 1)

    def memset(self):
        dst, value, size = (self.arg(i) for i in range(3))
        assert size < 0x10000
        self.u.mem_write(dst, bytes([value & 255]) * size)
        self.ret(result=dst)

    def memcpy(self):
        dst, src, size = (self.arg(i) for i in range(3))
        assert size < 0x10000
        self.u.mem_write(dst, bytes(self.u.mem_read(src, size)))
        self.ret(result=dst)

    def send_sink(self):
        buffer, length = self.arg(1), self.arg(2)
        assert 0 < length <= 64
        self.frames.append(bytes(self.u.mem_read(buffer, length)))
        self.ret(16, length)

    def run(self, entry, args=()):
        sp = self.STACK + 0x10000
        self.put32(sp, self.STOP)
        for i, value in enumerate(args):
            self.put32(sp + 4 + i * 4, value)
        self.u.reg_write(UC_X86_REG_ESP, sp)
        self.u.reg_write(UC_X86_REG_ECX, self.CTX)
        self.u.emu_start(entry, self.STOP + 1, count=300000)
        assert self.u.reg_read(UC_X86_REG_EIP) == self.STOP
        return self.u.reg_read(UC_X86_REG_EAX)


def sdk_identity(pe, vid, pid):
    s = Sandbox(pe, [(0x40FB60, 0x40FC14)])
    s.put32(s.CTX + 12, s.CTX + 0x8000)
    s.put32(s.CTX + 0x8000, 0x1234)

    def attrs():
        p = s.arg(1)
        s.u.mem_write(p, struct.pack("<IHHH", 12, vid, pid, 0x100))
        s.ret(8, 1)

    def buffers():
        s.put32(s.arg(1), 64)
        s.ret(8, 1)

    s.stubs[0x41E5B2], s.stubs[0x41E5AC] = attrs, buffers
    result = s.run(0x40FB60)
    assert (result == 1) == (vid == 0x10C4 and pid == 0x33)
    assert s.closed == (0 if result else 1)
    assert not s.frames
    return {"vid": f"{vid:04X}", "pid": f"{pid:04X}", "accepted": bool(result), "closeCalls": s.closed}


def sdk_prep(pe, mid):
    s = Sandbox(pe, [(0x417000, 0x4170EC), (0x41C0D0, 0x41C4AE)])
    s.u.mem_write(s.CTX + 0x1CC, b"\x01")  # Skip external mode-change/reopen branch.

    def receive():
        buffer, length = s.arg(1), s.arg(2)
        assert s.frames and 0 < length <= 64
        prefix = s.frames[-1][:2]
        data = mid.to_bytes(3, "little") if prefix == b"\x07\x9f" else bytes(length)
        assert len(data) == length
        s.reads.append({"command": prefix.hex(), "length": length})
        s.u.mem_write(buffer, data)
        s.ret(16, length)

    s.stubs[0x40FD20] = receive
    result = s.run(0x417000, [s.CTX])
    assert s.frames[0][0] == 0x0D
    assert sum(x[:2] == b"\x04\xd2" for x in s.frames) == 151
    assert any(x[:2] == b"\x07\x9f" for x in s.frames)
    mutated = any(x[:2] == b"\x04\x06" for x in s.frames)
    assert mutated == (mid != 0)
    return {"fixtureMid": f"{mid:06X}", "resultLowByte": result & 255,
            "frameCount": len(s.frames), "tailFramesHex": [x.hex() for x in s.frames[152:]],
            "receives": s.reads, "containsWriteEnable": mutated}


def sdk_dump_request(pe):
    s = Sandbox(pe, [(0x405670, 0x4057C9), (0x4166B0, 0x4166B3), (0x41E080, 0x41E08F)])
    s.put32(s.CTX + 0x1600, 17)
    s.put32(s.CTX + 0x1604, 0x1000)
    s.put32(s.CTX + 0x160C, 0x80000)  # Synthetic bound, not installed-chip evidence.
    s.put32(s.CTX + 0x9000, 0x12340)
    s.stubs[0x402FD0] = lambda: s.ret(8, 2)  # GUI-derived starting block; no MFC/UI execution.
    s.stubs[0x403090] = lambda: s.ret(12, 0x12340)
    assert s.run(0x405670, [0, s.CTX + 0x9000]) == 0
    assert len(s.frames) == 1
    expected = bytes.fromhex("00 40 23 01 00 10 00 00 02") + bytes(55)
    assert s.frames[0] == expected
    return {"syntheticRequestHex": expected.hex(), "logicalBytes": 64,
            "guiStartBlockStub": 2, "blockBytes": 4096, "lengthBytes": 0x12340}


def main():
    sdk = pinned("evidence/recovery-uart/usb/**/*.exe", SDK_HASH)
    oem = pinned("evidence/firmware-readonly/**/Update.exe", OEM_HASH)
    data = lambda pe, a, n: pe.get_data(a - pe.OPTIONAL_HEADER.ImageBase, n)
    assert data(sdk, 0x40FB92, 16) == bytes.fromhex("b8c4100000663945f4755a66837df633")
    assert data(oem, 0x4030BB, 7) == bytes.fromhex("6a336845a70000")
    assert data(oem, 0x403232, 8) == bytes.fromhex("c744244001e0fcff")
    assert data(sdk, 0x608FB0 + 17 * 4, 4) == struct.pack("<I", 0x417000)
    results = {"scope": "OFFLINE_ORIGINAL_FUNCTIONS_WITH_SINK_IO", "physicalDeviceTested": False,
               "sdkSha256": SDK_HASH, "oemSha256": OEM_HASH,
               "identities": [sdk_identity(sdk, *x) for x in [(0x10C4, 0x33), (0xA745, 0x33), (0x10C4, 0x34), (0x1D57, 0x2148)]],
               "preparation": [sdk_prep(sdk, 0x13311C), sdk_prep(sdk, 0)],
               "dumpRequest": sdk_dump_request(sdk)}
    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
