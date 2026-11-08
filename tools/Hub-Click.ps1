param([Parameter(Mandatory=$true)][int]$X,[Parameter(Mandatory=$true)][int]$Y,[long]$WindowHandle=0)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HubClick {
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
}
'@
$targets=@(Get-Process -Name FURYCUBE -ErrorAction Stop | Where-Object { $_.Path -eq 'D:\repos\fxpw\test\FURYCUBE-G9-PRO\portable-hub\FURYCUBE.exe' -and $_.MainWindowHandle -ne 0 })
if($targets.Count -ne 1){throw 'Expected one exact diagnostic HUB window'}
if($X -lt 0 -or $X -gt 1126 -or $Y -lt 0 -or $Y -gt 759){throw 'Outside known client bounds'}
$handle=$targets[0].MainWindowHandle
if($WindowHandle -ne 0){
 [uint32]$owner=0;[void][HubClick]::GetWindowThreadProcessId([IntPtr]$WindowHandle,[ref]$owner)
 if($owner -ne $targets[0].Id){throw 'Popup does not belong to diagnostic HUB'}
 $handle=[IntPtr]$WindowHandle
}
$lp=[IntPtr](($Y -shl 16) -bor $X)
[void][HubClick]::SendMessage($handle,0x200,[IntPtr]::Zero,$lp)
[void][HubClick]::SendMessage($handle,0x201,[IntPtr]1,$lp)
[void][HubClick]::SendMessage($handle,0x202,[IntPtr]::Zero,$lp)
