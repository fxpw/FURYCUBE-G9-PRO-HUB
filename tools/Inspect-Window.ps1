param([long]$WindowHandle=0,[string]$OutputName='hub-window.png')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FuryWindow {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
}
'@
$p = Get-Process -Name FURYCUBE | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
if (!$p) { throw 'FURYCUBE window not found' }
$handle=$p.MainWindowHandle
if($WindowHandle -ne 0){$handle=[IntPtr]$WindowHandle}
$r = New-Object FuryWindow+RECT
[void][FuryWindow]::GetWindowRect($handle, [ref]$r)
$bitmap = New-Object Drawing.Bitmap ($r.R-$r.L),($r.B-$r.T)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$dc=$graphics.GetHdc()
try { $ok=[FuryWindow]::PrintWindow($handle,$dc,2) } finally { $graphics.ReleaseHdc($dc) }
$out=Join-Path $PSScriptRoot ('..\evidence\'+$OutputName)
$bitmap.Save($out,[Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose(); $bitmap.Dispose()
[pscustomobject]@{Saved=$out; PrintWindow=$ok; Pid=$p.Id; Bounds=$r} | ConvertTo-Json
