#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>

static HMODULE original() {
    static HMODULE m = LoadLibraryW(L"real_hiddriver_1.dll");
    return m;
}
static FARPROC __cdecl resolve(const char* name) { return GetProcAddress(original(), name); }
#define FORWARD(wrapper, symbol) \
static const char wrapper##Name[] = symbol; \
extern "C" __declspec(naked) void wrapper() { \
    __asm { pushad } \
    __asm { lea eax, wrapper##Name } \
    __asm { push eax } \
    __asm { call resolve } \
    __asm { add esp, 4 } \
    __asm { mov [esp+28], eax } \
    __asm { popad } \
    __asm { jmp eax } \
}
FORWARD(ForwardId,"Set_VIDPID")
FORWARD(ForwardOpenMonitor,"Open_DevMonitor")
FORWARD(ForwardCloseMonitor,"Close_DevMonitor")
FORWARD(ForwardOpen,"Open_FeatureDevice")
FORWARD(ForwardClose,"Close_FeatureDevice")
FORWARD(ForwardRead,"Hid_Read")
FORWARD(ForwardWrite,"Hid_Write")
static void record(const char* operation, const unsigned char* data, int len, int result, DWORD error) {
    FILE* f=nullptr;
    if (fopen_s(&f,"hid-trace.log","ab") || !f) return;
    SYSTEMTIME t; GetSystemTime(&t);
    fprintf(f,"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ %s len=%d result=%d error=%lu data=",t.wYear,t.wMonth,t.wDay,t.wHour,t.wMinute,t.wSecond,t.wMilliseconds,operation,len,result,error);
    if (len>=0 && len<=512 && data)for(int i=0;i<len;i++)fprintf(f,"%02x",data[i]);
    fprintf(f,"\n"); fclose(f);
}
extern "C" int __stdcall TraceSet(unsigned char* data,int len) {
    auto fn=reinterpret_cast<int(__stdcall*)(unsigned char*,int)>(GetProcAddress(original(),"SetFeature"));
    if(!fn)return 0;
    int result=fn(data,len); DWORD error=GetLastError(); record("SET",data,len,result,error); SetLastError(error); return result;
}
extern "C" int __stdcall TraceGet(unsigned char* data,int len) {
    auto fn=reinterpret_cast<int(__stdcall*)(unsigned char*,int)>(GetProcAddress(original(),"GetFeature"));
    if(!fn)return 0;
    int result=fn(data,len); DWORD error=GetLastError(); record("GET",data,len,result,error); SetLastError(error); return result;
}
