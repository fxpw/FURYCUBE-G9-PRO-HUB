param(
 [switch]$PrepareTarget,
 [switch]$Capture,
 [switch]$HeadersOnly,
 [ValidateRange(1,30)][int]$Seconds=20
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$bin=Join-Path $root 'bin'
$evidence=Join-Path $root 'evidence'
New-Item -ItemType Directory -Path $bin,$evidence -Force | Out-Null
$exe=Join-Path $bin 'G9UsbTrace.exe'
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /platform:x64 /optimize+ /reference:System.Web.Extensions.dll "/out:$exe" (Join-Path $root 'src\G9UsbTrace.cs')
if($LASTEXITCODE -ne 0){throw "USB trace build failed ($LASTEXITCODE)"}
& $exe --self-test
if($LASTEXITCODE -ne 0){throw 'USB trace parser self-test failed'}
if(-not ($PrepareTarget -or $Capture -or $HeadersOnly)){Write-Output "Built $exe; no ETW session started.";return}

# Query only PnP metadata. Never open/reset the device or send it a USB command.
$targets=@(Get-PnpDevice -PresentOnly | Where-Object {$_.InstanceId -match '^USB\\VID_1D57&PID_2148\\' -and $_.Class -eq 'USB'})
if($targets.Count -ne 1){throw "Expected exactly one wired G9 PRO USBparent; found$($targets.Count)"}
$device=$targets[0]
$location=@((Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName DEVPKEY_Device_LocationPaths).Data | Where-Object {$_ -like 'PCIROOT(*)#*#USBROOT(*)#USB(*)'})
if($location.Count -ne 1){throw 'Cannot prove a unique PCI/USB location path'}
$ports=@([regex]::Matches($location[0],'#USB\(([0-9]+)\)') | ForEach-Object {[int]$_.Groups[1].Value})
if($ports.Count -lt 1 -or $ports.Count -gt 6){throw 'Unexpected USB port chain'}
$current=$device.InstanceId
$controller=$null
for($i=0;$i -lt 8;$i++){
 $current=(Get-PnpDeviceProperty -InstanceId $current -KeyName DEVPKEY_Device_Parent).Data
 if($current -like 'PCI\*'){$controller=$current;break}
}
if(-not $controller){throw 'Cannot find the G9 host PCI controller'}
$bus=[uint32](Get-PnpDeviceProperty -InstanceId $controller -KeyName DEVPKEY_Device_BusNumber).Data
$address=[uint32](Get-PnpDeviceProperty -InstanceId $controller -KeyName DEVPKEY_Device_Address).Data
if($controller -notmatch 'VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})'){throw 'Cannot identify PCI controller vendor/device'}
$pciVendor=[Convert]::ToInt32($Matches[1],16)
$pciProduct=[Convert]::ToInt32($Matches[2],16)
$manifest=[ordered]@{
 InstanceId=$device.InstanceId;VendorId=0x1d57;ProductId=0x2148
 PdoName=(Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName DEVPKEY_Device_PDOName).Data
 ControllerInstanceId=$controller;LocationPath=$location[0];Ports=@($ports)
 PciBus=$bus;PciDevice=($address -shr 16);PciFunction=($address -band 0xffff)
 PciVendorId=$pciVendor;PciDeviceId=$pciProduct;SnapshotUtc=[DateTime]::UtcNow.ToString('o')
}
$stamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$targetPath=Join-Path $evidence "usb-target-$stamp.json"
$outputPath=Join-Path $evidence "usb-reports-$stamp.jsonl"
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $targetPath -Encoding UTF8
Write-Output "Target metadata: $targetPath"
Write-Output "Capture output: $outputPath"
if(-not ($Capture -or $HeadersOnly)){
 Write-Output "Prepared only. Run elevated within5minutes:"
 Write-Output "& '$exe' --target-json '$targetPath' --output '$outputPath' --seconds $Seconds --headers-only"
 return
}
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(-not $admin){throw 'Capture requires administrator; the build and metadata preparation succeeded. Re-run this script elevated. No ETW session was started.'}
$traceArgs=@('--target-json',$targetPath,'--output',$outputPath,'--seconds',"$Seconds")
if($HeadersOnly){$traceArgs+='--headers-only'}
& $exe @traceArgs
if($LASTEXITCODE -ne 0){throw "USB trace failed; see$outputPath"}
