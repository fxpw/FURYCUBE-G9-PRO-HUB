@echo off
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /optimize+ /out:bin\OledStreamTests.exe src\G9Oled.cs src\G9OledStreaming.cs tests\OledStreamTests.cs
if errorlevel 1 exit /b 1
bin\OledStreamTests.exe
exit /b %errorlevel%
