#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>

static HMODULE original() { static HMODULE m=LoadLibraryW(L"real_hidapi.dll");return m; }
static FARPROC __cdecl resolve(const char* name) {return GetProcAddress(original(),name);}
#define FORWARD(wrapper,symbol,params) \
static const char wrapper##Name[]=symbol; \
extern "C" __declspec(naked) void __stdcall wrapper params { \
 __asm {pushad} __asm {lea eax,wrapper##Name} __asm {push eax} __asm {call resolve} \
 __asm {add esp,4} __asm {mov [esp+28],eax} __asm {popad} __asm {jmp eax} \
}
FORWARD(ApiInit,"_hid_init@0",())
FORWARD(ApiEnumerate,"_hid_enumerate@8",(int,int))
FORWARD(ApiFree,"_hid_free_enumeration@4",(void*))
FORWARD(ApiOpen,"_hid_open_path@4",(void*))
FORWARD(ApiNonblock,"_hid_set_nonblocking@8",(void*,int))
FORWARD(ApiRead,"_hid_read@12",(void*,void*,int))
FORWARD(ApiWrite,"_hid_write@12",(void*,void*,int))
static void record(const char* op,void* device,unsigned char* data,size_t len,int result,DWORD error) {
 FILE* f=nullptr;if(fopen_s(&f,"hidapi-trace.log","ab")||!f)return;
 SYSTEMTIME t;GetSystemTime(&t);
 fprintf(f,"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ %s device=%p len=%zu result=%d error=%lu data=",t.wYear,t.wMonth,t.wDay,t.wHour,t.wMinute,t.wSecond,t.wMilliseconds,op,device,len,result,error);
 if(data&&len<=512)for(size_t i=0;i<len;i++)fprintf(f,"%02x",data[i]);fprintf(f,"\n");fclose(f);
}
extern "C" int __stdcall ApiSend(void* device,unsigned char* data,size_t len) {
 auto fn=reinterpret_cast<int(__stdcall*)(void*,unsigned char*,size_t)>(GetProcAddress(original(),"_hid_send_feature_report@12"));
 int r=fn(device,data,len);DWORD e=GetLastError();record("SEND",device,data,len,r,e);SetLastError(e);return r;
}
extern "C" int __stdcall ApiGet(void* device,unsigned char* data,size_t len) {
 auto fn=reinterpret_cast<int(__stdcall*)(void*,unsigned char*,size_t)>(GetProcAddress(original(),"_hid_get_feature_report@12"));
 int r=fn(device,data,len);DWORD e=GetLastError();record("GET",device,data,len,r,e);SetLastError(e);return r;
}
