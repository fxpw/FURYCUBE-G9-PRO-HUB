using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public class HidProbe {
 [StructLayout(LayoutKind.Sequential)] struct IFACE {public uint cb; public Guid guid; public uint flags; public IntPtr reserved;}
 [StructLayout(LayoutKind.Sequential)] public struct CAPS {
  public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
  [MarshalAs(UnmanagedType.ByValArray,SizeConst=17)] public ushort[] Reserved;
  public ushort NumberLinkCollectionNodes,NumberInputButtonCaps,NumberInputValueCaps,NumberInputDataIndices,NumberOutputButtonCaps,NumberOutputValueCaps,NumberOutputDataIndices,NumberFeatureButtonCaps,NumberFeatureValueCaps,NumberFeatureDataIndices;
 }
 [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g,IntPtr e,IntPtr h,uint f);
 [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s,IntPtr d,ref Guid g,uint i,ref IFACE f);
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s,ref IFACE f,IntPtr detail,uint length,out uint needed,IntPtr d);
 [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(string p,uint access,uint share,IntPtr sa,uint creation,uint flags,IntPtr template);
 [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h,out IntPtr p);
 [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
 [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p,out CAPS c);
 [DllImport("hid.dll",SetLastError=true)] static extern bool HidD_GetFeature(SafeFileHandle h,byte[] data,int length);
 [DllImport("hid.dll",SetLastError=true)] static extern bool HidD_GetInputReport(SafeFileHandle h,byte[] data,int length);
 public static void Main(string[] args) {
  Guid g; HidD_GetHidGuid(out g); IntPtr set=SetupDiGetClassDevs(ref g,IntPtr.Zero,IntPtr.Zero,0x12);
  try {for(uint i=0;;i++) {
   IFACE f=new IFACE(); f.cb=(uint)Marshal.SizeOf(typeof(IFACE)); if(!SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref g,i,ref f))break;
   uint needed; SetupDiGetDeviceInterfaceDetail(set,ref f,IntPtr.Zero,0,out needed,IntPtr.Zero);
   IntPtr buf=Marshal.AllocHGlobal((int)needed);
   try { Marshal.WriteInt32(buf,IntPtr.Size==8?8:6); if(!SetupDiGetDeviceInterfaceDetail(set,ref f,buf,needed,out needed,IntPtr.Zero))continue;
    string path=Marshal.PtrToStringUni(IntPtr.Add(buf,4)); string lower=path.ToLowerInvariant(); if(!lower.Contains("vid_1d57&pid_fa60") && !lower.Contains("vid_1d57&pid_2148"))continue;
    using(var h=CreateFile(path,0,3,IntPtr.Zero,3,0,IntPtr.Zero)) {
     Console.WriteLine("PATH "+path); if(h.IsInvalid) {Console.WriteLine("OPEN ERROR "+Marshal.GetLastWin32Error());continue;}
     IntPtr prep; if(!HidD_GetPreparsedData(h,out prep))continue; CAPS caps;
     try { HidP_GetCaps(prep,out caps); } finally { HidD_FreePreparsedData(prep); }
     Console.WriteLine("CAPS usage="+caps.Usage.ToString("X4")+" page="+caps.UsagePage.ToString("X4")+" input="+caps.InputReportByteLength+" output="+caps.OutputReportByteLength+" feature="+caps.FeatureReportByteLength);
     if(args.Length>0 && args[0]=="input" && caps.UsagePage==1 && caps.Usage==6) {
      for(int id=0;id<=15;id++) {var bytes=new byte[caps.InputReportByteLength];bytes[0]=(byte)id;bool ok=HidD_GetInputReport(h,bytes,bytes.Length);Console.WriteLine("INPUT id="+id+" ok="+ok+" error="+Marshal.GetLastWin32Error()+" "+BitConverter.ToString(bytes));}
     } else if(args.Length>0 && args[0]!="input" && caps.FeatureReportByteLength>0) {
      foreach(string arg in args) {var bytes=new byte[caps.FeatureReportByteLength]; bytes[0]=Convert.ToByte(arg,16); bool ok=HidD_GetFeature(h,bytes,bytes.Length); Console.WriteLine("GET "+arg+" ok="+ok+" error="+Marshal.GetLastWin32Error()+" "+BitConverter.ToString(bytes));}
     }
    }
   } finally {Marshal.FreeHGlobal(buf);}
  }} finally {SetupDiDestroyDeviceInfoList(set);}
 }
}
