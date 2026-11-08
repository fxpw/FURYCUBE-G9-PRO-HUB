#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cstdlib>

int main(int argc, char** argv) {
    HMODULE mod = argc>1 ? LoadLibraryA(argv[1]) : LoadLibraryW(L"C:\\Program Files (x86)\\FURYCUBE HUB\\hiddriver_1.dll");
    if (!mod) { std::printf("LoadLibrary error=%lu\n", GetLastError()); return 1; }
    auto setId = reinterpret_cast<int (__stdcall*)(int,int)>(GetProcAddress(mod,"Set_VIDPID"));
    auto open = reinterpret_cast<int (__cdecl*)()>(GetProcAddress(mod,"Open_FeatureDevice"));
    auto close = reinterpret_cast<int (__cdecl*)()>(GetProcAddress(mod,"Close_FeatureDevice"));
    auto get = reinterpret_cast<int (__stdcall*)(unsigned char*,int)>(GetProcAddress(mod,"GetFeature"));
    if (!setId || !open || !close || !get) return 2;
    std::printf("Set_VIDPID=%d\n",setId(0x1d57,0xfa60));
    int opened=open(); std::printf("Open_FeatureDevice=%d error=%lu\n",opened,GetLastError());
    if (opened) {
        const int ids[]={0xA0,0x09};
        for (int id: ids) {
            unsigned char data[200]={}; data[0]=static_cast<unsigned char>(id);
            int len=id==0xA0?8:64;
            SetLastError(0); int result=get(data,len); DWORD error=GetLastError();
            std::printf("GetFeature id=%02x len=%d result=%d error=%lu data=",id,len,result,error);
            for(int i=0;i<len;++i)std::printf("%02x",data[i]); std::puts("");
        }
        close();
    }
    FreeLibrary(mod); return opened?0:3;
}
