param([Parameter(Mandatory=$true)][long]$WindowHandle)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class FuryInput {
 [DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll")]public static extern void keybd_event(byte vk,byte scan,uint flags,UIntPtr extra);
}
'@
$handle=[IntPtr]$WindowHandle
[uint32]$owner=0;[void][FuryInput]::GetWindowThreadProcessId($handle,[ref]$owner)
$target=Get-Process -Id $owner
if($target.Path -ne 'D:\repos\fxpw\test\FURYCUBE-G9-PRO\portable-hub\FURYCUBE.exe'){throw 'Not the diagnostic HUB'}
[void][FuryInput]::SetForegroundWindow($handle)
Start-Sleep -Milliseconds 100
if([FuryInput]::GetForegroundWindow() -ne $handle){throw 'Target focus not acquired'}
[FuryInput]::keybd_event(0x30,0x0b,0,[UIntPtr]::Zero)
[FuryInput]::keybd_event(0x30,0x0b,2,[UIntPtr]::Zero)
