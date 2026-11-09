"""Offline source experiment: original source with memory-only NVM/USB mocks.
No device APIs, serial/USB transport, marker writer CLI or downloaded executable.
"""
from pathlib import Path
import hashlib
import json
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = next(p for p in Path(__file__).resolve().parents if (p / "tools/audit_boot_usb_source.py").is_file())
sys.path.insert(0, str(ROOT / "tools"))
import audit_boot_usb_source as audit

s = audit.sources()
base = audit.SOURCE
startup = (base / "app/bim_handlers.s").read_text(errors="replace")
assert hashlib.sha256((base / "app/bim_handlers.s").read_bytes()).hexdigest() == "a019fb71d4e5aa0f71c96da678d6ad29c405f509bb8c07d97ce744c515ac77c3"
assert startup.index("BL _sysboot_copy_data_to_ram") < startup.index("BL _sysboot_zi_init") < startup.index("B bim_main")
assert "STRLO R2, [R4], #4" in startup
main = audit.function(s["app/bim_app.c"], "bim_main")
assert "uart_download_status" not in main
assert "check_cnt=0;" in re.sub(r"\s+", "", main)
assert s["app/bim_app.c"].count("0x12,0x34,0xaa") == 1
app_source = base.parent / "app_usb_2.4g/app/src/main.c"
app_raw = app_source.read_bytes()
assert hashlib.sha256(app_raw).hexdigest() == "6cf208da76cc338d6ffefed0598ee4eb6eedeb12a4b1c316603f9875ed354cd9"
assert b"0x12,0x34,0xaa" in app_raw
assert b"0x12,0x34,0x55" not in app_raw

header = audit.clean(s["app/bim_updataImage.h"])
defines = "\n".join(line for line in header.splitlines() if line.lstrip().startswith("#define"))
states = re.search(r"enum\s*\{.*?\};", header, re.S)[0]
prefix = r'''
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <setjmp.h>
#include <assert.h>
typedef uint32_t uint32;typedef uint8_t uint8;typedef void(*FUNCPTR)(void);
typedef struct {uint32_t crc;uint16_t ver,len;uint32_t uid;uint8_t crc_status,sec_status;uint16_t rom_ver;} img_hdr_t;
static jmp_buf stop;
static uint8_t persistent[16],read_updata_status[16],bim_uart_data[4104],MGC_RX_Buffer[64];
static uint8_t bim_user_flash_data1[16]={0x12,0x34,0xaa,0,0,0x20};
static int32_t check_cnt=0,delay_timer_count;
static uint8_t b_isDataing,b_isTRxing,uart_download_status,erase_fenable;
static unsigned usb_count,write_count,erase_count,max_erase_end,wp8,wp256,wpall,inject_aa,scratch_erased,scratch_reads,use_scratch_classifier;
static unsigned backup_status,image_status;
static uint32_t flash_mid;
static img_hdr_t hdr_img,hdr_back,hdr_back_part,scratch_header;
static void image_handoff(void){longjmp(stop,1);}
static int bim_printf(const char*fmt,...){(void)fmt;return 0;}
static void icu_init(void){}static void wdt_disable(void){}static void uart2_init(uint32_t n){(void)n;}
static void xvr_reg_initial(void){}static void updata_memset32(void*p,uint32 v,uint8 n){(void)p;(void)v;(void)n;}
static uint32_t get_flash_ID(void){return 0x13311c;}static uint32_t flash_read_sr(void){return 0;}
static void flash_wp_256k(void){wp256++;}static void flash_wp_8k(void){wp8++;}static void flash_wp_128k(void){}
static void flash_wp_ALL(void){wpall++;}
static uint8_t flash_read(uint8_t*b,uint32_t a,uint32_t n){
 memset(b,0x77,n);if(a==0x7d000&&n==16)memcpy(b,persistent,16);
 if(use_scratch_classifier&&a==0x52000&&n==sizeof(img_hdr_t)){memcpy(b,&scratch_header,n);scratch_reads++;}return 0;
}
static uint8_t flash_write(uint8_t*b,uint32_t a,uint32_t n){
 write_count++;if(a<0x7d010&&0x7d000<a+n){assert(a==0x7d000&&n==16);memcpy(persistent,b,16);}return 0;
}
static uint8_t flash_erase(uint32_t a,uint32_t n){
 erase_count++;if(a+n>max_erase_end)max_erase_end=a+n;
 if(a<0x53000&&0x52000<a+n)scratch_erased=1;
 if(a<0x7d010&&0x7d000<a+n)memset(persistent,255,16);return 0;
}
static void flash_erase_one_block(uint32_t a){flash_erase(a,0x10000);}
static uint8_t source_bim_check_backup_sec_status(void);
static uint8_t bim_check_backup_sec_status(void){return use_scratch_classifier?source_bim_check_backup_sec_status():backup_status;}
static uint8_t bim_check_image_sec_status(void){return image_status;}
static uint32_t bim_get_psec_image_header(void){hdr_img.uid=0x42424242;hdr_img.len=0x1000;return 0;}
static void usb_init_boot(void){usb_count++;if(read_updata_status[2]!=0x55)longjmp(stop,2);}
static void usb_cmd_response(uint8_t c,uint8_t n,uint8_t*p){(void)c;(void)n;(void)p;}
static int make_crc32_table(void){return 0;}
static uint32_t make_crc32(uint32_t c,unsigned char*p,uint32_t n){(void)p;(void)n;return c;}
static uint32_t calc_backup_sec_crc(uint32_t a){(void)a;return 0;}
static void wdt_enable(unsigned n){(void)n;longjmp(stop,3);}
#define LINK_CHECK_CMD 0
#define CRC_CHECK_CMD 0x10
#define SET_RESET_CMD 0x0e
#define SET_BAUDRATE_CMD 0x0f
#define STAY_ROM_CMD 0xaa
static void usb_cmd_dispath(uint8_t*,uint8_t);
static void bim_usb_data_callback(uint8_t*b,uint16_t n){(void)b;(void)n;uint8_t cmd[2]={0xaa,1};usb_cmd_dispath(cmd,2);}
static void irq_start(void){if(inject_aa)b_isDataing=1;}
#define GLOBAL_INT_START() irq_start()
#define GLOBAL_INT_STOP() ((void)0)
'''
suffix = r'''
static void set_timed(void){memset(persistent,0,16);persistent[0]=0x12;persistent[1]=0x34;persistent[2]=0x55;persistent[3]=2;}
static int reset_fixture(unsigned stat,uint32_t uid,int aa){
 /* Model only the source's DATA/BSS reset, preserving synthetic flash. */
 check_cnt=delay_timer_count=0;b_isDataing=b_isTRxing=uart_download_status=erase_fenable=0;
 memset(read_updata_status,0,16);usb_count=write_count=erase_count=max_erase_end=wp8=wp256=wpall=scratch_erased=scratch_reads=0;
 inject_aa=aa;backup_status=stat;image_status=SSTATUS_SECT_NORMAL;
 memset(&hdr_back,0,sizeof(hdr_back));hdr_back.uid=uid;hdr_back.len=8;
 int event=setjmp(stop);if(!event)bim_main();return event;
}
static void report(const char*name,int event){
 printf("{\"case\":\"%s\",\"event\":%d,\"usbInit\":%u,\"markerPreserved\":%s,\"counter\":%d,\"writes\":%u,\"erases\":%u,\"maxEraseEnd\":%u,\"scratch52000Erased\":%s,\"scratchHeaderReads\":%u,\"wp8\":%u,\"wp256\":%u,\"wpAll\":%u}\n",name,event,usb_count,persistent[0]==0x12&&persistent[1]==0x34&&persistent[2]==0x55?"true":"false",check_cnt,write_count,erase_count,max_erase_end,scratch_erased?"true":"false",scratch_reads,wp8,wp256,wpall);
}
int main(void){
 set_timed();uint8_t saved[16];memcpy(saved,persistent,16);
 int e=reset_fixture(SSTATUS_SECT_ERASED,OAD_APP_PART_UID,0);
 assert(e==1&&usb_count==1&&check_cnt==4&&!memcmp(saved,persistent,16));report("timed_trial_handoff",e);
 e=reset_fixture(SSTATUS_SECT_ERASED,OAD_APP_PART_UID,0);
 assert(e==1&&usb_count==1&&check_cnt==4&&!memcmp(saved,persistent,16));report("reset_after_simulated_app_failure",e);
 e=reset_fixture(SSTATUS_SECT_ERASED,OAD_APP_PART_UID,1);
 assert(e==1&&uart_download_status==1&&check_cnt==4&&!memcmp(saved,persistent,16));report("stay_rom_flag_does_not_pin_timed_loop",e);
 unsigned states[]={SSTATUS_SECT_NORMAL,SSTATUS_SECT_ABNORMAL,SSTATUS_SECT_DIFF_ROM_VER};
 for(unsigned i=0;i<3;i++)for(unsigned j=0;j<2;j++){
  set_timed();e=reset_fixture(states[i],j?OAD_APP_STACK_UID:OAD_APP_PART_UID,0);
  assert(e==1&&!memcmp(saved,persistent,16)&&max_erase_end<=0x7c000);
  char name[64];snprintf(name,sizeof(name),"selector_%u_uid_%u",states[i],j);report(name,e);
 }
 set_timed();memset(&scratch_header,0,sizeof(scratch_header));scratch_header.uid=OAD_APP_PART_UID;
 scratch_header.ver=scratch_header.rom_ver=1;scratch_header.len=8;scratch_header.crc_status=CRC_CHECK_FAIL;
 use_scratch_classifier=1;e=reset_fixture(SSTATUS_SECT_ERASED,OAD_APP_PART_UID,0);
 assert(e==1&&scratch_reads==1&&scratch_erased&&!memcmp(saved,persistent,16));report("scratch_classified_abnormal_app_backup",e);
 use_scratch_classifier=0;
 set_timed();usb_count=write_count=erase_count=max_erase_end=wp8=wp256=wpall=scratch_erased=scratch_reads=0;check_cnt=0;
 uint8_t cmd[9]={0x10,0,0,2,0,255,0,2,0};
 e=setjmp(stop);if(!e)usb_cmd_dispath(cmd,9);
 assert(e==3&&persistent[0]==255&&persistent[1]==255);report("standard_crc_finish_removes_marker",e);
 e=reset_fixture(SSTATUS_SECT_ERASED,OAD_APP_PART_UID,0);
 assert(e==1&&usb_count==0);report("reset_after_finish_no_usb",e);
 memset(persistent,0,16);persistent[0]=0x12;persistent[1]=0x34;persistent[2]=0xaa;
 e=reset_fixture(SSTATUS_SECT_ERASED,OAD_APP_PART_UID,0);
 assert(e==2&&usb_count==1);report("stock_aa_enters_usb_no_timed_exit",e);
 return 0;
}
'''

# Bind source layout before replacing only the final executable-image target.
layout = defines + "\n" + states + r'''
enum {SOURCE_STACK_HEADER=SEC_IMAGE_OAD_HEADER_STACK_FADDR,SOURCE_APP_HEADER=SEC_OAD_RUN_APP_FADDR};
#undef SEC_IMAGE_OAD_HEADER_STACK_FADDR
#define SEC_IMAGE_OAD_HEADER_STACK_FADDR SOURCE_STACK_HEADER
#undef SEC_OAD_RUN_APP_FADDR
#define SEC_OAD_RUN_APP_FADDR SOURCE_APP_HEADER
#undef SEC_IMAGE_RUN_CADDR
#define SEC_IMAGE_RUN_CADDR ((uintptr_t)&image_handoff)
'''
functions = [audit.function(s["app/bim_flash.c"], "flash_advance_init")]
functions += [audit.function(s["app/bim_updataImage.c"], "bim_get_psec_backup_header")]
classifier = audit.function(s["app/bim_updataImage.c"], "bim_check_backup_sec_status")
functions += [re.sub(r"\bbim_check_backup_sec_status\s*\(","source_bim_check_backup_sec_status(",classifier,count=1)]
functions += [audit.function(s["app/bim_updataImage.c"], n) for n in ["bim_erase_image_sec", "bim_erase_backup_sec", "bim_updata_backup_to_image_sec", "bim_select_sec"]]
functions += [audit.function(s["app/bim_app.c"], n) for n in ["usb_cmd_dispath", "bim_main"]]
source = prefix + layout + "\n".join(functions) + suffix
with tempfile.TemporaryDirectory(prefix="g9-marker-source-") as temp:
    p=Path(temp)/"fixture.c";exe=Path(temp)/"fixture";p.write_text(source)
    cc=shutil.which("cc");assert cc,"Use the existing WSL environment with cc; no auto-install"
    command=[cc,"-std=c99","-O0","-fno-pie","-no-pie","-fsanitize=address,undefined","-fno-sanitize-recover=all",str(p),"-o",str(exe)]
    c=subprocess.run(command,capture_output=True,text=True,timeout=30);assert c.returncode==0,c.stderr
    run=subprocess.run([str(exe)],capture_output=True,text=True,timeout=5);assert run.returncode==0,run.stderr
cases=[json.loads(line) for line in run.stdout.splitlines()]
assert len(cases)==13

def idle_trace(events,delay):
    count=0
    for step,event in enumerate(events):
        if event=="rx":count=0
        else:
            old=count;count+=1
            if old>delay:return {"exitAt":step,"count":count}
    return {"exitAt":None,"count":count}
traffic={"four_idle":idle_trace(["idle"]*4,2),"rx_resets_window":idle_trace(["idle","idle","rx"]*20,2)}
assert traffic["four_idle"]["exitAt"]==3 and traffic["rx_resets_window"]["exitAt"] is None
driver = base.parents[2] / "src/driver/flash/flash.c"
driver_raw = driver.read_bytes()
assert hashlib.sha256(driver_raw).hexdigest() == "ad211967503afaea01db5608641f2da2067c8a29931af0cf00a30cdda2a83e93"
helper = re.sub(r"\s+", "", audit.function(driver_raw.decode('gb18030',errors='replace'),"flash_write_some_data"))
for token in ["unsignedcharflash_temp[1024];","flash_read(0,address&0xfffffc00,1024,flash_temp,(void*)0);",
              "flash_erase(0,address,0x1000,0);","flash_write(0,address&0xfffffc00,1024,flash_temp,(void*)0);"]:
    assert token in helper, token
pairing = base.parent / "app_usb_2.4g/app/src/2.4/application_mode_page.c"
assert hashlib.sha256(pairing.read_bytes()).hexdigest() == "9835cd2b6b7474feacb6f39542cc75158bfad4e497ae742cf7447facbb49a42b"
assert "flash_write_some_data(szRXAddr,0x7d210,8);" in re.sub(r"\s+","",audit.clean(pairing.read_bytes().decode('gb18030',errors='replace')))
def pairing_write_model(complete):
    sector=bytearray([0x97])*4096;sector[:6]=bytes.fromhex('123455020000')
    saved=sector[:1024];saved[0x210:0x218]=b'PAIRTEST'
    sector[:]=bytes([255])*4096
    if complete:sector[:1024]=saved
    return {"model":True,"completedRewrite":complete,"markerPreserved":sector[:3]==bytes.fromhex('123455'),
            "sectorBytesErased":4096,"preservedRewriteBytes":1024 if complete else 0}
pairing_cases=[pairing_write_model(True),pairing_write_model(False)]
assert pairing_cases[0]['markerPreserved'] and not pairing_cases[1]['markerPreserved']
result={"scope":"OFFLINE_ORIGINAL_C_WITH_MOCK_NVM_USB_AND_RESET_MODEL","physicalDeviceTested":False,
        "installedG9CompatibilityVerified":False,"sourceCommit":audit.COMMIT,"cases":cases,"trafficLoopModel":traffic,"pairingWriteModels":pairing_cases,
        "extraSourceHashes":{"bim_handlers.s":hashlib.sha256((base/'app/bim_handlers.s').read_bytes()).hexdigest(),
                             "app_main.c":hashlib.sha256(app_raw).hexdigest(),"app_flash.c":hashlib.sha256(driver_raw).hexdigest(),
                             "application_mode_page.c":hashlib.sha256(pairing.read_bytes()).hexdigest()},
        "nativeFunctions":["flash_advance_init","bim_main","usb_cmd_dispath","bim_select_sec","bim_get_psec_backup_header","bim_check_backup_sec_status","bim_erase_image_sec","bim_erase_backup_sec","bim_updata_backup_to_image_sec"],
        "boundaryMocks":["flash persistence and geometry","protection effects","USB initialization/I/O","watchdog reset","image execution","backup checks (mocked except original scratch-header classification case)"],
        "compiledSourceSha256":hashlib.sha256(source.encode()).hexdigest()}
out=ROOT/'evidence/recovery-source-leads/timed-marker-results.json';out.write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2))
