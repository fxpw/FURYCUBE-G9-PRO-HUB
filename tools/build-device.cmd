@echo off
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:exe /out:bin\G9Device.exe src\G9Device.cs
if errorlevel 1 exit /b 1
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:exe /main:DeviceProtocolTests /out:bin\DeviceProtocolTests.exe src\G9Device.cs tests\DeviceProtocolTests.cs
if errorlevel 1 exit /b 1
bin\DeviceProtocolTests.exe
exit /b %errorlevel%
