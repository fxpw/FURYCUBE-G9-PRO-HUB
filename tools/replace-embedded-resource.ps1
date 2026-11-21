param(
    [Parameter(Mandatory)][string]$Hub,
    [Parameter(Mandatory)][string]$Original,
    [Parameter(Mandatory)][string]$Resource,
    [Parameter(Mandatory)][string]$Staging
)
$ErrorActionPreference = 'Stop'
if ([IO.Path]::GetExtension($Staging) -ne '.disabled') { throw 'Staging copy must be disabled' }
$sha256 = [Security.Cryptography.SHA256]::Create()
try { $hash = [BitConverter]::ToString($sha256.ComputeHash([IO.File]::ReadAllBytes($Original))).Replace('-', '') }
finally { $sha256.Dispose() }
if ($hash -ne '558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4') {
    throw 'Unexpected source updater'
}
$helper = [Reflection.Assembly]::LoadFrom($Hub).GetType('G9FirmwarePatch', $true)
$replace = $helper.GetMethod('ReplaceBinResource', [Reflection.BindingFlags]'Static,NonPublic')
$buffer = [IO.File]::ReadAllBytes($Resource)
[IO.File]::Copy($Original, $Staging, $false)
$null = $replace.Invoke($null, [object[]]@($Staging, $buffer))
