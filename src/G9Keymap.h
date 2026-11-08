#pragma once
#include <array>
#include <cstddef>
#include <cstring>
#include <stdexcept>

namespace g9 {
using Keymap = std::array<unsigned char,59>;
inline Keymap decode(const unsigned char* bytes,std::size_t size) {
 if(size<59 || bytes[0]!=8 || bytes[1]!=59 || bytes[2]!=1)
  throw std::runtime_error("Unexpected keymap response; no configuration was accepted");
 unsigned sum=0;for(std::size_t i=3;i<57;i++)sum+=bytes[i];
 if(sum!=static_cast<unsigned>((bytes[57]<<8)|bytes[58]))throw std::runtime_error("Keymap checksum mismatch");
 Keymap result{};std::memcpy(result.data(),bytes,result.size());return result;
}
inline void setZero(Keymap& packet,std::size_t index=3) {
 if(index>=18)throw std::runtime_error("Button index outside keymap");
 std::size_t offset=3+index*3;packet[offset]=0x11;packet[offset+1]=0;packet[offset+2]=0x27;
 unsigned sum=0;for(std::size_t i=3;i<57;i++)sum+=packet[i];
 packet[57]=static_cast<unsigned char>(sum>>8);packet[58]=static_cast<unsigned char>(sum);
}
}
