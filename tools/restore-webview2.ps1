$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version='1.0.4314.35'
$folder=Join-Path $root ('.deps\webview2-'+$version)
$archive=Join-Path $root ('.deps\microsoft.web.webview2.'+$version+'.zip')
$expected='90778DEB1B77E36292828B4B5833C9255C27533F40E0DDD55891AE718944EDC4'
$core=Join-Path $folder 'lib\net462\Microsoft.Web.WebView2.Core.dll'
if(!(Test-Path -LiteralPath $core)){
    [void][IO.Directory]::CreateDirectory((Join-Path $root '.deps'))
    if(!(Test-Path -LiteralPath $archive)){
        [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri ('https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/'+$version+'/microsoft.web.webview2.'+$version+'.nupkg') -OutFile $archive
    }
    if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected){throw 'WebView2 NuGet archive checksum mismatch'}
    Expand-Archive -LiteralPath $archive -DestinationPath $folder -Force
}
if(!(Test-Path -LiteralPath $core)){throw 'WebView2 dependency is incomplete'}
Write-Output $folder
