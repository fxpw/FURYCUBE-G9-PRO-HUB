#include "../src/G9Keymap.h"
#include <cstdio>
#include <vector>
static void require(bool value,const char* name){if(!value)throw std::runtime_error(name);}
static bool rejects(const std::vector<unsigned char>& bytes){try{g9::decode(bytes.data(),bytes.size());return false;}catch(const std::exception&){return true;}}
int main(){try{
 const char* hex="083b010200000300000400000b00000d00003c00000500000600000100000100000100000100000100000100000100000100000a00000900000083";
 std::vector<unsigned char> captured;for(std::size_t i=0;hex[i];i+=2){unsigned v=0;sscanf_s(hex+i,"%2x",&v);captured.push_back(static_cast<unsigned char>(v));}
 auto original=g9::decode(captured.data(),captured.size()),zero=original;g9::setZero(zero);
 require(zero[12]==0x11&&zero[13]==0&&zero[14]==0x27&&zero[57]==0&&zero[58]==0xb0,"zero usage/checksum");
 for(std::size_t i=0;i<zero.size();i++)if(i<12||(i>14&&i<57))require(zero[i]==original[i],"other button changed");
 g9::decode(zero.data(),zero.size());auto again=zero;g9::setZero(again);require(again==zero,"zero not idempotent");
 auto stale=captured;stale[0]=11;require(rejects(stale),"stale version response accepted");
 auto bad=captured;bad[14]^=1;require(rejects(bad),"bad checksum accepted");
 auto profile=captured;profile[2]=2;require(rejects(profile),"wrong profile accepted");
 auto truncated=captured;truncated.pop_back();require(rejects(truncated),"short response accepted");
 auto control=zero;g9::setZero(control,2);require(control[9]==0x11&&control[10]==0&&control[11]==0x27,"middle-button control");g9::decode(control.data(),control.size());
 for(std::size_t i=0;i<zero.size();i++)if(i<9||(i>11&&i<57))require(control[i]==zero[i],"middle control changed another button");
 bool outside=false;try{g9::setZero(control,18);}catch(const std::exception&){outside=true;}require(outside,"out-of-range button accepted");
 std::puts("PASS: captured map, USB usage/checksum, preservation, idempotence, stale ID, checksum, profile and truncation");return 0;
}catch(const std::exception& e){std::printf("FAIL: %s\n",e.what());return 1;}}
