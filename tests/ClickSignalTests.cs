using System;
class ClickSignalTests {
 static int Main() {
  string g9=@"\\?\HID#VID_1D57&PID_FA60&MI_01#receiver";
  bool[] pass={
   G9ClickSignal.Matches(g9,0x800,-120),
   G9ClickSignal.Matches(g9.ToLowerInvariant(),0x800,-120),
   !G9ClickSignal.Matches(g9,0x400,-120),
   !G9ClickSignal.Matches(g9,0x800,120),
   !G9ClickSignal.Matches(g9,0x800,-9120),
   !G9ClickSignal.Matches(g9,1,0),
   !G9ClickSignal.Matches(@"HID#VID_25A7&PID_FA70&MI_01",0x800,-120),
   !G9ClickSignal.Matches(@"HID#VID_1D57&PID_FA60&MI_00",0x800,-120),
   !G9ClickSignal.Matches(null,0x800,-120)
  };
  for(int i=0;i<pass.Length;i++)if(!pass[i]){Console.WriteLine("FAIL fixture "+i);return 1;}
  Console.WriteLine("PASS "+pass.Length+" fixtures: sixth-button signal, other devices/buttons and malformed delta");return 0;
 }
}
