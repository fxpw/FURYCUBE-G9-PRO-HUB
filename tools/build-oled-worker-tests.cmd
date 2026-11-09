@echo off
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /main:OledWorkerTests /out:bin\OledWorkerTests.exe src\G9Oled.cs src\G9OledRuntimeStatus.cs src\G9OledStreaming.cs src\G9OledWorker.cs tests\OledWorkerTests.cs
if errorlevel 1 exit /b 1
bin\OledWorkerTests.exe
exit /b %errorlevel%
