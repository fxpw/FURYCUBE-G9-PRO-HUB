using System;

public static class G9ClickSignal {
 public static bool Matches(string devicePath,ushort flags,short delta) {
  return devicePath!=null
   && devicePath.IndexOf("VID_1D57&PID_FA60",StringComparison.OrdinalIgnoreCase)>=0
   && devicePath.IndexOf("MI_01",StringComparison.OrdinalIgnoreCase)>=0
   && (flags&0x800)!=0 && delta==-120;
 }
}
