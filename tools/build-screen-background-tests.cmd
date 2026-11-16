@echo off
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /main:ScreenBackgroundTests /out:bin\ScreenBackgroundTests.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll src\G9ScreenEditor.cs src\G9OledResume.cs src\G9Screen.cs src\G9Sensors.cs src\G9Telemetry.cs src\G9TelemetryReader.cs src\G9Oled.cs src\G9OledRuntimeStatus.cs tests\ScreenBackgroundTests.cs
if errorlevel 1 exit /b 1
bin\ScreenBackgroundTests.exe
exit /b %errorlevel%
