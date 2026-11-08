Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class FuryWindows {
 public delegate bool EnumProc(IntPtr h,IntPtr p);
 [StructLayout(LayoutKind.Sequential)]public struct RECT {public int L,T,R,B;}
 [DllImport("user32.dll")]public static extern bool EnumWindows(EnumProc c,IntPtr p);
 [DllImport("user32.dll")]public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern int GetWindowText(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern int GetClassName(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h,out RECT r);
}
'@
$furyId=(Get-Process -Name FURYCUBE).Id
$found=[Collections.Generic.List[object]]::new()
[void][FuryWindows]::EnumWindows({param($h,$p)
 [uint32]$id=0;[void][FuryWindows]::GetWindowThreadProcessId($h,[ref]$id)
 if($id -eq $furyId -and [FuryWindows]::IsWindowVisible($h)) {
  $t=New-Object Text.StringBuilder 256;$c=New-Object Text.StringBuilder 256;$r=New-Object FuryWindows+RECT
  [void][FuryWindows]::GetWindowText($h,$t,256);[void][FuryWindows]::GetClassName($h,$c,256);[void][FuryWindows]::GetWindowRect($h,[ref]$r)
  $found.Add([pscustomobject]@{Handle=$h.ToInt64();Class=$c.ToString();Title=$t.ToString();Bounds=$r})
 };return $true
},[IntPtr]::Zero)
$found | ConvertTo-Json -Depth 3
