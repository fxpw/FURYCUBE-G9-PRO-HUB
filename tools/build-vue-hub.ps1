param([switch]$SkipFrontend,[string]$Output='FURYCUBE-G9-PRO-HUB.exe')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Copy-Dependency([string]$Source,[string]$Destination){
    if((Test-Path -LiteralPath $Destination) -and (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash){return}
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}
Push-Location $root
try {
    if(!$SkipFrontend){
        & npm.cmd --prefix frontend ci --no-audit --no-fund
        if($LASTEXITCODE -ne 0){throw 'npm ci failed'}
        & npm.cmd --prefix frontend run build
        if($LASTEXITCODE -ne 0){throw 'Vue build failed'}
    }
    $dependency=& (Join-Path $PSScriptRoot 'restore-webview2.ps1')
    $dependency=[string](@($dependency)[-1])
    $line=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build-hub.cmd') | Where-Object {$_ -match '/main:G9Hub '}
    if(@($line).Count -ne 1){throw 'Native build source manifest is ambiguous'}
    $sources=@([regex]::Matches($line,'src\\[A-Za-z0-9]+\.cs')|ForEach-Object {$_.Value})
    $sources+=@('src\G9WebApi.cs','src\G9VueEditors.cs','src\G9VueShell.cs')
    $arguments=@('/nologo','/platform:x64','/target:winexe','/main:G9VueHubProgram',('/out:bin\'+$Output),'/win32icon:assets\workspace.ico','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Web.Extensions.dll',('/reference:'+(Join-Path $dependency 'lib\net462\Microsoft.Web.WebView2.Core.dll')),('/reference:'+(Join-Path $dependency 'lib\net462\Microsoft.Web.WebView2.WinForms.dll')),'/resource:firmware\g9_oled.payload.hex,Furycube.G9.OledPayload','/resource:firmware\g9_ol3.payload.hex,Furycube.G9.Ol3Payload','/resource:firmware\g9_ol4.payload.hex,Furycube.G9.Ol4Payload')+$sources
    & "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" @arguments
    if($LASTEXITCODE -ne 0){throw 'Native Vue host compilation failed'}
    foreach($name in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll')) {Copy-Dependency (Join-Path $dependency ('lib\net462\'+$name)) (Join-Path $root ('bin\'+$name))}
    Copy-Dependency (Join-Path $dependency 'runtimes\win-x64\native\WebView2Loader.dll') (Join-Path $root 'bin\WebView2Loader.dll')
    Copy-Dependency (Join-Path $dependency 'LICENSE.txt') (Join-Path $root 'bin\WebView2-LICENSE.txt')
    Write-Output ('Vue Windows host built: bin\'+$Output)
}finally {Pop-Location}
