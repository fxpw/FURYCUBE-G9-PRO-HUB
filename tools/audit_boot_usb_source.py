"""Offline source audit: original C read functions + explicit boot decision model.

No transport, device command-line arguments, download, vendor executable or MCU
code installation. Run in the existing WSL environment with cc. The compiled
host fixture has mock flash registers and a memory-only USB sink.
"""
from pathlib import Path
import hashlib
import json
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "evidence/recovery-source-leads/himaexternal--BK3633_DesignKit_V06_220F/source/projects/app_2.4g/boot_usb"
COMMIT = "7720ca89ae8dbe26bc37942a33c675c97f127509"
HASHES = {
    "app/bim_app.c": "a22f5ef7918c105ec80a4ea9acee0b626b0e65170ca214cbe44f080675533550",
    "app/bim_flash.c": "4d4909a74a6436973972998537efb3504f0521fabb62777f64d0deeda0149674",
    "app/bim_flash.h": "9ab73df786d1b164f6dc215ef6a8de22a39aeb22cdbe8fab668e9d0db2d90362",
    "app/bim_updataImage.c": "50b48d5b5818e97ca84123a5ae0b77066c60d008478f91728d42037246aa2f9e",
    "app/bim_updataImage.h": "ed5123277e484e24c0aee2928b434cf45000d14ea60dd1e7e00c135b3ebfe133",
    "app/bim_uart2.h": "dd8af2cb68b079bb3d857b08284876bcc209b2c23a887be27439c4adafa22c04",
    "app/bim_icu.c": "e956eb53b7ada670a7f0b4f07839ce3f2343b8ba4caaee43a20eca4b94f88e1c",
    "app/rf_xvr.c": "4dde81d573b2a18347ba34e3002d912725bf8b50be12b1b66d984073674b4df7",
    "app/bim_gpio.c": "32975f1821f757e751ec3d35758a233f221f4cbe4d81aafb0b41a1b1e4db8586",
    "usb/driver_usb.c": "54e20574b63b10cec34faae064f74dc8d19fad5bb32ccc42580595e602f86c8f",
}


def clean(text):
    pattern = r'"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|//[^\n]*|/\*.*?\*/'
    return re.sub(pattern, lambda m: " " if m[0].startswith(("//", "/*")) else m[0], text, flags=re.S)


def function(text, name):
    text = clean(text)
    match = re.search(r"(?m)^[^\n;{}]*\b" + re.escape(name) + r"\s*\([^;{}]*\)\s*\{", text)
    assert match, "Missing original function: " + name
    pos, level = match.end(), 1
    while level:
        char = text[pos]
        level += (char == "{") - (char == "}")
        pos += 1
    return text[match.start():pos]


def sources():
    result = {}
    for name, digest in HASHES.items():
        data = (SOURCE / name).read_bytes()
        assert hashlib.sha256(data).hexdigest() == digest, "Source SHA mismatch: " + name
        result[name] = data.decode("gb18030", errors="replace")
    return result


def assert_source_contract(s):
    main = function(s["app/bim_app.c"], "bim_main")
    compact = re.sub(r"\s+", "", main)
    for token in [
        "flash_advance_init();", "flash_read(read_updata_status,0x7d000,16);",
        "if(hdr_img.uid==OAD_APP_PART_UID)",
        "addr_tmp=(hdr_img.len<<2)+SEC_OAD_RUN_APP_FADDR-0x100;",
        "tmp&=bim_uart_data[i];", "if(tmp==0xff)",
        "flash_write(bim_user_flash_data1,0x7d000,16);",
        "if(read_updata_status[2]==0x55)", "if(check_cnt++>2)break;",
        "if(1==bim_select_sec())", "(*(FUNCPTR)SEC_IMAGE_RUN_CADDR)();",
    ]:
        assert token in compact, "Boot model anchor missing: " + token
    assert compact.count("usb_init_boot();") == 3
    assert not re.search(r"gpio|button|chord|vbus|cable|usb.*present", main, re.I)
    assert not clean(s["app/bim_gpio.c"]).strip(), "GPIO file no longer empty"
    advance = function(s["app/bim_flash.c"], "flash_advance_init")
    assert "flash_wp_256k();" in advance
    selector = function(s["app/bim_updataImage.c"], "bim_select_sec")
    erased = selector.split("case SSTATUS_SECT_ERASED:")[1].split("break;")[0]
    assert "status = 1;" in erased and "flash_wp_ALL();" in erased
    assert "bim_check_image_sec_status" not in erased
    calls = sorted(set(re.findall(r"\b([A-Za-z_]\w*)\s*\(", main)) - {"if", "for", "while", "bim_main", "FUNCPTR"})
    return calls


def boot_model(marker, uid, tail, crash=False):
    """Labelled model, not executable ARM. Restricted to erased backup branch.

    Hardware startup functions are inventoried, not simulated electrically.
    A failed executable image after the final handoff is represented by crash.
    """
    assert len(marker) == 16 and len(tail) == 256
    marker = bytearray(marker)
    protection = ["flash_wp_256k (flash_advance_init)"]
    writes = []
    usb = marker[:2] == b"\x12\x34"
    if usb:
        protection.append("flash_wp_8k")
    elif uid != 0x42424242 or all(value == 255 for value in tail):
        protection.append("flash_wp_8k")
        writes.append({"address": 0x7D000, "lengthBytes": 16})
        # Native main changes only these two bytes in its RAM copy.
        marker[:2] = b"\x12\x34"
        usb = True
    if usb and marker[2] != 0x55:
        outcome = "USB_WAIT_NO_TIMEOUT"
    else:
        protection.append("flash_wp_ALL (erased backup branch)")
        outcome = "SIMULATED_IMAGE_APP_CRASH" if crash else "EXECUTABLE_IMAGE_HANDOFF"
    return {"model": True, "usbInitialized": usb, "markerWrites": writes,
            "protectionCalls": protection, "outcome": outcome,
            "backupFixture": "SSTATUS_SECT_ERASED"}


PREFIX = r'''
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#define GLOBAL_INT_DISABLE() ((void)0)
#define GLOBAL_INT_RESTORE() ((void)0)
#define BIT_ADDRESS_SW 0
#define BIT_OP_TYPE_SW 24
#define BIT_OP_SW 29
#define FLASH_OPCODE_READ 5
#define FLASH_ADDR_FIX 0x7D000
#define FLASH_PAGE_MASK 255
#define FLASH_SEC_SIZE 4096
#define ERASE_FLASH_SEC_SIZE 0x20
#define ERASE_FLASH_ONE_BLOCK_SIZE 0xD8
#define SEC_ALL_IMAGE_ALLOC_END_FADDR 0x40000
#define SEC_IMAGE_OAD_HEADER_STACK_FADDR 0x5006
#define USB_ENDPID_Hid_DBG_IN 1
static uint32_t operate, data_sink, fifo_words;
#define REG_FLASH_OPERATE_SW operate
#define REG_FLASH_DATA_SW_FLASH data_sink
static uint8_t pattern(uint32_t a) {return (uint8_t)(a*29u+(a>>8)+0x5a);}
static uint32_t read_fifo(void) {
 uint32_t a=(operate&0xffffffu)+(fifo_words++%8)*4, v=0;
 for(unsigned j=0;j<4;j++)v|=(uint32_t)pattern(a+j)<<(8*j);
 return v;
}
#define REG_FLASH_DATA_FLASH_SW read_fifo()
uint8_t bim_uart_cmd[16], bim_uart_data[4104], erase_fenable;
static uint8_t captured[64];
static unsigned tx_count, read_calls, last_read_len, mutation_calls;
void AplUsb_StartTx(int ep,void *buf,int len) {
 assert(ep==1&&len==64);memcpy(captured,buf,64);tx_count++;
}
static void forbidden(void) {mutation_calls++;abort();}
void usb_cmd_dispath(uint8_t*b,uint8_t n) {(void)b;(void)n;forbidden();}
void flash_wp_8k(void) {forbidden();}
void flash_erase(uint32_t a,uint32_t n) {(void)a;(void)n;forbidden();}
void flash_erase_one_block(uint32_t a) {(void)a;forbidden();}
void flash_write(uint8_t*b,uint32_t a,uint32_t n) {(void)b;(void)a;(void)n;forbidden();}
'''

SUFFIX = r'''
static void check_read(uint32_t address) {
 uint8_t req[12]={1,0xe0,0xfc,0xff,0xf4,5,0,9,0,0,0,0};
 for(unsigned j=0;j<4;j++)req[8+j]=(uint8_t)(address>>(8*j));
 memset(bim_uart_data,0xcc,sizeof(bim_uart_data));
 tx_count=read_calls=last_read_len=mutation_calls=fifo_words=0;
 bim_usb_data_callback(req,sizeof(req));
 assert(tx_count==1&&mutation_calls==0);
 assert(captured[0]==4&&captured[1]==0xe&&captured[9]==9);
 uint32_t echo=0;for(unsigned j=0;j<4;j++)echo|=(uint32_t)captured[11+j]<<(8*j);
 assert(echo==address);
 if(address<0x20000) {assert(captured[10]==6&&read_calls==0);}
 else {
  assert(captured[7]==6&&captured[8]==0x10&&captured[10]==0);
  assert(read_calls==1&&last_read_len==16);
  for(unsigned j=0;j<16;j++)assert(captured[15+j]==pattern(address+j));
  for(unsigned j=31;j<64;j++)assert(captured[j]==0xcc);
 }
 printf("%u ",address);for(unsigned j=0;j<64;j++)printf("%02x",captured[j]);
 printf(" %u %u %u\n",read_calls,last_read_len,fifo_words);
}
int main(void) {
 const uint32_t cases[]={0x1fff0,0x20000,0x20001,0x2001f,0x200f8,0x20010,0x80000};
 for(unsigned i=0;i<sizeof(cases)/sizeof(cases[0]);i++)check_read(cases[i]);
 return 0;
}
'''


def decode_fixture16(frame, expected_address):
    """Only the mirrored SDK's captured 64-byte fixture; not a G9 transport.

    A nominal 4-KiB reply is deliberately NOT reconstructed. Stale tail ignored.
    """
    if len(frame) != 64 or frame[:11] != bytes.fromhex("04 0e ff 01 e0 fc f4 06 10 09 00"):
        raise ValueError("Not a successful mirrored-source read16 fixture")
    if int.from_bytes(frame[11:15], "little") != expected_address:
        raise ValueError("Stale/wrong address")
    return frame[15:31]


def compile_read_fixtures(s, include_wire=False):
    assert sys.byteorder == "little", "Original ARM data layout needs little-endian host"
    cc = shutil.which("cc")
    assert cc, "Use the existing WSL research environment with cc; no auto-install"
    enum = re.search(r"typedef enum _UART_CMD_STATE\s*\{.*?\}\s*UART_CMD_STATE;", s["app/bim_uart2.h"], re.S)[0]
    low_read = function(s["app/bim_flash.c"], "flash_read_data")
    wrapped = function(s["app/bim_flash.c"], "flash_read")
    wrapped = re.sub(r"\bflash_read\s*\(", "original_flash_read(", wrapped, count=1)
    read_log = "uint8_t flash_read(uint8_t*b,uint32_t a,uint32_t n){read_calls++;last_read_len=n;return original_flash_read(b,a,n);}\n"
    source = (PREFIX + enum + low_read + wrapped + read_log +
              function(s["app/bim_app.c"], "usb_operate_flash_cmd_response") +
              function(s["app/bim_app.c"], "bim_usb_data_callback") + SUFFIX)
    with tempfile.TemporaryDirectory(prefix="g9-offline-source-") as temp:
        source_path, executable = Path(temp) / "fixture.c", Path(temp) / "fixture"
        source_path.write_text(source, encoding="utf-8")
        command = [cc, "-std=c99", "-O0", "-fno-pie", "-no-pie", "-fsanitize=address,undefined", "-fno-sanitize-recover=all", str(source_path), "-o", str(executable)]
        compiled = subprocess.run(command, capture_output=True, text=True, timeout=30)
        assert compiled.returncode == 0, compiled.stderr
        run = subprocess.run([str(executable)], capture_output=True, text=True, timeout=10)
        assert run.returncode == 0, f"Fixture exited {run.returncode}: {run.stderr}\n{run.stdout}"
    results = []
    for line in run.stdout.splitlines():
        address, hex_frame, calls, length, words = line.split()
        address, frame = int(address), bytes.fromhex(hex_frame)
        if address >= 0x20000:
            data = decode_fixture16(frame, address)
            for fault in [frame[:-1], bytes([5])+frame[1:], frame[:10]+b"\x06"+frame[11:], frame[:11]+bytes(4)+frame[15:]]:
                try:
                    decode_fixture16(fault, address)
                    raise AssertionError("Fault accepted")
                except ValueError:
                    pass
            assert decode_fixture16(frame[:31]+bytes(33), address) == data
        record = {"address": hex(address), "status": frame[10], "flashReadCalls": int(calls),
                  "requestedBytes": int(length), "mockFifoWords": int(words),
                  "knownDataBytes": 0 if address < 0x20000 else 16}
        if include_wire:
            # Failure helper leaves bytes16..63 uninitialized: do not preserve
            # host stack residue as if it were protocol data in a fixture file.
            normal_tail = address < 0x20000
            record.update({"requestHex": (bytes.fromhex("01e0fcfff4050009") + address.to_bytes(4, "little")).hex(),
                           "responseHex": (frame[:16] + bytes([0xDD])*48 if normal_tail else frame).hex(),
                           "unspecifiedFailureTailNormalized": normal_tail,
                           "successTailIsPoisonFixture": not normal_tail})
        results.append(record)
    assert len(results) == 7
    return results


def main():
    s = sources()
    calls = assert_source_contract(s)
    zero, tail = bytes(16), bytes([0x91]) * 256
    boot = {
        "validImage": boot_model(zero, 0x42424242, tail),
        "validMetadataExecutableCrash": boot_model(zero, 0x42424242, tail, crash=True),
        "wrongUid": boot_model(zero, 0, tail),
        "erasedTail": boot_model(zero, 0x42424242, bytes([255])*256),
        "existingMarker": boot_model(bytes.fromhex("1234aa")+bytes(13), 0, tail),
        "timedMarker": boot_model(bytes.fromhex("123455020000")+bytes(10), 0, tail),
    }
    assert not boot["validMetadataExecutableCrash"]["usbInitialized"]
    assert not boot["validMetadataExecutableCrash"]["markerWrites"]
    assert boot["wrongUid"]["markerWrites"] == [{"address": 0x7D000, "lengthBytes": 16}]
    assert boot["erasedTail"]["usbInitialized"] and boot["existingMarker"]["usbInitialized"]
    assert boot["timedMarker"]["outcome"] == "EXECUTABLE_IMAGE_HANDOFF"
    print(json.dumps({"scope": "ORIGINAL_C_READ_WITH_MOCK_REGISTERS_AND_LABELLED_BOOT_MODEL",
                      "sourceCommit": COMMIT, "physicalDeviceTested": False,
                      "liveTransportProvided": False, "mainCallInventory": calls,
                      "bootModelCases": boot, "compiledOriginalReadCases": compile_read_fixtures(s)}, indent=2))


if __name__ == "__main__":
    main()
