#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
#include <stdexcept>
#include "G9Keymap.h"

// ABI of the installed 32-bit HIDAPI. No filter driver or input injection.
struct HidInfo {
 char* path;unsigned short vendor,product;wchar_t* serial;unsigned short release;
 wchar_t* manufacturer;wchar_t* product_string;unsigned short usage_page,usage;
 int interface_number;HidInfo* next;
};
using Enumerate=HidInfo* (__stdcall*)(unsigned short,unsigned short);
using FreeInfo=void (__stdcall*)(HidInfo*);
using Open=void* (__stdcall*)(const char*);
using Close=void (__stdcall*)(void*);
using Transfer=int (__stdcall*)(void*,unsigned char*,size_t);
static void dump(const char* label,int result,const unsigned char* bytes,size_t size){
 std::printf("%s result=%d len=%zu data=",label,result,size);
 for(size_t i=0;i<size;i++)std::printf("%02x",bytes[i]);std::puts("");std::fflush(stdout);
}
struct Connection {
 HMODULE module=nullptr;void* handle=nullptr;Close close=nullptr;Transfer send=nullptr,get=nullptr;
 unsigned short pid=0;
 ~Connection(){if(handle&&close)close(handle);if(module)FreeLibrary(module);}
 void connect(unsigned short requested){
  module=LoadLibraryW(L"C:\\Program Files (x86)\\FURYCUBE HUB\\hidapi.dll");
  if(!module)throw std::runtime_error("Installed HIDAPI could not be loaded");
  auto enumerate=reinterpret_cast<Enumerate>(GetProcAddress(module,"_hid_enumerate@8"));
  auto freeInfo=reinterpret_cast<FreeInfo>(GetProcAddress(module,"_hid_free_enumeration@4"));
  auto open=reinterpret_cast<Open>(GetProcAddress(module,"_hid_open_path@4"));
  close=reinterpret_cast<Close>(GetProcAddress(module,"_hid_close@4"));
  send=reinterpret_cast<Transfer>(GetProcAddress(module,"_hid_send_feature_report@12"));
  get=reinterpret_cast<Transfer>(GetProcAddress(module,"_hid_get_feature_report@12"));
  if(!enumerate||!freeInfo||!open||!close||!send||!get)throw std::runtime_error("Unexpected HIDAPI exports");
  HidInfo* list=enumerate(0x1d57,0);std::vector<std::pair<unsigned short,std::string>> paths;
  for(auto p=list;p;p=p->next)if((p->product==0x2148||p->product==0xfa60)&&p->usage_page==0x0b&&p->path&&strstr(p->path,"mi_02&col04"))paths.emplace_back(p->product,p->path);
  freeInfo(list);
  pid=requested;if(!pid){pid=0xfa60;for(const auto& p:paths)if(p.first==0x2148)pid=0x2148;}
  std::string selected;for(const auto& p:paths)if(p.first==pid){if(!selected.empty())throw std::runtime_error("Multiple matching mice; refusing an ambiguous write");selected=p.second;}
  if(selected.empty())throw std::runtime_error("G9 configuration interface not connected");
  std::printf("TARGET VID=1d57 PID=%04x path=%s\n",pid,selected.c_str());std::fflush(stdout);
  handle=open(selected.c_str());if(!handle)throw std::runtime_error("Cannot open G9 configuration interface");
 }
 void write(unsigned char* packet,size_t size){
  int result=send(handle,packet,size);dump("SEND",result,packet,size);
  if(result!=static_cast<int>(size))throw std::runtime_error("USB write failed; no further commands sent");
 }
 void finishPendingKeyRead(){
  if(pid!=0x2148)throw std::runtime_error("Recovery only verified for wired G9");
  unsigned char state[262]={0xa0};int r=get(handle,state,8);dump("PENDING STATE",r,state,8);
  if(r<8||state[0]!=0xa0)throw std::runtime_error("Cannot read pending state");
  if(state[1]==0){std::puts("No pending read");return;}
  unsigned char data[262]={8};r=get(handle,data,59);dump("DISCARDED PENDING RESPONSE",r,data,59);
  if(r<59||data[0]!=8)throw std::runtime_error("Unexpected pending response; reconnect mouse");
  Sleep(500);memset(state,0,sizeof(state));state[0]=0xa0;r=get(handle,state,8);dump("AFTER CLEANUP",r,state,8);
  if(r<8||state[0]!=0xa0||state[1]!=0)throw std::runtime_error("Read still pending; reconnect mouse");
  std::puts("Pending key read finished. Discarded data is not a profile validation.");
 }
 void readProfile(){
  unsigned char state[262]={0xa0};int r=get(handle,state,8);dump("PREVIOUS READ STATE",r,state,8);
  if(r<8||state[0]!=0xa0||state[1]!=0)throw std::runtime_error("Previous read is pending");
  Sleep(100);unsigned char query[8]={0xa0,12,8,0,1,0,0,0};write(query,sizeof(query));Sleep(500);
  memset(state,0,sizeof(state));state[0]=0xa0;r=get(handle,state,8);dump("ACK",r,state,8);
  if(r<8||state[0]!=0xa0||state[1]!=1)throw std::runtime_error("Profile read not granted");
  Sleep(100);unsigned char data[262]={12};r=get(handle,data,8);dump("ACTIVE PROFILE RESPONSE",r,data,8);
  if(r<8||data[0]!=12||data[1]!=8)throw std::runtime_error("Unexpected profile response");
  int found=-1;for(int i=2;i<=3;i++)if((data[i]^data[i+1])==255&&(data[i+2]^data[i+3])==255&&data[i]>0&&data[i]<=8&&data[i+2]>0&&data[i+2]<=8){if(found!=-1)throw std::runtime_error("Ambiguous profile response");found=i;}
  if(found<0)throw std::runtime_error("Profile complement bytes invalid");
  std::printf("ACTIVE PROFILE=%u MACRO PROFILE=%u\n",data[found],data[found+2]);Sleep(150);
 }
 g9::Keymap readKeys(){
  // Byte four selects profile one. Consume each granted read exactly once.
  unsigned char idle[262]={0xa0};int idleResult=get(handle,idle,8);dump("PREVIOUS READ STATE",idleResult,idle,8);
  if(idleResult<8||idle[0]!=0xa0||idle[1]!=0)throw std::runtime_error("Previous read is still pending; reconnect before a new transaction");
  Sleep(100);unsigned char query[8]={0xa0,8,59,0,1,0,0,0};write(query,sizeof(query));Sleep(500);
  unsigned char ack[262]={0xa0};int ackResult=get(handle,ack,8);dump("ACK",ackResult,ack,8);
  if(ackResult<8||ack[0]!=0xa0||ack[1]!=1)throw std::runtime_error("Device did not grant a keymap read");
  Sleep(100);unsigned char response[262]={8};size_t length=pid==0x2148?59:64;
  int result=get(handle,response,length);dump("READBACK",result,response,length);
  if(result<59)throw std::runtime_error("Read failed; stop here and reconnect the mouse before another transaction");
  auto keys=g9::decode(response,length);Sleep(150);return keys;
 }
};
static std::string backup(const g9::Keymap& keys,unsigned short pid){
 if(!CreateDirectoryW(L"evidence",nullptr)&&GetLastError()!=ERROR_ALREADY_EXISTS)throw std::runtime_error("Cannot create backup directory");
 SYSTEMTIME t;GetSystemTime(&t);char path[240];
 sprintf_s(path,"evidence\\keymap-%04u%02u%02uT%02u%02u%02u%03uZ-%04x-%lu.bin",t.wYear,t.wMonth,t.wDay,t.wHour,t.wMinute,t.wSecond,t.wMilliseconds,pid,GetCurrentProcessId());
 HANDLE f=CreateFileA(path,GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
 if(f==INVALID_HANDLE_VALUE)throw std::runtime_error("Cannot create unique backup; refusing write");
 DWORD written=0;BOOL ok=WriteFile(f,keys.data(),static_cast<DWORD>(keys.size()),&written,nullptr);CloseHandle(f);
 if(!ok||written!=keys.size())throw std::runtime_error("Backup incomplete; refusing write");return path;
}
int main(int argc,char** argv){try{
 if(argc<2||(strcmp(argv[1],"inspect")&&strcmp(argv[1],"zero")&&strcmp(argv[1],"zero-control")&&strcmp(argv[1],"restore")&&strcmp(argv[1],"finish-read")&&strcmp(argv[1],"profile"))){std::puts("G9Protocol inspect | zero | zero-control (temporary middle-button test) | restore <backup.bin> | profile | finish-read [--pid 2148|fa60]");return 2;}
 unsigned short pid=0;std::string restore;
 for(int i=2;i<argc;i++){
  if(!strcmp(argv[i],"--pid")&&i+1<argc){unsigned n=static_cast<unsigned>(strtoul(argv[++i],nullptr,16));if(n!=0xfa60&&n!=0x2148)throw std::runtime_error("Unsupported PID");pid=static_cast<unsigned short>(n);}
  else if(!strcmp(argv[1],"restore")&&restore.empty())restore=argv[i];else throw std::runtime_error("Unexpected command argument");
 }
 Connection device;device.connect(pid);if(!strcmp(argv[1],"finish-read")){device.finishPendingKeyRead();return 0;}if(!strcmp(argv[1],"profile")){device.readProfile();return 0;}auto before=device.readKeys();
 std::printf("BUTTON6 function=%02x modifier=%02x usage=%02x\n",before[12],before[13],before[14]);
 if(!strcmp(argv[1],"inspect")){std::puts("KEYMAP VALIDATED. A physical keypress is a separate check.");return 0;}
 auto after=before;
 if(!strcmp(argv[1],"zero"))g9::setZero(after);
 else if(!strcmp(argv[1],"zero-control")){g9::setZero(after);g9::setZero(after,2);}
 else{FILE* f=nullptr;if(restore.empty()||fopen_s(&f,restore.c_str(),"rb")||!f)throw std::runtime_error("Backup file missing");unsigned char data[60]={};size_t n=fread(data,1,sizeof(data),f);fclose(f);if(n!=59)throw std::runtime_error("Backup length invalid");after=g9::decode(data,n);}
 std::printf("BACKUP %s\n",backup(before,device.pid).c_str());std::fflush(stdout);
 // This command selects profile 1/1; it is not merely a wake-up request.
 unsigned char selectProfile[10]={0x0c,0x0a,1,0xfe,1,0xfe,0,0,0,0};device.write(selectProfile,sizeof(selectProfile));Sleep(500);
 device.write(after.data(),after.size());Sleep(500);auto verified=device.readKeys();
 if(verified!=after)throw std::runtime_error("Readback differs from requested settings; physical output not verified");
 std::puts("CONFIG VERIFIED. Tool is exiting. Test the sixth button in a text field.");return 0;
}catch(const std::exception& e){std::printf("ERROR: %s\n",e.what());return 1;}}
