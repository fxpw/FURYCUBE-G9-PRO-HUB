@echo off
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:library /out:bin\G9Oled.dll src\G9Oled.cs src\G9OledRuntimeStatus.cs src\G9OledTransport.cs
if errorlevel 1 exit /b 1
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:exe /out:bin\OledProtocolTests.exe src\G9Oled.cs src\G9OledRuntimeStatus.cs src\G9OledTransport.cs tests\OledProtocolTests.cs
if errorlevel 1 exit /b 1
bin\OledProtocolTests.exe
exit /b %errorlevel%
