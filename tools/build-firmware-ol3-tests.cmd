@echo off
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:exe /main:FirmwareOl3Tests /out:bin\FirmwareOl3Tests.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /resource:firmware\g9_oled.payload.hex,Furycube.G9.OledPayload /resource:firmware\g9_ol3.payload.hex,Furycube.G9.Ol3Payload src\G9Hub.cs src\G9ButtonEditor.cs src\G9Bindings.cs src\G9ScreenEditor.cs src\G9Screen.cs src\G9Sensors.cs src\G9Telemetry.cs src\G9TelemetryReader.cs src\G9FirmwareOledPatch.cs src\G9FirmwareOl3Patch.cs src\G9Oled.cs src\G9OledRuntimeStatus.cs src\G9OledWorker.cs tests\FirmwareOl3Tests.cs
if errorlevel 1 exit /b 1
bin\FirmwareOl3Tests.exe
exit /b %errorlevel%
